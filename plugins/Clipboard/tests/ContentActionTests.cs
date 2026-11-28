// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Core.Modules;
using UeBar.Sdk;

namespace UeBar.Modules.Clipboard.Tests;

/// <summary>P3-1: Ctrl+K in the history offers the content actions of other plugins for the selected entry.</summary>
public sealed class ContentActionTests
{
    [Fact]
    public async Task Without_actions_of_other_plugins_there_is_no_ctrl_k()
    {
        await using var env = await new ClipEnv().StartAsync();
        await env.CopyAsync(env.Text("Hallo"), 1);
        await env.Module.ChooseAsync(default);
        Assert.DoesNotContain(env.Popup.Requests[0].Actions, a => a.Id == ClipboardModule.ContentActionsId);
    }

    [Fact]
    public async Task Ctrl_k_hands_only_the_chosen_entry_to_the_list_of_actions()
    {
        await using var env = new ClipEnv();
        var ui = new ChooserUi();
        env.Registry.Host.ContentActions = new ContentActionService(env.Registry, env.Log) { Ui = ui };
        var provider = new Provider();
        env.Registry.Register(provider);
        await env.StartAsync();
        Assert.True(await env.Registry.StartModuleAsync(provider.Id));
        await env.CopyAsync(env.Text("Erster"), 1);
        env.Time.Advance(TimeSpan.FromMinutes(1));
        await env.CopyAsync(env.Text("Zweiter"), 2);

        env.Popup.Answer = (1, ClipboardModule.ContentActionsId);
        await env.Module.ChooseAsync(default);

        Assert.Contains(env.Popup.Requests[0].Actions, a => (a.Id, a.Shortcut) == (ClipboardModule.ContentActionsId, "Ctrl+K"));
        var (content, start) = Assert.Single(ui.Calls);
        Assert.Equal("Erster", content.Text);
        Assert.Equal(ContentOrigin.Clipboard, start.Origin);
        Assert.Equal(env.Popup.Target, start.PasteTarget);
        Assert.Empty(provider.Runs); // nothing chosen in the list → nothing reached the plugin
        Assert.Empty(env.Paste.Pastes);
    }

    private sealed class ChooserUi : IHostContentActionUi
    {
        public List<(ContentItem Content, ContentActionStart Start)> Calls { get; } = [];

        public Task<ContentActionEntry?> ChooseAsync(IReadOnlyList<ContentActionEntry> actions, ContentItem content, ContentActionStart start, CancellationToken cancellationToken)
        {
            Calls.Add((content, start));
            return Task.FromResult<ContentActionEntry?>(null);
        }

        public void Show(ContentActionSession session)
        {
        }
    }

    private sealed class Provider : IModule, IContentActionProvider
    {
        public List<ContentActionRequest> Runs { get; } = [];

        public string Id => "ai";

        public string DisplayName => "AI";

        public string Icon => "Puzzle";

        public ModuleStatus Status => ModuleStatus.Connected;

        public IReadOnlyList<ITileType> TileTypes => [];

        public event EventHandler<ModuleStatus>? StatusChanged { add { } remove { } }

        public IReadOnlyList<ContentActionInfo> ContentActions => [new("sum", "Zusammenfassen", ContentKinds.Text)];

        public Task StartAsync(IModuleContext context, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RunContentActionAsync(ContentActionRequest request, IContentResponse response, CancellationToken cancellationToken)
        {
            Runs.Add(request);
            return Task.CompletedTask;
        }
    }
}
