// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Reflection;
using UeBar.Core.Localization;
using UeBar.Core.Logging;
using UeBar.Core.Modules;
using UeBar.Core.Settings;
using UeBar.Sdk;

namespace UeBar.Core.Plugins;

public enum PluginOrigin
{
    /// <summary>Comes with ue Bar (<c>&lt;app folder&gt;\plugins</c>).</summary>
    Bundled,

    /// <summary>Added by the user (<c>%APPDATA%\ueBar\plugins</c>).</summary>
    Installed,
}

public enum PluginState
{
    /// <summary>Found and fine – will be loaded by <see cref="PluginHost.LoadAsync"/>.</summary>
    Pending,

    /// <summary>Module created; its further life is the module registry's business.</summary>
    Loaded,

    /// <summary>Switched off in the settings – not loaded.</summary>
    Disabled,

    /// <summary>Not bundled and no (valid) decision yet – the user has to allow it first.</summary>
    NeedsApproval,

    /// <summary>The user chose "do not load".</summary>
    Refused,

    /// <summary>Made for another contract version (<see cref="CoreApiVersion"/>).</summary>
    Incompatible,

    /// <summary>Broken, in conflict with another plugin, or failed while loading.</summary>
    Error,
}

/// <summary>A plugin folder and what became of it.</summary>
public sealed class PluginInfo
{
    internal PluginInfo(string id, string directory, PluginOrigin origin)
    {
        Id = id;
        Directory = directory;
        Origin = origin;
    }

    /// <summary>Plugin ID = module ID. Not unique in the list if two folders claim the same ID.</summary>
    public string Id { get; }

    public string Directory { get; }

    public PluginOrigin Origin { get; }

    /// <summary>null if plugin.json is missing or unreadable.</summary>
    public PluginManifest? Manifest { get; internal set; }

    public PluginState State { get; internal set; } = PluginState.Error;

    /// <summary>Reason for <see cref="PluginState.Error"/>/<see cref="PluginState.Incompatible"/> in the UI language.</summary>
    public string? Problem { get; internal set; }

    /// <summary>Checksum of the code files (see <see cref="PluginHash"/>); empty if the folder is unreadable.</summary>
    public string Hash { get; internal set; } = "";

    /// <summary>Bundled and unchanged since ue Bar was built.</summary>
    public bool IsVerifiedBundled { get; internal set; }

    /// <summary>The files changed since the user's last decision (or since the build, for bundled plugins).</summary>
    public bool FilesChanged { get; internal set; }

    /// <summary>Another plugin with the same ID was found first; this one is ignored.</summary>
    public bool IsDuplicate { get; internal set; }

    /// <summary>Saved on/off setting (a plugin without one is on).</summary>
    public bool IsEnabled { get; internal set; } = true;

    public IModule? Module { get; internal set; }

    /// <summary>The user removed it while it was loaded – the folder is deleted on the next start.</summary>
    public bool RemovalPending { get; internal set; }

    /// <summary>A newer package was added while it was loaded – this version takes its place on the next start.</summary>
    public string? UpdatePendingVersion { get; internal set; }

    public string DisplayName => Manifest?.DisplayName() ?? Id;
}

public sealed class PluginHostOptions
{
    /// <summary>Folder with the plugins that come with ue Bar; null = none.</summary>
    public string? BundledDirectory { get; init; }

    /// <summary>Checksums of the bundled plugins (written at build time).</summary>
    public BundledPluginList BundledList { get; init; } = BundledPluginList.Empty;

    /// <summary>Folder with the plugins the user added.</summary>
    public required string UserDirectory { get; init; }

    /// <summary>Contract version of this app.</summary>
    public Version ApiVersion { get; init; } = CoreApiVersion.Version;

    /// <summary>A plugin that does not finish loading (constructor) within this time counts as failed.</summary>
    public TimeSpan LoadTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Checksums of unchanged files from the last start (P2-5-Fix 1); none = every file is hashed.</summary>
    public PluginHashCache HashCache { get; init; } = PluginHashCache.None;
}

