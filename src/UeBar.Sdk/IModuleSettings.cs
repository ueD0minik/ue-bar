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
}

/// <summary>Describes a module's settings as fields.</summary>
public interface IModuleSettings
{
    IReadOnlyList<SettingField> SettingFields { get; }

    /// <summary>Called before the start and on every change.</summary>
    Task ApplySettingsAsync(ModuleSettingValues values, CancellationToken cancellationToken);
}
