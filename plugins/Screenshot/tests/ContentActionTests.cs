// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Core.Modules;
using UeBar.Sdk;

namespace UeBar.Modules.Screenshot.Tests;

/// <summary>P3-1: "Actions …" (Ctrl+K) on the bar after a screenshot offers the content actions of other plugins for the image.</summary>
public sealed class ContentActionTests
{
    [Fact]
    public async Task Without_image_actions_of_other_plugins_the_bar_is_as_before()
    {
        await using var env = await new ShotEnv().StartAsync();
        await env.Module.CaptureAsync(null, null, default);
        Assert.DoesNotContain(env.Picker.Requests[0].Actions, a => a.Id == ScreenshotModule.ContentActionsId);
    }

    [Fact]
    public async Task Actions_hand_the_picked_image_to_the_list_and_nothing_else_happens()
    {
        await using var env = new ShotEnv();
        var ui = new ChooserUi();
        env.Registry.Host.ContentActions = new ContentActionService(env.Registry, env.Log) { Ui = ui };
        var provider = new Provider();
        env.Registry.Register(provider);
        await env.StartAsync();
        Assert.True(await env.Registry.StartModuleAsync(provider.Id));
        env.Picker.Choose = ScreenshotModule.ContentActionsId;

        var done = await env.Module.CaptureAsync(null, null, default);

        Assert.Contains(env.Picker.Requests[0].Actions, a => (a.Id, a.Shortcut) == (ScreenshotModule.ContentActionsId, "Ctrl+K"));
        var (content, start) = Assert.Single(ui.Calls);
        Assert.Equal(Png.Tiny, content.ImagePng);
        Assert.Equal(ContentOrigin.Screenshot, start.Origin);
        Assert.Null(done); // closed without choosing an action
        Assert.Empty(env.Pins.Pins); // not pinned on the side
        Assert.Empty(provider.Runs);
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

        public IReadOnlyList<ContentActionInfo> ContentActions => [new("ask", "Fragen …", ContentKinds.Image) { AsksQuestion = true }];

        public Task StartAsync(IModuleContext context, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RunContentActionAsync(ContentActionRequest request, IContentResponse response, CancellationToken cancellationToken)
        {
            Runs.Add(request);
            return Task.CompletedTask;
        }
    }
}
