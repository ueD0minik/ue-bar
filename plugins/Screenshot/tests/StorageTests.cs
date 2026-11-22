// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Sdk;

namespace UeBar.Modules.Screenshot.Tests;

/// <summary>
/// P2-5-Fix 1: where a screenshot ends up. The capture lives in memory only; only "Save" and "Save as …" write
/// a file, pinning never writes into the screenshot folder, copying and text recognition write nothing at all.
/// </summary>
public sealed class StorageTests
{
    /// <summary>Points TEMP/TMP at an empty folder for the test, so any temporary file would show up there.</summary>
    private sealed class TempFolder : IDisposable
    {
        private readonly string? _tmp = Environment.GetEnvironmentVariable("TMP");
        private readonly string? _temp = Environment.GetEnvironmentVariable("TEMP");

        public TempFolder()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "uebar-shot-temp", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            Environment.SetEnvironmentVariable("TMP", Path);
            Environment.SetEnvironmentVariable("TEMP", Path);
        }

        public string Path { get; }

        public IReadOnlyList<string> Files => [.. Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories)];

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("TMP", _tmp);
            Environment.SetEnvironmentVariable("TEMP", _temp);
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task Copy_and_text_recognition_write_no_file_anywhere()
    {
        await using var env = await new ShotEnv().StartAsync();
        var folder = System.IO.Path.Combine(env.DataDirectory, "shots");
        await env.SettingsAsync((ShotOptions.FolderKey, folder));
        using var temp = new TempFolder();
        Assert.Equal(temp.Path, System.IO.Path.GetTempPath().TrimEnd('\\'));

        env.Picker.Choose = "copy";
        await env.Module.CaptureAsync(null, null, default);
        env.Picker.Choose = "ocr";
        await env.Module.CaptureAsync(null, null, default);

        Assert.Single(env.Clipboard.Images);
        Assert.Single(env.Clipboard.Texts);
        Assert.Empty(temp.Files);
        Assert.False(Directory.Exists(folder));
        Assert.Empty(Directory.Exists(env.Pins.Directory) ? Directory.GetFiles(env.Pins.Directory) : []);
    }

    [Fact]
    public async Task Pinning_never_puts_anything_into_the_screenshot_folder()
    {
        await using var env = await new ShotEnv().StartAsync();
        var folder = System.IO.Path.Combine(env.DataDirectory, "shots");
        await env.SettingsAsync((ShotOptions.FolderKey, folder));

        await env.Module.CaptureAsync(null, ShotAction.Pin, default);

        Assert.Single(env.Pins.Pins);
        Assert.False(Directory.Exists(folder)); // the pin lives in the pins folder only
        Assert.Contains(Directory.GetFiles(env.Pins.Directory), f => System.IO.Path.GetFileName(f).StartsWith("pin-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Save_as_asks_the_core_dialog_and_writes_where_the_user_chose()
    {
        await using var env = await new ShotEnv().StartAsync();
        var folder = System.IO.Path.Combine(env.DataDirectory, "shots");
        var own = System.IO.Path.Combine(env.DataDirectory, "my folder");
        Directory.CreateDirectory(own);
        await env.SettingsAsync((ShotOptions.FolderKey, folder));
        env.SaveDialog.Answer = System.IO.Path.Combine(own, "Meine Folie.png");
        env.Picker.Choose = "saveas";

        Assert.Equal(ShotAction.SaveAs, await env.Module.CaptureAsync(null, null, default));

        var (owner, request) = Assert.Single(env.SaveDialog.Asked);
        Assert.Equal(ScreenshotModule.ModuleId, owner); // the core remembers the last folder per module
        Assert.Equal(("Screenshot 2026-11-20 14-03-05.png", ".png", folder), (request.SuggestedName, request.Extension, request.InitialFolder));
        Assert.Equal(Png.Tiny, File.ReadAllBytes(env.SaveDialog.Answer));
        Assert.False(Directory.Exists(folder)); // "Save as" does not also save into the default folder
        var note = env.Notifications.Last();
        Assert.Equal(("Screenshot gespeichert", "Meine Folie.png", true), (note.Title, note.Message, note.IsActionFeedback));
    }

    [Fact]
    public async Task Cancelled_save_as_writes_nothing()
    {
        await using var env = await new ShotEnv().StartAsync();
        env.SaveDialog.Answer = null;

        await env.Module.CaptureAsync(null, ShotAction.SaveAs, default);

        Assert.Single(env.SaveDialog.Asked);
        Assert.Empty(env.Notifications);
        Assert.Equal(ShotAction.SaveAs, ShotOptions.ParseAction("saveas"));
    }

    [Fact]
    public async Task Every_message_of_the_plugin_is_feedback_on_the_users_action()
    {
        await using var env = await new ShotEnv().StartAsync();
        var folder = System.IO.Path.Combine(env.DataDirectory, "shots");
        await env.SettingsAsync((ShotOptions.FolderKey, folder));

        foreach (var action in new[] { ShotAction.Copy, ShotAction.Save, ShotAction.Ocr })
        {
            await env.Module.CaptureAsync(null, action, default);
        }

        Assert.Equal(3, env.Notifications.Count);
        Assert.All(env.Notifications, n => Assert.True(n.IsActionFeedback, n.Title));
    }
}
