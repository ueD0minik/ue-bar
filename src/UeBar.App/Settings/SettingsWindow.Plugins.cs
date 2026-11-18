// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UeBar.App.Localization;
using UeBar.App.Plugins;
using UeBar.Core.Logging;
using UeBar.Core.Modules;
using UeBar.Core.Plugins;
using UeBar.Sdk;
using static UeBar.App.Settings.SettingsUi;

namespace UeBar.App.Settings;

/// <summary>
/// Page "Plugins" (SPEC 4.4 / 11): what comes with ue Bar and what the user added, with status,
/// version, author and license; switch on/off, details, open folder, add (ZIP or folder), remove.
/// Loading happens only when the app starts, so changes that need it show "Restart now".
/// </summary>
internal sealed partial class SettingsWindow
{
    private readonly HashSet<string> _expandedPlugins = new(StringComparer.OrdinalIgnoreCase);

    private void BuildPlugins(StackPanel page)
    {
        page.Children.Add(Heading(Loc.Get("Settings_Plugins")));
        var body = new StackPanel();
        void Fill()
        {
            body.Children.Clear();
            AddBusRequests(body);
            var plugins = _host.Plugins;
            if (plugins.RestartNeeded)
            {
                var restart = Button(Loc.Get("Plugins_RestartNow"), () => _ = _host.Restart(), accent: true, icon: "");
                body.Children.Add(Card(Row(Loc.Get("Plugins_RestartNeeded"), null, restart)));
            }

            body.Children.Add(Section(Loc.Get("Plugins_Bundled")));
            foreach (var plugin in plugins.Plugins.Where(p => p.Origin == PluginOrigin.Bundled))
            {
                body.Children.Add(PluginCard(plugin, Fill));
            }

            body.Children.Add(Section(Loc.Get("Plugins_Installed")));
            var installed = plugins.Plugins.Where(p => p.Origin == PluginOrigin.Installed).ToList();
            foreach (var plugin in installed)
            {
                body.Children.Add(PluginCard(plugin, Fill));
            }

            if (installed.Count == 0)
            {
                body.Children.Add(Card(Hint(Loc.Get("Plugins_NoneInstalled"))));
            }

            var add = Buttons(
                Button(Loc.Get("Plugins_Add"), () => AddPlugin(folder: false, Fill), accent: true, icon: ""),
                Button(Loc.Get("Plugins_AddFolder"), () => AddPlugin(folder: true, Fill), icon: ""),
                Button(Loc.Get("Plugins_OpenUserFolder"), () => OpenFolder(plugins.Options.UserDirectory), icon: ""));
            body.Children.Add(Card(Row(Loc.Get("Plugins_Add_Title"), Loc.Get("Plugins_Add_Hint"), add, controlBelow: true)));
        }

        Fill();
        _onRefresh = Fill;
        page.Children.Add(body);
        page.Children.Add(Hint(Loc.Get("Plugins_Hint")));
    }

    private static TextBlock Section(string text) => new()
    {
        Text = text,
        FontSize = 13,
        FontWeight = FontWeights.SemiBold,
        Foreground = Res("SubTextBrush"),
        Margin = new Thickness(6, 6, 0, 8),
    };

