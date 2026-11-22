// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Globalization;

namespace UeBar.Sdk;

public enum SettingFieldKind
{
    Text,

    /// <summary>Secret (token, password) – stored encrypted by the core.</summary>
    Password,
    Number,
    Toggle,
    Choice,

    /// <summary>Multiple choice (M7): value = chosen <see cref="SettingChoice.Value"/>s separated by commas.</summary>
    MultiChoice,

    /// <summary>
    /// A button (contract 1.2): <see cref="SettingField.Label"/> and <see cref="SettingField.Description"/>
    /// as the row, <see cref="SettingField.ButtonText"/> on the button; pressing it calls
    /// <see cref="IModuleSettingsActions.RunSettingsActionAsync"/> with the key. Stores nothing.
    /// </summary>
    Button,

    /// <summary>A file path (contract 1.3): text field + "Browse…" with the core's file dialog (<see cref="SettingField.FileFilter"/>).</summary>
    File,

    /// <summary>A folder path (contract 1.3): text field + "Browse…" with the core's folder dialog.</summary>
    Folder,

    /// <summary>
    /// A list of entries (contract 1.3), each with the fields of <see cref="SettingField.ItemFields"/>
    /// (no nested lists, no secrets). The value is a JSON array of objects (field key → text); the core
    /// gives every entry a stable <c>"id"</c>. Read it with <see cref="ModuleSettingValues.GetList"/>.
    /// </summary>
    List,

    /// <summary>
    /// A global hotkey (contract 1.5), e.g. <c>"Win+Alt+S"</c>: recorded like the quick search's hotkey;
    /// empty = off. While the module runs the core registers it and calls <see cref="IHotkeyHandler.OnHotkeyAsync"/>.
    /// </summary>
    Hotkey,

    /// <summary>
    /// A hint on the page (contract 1.5): <see cref="SettingField.Label"/> as a heading (may be empty) and
    /// <see cref="SettingField.Description"/> as text. Stores nothing.
    /// </summary>
    Info,
}

public sealed record SettingChoice(string Value, string Label);

/// <summary>A settings field; the core builds a page from these automatically (SPEC 4.1 / 11).</summary>
public sealed record SettingField(
    string Key,
    string Label,
    SettingFieldKind Kind,
    string? DefaultValue = null,
    string? Description = null,
    IReadOnlyList<SettingChoice>? Choices = null,
    double? Min = null,
    double? Max = null)
{
    public bool IsSecret => Kind == SettingFieldKind.Password;

    /// <summary>Example in the empty field (M7), e.g. a typical address.</summary>
    public string? Placeholder { get; init; }

    /// <summary>Belongs in the welcome assistant at first start (M7).</summary>
    public bool Essential { get; init; }

    /// <summary>Text on the button of a <see cref="SettingFieldKind.Button"/> field (contract 1.2).</summary>
    public string? ButtonText { get; init; }

    /// <summary>
    /// Filter of the file dialog for <see cref="SettingFieldKind.File"/> (contract 1.3), as in Windows:
    /// <c>"Sounds|*.wav;*.mp3|All files|*.*"</c>. null = all files.
    /// </summary>
    public string? FileFilter { get; init; }

    /// <summary>Fields of one entry of a <see cref="SettingFieldKind.List"/> (contract 1.3).</summary>
    public IReadOnlyList<SettingField>? ItemFields { get; init; }

    /// <summary>Text of the "Add" button of a <see cref="SettingFieldKind.List"/> (contract 1.3).</summary>
    public string? AddText { get; init; }

    /// <summary>
    /// Show the field only while another field of the same level (the page, or the same list entry) has
    /// one of these values (contract 1.3): <c>"kind=file"</c>, <c>"kind=app|command"</c>. null = always.
    /// A hidden field keeps its value.
    /// </summary>
    public string? ShowWhen { get; init; }

    /// <summary>Whether <see cref="ShowWhen"/> is met; <paramref name="valueOf"/> gives the current value of a key (or its default).</summary>
    public bool IsShown(Func<string, string?> valueOf)
    {
        if (string.IsNullOrWhiteSpace(ShowWhen) || ShowWhen.IndexOf('=') is var at && at <= 0)
        {
            return true;
        }

        var value = valueOf(ShowWhen[..at].Trim()) ?? "";
        return ShowWhen[(at + 1)..].Split('|', StringSplitOptions.TrimEntries).Contains(value, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>Current values of the module settings (secrets already decrypted, in memory only).</summary>
public sealed class ModuleSettingValues
{
    private readonly IReadOnlyDictionary<string, string> _values;

    public ModuleSettingValues(IReadOnlyDictionary<string, string>? values = null)
    {
        _values = values ?? new Dictionary<string, string>();
    }

    public static ModuleSettingValues Empty { get; } = new();

    public IEnumerable<string> Keys => _values.Keys;

    public string? GetString(string key) => _values.TryGetValue(key, out var v) ? v : null;

    public bool GetBool(string key, bool fallback = false) =>
        _values.TryGetValue(key, out var v) && bool.TryParse(v, out var b) ? b : fallback;

    public double GetNumber(string key, double fallback = 0) =>
        _values.TryGetValue(key, out var v)
        && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d
            : fallback;

    /// <summary>
    /// Entries of a <see cref="SettingFieldKind.List"/> field (contract 1.3): per entry field key → text,
    /// including <c>"id"</c>. Empty if there is none or the value is not a list.
    /// </summary>
    public IReadOnlyList<IReadOnlyDictionary<string, string>> GetList(string key) => ParseList(GetString(key));

    /// <summary>Parses the value of a list field (JSON array of objects); broken values give an empty list.</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, string>> ParseList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(json) is not System.Text.Json.Nodes.JsonArray array)
            {
                return [];
            }

            var items = new List<IReadOnlyDictionary<string, string>>();
            foreach (var node in array)
            {
                if (node is not System.Text.Json.Nodes.JsonObject entry)
                {
                    continue;
                }

                var item = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (name, value) in entry)
                {
                    if (value is System.Text.Json.Nodes.JsonValue v)
                    {
                        item[name] = v.TryGetValue<string>(out var s) ? s : v.ToJsonString();
                    }
                }

                items.Add(item);
            }

            return items;
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    /// <summary>Builds the value of a list field from entries (the counterpart of <see cref="ParseList"/>).</summary>
    public static string FormatList(IEnumerable<IReadOnlyDictionary<string, string>> items)
    {
        var array = new System.Text.Json.Nodes.JsonArray();
        foreach (var item in items)
        {
            var entry = new System.Text.Json.Nodes.JsonObject();
            foreach (var (name, value) in item)
            {
                entry[name] = value;
            }

            array.Add(entry);
        }

        return array.ToJsonString();
    }
}

/// <summary>Describes a module's settings as fields.</summary>
public interface IModuleSettings
{
    IReadOnlyList<SettingField> SettingFields { get; }

    /// <summary>Called before the start and on every change.</summary>
    Task ApplySettingsAsync(ModuleSettingValues values, CancellationToken cancellationToken);
}