/// <summary>
/// Finds, checks and loads plugins (SPEC 4.4). Three steps, so that the app can ask the user in
/// between: <see cref="Discover"/> (reads <c>plugin.json</c>, runs no plugin code) →
/// <see cref="Decide"/> for every plugin in <see cref="PluginState.NeedsApproval"/> →
/// <see cref="LoadAsync"/>. Nothing here throws because of a plugin: a broken one gets the state
/// <see cref="PluginState.Error"/> with a reason and the app keeps running (SPEC 4.6).
/// </summary>
public sealed class PluginHost
{
    private readonly PluginHostOptions _options;
    private readonly IModuleEnabledLookup? _enabled;
    private readonly PluginTrustStore _trust;
    private readonly IUeLogger _log;
    private List<PluginInfo> _plugins = [];

    public PluginHost(PluginHostOptions options, IModuleEnabledLookup? enabled, PluginTrustStore trust, IUeLogger? log = null)
    {
        _options = options;
        _enabled = enabled;
        _trust = trust;
        _log = log ?? NullLogger.Instance;
        Installer = new PluginInstaller(options.UserDirectory, IsBundledId, options.ApiVersion, _log);
    }

    /// <summary>Opens and checks plugin packages (ZIP/folder) for <see cref="Install"/>.</summary>
    public PluginInstaller Installer { get; }

    public PluginHostOptions Options => _options;

    public PluginTrustStore Trust => _trust;

    /// <summary>All plugin folders found: bundled first (order of the build list), then installed by ID.</summary>
    public IReadOnlyList<PluginInfo> Plugins => _plugins;

