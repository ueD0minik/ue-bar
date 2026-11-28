// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Core.Modules;
using UeBar.Sdk;

namespace UeBar.Core.Tests;

/// <summary>
/// Contract 1.8 (P3-1): content actions. Modules announce actions, other modules list and start them, the core runs
/// the provider through the guard – and a provider gets content only when the user started its action.
/// </summary>
public sealed class Contract18Tests
{
    [Fact]
    public void Contract_is_1_8()
    {
        Assert.Equal(new Version(1, 8), CoreApiVersion.Version);
        Assert.Equal("1.8", CoreApiVersion.Text);
    }

    [Fact]
    public void Content_knows_its_kinds()
    {
        Assert.True(ContentItem.Empty.IsEmpty);
        Assert.Equal(ContentKinds.Text, ContentItem.FromText("a").Kinds);
        Assert.Equal(ContentKinds.None, ContentItem.FromText("   ").Kinds);
        Assert.Equal(ContentKinds.Image | ContentKinds.Files, new ContentItem { ImagePng = [1], Files = ["C:\\a"] }.Kinds);
    }

    [Fact]
    public async Task Actions_are_listed_by_kind_from_running_modules_only()
    {
        await using var env = await Env.StartAsync();
        var texts = env.Service.GetActions(ContentKinds.Text);
        Assert.Equal(["sum", "ask"], texts.Select(a => a.Action.Id));
        Assert.All(texts, a => Assert.Equal("ai", a.ModuleId));
        Assert.Equal(["ask", "ocr"], env.Service.GetActions(ContentKinds.Image).Select(a => a.Action.Id));
        Assert.Empty(env.Service.GetActions(ContentKinds.None));

        await env.Registry.StopModuleAsync("ai");
        Assert.Empty(env.Service.GetActions(ContentKinds.All));
    }

    [Fact]
    public async Task Listing_never_hands_out_content_and_a_broken_list_is_skipped()
    {
        await using var env = await Env.StartAsync();
        env.Provider.BrokenList = true;
        Assert.Empty(env.Service.GetActions(ContentKinds.All));
        Assert.Empty(env.Provider.Requests); // nobody was asked anything
        Assert.Equal("ai", env.Registry.Find("ai")!.Id);
    }

    [Fact]
    public async Task Other_modules_see_the_actions_through_their_context_and_start_one()
    {
        await using var env = await Env.StartAsync();
        var context = (IContentActions)env.Caller.Context!;
        Assert.Equal(3, context.GetContentActions(ContentKinds.All).Count);

        Assert.True(await context.RunContentActionAsync("ai", "sum", ContentItem.FromText("Hallo Welt"), new ContentActionStart { Origin = ContentOrigin.Clipboard }, default));
        var session = Assert.Single(env.Ui.Shown);
        Assert.True(await Wait.UntilAsync(() => !session.IsRunning && session.HasStarted));
        var request = Assert.Single(env.Provider.Requests);
        Assert.Equal(("sum", "Hallo Welt", ContentOrigin.Clipboard), (request.ActionId, request.Content.Text, request.Origin));
        Assert.Equal("Kurz: Hallo Welt", session.CurrentAnswer);
        Assert.Null(session.Error);
    }

    [Fact]
    public async Task Content_of_the_wrong_kind_or_an_unknown_action_does_not_start()
    {
        await using var env = await Env.StartAsync();
        var context = (IContentActions)env.Caller.Context!;
        Assert.False(await context.RunContentActionAsync("ai", "sum", ContentItem.FromImage([1, 2]), null, default));
        Assert.False(await context.RunContentActionAsync("ai", "nope", ContentItem.FromText("x"), null, default));
        Assert.False(await context.RunContentActionAsync("other", "sum", ContentItem.FromText("x"), null, default));

        // Only the module's own chat may start without content.
        Assert.False(await context.RunContentActionAsync("ai", "ask", ContentItem.Empty, new ContentActionStart { Origin = ContentOrigin.Chat, Question = "Hi" }, default));
        Assert.True(await ((IContentActions)env.Provider.Context!).RunContentActionAsync("ai", "ask", ContentItem.Empty, new ContentActionStart { Origin = ContentOrigin.Chat, Question = "Hi" }, default));
        Assert.DoesNotContain(env.Ui.Shown, s => s.Action.Action.Id == "sum");
    }

