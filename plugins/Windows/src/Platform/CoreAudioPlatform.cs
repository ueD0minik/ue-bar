// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace UeBar.Modules.Windows.Platform;

/// <summary>
/// Volume, outputs and app sessions through Core Audio. Event driven: device notifications, endpoint
/// volume callback and session notifications only raise <see cref="Changed"/>/<see cref="AppsChanged"/>;
/// the module reads the state afterwards on its worker (never inside a callback).
/// Must be created on an MTA thread (the thread pool).
/// </summary>
internal sealed class CoreAudioPlatform : IAudioPlatform, IMMNotificationClient, IAudioEndpointVolumeCallback, IAudioSessionNotification
{
    private static Guid _context = Guid.NewGuid();
    private static Guid _endpointVolumeId = typeof(IAudioEndpointVolume).GUID;
    private static Guid _sessionManagerId = typeof(IAudioSessionManager2).GUID;

    private readonly IMMDeviceEnumerator _enumerator;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, SessionEntry> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, (string Key, string Name)> _processNames = [];
    private readonly HashSet<string> _skipped = new(StringComparer.Ordinal);
    private string? _endpointDeviceId;
    private IAudioEndpointVolume? _endpointVolume;
    private readonly Dictionary<string, IAudioSessionManager2> _managers = new(StringComparer.Ordinal);
    private bool _disposed;

    public CoreAudioPlatform()
    {
        _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorClass();
        Check(_enumerator.RegisterEndpointNotificationCallback(this), "RegisterEndpointNotificationCallback");
        AttachDefaultDevice();
        SyncSessions();
    }

    public event EventHandler? Changed;

    public event EventHandler? AppsChanged;

    // ------------------------------------------------------------------ state

    public AudioState GetState()
    {
        lock (_lock)
        {
            AttachDefaultDevice();
            var outputs = new List<AudioDevice>();
            if (_enumerator.EnumAudioEndpoints(EDataFlow.Render, DeviceState.Active, out var collection) == 0)
            {
                collection.GetCount(out var count);
                for (uint i = 0; i < count; i++)
                {
                    if (collection.Item(i, out var device) == 0 && device.GetId(out var id) == 0)
                    {
                        outputs.Add(Describe(device, id));
                    }
                }
            }

            var volume = 0f;
            var muted = false;
            if (_endpointVolume is { } endpoint)
            {
                endpoint.GetMasterVolumeLevelScalar(out volume);
                endpoint.GetMute(out muted);
            }

            return new AudioState(ShortNames(outputs), _endpointDeviceId, Math.Clamp(volume, 0, 1), muted);
        }
    }

    public void SetVolume(double volume)
    {
        lock (_lock)
        {
            if (_endpointVolume is { } endpoint)
            {
                Check(endpoint.SetMasterVolumeLevelScalar((float)Math.Clamp(volume, 0, 1), ref _context), "SetMasterVolumeLevelScalar");
            }
        }
    }

    public void SetMute(bool muted)
    {
        lock (_lock)
        {
            if (_endpointVolume is { } endpoint)
            {
                Check(endpoint.SetMute(muted, ref _context), "SetMute");
            }
        }
    }

    public void SetDefaultOutput(string deviceId)
    {
        IPolicyConfig policy;
        try
        {
            policy = (IPolicyConfig)new PolicyConfigClientClass();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            throw new AudioSwitchException("IPolicyConfig is not available on this Windows version", ex);
        }

        try
        {
            foreach (var role in new[] { ERole.Console, ERole.Multimedia, ERole.Communications })
            {
                var hr = policy.SetDefaultEndpoint(deviceId, role);
                if (hr != 0)
                {
                    throw new AudioSwitchException($"SetDefaultEndpoint failed (0x{hr:X8})");
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(policy);
        }
    }

    // ------------------------------------------------------------------ apps

    public IReadOnlyList<AppAudio> GetApps()
    {
        lock (_lock)
        {
            SyncSessions();
            return
            [
                .. _sessions.Values
                    .Where(s => s.Key.Length > 0)
                    .GroupBy(s => s.Key, StringComparer.Ordinal)
                    .Select(g =>
                    {
                        var states = g.Select(s => (Volume: Volume(s), Muted: Muted(s), Active: State(s) == AudioSessionStates.Active)).ToList();
                        return new AppAudio(g.Key, g.First().Name, states.Max(s => s.Volume), states.All(s => s.Muted), states.Any(s => s.Active));
                    })
                    .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase),
            ];
        }
    }

    public void SetAppVolume(string key, double volume)
    {
        lock (_lock)
        {
            foreach (var session in _sessions.Values.Where(s => s.Key == key))
            {
                session.Volume.SetMasterVolume((float)Math.Clamp(volume, 0, 1), ref _context);
            }
        }
    }

    public void SetAppMute(string key, bool muted)
    {
        lock (_lock)
        {
            foreach (var session in _sessions.Values.Where(s => s.Key == key))
            {
                session.Volume.SetMute(muted, ref _context);
            }
        }
    }

    // ------------------------------------------------------------------ internals

    /// <summary>(Re)binds the endpoint volume to the current default output.</summary>
    private void AttachDefaultDevice()
    {
        if (_disposed || _enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out var device) != 0)
        {
            DetachEndpoint();
            return;
        }

        device.GetId(out var id);
        if (id == _endpointDeviceId && _endpointVolume is not null)
        {
            return;
        }

        DetachEndpoint();
        _endpointDeviceId = id;
        if (device.Activate(ref _endpointVolumeId, AudioNative.ClsCtxAll, IntPtr.Zero, out var volume) == 0)
        {
            _endpointVolume = (IAudioEndpointVolume)volume;
            _endpointVolume.RegisterControlChangeNotify(this);
        }
    }

