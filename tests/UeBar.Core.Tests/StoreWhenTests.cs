// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Core.Settings;
using UeBar.Sdk;

namespace UeBar.Core.Tests;

/// <summary>
/// Contract 1.8: <see cref="SettingField.StoreWhen"/> – a value (e.g. an API key) reaches the disk only while another
/// field allows it; otherwise it lives in memory until the app ends.
/// </summary>
public sealed class StoreWhenTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "uebar-storewhen-" + Guid.NewGuid().ToString("N"));

    private static readonly IReadOnlyList<SettingField> Fields =
    [
        new("remember", "Remember", SettingFieldKind.Toggle, "false"),
        new("key", "Key", SettingFieldKind.Password) { StoreWhen = "remember=true" },
    ];

    public StoreWhenTests() => Directory.CreateDirectory(_dir);

    private string File => Path.Combine(_dir, "settings.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private SettingsService Open()
    {
        var settings = new SettingsService(File, new ReversingProtector(), new ListLogger()) { FieldsOf = id => id == "ai" ? Fields : null };
        settings.Load();
        return settings;
    }

    [Fact]
    public void Without_remember_the_key_lives_in_memory_only()
    {
        var settings = Open();
        settings.SetModuleValues("ai", [("key", "sk-secret-123", true)]);

        Assert.Equal("sk-secret-123", settings.GetModuleSettings("ai").GetString("key"));
        Assert.DoesNotContain("terces", System.IO.File.ReadAllText(File), StringComparison.Ordinal); // not even encrypted
        Assert.Null(Open().GetModuleSettings("ai").GetString("key")); // gone after a restart
    }

    [Fact]
    public void Switching_remember_on_stores_it_and_off_removes_it_from_the_disk()
    {
        var settings = Open();
        settings.SetModuleValues("ai", [("key", "sk-secret-123", true)]);
        settings.SetModuleValues("ai", [("remember", "true", false)]);
        Assert.Equal("sk-secret-123", Open().GetModuleSettings("ai").GetString("key"));

        settings.SetModuleValues("ai", [("remember", "false", false)]);
        Assert.Equal("sk-secret-123", settings.GetModuleSettings("ai").GetString("key")); // still usable now
        Assert.Null(Open().GetModuleSettings("ai").GetString("key"));

        // Emptying the field forgets it in memory, too.
        settings.SetModuleValues("ai", [("key", "", true)]);
        Assert.Null(settings.GetModuleSettings("ai").GetString("key"));
    }

    [Fact]
    public void A_stored_value_from_before_is_moved_off_the_disk_at_start()
    {
        var plain = new SettingsService(File, new ReversingProtector(), new ListLogger());
        plain.Load();
        plain.SetModuleValues("ai", [("key", "sk-old", true)]);

        var settings = Open();
        settings.EnforceStoreRules("ai");
        Assert.Equal("sk-old", settings.GetModuleSettings("ai").GetString("key"));
        Assert.Null(Open().GetModuleSettings("ai").GetString("key"));
    }

    private sealed class ReversingProtector : ISecretProtector
    {
        public string Protect(string plainText) => new([.. plainText.Reverse()]);

        public string Unprotect(string protectedText) => new([.. protectedText.Reverse()]);
    }
}