    [Fact]
    public async Task Choosing_shows_only_fitting_actions_and_nothing_reaches_a_provider_without_a_choice()
    {
        await using var env = await Env.StartAsync();
        var context = (IContentActions)env.Caller.Context!;

        env.Ui.Choice = _ => null; // closed without choosing
        Assert.False(await context.ChooseContentActionAsync(ContentItem.FromText("geheim"), null, default));
        Assert.Equal(["sum", "ask"], env.Ui.Offered.Select(a => a.Action.Id));
        Assert.Empty(env.Provider.Requests);

        env.Ui.Choice = list => list[0];
        Assert.True(await context.ChooseContentActionAsync(ContentItem.FromText("geheim"), new ContentActionStart { Origin = ContentOrigin.Selection }, default));
        Assert.True(await Wait.UntilAsync(() => env.Provider.Requests.Count == 1));
        Assert.Equal("sum", env.Provider.Requests[0].ActionId);
    }

    [Fact]
    public async Task An_action_with_a_question_waits_for_it_and_follow_ups_carry_the_history()
    {
        await using var env = await Env.StartAsync();
        Assert.True(env.Service.Run("other", "ai", "ask", ContentItem.FromText("Folie"), null));
        var session = env.Ui.Shown[0];
        Assert.False(session.HasStarted);
        Assert.Empty(env.Provider.Requests);

        await session.SendAsync("Was steht da?");
        await session.SendAsync("Und genauer?");
        Assert.Equal(2, env.Provider.Requests.Count);
        var second = env.Provider.Requests[1];
        Assert.Equal("Und genauer?", second.Question);
        var turn = Assert.Single(second.History);
        Assert.Equal(("Was steht da?", "Antwort 1"), (turn.Question, turn.Answer));
        Assert.Equal("Folie", second.Content.Text);
    }

    [Fact]
    public async Task Another_option_repeats_the_last_turn_and_the_result_text_is_used_for_copying()
    {
        await using var env = await Env.StartAsync();
        env.Service.Run("other", "ai", "sum", ContentItem.FromText("abc"), null);
        var session = env.Ui.Shown[0];
        Assert.True(await Wait.UntilAsync(() => session.HasStarted && !session.IsRunning));
        Assert.Equal(["small", "big"], session.Options.Select(o => o.Id));
        Assert.Equal("abc", session.ResultText);

        await session.SelectOptionAsync("big");
        Assert.Equal(2, env.Provider.Requests.Count);
        Assert.Equal("big", env.Provider.Requests[1].OptionId);
        Assert.Empty(env.Provider.Requests[1].History); // repeated, not a follow-up
        Assert.Equal("Kurz: abc (big)", session.CurrentAnswer);
    }

