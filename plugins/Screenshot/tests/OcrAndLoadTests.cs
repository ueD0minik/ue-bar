// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Globalization;
using UeBar.Core.Plugins;
using UeBar.Core.Settings;
using UeBar.Sdk;

namespace UeBar.Modules.Screenshot.Tests;

/// <summary>
/// The real Windows text recognition with test images (demo text, German and English). Which recognition
/// languages exist depends on the PC: with none the plugin must say so; German is checked with umlauts
/// only where the German package is installed.
/// </summary>
public sealed class WindowsOcrTests
{
    private static byte[] Image(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "images", name));

    private static readonly WindowsOcr Ocr = new();

    private static bool Has(string prefix) => Ocr.Languages.Any(l => l.Tag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public async Task English_text_is_recognized()
    {
        var outcome = await Ocr.RecognizeAsync(Image("ocr-en.png"), null, default);
        if (Ocr.Languages.Count == 0)
        {
            Assert.Equal(OcrProblem.NoLanguage, outcome.Problem);
            return;
        }

        Assert.True(outcome.HasText, $"{outcome.Problem}: {outcome.Detail}");
        var text = outcome.Text.ToLowerInvariant();
        Assert.Contains("quick", text, StringComparison.Ordinal);
        Assert.Contains("brown", text, StringComparison.Ordinal);
        Assert.Contains("lazy", text, StringComparison.Ordinal);
        Assert.Equal(2, outcome.Text.Split(Environment.NewLine).Length); // one line per line
    }

    [Fact]
    public async Task German_text_is_recognized_with_umlauts_when_german_is_installed()
    {
        var german = Ocr.Languages.FirstOrDefault(l => l.Tag.StartsWith("de", StringComparison.OrdinalIgnoreCase)).Tag;
        var outcome = await Ocr.RecognizeAsync(Image("ocr-de.png"), german ?? "de-DE", default);
        if (german is null)
        {
            // No German package on this PC (e.g. the build server): asked for German, it must say so.
            Assert.Equal(OcrProblem.NoLanguage, outcome.Problem);
            return;
        }

        Assert.True(outcome.HasText, $"{outcome.Problem}: {outcome.Detail}");
        Assert.Contains("Mathematik", outcome.Text, StringComparison.Ordinal);
        Assert.Contains("Meister", outcome.Text, StringComparison.Ordinal);
        Assert.Contains("Übung", outcome.Text, StringComparison.Ordinal);
        Assert.StartsWith("de", outcome.Language, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_recognition_language_that_is_not_installed_is_reported_not_guessed()
    {
        var missing = new[] { "ja-JP", "ko-KR", "zh-CN", "ar-SA" }.First(tag => !Has(tag));

        var outcome = await Ocr.RecognizeAsync(Image("ocr-en.png"), missing, default);

        Assert.Equal(OcrProblem.NoLanguage, outcome.Problem);
        Assert.False(outcome.HasText);
    }

    [Fact]
    public async Task The_real_recognition_writes_no_temporary_file()
    {
        // P2-5-Fix 1: the image goes to Windows OCR from memory – nothing lands on the disk.
        var temp = Path.Combine(Path.GetTempPath(), "uebar-ocr-temp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var (tmp, tempVar) = (Environment.GetEnvironmentVariable("TMP"), Environment.GetEnvironmentVariable("TEMP"));
        try
        {
            Environment.SetEnvironmentVariable("TMP", temp);
            Environment.SetEnvironmentVariable("TEMP", temp);
            await Ocr.RecognizeAsync(Image("ocr-en.png"), null, default);
            Assert.Empty(Directory.EnumerateFileSystemEntries(temp, "*", SearchOption.AllDirectories));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TMP", tmp);
            Environment.SetEnvironmentVariable("TEMP", tempVar);
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public async Task Not_an_image_fails_cleanly()
    {
        if (Ocr.Languages.Count == 0)
        {
            return;
        }

        var outcome = await Ocr.RecognizeAsync([1, 2, 3, 4], null, default);

        Assert.Equal(OcrProblem.Failed, outcome.Problem);
    }

    [Fact]
    public void The_settings_offer_the_installed_languages()
    {
        var field = ShotOptions.Fields(Ocr.Languages).Single(f => f.Key == ShotOptions.OcrLanguageKey);
        Assert.Equal(["auto", .. Ocr.Languages.Select(l => l.Tag)], field.Choices!.Select(c => c.Value));
        Assert.Contains("Optische Zeichenerkennung", ShotOptions.Fields([]).Single(f => f.Key == ShotOptions.OcrLanguageKey).Description, StringComparison.Ordinal);
    }
}

/// <summary>The real plugin folder loads through the normal plugin loader – no special treatment in the core.</summary>
public sealed class PluginLoadTests
{
    private static string BuiltPlugin => Path.GetDirectoryName(typeof(ScreenshotModule).Assembly.Location)!;

    [Fact]
    public async Task Plugin_folder_loads_like_any_bundled_plugin()
    {
        var root = Path.Combine(Path.GetTempPath(), "uebar-shot-load", Guid.NewGuid().ToString("N"));
        var bundled = Path.Combine(root, "app", "plugins");
        var target = Path.Combine(bundled, "screenshot");
        try
        {
            foreach (var file in Directory.EnumerateFiles(BuiltPlugin, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(BuiltPlugin, file);
                if (!IsPluginFile(relative))
                {
                    continue;
                }

                var destination = Path.Combine(target, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination);
            }

            var list = BundledPluginList.Parse(string.Join("\n", PluginHash.Files(target).Select(f => $"{f.Value.ToUpperInvariant()}  screenshot/{f.Key}")));
            var log = new ListLogger();
            var host = new PluginHost(
                new PluginHostOptions
                {
                    BundledDirectory = bundled,
                    BundledList = list,
                    UserDirectory = Path.Combine(root, "data", "plugins"),
                    ApiVersion = CoreApiVersion.Version,
                    LoadTimeout = TimeSpan.FromSeconds(15),
                },
                new InMemoryModuleStateStore(),
                new PluginTrustStore(Path.Combine(root, "data", "plugin-trust.json"), log),
                log);

            var found = Assert.Single(host.Discover());
            Assert.Equal(PluginState.Pending, found.State);
            await host.LoadAsync();

            var plugin = Assert.Single(host.Plugins);
            Assert.True(plugin.State == PluginState.Loaded, $"{plugin.State}: {plugin.Problem}");
            Assert.Equal(ScreenshotModule.ModuleId, plugin.Module!.Id);
            Assert.Equal("Screenshot", plugin.Manifest!.DisplayName(CultureInfo.GetCultureInfo("de-DE")));
            Assert.Equal(CoreApiVersion.Version, plugin.Manifest.SdkVersion);
            Assert.Contains("shot fenster", ((IQuickCommandProvider)plugin.Module).QuickCommands[0].Description, StringComparison.Ordinal); // texts from its own satellite assembly
            Assert.Contains(plugin.Module.TileTypes, t => t.Id == CaptureTileType.TypeId);
            Assert.IsAssignableFrom<IHotkeyHandler>(plugin.Module);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static readonly string[] PluginFiles =
    [
        "UeBar.Modules.Screenshot.dll", "UeBar.Modules.Screenshot.deps.json", "plugin.json", "Microsoft.Windows.SDK.NET.dll", "WinRT.Runtime.dll",
    ];

    private static bool IsPluginFile(string relative) =>
        PluginFiles.Contains(relative, StringComparer.OrdinalIgnoreCase)
        || relative.Equals(Path.Combine("de", "UeBar.Modules.Screenshot.resources.dll"), StringComparison.OrdinalIgnoreCase);
}