    /// <summary>The plugin that owns an ID (the first one found).</summary>
    public PluginInfo? Find(string id) =>
        _plugins.FirstOrDefault(p => !p.IsDuplicate && string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Does a plugin with this ID come with ue Bar?</summary>
    public bool IsBundledId(string id) =>
        _plugins.Any(p => p.Origin == PluginOrigin.Bundled && string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Saved on/off setting right now (may differ from what was loaded at the start).</summary>
    public bool IsEnabledNow(PluginInfo plugin) => _enabled?.GetModuleEnabled(plugin.Id) ?? true;

    /// <summary>Is a change waiting that only takes effect when the app starts again?</summary>
    public bool RestartNeeded => _plugins.Any(p =>
        p.RemovalPending
        || p.UpdatePendingVersion is not null
        || p.State == PluginState.Pending
        || (p.State == PluginState.Disabled && !p.IsDuplicate && IsEnabledNow(p)));

    /// <summary>Did the user decide about this plugin before, with other files?</summary>
    public bool IsChanged(PluginPackage package) => _trust.HasOtherDecision(package.Manifest.Id, package.Hash);

    /// <summary>
    /// Adds a checked package to the user's plugins. Call it only after the user allowed the plugin –
    /// the permission is remembered with the checksum. The plugin is loaded on the next start; if a
    /// plugin with this ID is loaded right now, the new version replaces it then.
    /// </summary>
    public PluginInfo Install(PluginPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var id = package.Manifest.Id;
        var existing = _plugins.FirstOrDefault(p => p.Origin == PluginOrigin.Installed && !p.IsDuplicate && string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        var loaded = existing?.State == PluginState.Loaded;
        var target = Installer.Install(package, replacesLoaded: loaded);
        _trust.Set(id, package.Hash, allowed: true, package.Manifest.DisplayName(), package.Manifest.Version);
        if (loaded)
        {
            existing!.UpdatePendingVersion = package.Manifest.Version;
            existing.RemovalPending = false;
            try
            {
                File.Delete(Path.Combine(existing.Directory, PluginHash.RemoveMarker));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // the staged update replaces the folder anyway
            }

            return existing;
        }

        if (existing is not null)
        {
            _plugins.Remove(existing);
        }

        var added = Inspect(target, PluginOrigin.Installed, _plugins);
        _plugins.Add(added);
        return added;
    }

    /// <summary>
    /// Removes an installed plugin and forgets the permission. Returns true if it is gone now, false if
    /// it is loaded and its folder is deleted on the next start.
    /// </summary>
    public bool Uninstall(PluginInfo plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        var gone = Installer.Remove(plugin);
        if (!plugin.IsDuplicate)
        {
            _trust.Remove(plugin.Id);
        }

        plugin.UpdatePendingVersion = null;
        if (gone)
        {
            _plugins.Remove(plugin);
        }
        else
        {
            plugin.RemovalPending = true;
        }

        return gone;
    }

    /// <summary>
    /// Looks at all plugin folders. Applies removals/updates that were waiting for a restart first.
    /// No plugin code runs.
    /// </summary>
    public IReadOnlyList<PluginInfo> Discover()
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        PluginInstaller.ApplyPending(_options.UserDirectory, _log);
        _options.HashCache.Load();

        var found = new List<PluginInfo>();
        foreach (var directory in BundledFolders())
        {
            found.Add(Timed(directory, () => Inspect(directory, PluginOrigin.Bundled, found)));
        }

        foreach (var directory in Folders(_options.UserDirectory).OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
        {
            found.Add(Timed(directory, () => Inspect(directory, PluginOrigin.Installed, found)));
        }

        _sharedVerified = VerifyShared();
        _options.HashCache.Save();
        var (hits, misses) = _options.HashCache.Stats;
        _log.Debug(null, $"Startup: plugins discovered in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms (checksums: {hits} from the cache, {misses} computed)");

        _plugins = found;
        foreach (var plugin in found)
        {
            var version = plugin.Manifest is { } m ? $" {m.Version} (API {m.SdkVersionText})" : "";
            var line = $"Plugin '{plugin.Id}'{version}, {plugin.Origin.ToString().ToLowerInvariant()}: {plugin.State}{(plugin.Problem is null ? "" : " – " + plugin.Problem)} [{plugin.Directory}]";
            if (plugin.State is PluginState.Error or PluginState.Incompatible)
            {
                _log.Warn(null, line);
            }
            else
            {
                _log.Info(null, line);
            }
        }

        return found;
    }

    /// <summary>The user's answer for a plugin in <see cref="PluginState.NeedsApproval"/> (remembered with the checksum).</summary>
    public void Decide(PluginInfo plugin, bool allow)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        if (plugin.State is not (PluginState.NeedsApproval or PluginState.Refused))
        {
            return;
        }

        _trust.Set(plugin.Id, plugin.Hash, allow, plugin.Manifest?.DisplayName(), plugin.Manifest?.Version);
        plugin.FilesChanged = false;
        plugin.State = allow ? PluginState.Pending : PluginState.Refused;
    }

    /// <summary>Loads every plugin in <see cref="PluginState.Pending"/>: own load context, create the module.</summary>
    public async Task LoadAsync()
    {
        // P2-5: copies of assemblies that may exist only once per process – the highest version is shared.
        // P2-5-Fix 1: the bundled plugins bring them once, in plugins\.shared (only if they match the list).
        if (_sharedVerified is { } shared)
        {
            ProcessWideAssemblies.Offer(shared);
        }

        foreach (var plugin in _plugins.Where(p => p.State == PluginState.Pending))
        {
            ProcessWideAssemblies.Offer(plugin.Directory);
        }

        foreach (var plugin in _plugins.Where(p => p.State == PluginState.Pending).ToList())
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                // On the thread pool with a time limit: a constructor that hangs must not hang the app.
                plugin.Module = await Task.Run(() => CreateModule(plugin)).WaitAsync(_options.LoadTimeout).ConfigureAwait(false);
                plugin.State = PluginState.Loaded;
                _log.Info(plugin.Id, $"Plugin loaded in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms");
            }
            catch (PluginException ex)
            {
                Fail(plugin, ex.Message, ex.InnerException);
            }
            catch (TimeoutException)
            {
                Fail(plugin, CoreText.T.Format("Plugin_LoadTimeout", _options.LoadTimeout.TotalSeconds), null);
            }
            catch (Exception ex)
            {
                Fail(plugin, CoreText.T.Format("Plugin_LoadFailed", ex.Message), ex);
            }
        }
    }

    /// <summary>
    /// Hands the result to the module registry: loaded modules are registered; plugins that are off,
    /// refused or broken are announced so that their tiles show the right placeholder (SPEC 4.5).
    /// </summary>
    public void RegisterAll(ModuleRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        foreach (var plugin in _plugins.Where(p => !p.IsDuplicate))
        {
            if (plugin.State == PluginState.Loaded && plugin.Module is { } module)
            {
                if (registry.Register(module) is null)
                {
                    Fail(plugin, CoreText.T.Format("Plugin_LoadFailed", "IModule.Id"), null);
                }
                else
                {
                    continue;
                }
            }

            var status = plugin.State switch
            {
                PluginState.Disabled => ModuleStatus.Disabled,
                PluginState.NeedsApproval or PluginState.Refused => new ModuleStatus(ModuleState.Disabled, CoreText.T["Plugin_NotAllowed"]),
                _ => ModuleStatus.Error(plugin.Problem ?? CoreText.T["ModuleState_Error"]),
            };
            registry.RegisterUnavailable(plugin.Id, plugin.DisplayName, status);
        }
    }

