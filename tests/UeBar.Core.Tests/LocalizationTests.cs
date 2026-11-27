// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using UeBar.Sdk;
using UeBar.Core.Layout;
using UeBar.Core.Localization;
using UeBar.Modules.Dummy;
using UeBar.Core.Modules;

namespace UeBar.Core.Tests;

/// <summary>Tests that switch the shared UI culture run alone (other tests check German texts).</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LanguageCollection
{
    public const string Name = "Language switching";
}

/// <summary>The source tree of <c>app/</c> (tests run in <c>tests/X/bin/…</c>).</summary>
internal static class SourceTree
{
    public static string AppRoot { get; } = FindAppRoot();

    public static string Src => Path.Combine(AppRoot, "src");

    /// <summary>Official plugins (P2-1-Fix 1): <c>plugins/&lt;Name&gt;/src</c>, tests in <c>plugins/&lt;Name&gt;/tests</c>.</summary>
    public static string Plugins => Path.Combine(AppRoot, "plugins");

    /// <summary>Source folder of every official plugin (one per <c>plugins/&lt;Name&gt;/src</c>).</summary>
    public static IReadOnlyList<string> PluginSources { get; } =
        [.. Directory.GetDirectories(Path.Combine(FindAppRoot(), "plugins")).Select(d => Path.Combine(d, "src")).Where(Directory.Exists).Order(StringComparer.Ordinal)];

    /// <summary>Project folder (relative to app/) per short name as in the code: Core, App, Bar, Ha, Dummy, Win.</summary>
    public static IReadOnlyDictionary<string, string> Projects { get; } = new Dictionary<string, string>
    {
        ["Core"] = Path.Combine("src", "UeBar.Core"),
        ["App"] = Path.Combine("src", "UeBar.App"),
        ["Bar"] = Path.Combine("src", "UeBar.Bar"),
        ["Ha"] = Path.Combine("plugins", "HomeAssistant", "src"),
        ["Dummy"] = Path.Combine("plugins", "Dummy", "src"),
        ["Win"] = Path.Combine("plugins", "Windows", "src"),
        ["Timer"] = Path.Combine("plugins", "Timer", "src"),
        ["Qa"] = Path.Combine("plugins", "QuickAccess", "src"),
        ["Shot"] = Path.Combine("plugins", "Screenshot", "src"),
    };

