// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Sdk;

namespace UeBar.Core.Modules;

/// <summary>
/// Contract 1.7 (P2-6) on the module's context: clipboard watching and writing, pasting, the choice popup and
/// hotkeys taken over from Windows. Every call back into the module goes through the guard (SPEC 4.6); watches and
/// hotkeys end when the module stops – a stopped module never hears about the clipboard again.
/// </summary>
internal sealed partial class ModuleContext : IClipboardWatcher, IClipboardWriter, IPasteService, IChoicePopup, IHotkeyTakeoverService
{
    /// <summary>Time a module may take for an action of the open choice popup.</summary>
    internal static readonly TimeSpan ChoiceActionTimeout = TimeSpan.FromSeconds(5);

    private readonly Lock _contract17Lock = new();
    private readonly List<IDisposable> _held = [];

    // ------------------------------------------------------------------ clipboard

    public IDisposable WatchClipboard(ClipboardWatchOptions options, Action<ClipboardChange> changed)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(changed);
        if (_host.ClipboardWatcher is not { } watcher || Stopping.IsCancellationRequested)
        {
            return Nothing.Instance;
        }

        var watch = watcher.Watch(ModuleId, options, change =>
        {
            if (Volatile.Read(ref _disposed) != 0 || Stopping.IsCancellationRequested)
            {
                return;
            }

            // Never on the caller's (UI) thread: storing an image may take a moment.
            _ = Task.Run(() =>
            {
                if (!Stopping.IsCancellationRequested)
                {
                    _guard.Invoke(ModuleId, "Clipboard changed", () => changed(change));
                }
            });
        });
        Log.Info("Clipboard watch started");
        return Hold(watch);
    }

    public Task<bool> SetContentAsync(ClipboardContent content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.IsEmpty || Stopping.IsCancellationRequested)
        {
            return Task.FromResult(false);
        }

        if (_host.ClipboardWriter is { } writer)
        {
            return writer.SetContentAsync(ModuleId, content, cancellationToken);
        }

        // Without the 1.7 writer: the parts the 1.5 clipboard knows.
        return _host.Clipboard is not { } clipboard ? Task.FromResult(false)
            : content.ImagePng is { Length: > 0 } png ? clipboard.SetImageAsync(png, cancellationToken)
            : !string.IsNullOrEmpty(content.Text) ? clipboard.SetTextAsync(content.Text, cancellationToken)
            : Task.FromResult(false);
    }

    // ------------------------------------------------------------------ paste

    public PasteTarget? CurrentTarget() => _host.Paste?.CurrentTarget();

    public async Task<bool> PasteAsync(ClipboardContent content, PasteTarget? target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (_host.Paste is not { } paste || content.IsEmpty || Stopping.IsCancellationRequested)
        {
            return false;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Stopping);
        return await paste.PasteAsync(ModuleId, content, target, linked.Token).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ choice popup

    public async Task<ChoicePopupResult?> ShowChoicesAsync(ChoicePopupRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_host.ChoicePopup is not { } popup || Stopping.IsCancellationRequested)
        {
            return null;
        }

        var guarded = request;
        if (request.OnAction is { } onAction)
        {
            guarded = request with
            {
                OnAction = async (choice, ct) =>
                {
                    var result = await _guard.RunAsync(ModuleId, $"Choice action {choice.ActionId}", c => onAction(choice, c), ChoiceActionTimeout, cancellationToken: ct).ConfigureAwait(false);
                    return result.Succeeded ? result.Value : null;
                },
            };
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Stopping);
        return await popup.ShowAsync(ModuleId, guarded, linked.Token).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ hotkeys taken over from Windows

    public IHotkeyTakeover? TakeOverHotkey(string hotkey, Action pressed)
    {
        ArgumentNullException.ThrowIfNull(pressed);
        if (_host.HotkeyTakeover is not { } host || Stopping.IsCancellationRequested || string.IsNullOrWhiteSpace(hotkey))
        {
            return null;
        }

        var inner = host.TakeOver(ModuleId, hotkey, () =>
        {
            if (!Stopping.IsCancellationRequested)
            {
                _ = Task.Run(() => _guard.Invoke(ModuleId, $"Hotkey {hotkey}", pressed));
            }
        });
        if (inner is null)
        {
            return null;
        }

        var takeover = new GuardedTakeover(inner, this);
        Hold(takeover);
        Log.Info($"Hotkey {hotkey} taken over: {inner.State}");
        return takeover;
    }

    // ------------------------------------------------------------------ bookkeeping

    private IDisposable Hold(IDisposable item)
    {
        var release = new Release(this, item);
        lock (_contract17Lock)
        {
            _held.Add(release);
        }

        if (Stopping.IsCancellationRequested)
        {
            release.Dispose();
        }

        return release;
    }

    private void Forget(IDisposable item)
    {
        lock (_contract17Lock)
        {
            _held.Remove(item);
        }
    }

    private void ReleaseContract17()
    {
        List<IDisposable> held;
        lock (_contract17Lock)
        {
            held = [.. _held];
            _held.Clear();
        }

        foreach (var item in held)
        {
            try
            {
                item.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn($"Release failed: {ex.Message}");
            }
        }
    }

    private sealed class Release(ModuleContext owner, IDisposable inner) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                owner.Forget(this);
                inner.Dispose();
            }
        }
    }

    private sealed class Nothing : IDisposable
    {
        public static Nothing Instance { get; } = new();

        public void Dispose()
        {
        }
    }

    /// <summary>The host's takeover with state changes going to the module through the guard.</summary>
    private sealed class GuardedTakeover : IHotkeyTakeover
    {
        private readonly IHotkeyTakeover _inner;
        private readonly ModuleContext _owner;
        private int _disposed;

        public GuardedTakeover(IHotkeyTakeover inner, ModuleContext owner)
        {
            _inner = inner;
            _owner = owner;
            _inner.StateChanged += OnStateChanged;
        }

        public HotkeyTakeoverState State => _inner.State;

        public string? Problem => _inner.Problem;

        public event EventHandler? StateChanged;

        private void OnStateChanged(object? sender, EventArgs e)
        {
            if (Volatile.Read(ref _disposed) != 0 || _owner.Stopping.IsCancellationRequested || StateChanged is not { } handler)
            {
                return;
            }

            _ = Task.Run(() => _owner._guard.Invoke(_owner.ModuleId, "Hotkey state changed", () => handler(this, EventArgs.Empty)));
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _inner.StateChanged -= OnStateChanged;
                _inner.Dispose();
            }
        }
    }
}