    private void DetachEndpoint()
    {
        if (_endpointVolume is { } volume)
        {
            volume.UnregisterControlChangeNotify(this);
            _endpointVolume = null;
        }

        _endpointDeviceId = null;
    }

    /// <summary>
    /// One session manager per active output: apps may play on any of them (mixer tools such as
    /// Wave Link route every app to its own virtual device) – like the Windows volume mixer.
    /// </summary>
    private void SyncManagers()
    {
        var active = new HashSet<string>(StringComparer.Ordinal);
        if (!_disposed && _enumerator.EnumAudioEndpoints(EDataFlow.Render, DeviceState.Active, out var collection) == 0)
        {
            collection.GetCount(out var count);
            for (uint i = 0; i < count; i++)
            {
                if (collection.Item(i, out var device) != 0 || device.GetId(out var id) != 0)
                {
                    continue;
                }

                active.Add(id);
                if (!_managers.ContainsKey(id) && device.Activate(ref _sessionManagerId, AudioNative.ClsCtxAll, IntPtr.Zero, out var manager) == 0)
                {
                    var sessions = (IAudioSessionManager2)manager;
                    sessions.RegisterSessionNotification(this);
                    _managers[id] = sessions;
                }
            }
        }

        foreach (var gone in _managers.Keys.Where(k => !active.Contains(k)).ToList())
        {
            _managers[gone].UnregisterSessionNotification(this);
            _managers.Remove(gone);
        }
    }

    private void DetachAll()
    {
        DetachEndpoint();
        foreach (var session in _sessions.Values)
        {
            session.Detach();
        }

        _sessions.Clear();
        foreach (var manager in _managers.Values)
        {
            manager.UnregisterSessionNotification(this);
        }

        _managers.Clear();
    }

    /// <summary>Brings the session list up to date: registers events for new sessions, drops expired ones.</summary>
    private void SyncSessions()
    {
        SyncManagers();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var manager in _managers.Values)
        {
            // Asking the enumerator is also what starts the session notifications.
            if (manager.GetSessionEnumerator(out var enumerator) != 0)
            {
                continue;
            }

            enumerator.GetCount(out var count);
            for (var i = 0; i < count; i++)
            {
                if (enumerator.GetSession(i, out var control) != 0
                    || control.GetSessionInstanceIdentifier(out var instance) != 0
                    || control.IsSystemSoundsSession() == 0)
                {
                    continue;
                }

                control.GetState(out var state);
                if (state == AudioSessionStates.Expired)
                {
                    continue;
                }

                seen.Add(instance);
                if (!_sessions.ContainsKey(instance) && !_skipped.Contains(instance))
                {
                    if (control is not ISimpleAudioVolume volume)
                    {
                        // Some sessions (e.g. of protected processes) do not offer their volume – skip them.
                        _skipped.Add(instance);
                        continue;
                    }

                    control.GetProcessId(out var pid);
                    var (key, name) = ProcessName(pid);
                    var entry = new SessionEntry(control, volume, key, name, this);
                    control.RegisterAudioSessionNotification(entry);
                    _sessions[instance] = entry;
                }
            }
        }

        foreach (var gone in _sessions.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _sessions[gone].Detach();
            _sessions.Remove(gone);
        }