    [Fact]
    public async Task Errors_show_in_the_window_without_faulting_the_module_and_late_writes_are_ignored()
    {
        await using var env = await Env.StartAsync();
        env.Provider.Throw = "Limit erreicht";
        env.Service.Run("other", "ai", "sum", ContentItem.FromText("abc"), null);
        var session = env.Ui.Shown[0];
        Assert.True(await Wait.UntilAsync(() => session.Error is not null));
        Assert.Equal("Limit erreicht", session.Error);
        Assert.Equal(ModuleState.Connected, env.Registry.Find("ai")!.Status.State);

        env.Provider.LastWriter!.Append("zu spät");
        Assert.DoesNotContain("zu spät", session.CurrentAnswer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task New_chat_and_closing_forget_everything_and_the_log_has_no_content()
    {
        await using var env = await Env.StartAsync();
        env.Service.Run("other", "ai", "sum", ContentItem.FromText("Streng geheimer Text"), null);
        var session = env.Ui.Shown[0];
        Assert.True(await Wait.UntilAsync(() => session.HasStarted && !session.IsRunning));
        await session.SendAsync("Nachfrage Geheim");

        session.Clear();
        Assert.Empty(session.Turns);
        Assert.Equal("", session.CurrentAnswer);
        Assert.True(session.Content.IsEmpty);

        session.Dispose();
        Assert.True(session.IsDisposed);
        Assert.All(env.Log.Entries, e => Assert.DoesNotContain("geheim", e.Message, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(env.Log.Entries, e => e.Message.Contains("text 20 chars", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_running_turn_can_be_cancelled_and_keeps_what_came()
    {
        await using var env = await Env.StartAsync();
        env.Provider.Hold = true;
        env.Service.Run("other", "ai", "sum", ContentItem.FromText("abc"), null);
        var session = env.Ui.Shown[0];
        Assert.True(await Wait.UntilAsync(() => session.CurrentAnswer.Length > 0));
        session.Cancel();
        Assert.True(await Wait.UntilAsync(() => !session.IsRunning));
        Assert.Null(session.Error);
        Assert.Equal("Kurz: ", session.CurrentAnswer);
    }

    [Fact]
    public async Task Idle_timeout_comes_from_the_provider()
    {
        await using var env = await Env.StartAsync();
        env.Service.Run("other", "ai", "sum", ContentItem.FromText("abc"), null);
        Assert.Equal(TimeSpan.FromMinutes(7), env.Ui.Shown[0].IdleTimeout);
    }

    // ------------------------------------------------------------------ support

    private sealed class Env : IAsyncDisposable
    {
        private readonly TestEnv _env = new();

        public ModuleRegistry Registry => _env.Registry;

        public ListLogger Log => _env.Log;

        public ContentActionService Service { get; private set; } = null!;

        public FakeUi Ui { get; } = new();

        public ProviderModule Provider { get; } = new("ai");

        public TestModule Caller { get; } = new("other");

        public static async Task<Env> StartAsync()
        {
            var env = new Env();
            env.Service = new ContentActionService(env.Registry, env.Log) { Ui = env.Ui };
            env.Registry.Host.ContentActions = env.Service;
            env.Registry.Register(env.Provider);
            env.Registry.Register(env.Caller);
            await env.Registry.StartAllAsync();
            return env;
        }

        public ValueTask DisposeAsync() => _env.DisposeAsync();
    }

    private sealed class FakeUi : IHostContentActionUi
    {
        public List<ContentActionSession> Shown { get; } = [];

        public List<ContentActionEntry> Offered { get; } = [];

        public Func<IReadOnlyList<ContentActionEntry>, ContentActionEntry?> Choice { get; set; } = _ => null;

        public Task<ContentActionEntry?> ChooseAsync(IReadOnlyList<ContentActionEntry> actions, ContentItem content, ContentActionStart start, CancellationToken cancellationToken)
        {
            Offered.Clear();
            Offered.AddRange(actions);
            return Task.FromResult(Choice(actions));
        }

        public void Show(ContentActionSession session) => Shown.Add(session);
    }

    internal sealed class ProviderModule(string id) : IModule, IContentActionProvider
    {
        public string Id => id;

        public string DisplayName => "AI";

        public string Icon => "U+E8BD";

        public ModuleStatus Status => ModuleStatus.Connected;

        public event EventHandler<ModuleStatus>? StatusChanged { add { } remove { } }

        public IReadOnlyList<ITileType> TileTypes => [];

        public IModuleContext? Context { get; private set; }

        public bool BrokenList { get; set; }

        public string? Throw { get; set; }

        public bool Hold { get; set; }

        public List<ContentActionRequest> Requests { get; } = [];

        public IContentResponse? LastWriter { get; private set; }

        public TimeSpan ConversationIdleTimeout => TimeSpan.FromMinutes(7);

        public IReadOnlyList<ContentActionInfo> ContentActions => BrokenList
            ? throw new InvalidOperationException("broken")
            :
            [
                new("sum", "Zusammenfassen", ContentKinds.Text),
                new("ask", "Fragen …", ContentKinds.Text | ContentKinds.Image) { AsksQuestion = true },
                new("ocr", "Text aus Bild", ContentKinds.Image),
            ];

        public Task StartAsync(IModuleContext context, CancellationToken cancellationToken)
        {
            Context = context;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task RunContentActionAsync(ContentActionRequest request, IContentResponse response, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(request);
            }

            LastWriter = response;
            if (Throw is { } message)
            {
                throw new InvalidOperationException(message);
            }

            response.SetOptions([new ResponseOption("small", "Klein"), new ResponseOption("big", "Groß")], request.OptionId ?? "small");
            response.SetStatus("Denkt nach …");
            if (request.ActionId == "ask")
            {
                response.Append($"Antwort {request.History.Count + 1}");
                return;
            }

            response.Append("Kurz: ");
            if (Hold)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            response.Append(request.Content.Text ?? "");
            if (request.OptionId is { } option && option != "small")
            {
                response.Append($" ({option})");
            }

            response.SetResultText(request.Content.Text);
        }
    }
}