    // ------------------------------------------------------------------ internal

    private string? _sharedVerified;

    private PluginInfo Timed(string directory, Func<PluginInfo> inspect)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var plugin = inspect();
        _log.Debug(null, $"Startup: plugin folder {Path.GetFileName(directory)} checked in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms");
        return plugin;
    }

    /// <summary>
    /// The shared folder of the bundled plugins (<see cref="PluginHash.SharedFolder"/>): used only if every code
    /// file in it is in <c>bundled.sha256</c> with the same checksum – otherwise ignored (with a warning).
    /// </summary>
    private string? VerifyShared()
    {
        if (_options.BundledDirectory is not { Length: > 0 } root)
        {
            return null;
        }

        var shared = Path.Combine(root, PluginHash.SharedFolder);
        if (!Directory.Exists(shared))
        {
            return null;
        }

        try
        {
            var files = PluginHash.Files(shared, _options.HashCache);
            var bad = files.Where(f => !string.Equals(_options.BundledList.HashOf(PluginHash.SharedFolder, f.Key), f.Value, StringComparison.OrdinalIgnoreCase)).Select(f => f.Key).ToList();
            if (files.Count == 0 || bad.Count > 0)
            {
                _log.Warn(null, $"Shared plugin assemblies not used – they do not match the bundled list: {string.Join(", ", bad)}");
                return null;
            }

            return shared;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn(null, $"Shared plugin assemblies not readable: {ex.Message}");
            return null;
        }
    }

    private IEnumerable<string> BundledFolders()
    {
        if (_options.BundledDirectory is not { Length: > 0 } root)
        {
            return [];
        }

        // Order of the build list = order of the modules (and of their suggested tabs).
        var order = _options.BundledList.Ids.Select((id, index) => (id, index)).ToDictionary(x => x.id, x => x.index, StringComparer.OrdinalIgnoreCase);
        return Folders(root)
            .OrderBy(d => order.TryGetValue(Path.GetFileName(d), out var index) ? index : int.MaxValue)
            .ThenBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> Folders(string root)
    {
        try
        {
            return Directory.Exists(root)
                ? [.. Directory.EnumerateDirectories(root).Where(d => !Path.GetFileName(d).StartsWith('.') && !File.Exists(Path.Combine(d, PluginHash.RemoveMarker)))]
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private PluginInfo Inspect(string directory, PluginOrigin origin, List<PluginInfo> earlier)
    {
        PluginManifest? manifest = null;
        string? problem = null;
        var incompatible = false;
        try
        {
            manifest = PluginManifest.Load(directory);
        }
        catch (PluginException ex)
        {
            problem = ex.Message;
        }

        var plugin = new PluginInfo(manifest?.Id ?? Path.GetFileName(directory).ToLowerInvariant(), directory, origin) { Manifest = manifest };
        plugin.IsEnabled = _enabled?.GetModuleEnabled(plugin.Id) ?? true;

        if (manifest is not null)
        {
            var first = earlier.FirstOrDefault(p => !p.IsDuplicate && string.Equals(p.Id, plugin.Id, StringComparison.OrdinalIgnoreCase));
            if (first is not null)
            {
                plugin.IsDuplicate = true;
                problem = first.Origin == PluginOrigin.Bundled && origin == PluginOrigin.Installed
                    ? CoreText.T.Format("Plugin_BundledId", plugin.Id)
                    : CoreText.T.Format("Plugin_DuplicateId", plugin.Id, first.Directory);
            }
        }

        if (problem is null && manifest is not null)
        {
            try
            {
                var entry = Path.Combine(directory, manifest.EntryAssembly);
                if (!File.Exists(entry))
                {
                    throw new PluginException(CoreText.T.Format("Plugin_EntryMissing", manifest.EntryAssembly));
                }

                PluginInspector.ThrowIfIncompatible(manifest.SdkVersion, _options.ApiVersion);
                PluginInspector.Check(entry, _options.ApiVersion);
            }
            catch (PluginException ex)
            {
                problem = ex.Message;
                incompatible = ex.Incompatible;
            }
        }

        IReadOnlyDictionary<string, string> files = new Dictionary<string, string>();
        try
        {
            files = PluginHash.Files(directory, _options.HashCache);
            plugin.Hash = PluginHash.Combine(files);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem ??= CoreText.T.Format("Plugin_LoadFailed", ex.Message);
        }

        if (problem is not null)
        {
            plugin.Problem = problem;
            plugin.State = incompatible ? PluginState.Incompatible : PluginState.Error;
            return plugin;
        }

        plugin.IsVerifiedBundled = origin == PluginOrigin.Bundled && _options.BundledList.Matches(plugin.Id, files);
        if (!plugin.IsEnabled)
        {
            plugin.State = PluginState.Disabled;
            return plugin;
        }

        if (plugin.IsVerifiedBundled)
        {
            plugin.State = PluginState.Pending;
            return plugin;
        }

        plugin.FilesChanged = origin == PluginOrigin.Bundled || _trust.HasOtherDecision(plugin.Id, plugin.Hash);
        plugin.State = _trust.Check(plugin.Id, plugin.Hash) switch
        {
            PluginTrust.Allowed => PluginState.Pending,
            PluginTrust.Denied => PluginState.Refused,
            _ => PluginState.NeedsApproval,
        };
        if (plugin.State != PluginState.NeedsApproval)
        {
            plugin.FilesChanged = false;
        }

        return plugin;
    }

    private static IModule CreateModule(PluginInfo plugin)
    {
        var manifest = plugin.Manifest!;
        var entry = Path.Combine(plugin.Directory, manifest.EntryAssembly);
        var context = new PluginLoadContext(plugin.Id, entry);

        Assembly assembly;
        Type[] types;
        try
        {
            assembly = context.LoadFromAssemblyPath(entry);
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            var reason = ex.LoaderExceptions.FirstOrDefault(e => e is not null)?.Message ?? ex.Message;
            throw new PluginException(CoreText.T.Format("Plugin_DependencyMissing", reason), inner: ex);
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or FileNotFoundException)
        {
            throw new PluginException(CoreText.T.Format("Plugin_LoadFailed", ex.Message), inner: ex);
        }

        var modules = types.Where(t => t is { IsClass: true, IsAbstract: false, IsPublic: true } && typeof(IModule).IsAssignableFrom(t)).ToList();
        Type type;
        if (manifest.EntryType is { } wanted)
        {
            type = modules.FirstOrDefault(t => string.Equals(t.FullName, wanted, StringComparison.Ordinal))
                ?? throw new PluginException(CoreText.T.Format("Plugin_EntryTypeMissing", wanted));
        }
        else if (modules.Count == 1)
        {
            type = modules[0];
        }
        else
        {
            throw new PluginException(modules.Count == 0
                ? CoreText.T.Format("Plugin_NoModule", manifest.EntryAssembly)
                : CoreText.T.Format("Plugin_SeveralModules", manifest.EntryAssembly, string.Join(", ", modules.Select(t => t.FullName))));
        }

        if (type.GetConstructor(Type.EmptyTypes) is null)
        {
            throw new PluginException(CoreText.T.Format("Plugin_NoModule", manifest.EntryAssembly));
        }

        IModule module;
        string id;
        try
        {
            module = (IModule)Activator.CreateInstance(type)!;
            id = module.Id;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is { } inner)
        {
            throw Unwrap(inner);
        }
        catch (Exception ex)
        {
            throw Unwrap(ex);
        }

        if (!string.Equals(id, manifest.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new PluginException(CoreText.T.Format("Plugin_IdMismatch", id, manifest.Id));
        }

        return module;

        static PluginException Unwrap(Exception ex) => ex is FileNotFoundException or FileLoadException or TypeLoadException or MissingMethodException
            ? new PluginException(CoreText.T.Format("Plugin_DependencyMissing", ex.Message), inner: ex)
            : new PluginException(CoreText.T.Format("Plugin_ConstructorFailed", ex.Message), inner: ex);
    }

    private void Fail(PluginInfo plugin, string problem, Exception? exception)
    {
        plugin.Module = null;
        plugin.State = PluginState.Error;
        plugin.Problem = problem;
        _log.Error(plugin.Id, $"Plugin not loaded: {problem}", exception);
    }
}