    public static Dictionary<string, string> ReadResx(string project, string? language)
    {
        var file = Path.Combine(AppRoot, project, "Resources", language is null ? "Strings.resx" : $"Strings.{language}.resx");
        return XDocument.Load(file).Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? "");
    }

    /// <summary>Short name of the project a source file belongs to (null: e.g. UeBar.Sdk).</summary>
    public static string? ProjectOf(string file) =>
        Projects.FirstOrDefault(p => file.StartsWith(Path.Combine(AppRoot, p.Value) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).Key;

    /// <summary>Product code: <c>src/</c> and the official plugins' <c>src/</c> (no tests, no build output).</summary>
    public static IEnumerable<string> SourceFiles(params string[] extensions) =>
        new[] { Src }.Concat(PluginSources)
            .SelectMany(root => Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
            .Where(f => extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string FindAppRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")) && Directory.Exists(Path.Combine(dir.FullName, "src")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("app/ folder with src/ not found");
    }
}

/// <summary>R1: every text exists in German and English, the code uses only existing keys.</summary>
public sealed class ResourceCompletenessTests
{
    private static readonly Regex Placeholder = new(@"\{(\d+)(?::[^}]*)?\}", RegexOptions.Compiled);

    public static TheoryData<string> Projects() => [.. SourceTree.Projects.Values];

    [Theory]
    [MemberData(nameof(Projects))]
    public void Every_key_exists_in_both_languages_with_the_same_placeholders(string project)
    {
        var english = SourceTree.ReadResx(project, null);
        var german = SourceTree.ReadResx(project, "de");

        Assert.NotEmpty(english);
        Assert.Empty(english.Keys.Except(german.Keys));
        Assert.Empty(german.Keys.Except(english.Keys));
        foreach (var (key, en) in english)
        {
            var de = german[key];
            Assert.False(string.IsNullOrWhiteSpace(en), $"{project}: {key} is empty (en)");
            Assert.False(string.IsNullOrWhiteSpace(de), $"{project}: {key} is empty (de)");
            Assert.True(
                Placeholders(en).SetEquals(Placeholders(de)),
                $"{project}: {key} has different placeholders: \"{en}\" / \"{de}\"");
        }
    }

    [Fact]
    public void Every_key_used_in_the_code_exists()
    {
        var keys = SourceTree.Projects.ToDictionary(p => p.Key, p => SourceTree.ReadResx(p.Value, null).Keys.ToHashSet());
        var catalogs = new Dictionary<string, string> { ["Loc"] = "App", ["CoreText"] = "Core", ["BarText"] = "Bar", ["HaText"] = "Ha", ["DummyText"] = "Dummy", ["WinText"] = "Win", ["TimerText"] = "Timer", ["QuickText"] = "Qa", ["ShotText"] = "Shot" };
        var call = new Regex(@"\b(Loc|CoreText|BarText|HaText|DummyText|WinText|TimerText|QuickText|ShotText)\.(?:Get\(|F\(|T\[|T\.Format\()""([A-Za-z0-9_]+)""");
        var xaml = new Regex(@"\{l:T ([A-Za-z0-9_]+)\}");
        var literalKey = new Regex(@"""([A-Z][a-z][A-Za-z]*_[A-Za-z0-9_]+)""");
        var missing = new List<string>();
        var used = 0;

        foreach (var file in SourceTree.SourceFiles(".cs", ".xaml"))
        {
            // Without comment lines (doc examples like {l:T Key}).
            var text = string.Join(Environment.NewLine, File.ReadAllLines(file).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
            var project = SourceTree.ProjectOf(file);
            if (project is null)
            {
                continue; // UeBar.Sdk: contracts only, no texts of its own
            }
            foreach (Match m in call.Matches(text))
            {
                used++;
                if (m.Groups[2].Value.EndsWith('_'))
                {
                    continue; // dynamic key ("ModuleState_" + state) – checked separately
                }

                if (!keys[catalogs[m.Groups[1].Value]].Contains(m.Groups[2].Value))
                {
                    missing.Add($"{Path.GetFileName(file)}: {m.Value}");
                }
            }

            foreach (Match m in xaml.Matches(text))
            {
                used++;
                if (!keys["App"].Contains(m.Groups[1].Value))
                {
                    missing.Add($"{Path.GetFileName(file)}: {m.Value}");
                }
            }

            // Keys passed as plain strings (tile type names, slot labels, device names …) belong to the project of the file.
            foreach (Match m in literalKey.Matches(call.Replace(text, "")))
            {
                if (keys.Values.Any(k => k.Contains(m.Groups[1].Value)) && !keys[project].Contains(m.Groups[1].Value))
                {
                    missing.Add($"{Path.GetFileName(file)}: {m.Value} (key of another project)");
                }
                else if (file.Contains("Modules", StringComparison.Ordinal) && !keys.Values.Any(k => k.Contains(m.Groups[1].Value)))
                {
                    missing.Add($"{Path.GetFileName(file)}: {m.Value}");
                }
            }
        }

        Assert.True(used > 300, $"only {used} key usages found – is the pattern still right?");
        Assert.True(missing.Count == 0, "Missing keys: " + string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void Module_states_have_texts()
    {
        var keys = SourceTree.ReadResx(SourceTree.Projects["Core"], null);
        foreach (var state in Enum.GetValues<ModuleState>())
        {
            Assert.Contains("ModuleState_" + state, keys.Keys);
        }
    }

    private static HashSet<string> Placeholders(string text) => [.. Placeholder.Matches(text).Select(m => m.Groups[1].Value)];
}

/// <summary>
/// R1: no UI texts hard-wired in the code. Looks for German letters/typical words in string
/// literals of <c>.cs</c> files (comments are English anyway) and for literal texts in XAML.
/// </summary>
public sealed class HardcodedTextTests
{
    /// <summary>Allowed German literals: (file name, literal) – with the reason.</summary>
    private static readonly HashSet<(string File, string Literal)> Allowed =
    [
        ("HaMobileApp.cs", "ß"), // slugify like Home Assistant (ß → ss)
        ("Loc.cs", "Deutsch"), // language names are always shown in their own language
        ("SettingsWindow.cs", "Deutsch"),
        // P2-5-Fix 2: German conjunctions after a hyphen at the line end ("Ein- und Ausgang") – data, not UI text
        ("OcrLayout.cs", "und"),
        ("OcrLayout.cs", "oder"),
        ("OcrLayout.cs", "bis"),
        ("OcrLayout.cs", "sowie"),
        ("OcrLayout.cs", "bzw."),
        // P2-7: words the quick command "privat an/aus" understands in every UI language – input, not UI text
        ("CapturePrivacyCommands.cs", "ein"),
        ("CapturePrivacyCommands.cs", "aus"),
    ];

    private static readonly Regex Literal = new(@"(?<![A-Za-z0-9_])\$?@?""((?:[^""\\]|\\.)*)""", RegexOptions.Compiled);

    private static readonly Regex German = new(
        @"[äöüÄÖÜß„“]|\b(und|oder|nicht|wird|werden|für|über|bitte|kein|keine|nur|mit|ist|sind|der|das|dem|den|ein|eine|einen|auf|aus|bei|nach|noch|schon|wenn|dann|auch|jetzt|Fehler|Einstellungen|Kachel|Kacheln|Gerät|Modul|Verbindung|Zurück|Weiter|Abbrechen|Speichern|Schließen|Benachrichtigung|Benachrichtigungen|Verlauf|Schnellsuche|Tastenkürzel|Statuszeile|Gestartet|Gestoppt|Verbunden|Getrennt)\b|^(An|Aus)\b",
        RegexOptions.Compiled);

    private static readonly Regex XamlText = new(@"\b(Text|Content|ToolTip|Header|Title|Tag)=""([^""{][^""]*)""", RegexOptions.Compiled);

    private static readonly Regex XamlToolTipSetter = new(@"Property=""ToolTip""\s+Value=""([^""{][^""]*)""", RegexOptions.Compiled);

    [Fact]
    public void No_german_texts_in_code_outside_the_resources()
    {
        var found = new List<string>();
        foreach (var file in SourceTree.SourceFiles(".cs"))
        {
            var name = Path.GetFileName(file);
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (Match m in Literal.Matches(line))
                {
                    var literal = m.Groups[1].Value;
                    if (German.IsMatch(literal) && !Allowed.Contains((name, literal)))
                    {
                        found.Add($"{name}:{i + 1}: \"{literal}\"");
                    }
                }
            }
        }

        Assert.True(found.Count == 0, "German literals: " + string.Join(Environment.NewLine, found));
    }

    [Fact]
    public void No_literal_texts_in_xaml()
    {
        var found = new List<string>();
        foreach (var file in SourceTree.SourceFiles(".xaml"))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var texts = XamlText.Matches(lines[i]).Select(m => m.Groups[2].Value)
                    .Concat(XamlToolTipSetter.Matches(lines[i]).Select(m => m.Groups[1].Value));
                foreach (var text in texts)
                {
                    // Icon glyphs (&#xE70D;) are not text.
                    if (Regex.IsMatch(Regex.Replace(text, "&#x[0-9A-Fa-f]+;", ""), "[A-Za-zÄÖÜäöü]{2,}") && text != "ue Bar")
                    {
                        found.Add($"{Path.GetFileName(file)}:{i + 1}: \"{text}\"");
                    }
                }
            }
        }

        Assert.True(found.Count == 0, "Literal texts: " + string.Join(Environment.NewLine, found));
    }

    [Fact]
    public void Comments_and_log_messages_are_english()
    {
        // Spot check for leftovers: German letters or function words in comments.
        var germanComment = new Regex(@"[äöüÄÖÜß„“]|\b(und|nicht|oder|wird|werden|für|über|kein|keine|nur|mit|der|das|ist|sind|bei|nach|wenn|dann|auch|noch|schon)\b");
        var found = new List<string>();
        foreach (var file in SourceTree.SourceFiles(".cs", ".xaml"))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var index = line.IndexOf("//", StringComparison.Ordinal);
                var comment = index >= 0 && !line[..index].Contains('"') ? line[index..] : line.Contains("<!--", StringComparison.Ordinal) ? line : null;
                if (comment is not null && germanComment.IsMatch(comment))
                {
                    found.Add($"{Path.GetFileName(file)}:{i + 1}: {comment.Trim()}");
                }
            }
        }

        Assert.True(found.Count == 0, "German comments: " + string.Join(Environment.NewLine, found));
    }
}

