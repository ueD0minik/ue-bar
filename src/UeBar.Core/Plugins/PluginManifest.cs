// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using UeBar.Core.Localization;
using UeBar.Sdk;

namespace UeBar.Core.Plugins;

/// <summary>A plugin cannot be used; <see cref="Exception.Message"/> is the reason in the UI language.</summary>
public sealed class PluginException : Exception
{
    public PluginException(string message, bool incompatible = false, Exception? inner = null)
        : base(message, inner)
    {
        Incompatible = incompatible;
    }

    /// <summary>The plugin itself is fine but does not fit this version of ue Bar.</summary>
    public bool Incompatible { get; }
}

/// <summary>
/// Content of <c>plugin.json</c> (SPEC 4.4): what a plugin is, who made it and which contract version
/// it needs. <c>name</c> and <c>description</c> are either a text or an object with one text per
/// language (<c>{ "en": "…", "de": "…" }</c>).
/// </summary>
public sealed partial class PluginManifest
{
    public const string FileName = "plugin.json";

    private PluginManifest()
    {
    }

    /// <summary>Unique ID = module ID = folder name: lower-case letters, digits, '.', '-', '_'.</summary>
    public string Id { get; private init; } = "";

    public IReadOnlyDictionary<string, string> Names { get; private init; } = new Dictionary<string, string>();

    public IReadOnlyDictionary<string, string> Descriptions { get; private init; } = new Dictionary<string, string>();

    public string Version { get; private init; } = "";

    public string Author { get; private init; } = "";

    public string License { get; private init; } = "";

    public string Homepage { get; private init; } = "";

    /// <summary>Contract version the plugin needs (<see cref="CoreApiVersion"/>), as written.</summary>
    public string SdkVersionText { get; private init; } = "";

    public Version SdkVersion { get; private init; } = new(0, 0);

    /// <summary>File name of the assembly with the module, e.g. "My.Plugin.dll".</summary>
    public string EntryAssembly { get; private init; } = "";

    /// <summary>Optional: full name of the module class if the assembly contains several.</summary>
    public string? EntryType { get; private init; }

    public string DisplayName(CultureInfo? culture = null) => Pick(Names, culture) ?? Id;

    public string Description(CultureInfo? culture = null) => Pick(Descriptions, culture) ?? "";

    /// <summary>Reads <c>plugin.json</c> in a plugin folder.</summary>
    public static PluginManifest Load(string directory)
    {
        var file = Path.Combine(directory, FileName);
        if (!File.Exists(file))
        {
            throw new PluginException(CoreText.T["Plugin_ManifestMissing"]);
        }

        string json;
        try
        {
            json = File.ReadAllText(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PluginException(CoreText.T.Format("Plugin_ManifestInvalid", ex.Message), inner: ex);
        }

        return Parse(json);
    }

    public static PluginManifest Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex)
        {
            throw new PluginException(CoreText.T.Format("Plugin_ManifestInvalid", ex.Message), inner: ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new PluginException(CoreText.T.Format("Plugin_ManifestInvalid", "not an object"));
            }

            var id = Text(root, "id");
            if (id is null || !IdPattern().IsMatch(id))
            {
                throw Field("id");
            }

            var names = Texts(root, "name");
            if (names.Count == 0)
            {
                throw Field("name");
            }

            var version = Text(root, "version") ?? throw Field("version");
            var sdkText = Text(root, "sdkVersion");
            if (!CoreApiVersion.TryParse(sdkText, out var sdk))
            {
                throw Field("sdkVersion");
            }

            var entry = Text(root, "entryAssembly");
            if (entry is null
                || entry.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || !entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                throw Field("entryAssembly");
            }

            return new PluginManifest
            {
                Id = id,
                Names = names,
                Descriptions = Texts(root, "description"),
                Version = version,
                Author = Text(root, "author") ?? "",
                License = Text(root, "license") ?? "",
                Homepage = Text(root, "homepage") ?? "",
                SdkVersionText = sdkText!.Trim(),
                SdkVersion = sdk,
                EntryAssembly = entry,
                EntryType = Text(root, "entryType"),
            };
        }
    }

    /// <summary>Is this a usable plugin ID (also a safe folder name)?</summary>
    public static bool IsValidId(string? id) => id is not null && IdPattern().IsMatch(id);

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    private static PluginException Field(string name) => new(CoreText.T.Format("Plugin_ManifestField", name));

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { } text && text.Trim().Length > 0
            ? text.Trim()
            : null;

    /// <summary>A text or an object "language → text"; a plain text is stored under the key "".</summary>
    private static Dictionary<string, string> Texts(JsonElement root, string name)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty(name, out var value))
        {
            return result;
        }

        if (value.ValueKind == JsonValueKind.String && value.GetString() is { } single && single.Trim().Length > 0)
        {
            result[""] = single.Trim();
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { } text && text.Trim().Length > 0)
                {
                    result[property.Name.Trim()] = text.Trim();
                }
            }
        }

        return result;
    }

    /// <summary>Text for the culture: exact language, then English, then the plain text, then any.</summary>
    private static string? Pick(IReadOnlyDictionary<string, string> texts, CultureInfo? culture)
    {
        if (texts.Count == 0)
        {
            return null;
        }

        culture ??= UiCultureSource.Shared.Current;
        foreach (var key in new[] { culture.Name, culture.TwoLetterISOLanguageName, "en", "" })
        {
            if (texts.TryGetValue(key, out var text))
            {
                return text;
            }
        }

        return texts.Values.First();
    }
}
