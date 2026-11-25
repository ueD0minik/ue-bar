// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Core.Modules;
using UeBar.Sdk;

namespace UeBar.Core.Tests;

/// <summary>
/// Contract 1.7 (P2-6) on the module context: clipboard watching and writing, pasting, the choice popup and hotkeys
/// taken over from Windows – always through the guard, everything ends with the module.
/// </summary>
public sealed class Contract17Tests
{
    [Fact]
    public void Contract_is_1_7()
    {
        Assert.Equal(new Version(1, 7), CoreApiVersion.Version);
        Assert.Equal("1.7", CoreApiVersion.Text);
    }

    [Fact]
    public async Task Watching_goes_through_the_guard_and_ends_when_the_module_stops()
    {
        await using var env = new TestEnv();
        var watcher = new HostWatcher();
        env.Registry.Host.ClipboardWatcher = watcher;
        var module = new TestModule("clip");
        var seen = new List<ClipboardChange>();
        var throwNext = false;
        module.OnStart = (context, _) =>
        {
            ((IClipboardWatcher)context).WatchClipboard(new ClipboardWatchOptions { Images = false }, change =>
            {
                if (throwNext)
                {
                    throw new InvalidOperationException("broken module");
                }

                lock (seen)
                {
                    seen.Add(change);
                }
            });
            return Task.CompletedTask;
        };
        env.Registry.Register(module);
        await env.Registry.StartAllAsync();

        Assert.Equal(("clip", false), (watcher.Module, watcher.Options!.Images));
        watcher.Raise(new ClipboardChange(new ClipboardContent { Text = "a" }, DateTimeOffset.Now));
        Assert.True(await Wait.UntilAsync(() => seen.Count == 1));

        // A module that throws gets a fault – the watcher (app) is not affected.
        throwNext = true;
        watcher.Raise(new ClipboardChange(new ClipboardContent { Text = "b" }, DateTimeOffset.Now));
        Assert.True(await Wait.UntilAsync(() => env.Log.Entries.Any(l => l.Message.Contains("Clipboard changed", StringComparison.Ordinal))));

        await env.Registry.StopModuleAsync("clip");
        Assert.True(watcher.Disposed);
    }

    [Fact]
    public async Task Text_and_images_go_through_the_writer_so_the_watcher_knows_the_module()
    {
        await using var env = new TestEnv();
        var writer = new HostWriter();
        env.Registry.Host.ClipboardWriter = writer;
        var module = new TestModule("shot");
        env.Registry.Register(module);
        await env.Registry.StartAllAsync();

        Assert.True(await ((IClipboardAccess)module.Context!).SetTextAsync("x", default));
        Assert.True(await ((IClipboardAccess)module.Context!).SetImageAsync([1, 2], default));
        Assert.True(await ((IClipboardWriter)module.Context!).SetContentAsync(new ClipboardContent { Files = [@"C:\a.txt"] }, default));
        Assert.False(await ((IClipboardWriter)module.Context!).SetContentAsync(new ClipboardContent(), default)); // nothing to write
        Assert.Equal(["shot", "shot", "shot"], writer.Writes.Select(w => w.Module));
        Assert.Equal([@"C:\a.txt"], writer.Writes[2].Content.Files);
    }

