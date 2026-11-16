// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Globalization;
using UeBar.Core.Abstractions;
using UeBar.Core.Localization;
using UeBar.Core.Logging;

namespace UeBar.Core.Modules;

/// <summary>Context of a running module. Each start gets a new context with its own token.</summary>
internal sealed class ModuleContext : IModuleContext, IModuleSecrets, IHostInfo, IUiLanguageInfo, IDisposable
{
    private readonly ModuleGuard _guard;
    private readonly string _dataRoot;
    private readonly Settings.IModuleSecretStore? _secrets;
    private readonly CancellationTokenSource _cts = new();
    private readonly UiCultureSource _culture;
    private int _disposed;

    private readonly Func<string>? _deviceName;

    public ModuleContext(string moduleId, IModuleBus bus, IModuleLogger log, ModuleGuard guard, string dataRoot, Settings.IModuleSecretStore? secrets = null, Func<string>? deviceName = null, UiCultureSource? culture = null)
    {
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

    public string? GetSecret(string key) => _secrets?.GetModuleSecret(ModuleId, key);

    public void SetSecret(string key, string? value) => _secrets?.SetModuleSecret(ModuleId, key, value);

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
