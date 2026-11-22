// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Globalization;
using UeBar.Sdk;
using UeBar.Core.Localization;
using UeBar.Core.Logging;

namespace UeBar.Core.Modules;

/// <summary>Context of a running module. Each start gets a new context with its own token.</summary>
internal sealed class ModuleContext : IModuleContext, IModuleSecrets, IHostInfo, IUiLanguageInfo, IDeviceBatteryCatalog, IPinService, IScreenAreaPicker, IClipboardAccess, IDisposable
{
    private readonly ModuleGuard _guard;
    private readonly string _dataRoot;
    private readonly Settings.IModuleSecretStore? _secrets;
    private readonly CancellationTokenSource _cts = new();
    private readonly UiCultureSource _culture;
    private int _disposed;

    private readonly Func<string>? _deviceName;
    private readonly DeviceBatteryHub? _batteries;
    private readonly HostServices _host;

    public ModuleContext(string moduleId, IModuleBus bus, IModuleLogger log, ModuleGuard guard, string dataRoot, Settings.IModuleSecretStore? secrets = null, Func<string>? deviceName = null, UiCultureSource? culture = null, DeviceBatteryHub? batteries = null, HostServices? host = null)
    {
        _batteries = batteries;
        _host = host ?? new HostServices();
        _secrets = secrets;
        _deviceName = deviceName;
        _culture = culture ?? UiCultureSource.Shared;
        ModuleId = moduleId;
        Bus = bus;
        Log = log;
        _guard = guard;
        _dataRoot = dataRoot;
        Stopping = _cts.Token;
        _culture.Changed += OnCultureChanged;
        if (_batteries is not null)
        {
            _batteries.Changed += OnBatteriesChanged;
        }

        if (_host.Pins is { } pins)
        {
            pins.Changed += OnPinsChanged;
        }
    }

    public string ModuleId { get; }

    public IModuleBus Bus { get; }

    public IModuleLogger Log { get; }

    public CancellationToken Stopping { get; }

    public string DataDirectory
    {
        get
        {
            var dir = Path.Combine(_dataRoot, "modules", ModuleId);
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>Device name (M7) – setting or the Windows computer name.</summary>
    public string DeviceName => _deviceName?.Invoke() is { Length: > 0 } name ? name : Environment.MachineName;

    /// <summary>UI culture (R1).</summary>
    public CultureInfo UiCulture => _culture.Current;

    public event EventHandler<CultureInfo>? UiCultureChanged;

    /// <summary>Device batteries of the other modules (P2-2).</summary>
    public event EventHandler? DeviceBatteriesChanged;

    public IReadOnlyList<DeviceBatteryInfo> GetDeviceBatteries() => _batteries?.Get(ModuleId) ?? [];

    public string? GetSecret(string key) => _secrets?.GetModuleSecret(ModuleId, key);

    public void SetSecret(string key, string? value) => _secrets?.SetModuleSecret(ModuleId, key, value);

    // ------------------------------------------------------------------ pins (P2-5)

    public event EventHandler? PinsChanged;

    public IReadOnlyList<PinInfo> GetPins() => _host.Pins?.InfosOf(ModuleId) ?? [];

    public string? CreateTextPin(TextPinRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Stopping.IsCancellationRequested ? null : _host.Pins?.CreateText(request.Text, request.Title, request.FontSize, ModuleId);
    }

    public string? CreateImagePin(ImagePinRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Stopping.IsCancellationRequested ? null : _host.Pins?.CreateImage(request.ImageData, request.Title, request.ScreenBounds, ModuleId);
    }

    public bool UpdatePin(string pinId, PinUpdate update) => _host.Pins?.UpdateFromModule(ModuleId, pinId, update) == true;

    public bool ClosePin(string pinId) => _host.Pins?.CloseFromModule(ModuleId, pinId) == true;

    private void OnPinsChanged(object? sender, Pins.PinChangedEventArgs e)
    {
        if (e.Kind is Pins.PinChangeKind.Placement or Pins.PinChangeKind.Options
            || !string.Equals(e.Pin.Owner, ModuleId, StringComparison.OrdinalIgnoreCase)
            || Volatile.Read(ref _disposed) != 0 || Stopping.IsCancellationRequested || PinsChanged is not { } handler)
        {
            return;
        }

        _guard.Invoke(ModuleId, "Pins changed", () => handler(this, EventArgs.Empty));
    }

    // ------------------------------------------------------------------ screen area, clipboard (P2-5)

    public async Task<ScreenAreaResult?> PickScreenAreaAsync(ScreenAreaRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_host.ScreenAreaPicker is not { } picker || Stopping.IsCancellationRequested)
        {
            return null;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Stopping);
        return await picker.PickScreenAreaAsync(request, linked.Token).ConfigureAwait(false);
    }

    public Task<bool> SetTextAsync(string text, CancellationToken cancellationToken) =>
        _host.Clipboard is { } clipboard && !string.IsNullOrEmpty(text) ? clipboard.SetTextAsync(text, cancellationToken) : Task.FromResult(false);

    public Task<bool> SetImageAsync(byte[] png, CancellationToken cancellationToken) =>
        _host.Clipboard is { } clipboard && png is { Length: > 0 } ? clipboard.SetImageAsync(png, cancellationToken) : Task.FromResult(false);

    public void RunBackground(string name, Func<CancellationToken, Task> work)
    {
        _ = RunAsync();

        async Task RunAsync()
        {
            try
            {
                await Task.Run(() => work(Stopping), CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (Stopping.IsCancellationRequested)
            {
                // normal end while stopping
            }
            catch (Exception ex)
            {
                if (!Stopping.IsCancellationRequested)
                {
                    _guard.ReportFault(ModuleId, $"Background: {name}", ex);
                }
            }
        }
    }

    public void ReportFault(Exception exception, string? operation = null) =>
        _guard.ReportFault(ModuleId, operation ?? "Reported by module", exception);

    private void OnCultureChanged(object? sender, CultureInfo culture)
    {
        if (Volatile.Read(ref _disposed) != 0 || Stopping.IsCancellationRequested || UiCultureChanged is not { } handler)
        {
            return;
        }

        // Module code: always through the guard (SPEC 4.6).
        _guard.Invoke(ModuleId, "Language changed", () => handler(this, culture));
    }

    private void OnBatteriesChanged(object? sender, string sourceModuleId)
    {
        if (string.Equals(sourceModuleId, ModuleId, StringComparison.OrdinalIgnoreCase)
            || Volatile.Read(ref _disposed) != 0 || Stopping.IsCancellationRequested || DeviceBatteriesChanged is not { } handler)
        {
            return;
        }

        _guard.Invoke(ModuleId, "Device batteries changed", () => handler(this, EventArgs.Empty));
    }

    public void Cancel()
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            try
            {
                _cts.Cancel();
            }
            catch (AggregateException)
            {
                // Module callbacks must not escape while cancelling.
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _culture.Changed -= OnCultureChanged;
            if (_batteries is not null)
            {
                _batteries.Changed -= OnBatteriesChanged;
            }

            if (_host.Pins is { } pins)
            {
                pins.Changed -= OnPinsChanged;
            }

            try
            {
                _cts.Cancel();
            }
            catch (AggregateException)
            {
            }

            _cts.Dispose();
        }
    }
}