    private Border PluginCard(PluginInfo plugin, Action refresh)
    {
        var host = _host.Plugins;
        var module = plugin.IsDuplicate ? null : _host.Registry.Find(plugin.Id);
        var loaded = plugin.State == PluginState.Loaded && module is { IsLoaded: true };
        var enabledNow = host.IsEnabledNow(plugin);
        var usable = plugin.State is not (PluginState.Error or PluginState.Incompatible);

        // Header: dot, name, badges
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 10,
            Height = 10,
            Fill = StateBrush(DotState(plugin, module, loaded)),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        header.Children.Add(new TextBlock { Text = plugin.DisplayName, FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        if (module is { IsLoaded: true, IsDeveloper: true })
        {
            header.Children.Add(Badge(Loc.Get("Modules_Developer")));
        }

        var info = new StackPanel();
        info.Children.Add(header);
        if (plugin.Manifest is { } manifest)
        {
            info.Children.Add(Hint(Loc.F(
                "Plugins_Meta",
                manifest.Version,
                manifest.Author.Length > 0 ? manifest.Author : Loc.Get("Plugins_UnknownAuthor"),
                manifest.License.Length > 0 ? manifest.License : Loc.Get("Plugins_NoLicense")), 4));
        }

        info.Children.Add(Hint(StatusText(plugin, module, loaded, enabledNow), 4));
        if (PendingText(plugin, loaded, enabledNow) is { } pending)
        {
            var note = Hint(pending, 4);
            note.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            info.Children.Add(note);
        }

        // Details (folded)
        var details = new StackPanel { Margin = new Thickness(0, 10, 0, 0), Visibility = _expandedPlugins.Contains(plugin.Directory) ? Visibility.Visible : Visibility.Collapsed };
        if (plugin.Manifest is { } m)
        {
            if (m.Description().Length > 0)
            {
                details.Children.Add(new TextBlock { Text = m.Description(), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
            }

            details.Children.Add(DetailLine(Loc.Get("Plugins_Detail_Id"), plugin.Id));
            details.Children.Add(DetailLine(Loc.Get("Plugins_Detail_Api"), Loc.F("Plugins_Detail_ApiValue", m.SdkVersionText, CoreApiVersion.Text)));
            if (m.Homepage.Length > 0)
            {
                details.Children.Add(DetailLine(Loc.Get("Plugins_Detail_Homepage"), m.Homepage));
            }
        }

        details.Children.Add(DetailLine(Loc.Get("Plugins_Detail_Folder"), plugin.Directory));
        if (plugin.Hash.Length > 0)
        {
            var checksum = plugin.Hash[..Math.Min(16, plugin.Hash.Length)] + "…";
            if (plugin.Origin == PluginOrigin.Bundled)
            {
                checksum += " · " + Loc.Get(plugin.IsVerifiedBundled ? "Plugins_Detail_Verified" : "Plugins_Detail_Changed");
            }

            details.Children.Add(DetailLine(Loc.Get("Plugins_Detail_Checksum"), checksum));
        }

        // Actions
        // Wraps: an installed plugin has up to six buttons, more than fit next to each other.
        var actions = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        void Add(Button button, bool enabled = true)
        {
            button.Margin = new Thickness(0, 0, 8, 8);
            button.IsEnabled = enabled;
            actions.Children.Add(button);
        }

        if (loaded)
        {
            Add(Button(Loc.Get("Settings_Title"), () => ShowPage("module:" + plugin.Id), icon: ""));
            Add(Button(Loc.Get("Modules_Restart"), async () => await _host.Registry.RestartModuleAsync(plugin.Id), icon: ""), module!.IsEnabled);
        }

        if (plugin.State is PluginState.Refused or PluginState.NeedsApproval && !plugin.IsDuplicate)
        {
            Add(Button(Loc.Get("Plugins_Allow"), () =>
            {
                if (PluginTrustDialog.Ask(this, PluginTrustRequest.From(plugin)))
                {
                    host.Decide(plugin, allow: true);
                    refresh();
                }
            }, icon: ""));
        }

        Add(Button(Loc.Get("Plugins_Details"), () =>
        {
            if (!_expandedPlugins.Remove(plugin.Directory))
            {
                _expandedPlugins.Add(plugin.Directory);
            }

            details.Visibility = _expandedPlugins.Contains(plugin.Directory) ? Visibility.Visible : Visibility.Collapsed;
        }, icon: ""));
        Add(Button(Loc.Get("Plugins_OpenFolder"), () => OpenFolder(plugin.Directory), icon: ""));
        if (plugin.Origin == PluginOrigin.Installed)
        {
            Add(Button(Loc.Get("Plugins_Remove"), () => RemovePlugin(plugin, refresh), icon: ""), !plugin.RemovalPending);
        }

        info.Children.Add(actions);
        info.Children.Add(details);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(info);
        var toggle = Switch(usable && !plugin.RemovalPending && (loaded ? module!.IsEnabled : enabledNow), async on =>
        {
            // Loaded: starts/stops right away (as before). Not loaded: only saved – the restart banner appears.
            await _host.Registry.SetEnabledAsync(plugin.Id, on);
            if (_host.Registry.Find(plugin.Id) is null)
            {
                _host.Settings.SetModuleEnabled(plugin.Id, on);
            }

            refresh();
        });
        toggle.IsEnabled = usable && !plugin.IsDuplicate && !plugin.RemovalPending;
        toggle.VerticalAlignment = VerticalAlignment.Top;
        toggle.ToolTip = Loc.Get("Modules_OnOff");
        Grid.SetColumn(toggle, 1);
        grid.Children.Add(toggle);
        return Card(grid);
    }

    private static ModuleState DotState(PluginInfo plugin, ModuleEntry? module, bool loaded) => plugin.State switch
    {
        PluginState.Loaded when loaded => module!.Status.State,
        PluginState.Error or PluginState.Incompatible => ModuleState.Error,
        PluginState.Pending => ModuleState.Starting,
        _ => ModuleState.Disabled,
    };

    private static string StatusText(PluginInfo plugin, ModuleEntry? module, bool loaded, bool enabledNow) => plugin.State switch
    {
        PluginState.Loaded when loaded => module!.Status.DisplayText
            + (module.LastError is { Length: > 0 } error && module.Status.State != ModuleState.Error ? $" · {error}" : ""),
        PluginState.Loaded => Loc.Get("Plugins_State_Loaded"),
        PluginState.Disabled => Loc.Get("Plugins_State_Disabled"),
        PluginState.Refused => Loc.Get("Plugins_State_NotAllowed"),
        PluginState.NeedsApproval => Loc.Get("Plugins_State_NeedsApproval"),
        PluginState.Incompatible => Loc.F("Plugins_State_Incompatible", plugin.Problem),
        PluginState.Error => Loc.F("Plugins_State_Error", plugin.Problem),
        _ => enabledNow ? Loc.Get("Plugins_Pending_Load") : Loc.Get("Plugins_State_Disabled"),
    };

    /// <summary>What happens to this plugin on the next start – null if nothing is waiting.</summary>
    private static string? PendingText(PluginInfo plugin, bool loaded, bool enabledNow)
    {
        if (plugin.RemovalPending)
        {
            return Loc.Get("Plugins_Pending_Remove");
        }

        if (plugin.UpdatePendingVersion is { } version)
        {
            return Loc.F("Plugins_Pending_Update", version);
        }

        return plugin.State switch
        {
            PluginState.Disabled when enabledNow && !plugin.IsDuplicate => Loc.Get("Plugins_Pending_Load"),
            PluginState.Loaded when loaded && !enabledNow => Loc.Get("Plugins_Pending_Off"),
            _ => null,
        };
    }

    private static Grid DetailLine(string label, string value)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = Res("SubTextBrush") });
        var text = new TextBlock { Text = value, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    /// <summary>"Add plugin …": pick a ZIP (or a folder), check it, ask, copy it to the user's plugin folder.</summary>
    private void AddPlugin(bool folder, Action refresh)
    {
        string? source;
        if (folder)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Loc.Get("Plugins_PickFolder") };
            source = dialog.ShowDialog(this) == true ? dialog.FolderName : null;
        }
        else
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = Loc.Get("Plugins_Add_Title"), Filter = Loc.Get("Plugins_ZipFilter"), CheckFileExists = true };
            source = dialog.ShowDialog(this) == true ? dialog.FileName : null;
        }