        _skipped.IntersectWith(seen);
    }

    private (string Key, string Name) ProcessName(uint pid)
    {
        if (_processNames.TryGetValue(pid, out var known))
        {
            return known;
        }

        var result = ("", "");
        try
        {
            using var process = Process.GetProcessById((int)pid);
            var key = process.ProcessName.ToLowerInvariant();
            string? name = null;
            try
            {
                name = process.MainModule?.FileVersionInfo.FileDescription;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // Elevated or protected process: the process name has to do.
            }

            result = (key, string.IsNullOrWhiteSpace(name) ? process.ProcessName : name.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // Process already gone.
        }

        if (_processNames.Count > 200)
        {
            _processNames.Clear();
        }

        _processNames[pid] = result;
        return result;
    }

    private AudioDevice Describe(IMMDevice device, string id)
    {
        string? description = null;
        string? detail = null;
        string? friendly = null;
        string? enumerator = null;
        if (device.OpenPropertyStore(0, out var store) == 0)
        {
            description = AudioNative.ReadString(store, AudioKeys.DeviceDescription);
            detail = AudioNative.ReadString(store, AudioKeys.InterfaceFriendlyName);
            friendly = AudioNative.ReadString(store, AudioKeys.DeviceFriendlyName);
            enumerator = AudioNative.ReadString(store, AudioKeys.EnumeratorName);
        }

        return new AudioDevice(id, description ?? friendly ?? id, detail, IsVirtual(enumerator, detail, friendly));
    }

    /// <summary>
    /// Software devices: enumerator ROOT/SWD (VB-Cable, Voicemeeter …) or "Virtual" in the driver name
    /// (Elgato Wave Link's app channels, virtual cables).
    /// </summary>
    internal static bool IsVirtual(string? enumerator, string? detail, string? friendlyName) =>
        enumerator is not null && (enumerator.Equals("ROOT", StringComparison.OrdinalIgnoreCase) || enumerator.Equals("SWD", StringComparison.OrdinalIgnoreCase))
        || (detail ?? "").Contains("Virtual", StringComparison.OrdinalIgnoreCase)
        || (friendlyName ?? "").Contains("Virtual", StringComparison.OrdinalIgnoreCase);

    /// <summary>"Speakers" twice → "Speakers (Realtek(R) Audio)" – short where possible, unique always.</summary>
    internal static IReadOnlyList<AudioDevice> ShortNames(IReadOnlyList<AudioDevice> devices) =>
    [
        .. devices.Select(d =>
            devices.Count(o => string.Equals(o.Name, d.Name, StringComparison.CurrentCultureIgnoreCase)) > 1 && !string.IsNullOrWhiteSpace(d.Detail)
                ? d with { Name = $"{d.Name} ({d.Detail})" }
                : d),
    ];

    private static double Volume(SessionEntry session) => session.Volume.GetMasterVolume(out var level) == 0 ? level : 0;

    private static bool Muted(SessionEntry session) => session.Volume.GetMute(out var muted) == 0 && muted;

    private static int State(SessionEntry session) => session.Control.GetState(out var state) == 0 ? state : AudioSessionStates.Expired;

    private static void Check(int hr, string what)
    {
        if (hr < 0)
        {
            throw new COMException($"{what} failed", hr);
        }
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private void RaiseAppsChanged() => AppsChanged?.Invoke(this, EventArgs.Empty);

    // ------------------------------------------------------------------ callbacks (any thread – raise only)

    void IMMNotificationClient.OnDeviceStateChanged(string deviceId, uint newState) => RaiseDevicesChanged();

    void IMMNotificationClient.OnDeviceAdded(string deviceId) => RaiseDevicesChanged();

    void IMMNotificationClient.OnDeviceRemoved(string deviceId) => RaiseDevicesChanged();

    /// <summary>Outputs came or went: also their app sessions.</summary>
    private void RaiseDevicesChanged()
    {
        RaiseChanged();
        RaiseAppsChanged();
    }

    void IMMNotificationClient.OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? defaultDeviceId)
    {
        if (flow == EDataFlow.Render && role == ERole.Multimedia)
        {
            RaiseChanged();
            RaiseAppsChanged();
        }
    }

    void IMMNotificationClient.OnPropertyValueChanged(string deviceId, PropertyKey key)
    {
        // Fires constantly for many properties – names change rarely; the next state read gets them.
    }

    int IAudioEndpointVolumeCallback.OnNotify(IntPtr notifyData)
    {
        RaiseChanged();
        return 0;
    }

    int IAudioSessionNotification.OnSessionCreated(IAudioSessionControl2 newSession)
    {
        RaiseAppsChanged();
        return 0;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                DetachAll();
                _enumerator.UnregisterEndpointNotificationCallback(this);
            }
            catch (COMException)
            {
                // Audio service gone – nothing to unregister.
            }
        }
    }

    /// <summary>A session with its event sink.</summary>
    private sealed class SessionEntry(IAudioSessionControl2 control, ISimpleAudioVolume volume, string key, string name, CoreAudioPlatform owner) : IAudioSessionEvents
    {
        public IAudioSessionControl2 Control { get; } = control;

        public ISimpleAudioVolume Volume { get; } = volume;

        public string Key { get; } = key;

        public string Name { get; } = name;

        public void Detach()
        {
            try
            {
                Control.UnregisterAudioSessionNotification(this);
            }
            catch (COMException)
            {
            }
        }

        public int OnDisplayNameChanged(string newName, ref Guid eventContext) => 0;

        public int OnIconPathChanged(string newPath, ref Guid eventContext) => 0;

        public int OnSimpleVolumeChanged(float newVolume, bool newMute, ref Guid eventContext)
        {
            owner.RaiseAppsChanged();
            return 0;
        }

        public int OnChannelVolumeChanged(uint channelCount, IntPtr newChannelVolumes, uint changedChannel, ref Guid eventContext) => 0;

        public int OnGroupingParamChanged(ref Guid newGroupingParam, ref Guid eventContext) => 0;

        public int OnStateChanged(int newState)
        {
            owner.RaiseAppsChanged();
            return 0;
        }

        public int OnSessionDisconnected(int disconnectReason)
        {
            owner.RaiseAppsChanged();
            return 0;
        }
    }
}
