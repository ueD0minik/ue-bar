// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Text.Json;
using System.Text.Json.Serialization;
using UeBar.Sdk;
using UeBar.Core.Bus;
using UeBar.Core.Logging;

namespace UeBar.Core.Settings;

/// <summary>Content of %APPDATA%\ueBar\settings.json (device-specific, not synchronized).</summary>
public sealed class AppSettings
{
    public int Version { get; set; } = 1;

    /// <summary>Per module ID: on/off + values.</summary>
    public Dictionary<string, ModuleSettingsEntry> Modules { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Permissions for the module bus. Default: empty = everything denied. No UI yet.</summary>
    public List<BusGrant> BusGrants { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class ModuleSettingsEntry
{
    public bool Enabled { get; set; } = true;

    /// <summary>Normal values in plain text.</summary>
    public Dictionary<string, string> Values { get; set; } = [];

    /// <summary>Secrets, encrypted through <see cref="ISecretProtector"/> (DPAPI in the app).</summary>
    public Dictionary<string, string> Secrets { get; set; } = [];
}

/// <summary>Encrypts secrets. The Windows implementation (DPAPI) lives in the app, not in the core.</summary>
public interface ISecretProtector
{
    string Protect(string plainText);

    string Unprotect(string protectedText);
}

/// <summary>What the registry needs to know about modules: on/off and setting values.</summary>
public interface IModuleStateStore
{
    bool IsModuleEnabled(string moduleId);

    void SetModuleEnabled(string moduleId, bool enabled);

    ModuleSettingValues GetModuleSettings(string moduleId);
}

/// <summary>Saved on/off setting of a module, if there is one (M7: default for developer modules).</summary>
public interface IModuleEnabledLookup
{
    /// <summary>null = nothing saved (then the default applies).</summary>
    bool? GetModuleEnabled(string moduleId);
}

/// <summary>Secrets a module stores itself (e.g. webhook ID, M6) – encrypted like the token.</summary>
public interface IModuleSecretStore
{
    string? GetModuleSecret(string moduleId, string key);

    void SetModuleSecret(string moduleId, string key, string? value);
}

/// <summary>Simple in-memory store (tests, or when no file is wanted).</summary>
public sealed class InMemoryModuleStateStore : IModuleStateStore, IModuleSecretStore, IModuleEnabledLookup
{
    private readonly Dictionary<string, bool> _enabled = new(StringComparer.OrdinalIgnoreCase);

    public bool? GetModuleEnabled(string moduleId) => _enabled.TryGetValue(moduleId, out var e) ? e : null;
    private readonly Dictionary<string, Dictionary<string, string>> _values = new(StringComparer.OrdinalIgnoreCase);

    public bool IsModuleEnabled(string moduleId) => !_enabled.TryGetValue(moduleId, out var e) || e;

    public void SetModuleEnabled(string moduleId, bool enabled) => _enabled[moduleId] = enabled;

    public void SetValue(string moduleId, string key, string value)
    {
        if (!_values.TryGetValue(moduleId, out var dict))
        {
            dict = [];
            _values[moduleId] = dict;
        }

        dict[key] = value;
    }

    public ModuleSettingValues GetModuleSettings(string moduleId) =>
        _values.TryGetValue(moduleId, out var dict) ? new ModuleSettingValues(new Dictionary<string, string>(dict)) : ModuleSettingValues.Empty;

    public string? GetModuleSecret(string moduleId, string key) =>
        _values.TryGetValue(moduleId, out var dict) && dict.TryGetValue(key, out var value) ? value : null;

    public void SetModuleSecret(string moduleId, string key, string? value)
    {
        if (value is null)
        {
            if (_values.TryGetValue(moduleId, out var dict))
            {
                dict.Remove(key);
            }

            return;
        }

        SetValue(moduleId, key, value);
    }
}

/// <summary>
/// Reads/writes settings.json. Provides module on/off, module values (secrets decrypted)
/// and bus permissions.
/// </summary>
public sealed class SettingsService : IModuleStateStore, IModuleSecretStore, IModuleEnabledLookup
{
    private readonly string _path;
    private readonly ISecretProtector? _protector;
    private readonly IUeLogger _log;
    private readonly Lock _lock = new();

    public SettingsService(string path, ISecretProtector? protector, IUeLogger log)
    {
        _path = path;
        _protector = protector;
        _log = log;
        BusPolicy = new GrantListPermissionPolicy(() =>
        {
            lock (_lock)
            {
                return [.. Current.BusGrants];
            }
        });
    }

    public AppSettings Current { get; private set; } = new();

    public GrantListPermissionPolicy BusPolicy { get; }

    /// <summary>Did a settings.json exist at load time? (M7: otherwise welcome assistant)</summary>
    public bool FileExisted { get; private set; }

    public bool? GetModuleEnabled(string moduleId)
    {
        lock (_lock)
        {
            return Current.Modules.TryGetValue(moduleId, out var entry) ? entry.Enabled : null;
        }
    }

    public void Load()
    {
        lock (_lock)
        {
            FileExisted = File.Exists(_path);
            if (!FileExisted)
            {
                Current = new AppSettings();
                return;
            }

            try
            {
                Current = UeBarJson.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new AppSettings();
                Current.Modules = new Dictionary<string, ModuleSettingsEntry>(Current.Modules, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // Do not overwrite a broken file, move it aside instead.
                var broken = _path + $".kaputt-{DateTime.Now:yyyyMMdd-HHmmss}";
                _log.Error(null, $"settings.json is invalid, moved to {Path.GetFileName(broken)}: {ex.Message}");
                try
                {
                    File.Move(_path, broken);
                }
                catch (IOException)
                {
                }

                Current = new AppSettings();
            }
        }
    }

    public void Save()
    {
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, UeBarJson.Serialize(Current));
            File.Move(tmp, _path, overwrite: true);
        }
    }

    public bool IsModuleEnabled(string moduleId)
    {
        lock (_lock)
        {
            return !Current.Modules.TryGetValue(moduleId, out var entry) || entry.Enabled;
        }
    }

    public void SetModuleEnabled(string moduleId, bool enabled)
    {
        lock (_lock)
        {
            GetOrCreate(moduleId).Enabled = enabled;
        }

        Save();
    }

    public ModuleSettingValues GetModuleSettings(string moduleId)
    {
        lock (_lock)
        {
            if (!Current.Modules.TryGetValue(moduleId, out var entry))
            {
                return ModuleSettingValues.Empty;
            }

            var values = new Dictionary<string, string>(entry.Values);
            foreach (var (key, protectedValue) in entry.Secrets)
            {
                if (_protector is null)
                {
                    continue;
                }

                try
                {
                    values[key] = _protector.Unprotect(protectedValue);
                }
                catch (Exception ex)
                {
                    // Deliberately logged without the value.
                    _log.Warn(moduleId, $"Secret '{key}' could not be decrypted ({ex.GetType().Name})");
                }
            }

            return new ModuleSettingValues(values);
        }
    }

    /// <summary>
    /// Reads an own section (e.g. "bar") from settings.json. If it is missing or
    /// invalid, a new object with default values comes back.
    /// </summary>
    public T GetSection<T>(string name)
        where T : class, new()
    {
        lock (_lock)
        {
            if (Current.Extra is { } extra && extra.TryGetValue(name, out var element))
            {
                try
                {
                    return element.Deserialize<T>(UeBarJson.Options) ?? new T();
                }
                catch (JsonException ex)
                {
                    _log.Warn(null, $"Section '{name}' in settings.json invalid, using defaults: {ex.Message}");
                }
            }

            return new T();
        }
    }

    /// <summary>Writes an own section and saves the file.</summary>
    public void SetSection<T>(string name, T value)
    {
        lock (_lock)
        {
            Current.Extra ??= [];
            Current.Extra[name] = JsonSerializer.SerializeToElement(value, UeBarJson.Options);
        }

        Save();
    }

    /// <summary>Sets a value; secrets are stored encrypted.</summary>
    public void SetModuleValue(string moduleId, string key, string? value, bool isSecret)
    {
        lock (_lock)
        {
            var entry = GetOrCreate(moduleId);
            entry.Values.Remove(key);
            entry.Secrets.Remove(key);
            if (value is not null)
            {
                if (isSecret)
                {
                    if (_protector is null)
                    {
                        throw new InvalidOperationException("No encryption service for secrets available.");
                    }

                    entry.Secrets[key] = _protector.Protect(value);
                }
                else
                {
                    entry.Values[key] = value;
                }
            }
        }

        Save();
    }

    /// <summary>Set several values at once (one write). Secret with value null = leave unchanged.</summary>
    public void SetModuleValues(string moduleId, IEnumerable<(string Key, string? Value, bool IsSecret)> values)
    {
        lock (_lock)
        {
            var entry = GetOrCreate(moduleId);
            foreach (var (key, value, isSecret) in values)
            {
                if (isSecret)
                {
                    if (value is null)
                    {
                        continue; // left empty = keep the previous secret
                    }

                    if (_protector is null)
                    {
                        throw new InvalidOperationException("No encryption service for secrets available.");
                    }

                    entry.Values.Remove(key);
                    if (value.Length == 0)
                    {
                        entry.Secrets.Remove(key);
                    }
                    else
                    {
                        entry.Secrets[key] = _protector.Protect(value);
                    }
                }
                else
                {
                    entry.Secrets.Remove(key);
                    if (value is null)
                    {
                        entry.Values.Remove(key);
                    }
                    else
                    {
                        entry.Values[key] = value;
                    }
                }
            }
        }

        Save();
    }

    public string? GetModuleSecret(string moduleId, string key) =>
        HasSecret(moduleId, key) && GetModuleSettings(moduleId).GetString(key) is { Length: > 0 } value ? value : null;

    public void SetModuleSecret(string moduleId, string key, string? value) => SetModuleValue(moduleId, key, value, isSecret: true);

    /// <summary>Is there a saved secret (without decrypting it)?</summary>
    public bool HasSecret(string moduleId, string key)
    {
        lock (_lock)
        {
            return Current.Modules.TryGetValue(moduleId, out var entry) && entry.Secrets.ContainsKey(key);
        }
    }

    private ModuleSettingsEntry GetOrCreate(string moduleId)
    {
        if (!Current.Modules.TryGetValue(moduleId, out var entry))
        {
            entry = new ModuleSettingsEntry();
            Current.Modules[moduleId] = entry;
        }

        return entry;
    }
}