        if (source is null)
        {
            return;
        }

        var host = _host.Plugins;
        try
        {
            using var package = host.Installer.Open(source);
            var name = package.Manifest.DisplayName();
            if (!PluginTrustDialog.Ask(this, PluginTrustRequest.From(package, source, host.IsChanged(package))))
            {
                _host.Log.Info(null, $"Plugin '{package.Manifest.Id}' from {Path.GetFileName(source)}: not allowed by the user, not added");
                MessageBox.Show(this, Loc.F("Plugins_NotAdded", name), "ue Bar", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Adding means "I want to use it": on, even if a plugin with this ID was switched off before.
            _host.Settings.SetModuleEnabled(package.Manifest.Id, true);
            host.Install(package);
            refresh();
        }
        catch (PluginException ex)
        {
            _host.Log.Warn(null, $"Plugin from {Path.GetFileName(source)} not added: {ex.Message}");
            MessageBox.Show(this, ex.Message, Loc.Get("Plugins_AddFailed"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void RemovePlugin(PluginInfo plugin, Action refresh)
    {
        try
        {
            if (MessageBox.Show(this, Loc.F("Plugins_Remove_Confirm", plugin.DisplayName), "ue Bar", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            {
                return;
            }

            // A running module is stopped now; its files go on the next start.
            if (plugin.State == PluginState.Loaded && !plugin.IsDuplicate)
            {
                await _host.Registry.StopModuleAsync(plugin.Id);
            }

            _host.Plugins.Uninstall(plugin);
            refresh();
        }
        catch (PluginException ex)
        {
            MessageBox.Show(this, ex.Message, "ue Bar", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            _host.Log.Error(null, "Removing a plugin failed", ex);
        }
    }
}