/// <summary>R1: language switching at runtime, WPF-free.</summary>
[Collection(LanguageCollection.Name)]
public sealed class LanguageSwitchTests
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    [Theory]
    [InlineData("system", "de-AT", "de-DE")] // German Windows → German texts
    [InlineData("system", "en-GB", "en-US")]
    [InlineData("system", "fr-FR", "en-US")] // neither German nor English → English
    [InlineData("de", "en-US", "de-DE")]
    [InlineData("en", "de-AT", "en-US")]
    [InlineData("EN", "de-DE", "en-US")]
    [InlineData(null, "de-DE", "de-DE")]
    [InlineData("klingon", "de-DE", "de-DE")] // unknown = system
    public void Language_setting_resolves_to_german_or_english(string? setting, string uiCulture, string expected)
    {
        var culture = UiLanguages.Resolve(setting, CultureInfo.GetCultureInfo(uiCulture));
        Assert.Equal(expected, culture.Name);
    }

    [Fact]
    public void Numbers_and_dates_follow_the_windows_region_not_the_ui_language()
    {
        // Tests run with the region de-DE (TestCulture); the UI switches to English.
        try
        {
            UiCultureSource.Shared.Set(English);
            Assert.Equal("Error: boom – restart in 5 s", CoreText.T.Format("Module_ErrorRestartIn", "boom", 5.0));
            Assert.Equal("No answer after 30 seconds.", CoreText.T.Format("Assist_TimedOut", 30.4));
            Assert.Equal("21,5", string.Format(CultureInfo.CurrentCulture, "{0:0.0}", 21.5)); // region stays German
            Assert.Equal("14:05", new DateTime(2026, 11, 16, 14, 5, 0).ToString("t", CultureInfo.CurrentCulture)); // 24 h
        }
        finally
        {
            UiCultureSource.Shared.Set(German);
        }
    }

    [Fact]
    public void Normalize_keeps_only_known_values()
    {
        Assert.Equal("de", UiLanguages.Normalize(" DE "));
        Assert.Equal("en", UiLanguages.Normalize("en"));
        Assert.Equal("system", UiLanguages.Normalize(null));
        Assert.Equal("system", UiLanguages.Normalize("fr"));
    }

    [Fact]
    public void Culture_source_reports_only_real_changes()
    {
        var source = new UiCultureSource(German);
        var changes = new List<string>();
        source.Changed += (_, c) => changes.Add(c.Name);

        Assert.False(source.Set(German));
        Assert.True(source.Set(English));
        Assert.True(source.Set(German));

        Assert.Equal(["en-US", "de-DE"], changes);
    }

    [Fact]
    public void Core_texts_follow_the_shared_culture()
    {
        try
        {
            UiCultureSource.Shared.Set(English);
            Assert.Equal("Connected", ModuleStatus.Connected.DisplayText);
            Assert.Equal("Module disabled", TileModel.CreatePlaceholder(new TileConfig { Id = "t", Module = "x", Type = "x.y" }, TilePlaceholder.ModuleDisabled).StateText);
            Assert.Equal("New tab", CoreText.T["Layout_NewTab"]);

            UiCultureSource.Shared.Set(German);
            Assert.Equal("Verbunden", ModuleStatus.Connected.DisplayText);
            Assert.Equal("Neuer Tab", CoreText.T["Layout_NewTab"]);
            Assert.Equal("Keine Antwort nach 30 Sekunden.", CoreText.T.Format("Assist_TimedOut", 30.0));
        }
        finally
        {
            UiCultureSource.Shared.Set(German);
        }
    }

    [Fact]
    public async Task Modules_get_the_language_through_their_context_and_hear_switches()
    {
        await using var env = new TestEnv();
        var source = new UiCultureSource(German);
        env.Registry.UiCulture = source;
        var heard = new List<string>();
        CultureInfo? atStart = null;
        var module = new TestModule("m")
        {
            OnStart = (ctx, _) =>
            {
                var language = Assert.IsAssignableFrom<IUiLanguageInfo>(ctx);
                atStart = language.UiCulture;
                language.UiCultureChanged += (_, c) => heard.Add(c.Name);
                return Task.CompletedTask;
            },
        };
        env.Registry.Register(module);
        await env.Registry.StartAllAsync();

        source.Set(English);
        source.Set(German);

        Assert.Equal(German, atStart);
        Assert.Equal(["en-US", "de-DE"], heard);

        // After stopping the context is gone: no more calls into the module.
        await env.Registry.StopModuleAsync("m");
        source.Set(English);
        Assert.Equal(2, heard.Count);
    }

    [Fact]
    public async Task A_failing_language_handler_does_not_take_the_app_down()
    {
        await using var env = new TestEnv();
        var source = new UiCultureSource(German);
        env.Registry.UiCulture = source;
        env.Registry.Register(new TestModule("bad")
        {
            OnStart = (ctx, _) =>
            {
                ((IUiLanguageInfo)ctx).UiCultureChanged += (_, _) => throw new InvalidOperationException("broken handler");
                return Task.CompletedTask;
            },
        });
        await env.Registry.StartAllAsync();

        source.Set(English); // must not throw – the guard catches it

        Assert.Contains(env.Log.Entries, e => e.Message.Contains("Language changed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Test_module_tiles_and_status_switch_language()
    {
        await using var env = new TestEnv();
        var source = new UiCultureSource(German);
        env.Registry.UiCulture = source;
        env.Registry.Register(new DummyModule());
        await env.Registry.StartAllAsync();
        try
        {
            var tile = await env.Registry.CreateTileAsync(new TileConfig { Id = "t", Module = "dummy", Type = "dummy.switch", Entities = new() { ["main"] = "dummy.fan" } });
            Assert.Equal("Aus", tile.StateText);
            Assert.Equal("Läuft (nur im Speicher)", env.Registry.Find("dummy")!.Status.DisplayText);

            source.Set(English);

            Assert.True(await Wait.UntilAsync(() => tile.StateText == "Off"));
            Assert.True(await Wait.UntilAsync(() => env.Registry.Find("dummy")!.Status.DisplayText == "Running (in memory only)"));
            var recreated = await env.Registry.CreateTileAsync(new TileConfig { Id = "t2", Module = "dummy", Type = "dummy.dimmer", Entities = new() { ["main"] = "dummy.desk" } });
            Assert.Equal("Desk lamp", recreated.Title);
            Assert.Equal("Brightness", recreated.FindElement<SliderElement>("brightness")!.Label);
        }
        finally
        {
            source.Set(German); // the test module keeps its culture statically
        }
    }
}