    [Fact]
    public async Task Popup_actions_run_through_the_guard_with_a_time_limit()
    {
        await using var env = new TestEnv();
        var popup = new HostPopup();
        env.Registry.Host.ChoicePopup = popup;
        var module = new TestModule("clip");
        env.Registry.Register(module);
        await env.Registry.StartAllAsync();

        var request = new ChoicePopupRequest([new ChoiceItem("1", "eins")])
        {
            Actions = [new ChoiceAction("paste", "Einfügen"), new ChoiceAction("delete", "Löschen", "Delete") { KeepOpen = true }],
            OnAction = (_, _) => throw new InvalidOperationException("module bug"),
        };
        var result = await ((IChoicePopup)module.Context!).ShowChoicesAsync(request, default);

        Assert.Equal("1", result!.ItemId);
        Assert.Null(popup.ActionAnswer); // the exception stayed in the guard
        Assert.Contains(env.Log.Entries, l => l.Message.Contains("Choice action delete", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pasting_and_hotkeys_need_the_host_and_hotkeys_are_given_back_on_stop()
    {
        await using var env = new TestEnv();
        var module = new TestModule("clip");
        env.Registry.Register(module);
        await env.Registry.StartAllAsync();
        var context = module.Context!;
        Assert.False(await ((IPasteService)context).PasteAsync(new ClipboardContent { Text = "x" }, null, default)); // no host: false
        Assert.Null(((IHotkeyTakeoverService)context).TakeOverHotkey("Win+V", () => { }));
        Assert.Null(await ((IChoicePopup)context).ShowChoicesAsync(new ChoicePopupRequest([]), default));

        await env.Registry.StopModuleAsync("clip");
        var hook = new HostHook();
        env.Registry.Host.HotkeyTakeover = hook;
        env.Registry.Host.Paste = new HostPaste();
        await env.Registry.StartModuleAsync("clip");
        context = module.Context!;
        var pressed = 0;
        var takeover = ((IHotkeyTakeoverService)context).TakeOverHotkey("Win+V", () => Interlocked.Increment(ref pressed))!;
        Assert.Equal(HotkeyTakeoverState.Active, takeover.State);
        hook.Press();
        Assert.True(await Wait.UntilAsync(() => Volatile.Read(ref pressed) == 1));
        Assert.True(await ((IPasteService)context).PasteAsync(new ClipboardContent { Text = "x" }, new PasteTarget(1), default));

        await env.Registry.StopModuleAsync("clip");
        Assert.True(hook.Disposed);
    }

    private sealed class HostWatcher : IHostClipboardWatcher
    {
        private Action<ClipboardChange>? _changed;

        public string? Module { get; private set; }

        public ClipboardWatchOptions? Options { get; private set; }

        public bool Disposed { get; private set; }

        public IDisposable Watch(string moduleId, ClipboardWatchOptions options, Action<ClipboardChange> changed)
        {
            (Module, Options, _changed) = (moduleId, options, changed);
            return new Callback(() => Disposed = true);
        }

        public void Raise(ClipboardChange change) => _changed?.Invoke(change);
    }

    private sealed class HostWriter : IHostClipboardWriter
    {
        public List<(string Module, ClipboardContent Content)> Writes { get; } = [];

        public Task<bool> SetContentAsync(string moduleId, ClipboardContent content, CancellationToken cancellationToken)
        {
            Writes.Add((moduleId, content));
            return Task.FromResult(true);
        }
    }

    private sealed class HostPopup : IHostChoicePopup
    {
        public ChoicePopupUpdate? ActionAnswer { get; private set; } = new([]);

        public async Task<ChoicePopupResult?> ShowAsync(string moduleId, ChoicePopupRequest request, CancellationToken cancellationToken)
        {
            ActionAnswer = await request.OnAction!(new ChoicePopupResult("1", "delete"), cancellationToken);
            return new ChoicePopupResult("1", "paste");
        }
    }

    private sealed class HostPaste : IHostPaste
    {
        public PasteTarget? CurrentTarget() => null;

        public Task<bool> PasteAsync(string moduleId, ClipboardContent content, PasteTarget? target, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class HostHook : IHostHotkeyTakeover, IHotkeyTakeover
    {
        private Action? _pressed;

        public bool Disposed { get; private set; }

        public HotkeyTakeoverState State => HotkeyTakeoverState.Active;

        public string? Problem => null;

        public event EventHandler? StateChanged { add { } remove { } }

        public IHotkeyTakeover? TakeOver(string moduleId, string hotkey, Action pressed)
        {
            _pressed = pressed;
            return this;
        }

        public void Press() => _pressed?.Invoke();

        public void Dispose() => Disposed = true;
    }

    private sealed class Callback(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
