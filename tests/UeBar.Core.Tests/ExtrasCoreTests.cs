// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Sdk;
using UeBar.Core.Layout;
using UeBar.Core.Modules;
using UeBar.Core.Settings;

namespace UeBar.Core.Tests;

/// <summary>M6 in the core: quick commands, Now Playing choice, status line, secrets for modules.</summary>
public class ExtrasCoreTests
{
    private static readonly (string, QuickCommandInfo)[] Commands =
    [
        ("ha", new QuickCommandInfo("+", "Einkaufsliste")),
        ("x", new QuickCommandInfo("++", "Doppelt")),
    ];

    [Theory]
    [InlineData("+ Milch", "+", "Milch")]
    [InlineData("+Milch", "+", "Milch")]
    [InlineData("  +   zwei Wörter  ", "+", "zwei Wörter")]
    [InlineData("++ Brot", "++", "Brot")]
    public void Quick_command_parser_finds_prefix_and_argument(string text, string prefix, string argument)
    {
        var match = QuickCommandService.Parse(text, Commands);

        Assert.NotNull(match);
        Assert.Equal(prefix, match.Command.Prefix);
        Assert.Equal(argument, match.Argument);
    }

    [Theory]
    [InlineData("+")]
    [InlineData("+   ")]
    [InlineData("desk")]
    [InlineData("")]
    [InlineData(null)]
    public void Quick_command_parser_ignores_everything_else(string? text) => Assert.Null(QuickCommandService.Parse(text, Commands));

    [Fact]
    public void Now_playing_across_modules_prefers_playing_then_most_recent()
    {
        var now = DateTimeOffset.Now;
        var paused = new NowPlayingEntry("a", new NowPlayingInfo("a1", "Pausiert neu", IsPlaying: false) { LastActive = now });
        var playingOld = new NowPlayingEntry("b", new NowPlayingInfo("b1", "Spielt", IsPlaying: true) { LastActive = now.AddDays(-1) });
        var pausedOld = new NowPlayingEntry("c", new NowPlayingInfo("c1", "Pausiert alt", IsPlaying: false) { LastActive = now.AddDays(-2) });

        Assert.Equal("b", NowPlayingService.Choose([paused, playingOld, pausedOld])!.ModuleId);
        Assert.Equal("a", NowPlayingService.Choose([pausedOld, paused])!.ModuleId);
        Assert.Null(NowPlayingService.Choose([]));
    }

    [Fact]
    public void Status_line_add_remove_move_with_limit_and_undo()
    {
        var editor = new LayoutEditor(new LayoutDocument(), new DeviceLayout());
        for (var i = 0; i < 6; i++)
        {
            editor.AddStatus("ha", $"sensor.s{i}");
        }

        Assert.Equal(LayoutEditor.MaxStatusItems, editor.StatusItems.Count); // at most 5
        Assert.False(editor.AddStatus("ha", "sensor.s0")); // no duplicates (even with space left)

        Assert.True(editor.MoveStatus(0, 2));
        Assert.Equal(["sensor.s1", "sensor.s2", "sensor.s0", "sensor.s3", "sensor.s4"], editor.StatusItems.Select(s => s.Entity));
        Assert.True(editor.RemoveStatus(4));
        Assert.Equal(4, editor.StatusItems.Count);
        Assert.False(editor.MoveStatus(0, 9));

        editor.Undo();
        Assert.Equal(5, editor.StatusItems.Count);
        Assert.Equal(["sensor.s1", "sensor.s2", "sensor.s0", "sensor.s3", "sensor.s4"], editor.Result.Base.Status.Select(s => s.Entity));
    }

    private sealed class SecretModule(string id) : IModule
    {
        public string Id { get; } = id;

        public string DisplayName => Id;

        public string Icon => "Puzzle";

        public ModuleStatus Status => ModuleStatus.Connected;

        public IReadOnlyList<ITileType> TileTypes => [];

        public IModuleSecrets? Secrets { get; private set; }

        public event EventHandler<ModuleStatus>? StatusChanged
        {
            add { }
            remove { }
        }

        public Task StartAsync(IModuleContext context, CancellationToken cancellationToken)
        {
            Secrets = context as IModuleSecrets;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public async Task Modules_can_store_secrets_through_their_context_and_they_are_encrypted_on_disk()
    {
        using var dir = new TempDirectory();
        var log = new ListLogger();
        var settings = new SettingsService(Path.Combine(dir.Path, "settings.json"), new ReversingProtector(), log);
        var guard = new ModuleGuard(log);
        var registry = new ModuleRegistry(guard, new Bus.ModuleBus(guard, settings.BusPolicy, log), log, settings, dir.Path);
        var module = new SecretModule("m");
        registry.Register(module);
        await registry.StartAllAsync();

        module.Secrets!.SetSecret("webhook", "geheim-123");

        Assert.Equal("geheim-123", module.Secrets.GetSecret("webhook"));
        var file = await File.ReadAllTextAsync(Path.Combine(dir.Path, "settings.json"));
        Assert.DoesNotContain("geheim-123", file); // encrypted
        Assert.Contains("321-mieheg", file);
        module.Secrets.SetSecret("webhook", null);
        Assert.Null(module.Secrets.GetSecret("webhook"));
        await registry.DisposeAsync();
    }

    private sealed class PlainAssistModule : IModule, IAssistProvider
    {
        public string Id => "plain";

        public string DisplayName => "Einfach";

        public string Icon => "Puzzle";

        public ModuleStatus Status => ModuleStatus.Connected;

        public IReadOnlyList<ITileType> TileTypes => [];

        public string AssistId => Id;

        public string AssistDisplayName => DisplayName;

        public int Questions { get; private set; }

        public event EventHandler<ModuleStatus>? StatusChanged
        {
            add { }
            remove { }
        }

        public Task<AssistResponse> AskAsync(string text, CancellationToken cancellationToken)
        {
            Questions++;
            return Task.FromResult(new AssistResponse("ok"));
        }

        public Task StartAsync(IModuleContext context, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task Assist_without_conversation_support_never_expires_unless_a_timeout_is_set()
    {
        using var dir = new TempDirectory();
        var log = new ListLogger();
        var guard = new ModuleGuard(log);
        var registry = new ModuleRegistry(guard, new Bus.ModuleBus(guard, new Bus.GrantListPermissionPolicy(), log), log, new InMemoryModuleStateStore(), dir.Path);
        var module = new PlainAssistModule();
        registry.Register(module);
        await registry.StartAllAsync();
        var time = new ManualTime();
        var assist = new AssistService(registry, new QuickCommandService(registry, log), log, null, time);

        Assert.Null(assist.ConversationTimeout); // the module knows no timeout
        Assert.False(assist.IsConversationExpired()); // no message yet
        Assert.True((await assist.AskAsync("Hallo")).Success);
        Assert.Equal(1, module.Questions);
        Assert.Null(assist.ConversationId);
        time.Now += TimeSpan.FromDays(1);
        Assert.False(assist.IsConversationExpired());

        assist.ConversationTimeoutOverride = () => TimeSpan.FromMinutes(5);
        Assert.True(assist.IsConversationExpired());
        await registry.DisposeAsync();
    }

    /// <summary>Test encryption: reverses the text (visibly different from the plain text).</summary>
    private sealed class ReversingProtector : ISecretProtector
    {
        public string Protect(string plainText) => new([.. plainText.Reverse()]);

        public string Unprotect(string protectedText) => new([.. protectedText.Reverse()]);
    }
}
