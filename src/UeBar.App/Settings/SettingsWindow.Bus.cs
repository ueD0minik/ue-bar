// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Windows;
using System.Windows.Controls;
using UeBar.App.Localization;
using UeBar.Core.Bus;
using UeBar.Core.Modules;
using UeBar.Sdk;
using static UeBar.App.Settings.SettingsUi;

namespace UeBar.App.Settings;

/// <summary>
/// Permissions of the module bus (SPEC 4.7, P2-3): on a plugin's page, what it may use of the other
/// plugins – per provider and kind (actions, events, values), default everything off. Open requests
/// appear with "Allow". Generic: the page knows no plugin by name.
/// </summary>
internal sealed partial class SettingsWindow
{
    private static readonly (BusAccess Kind, string Label)[] BusKinds =
    [
        (BusAccess.Actions, "Bus_Actions"),
        (BusAccess.Events, "Bus_Events"),
        (BusAccess.Values, "Bus_Values"),
    ];

    /// <summary>Card "May use" for <paramref name="consumer"/> (nothing if no other plugin publishes anything and nothing is asked).</summary>
    private void AddBusPermissions(StackPanel page, ModuleEntry consumer)
    {
        foreach (var request in RequestsOf(consumer.Id))
        {
            page.Children.Add(RequestCard(request));
        }

        var byProvider = _host.Bus.GetAllPublications()
            .Where(p => !string.Equals(p.OwnerModuleId, consumer.Id, StringComparison.OrdinalIgnoreCase))
            .GroupBy(p => p.OwnerModuleId, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => DisplayNameOf(g.Key), StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var rows = new List<UIElement>
        {
            Row(Loc.Get("Bus_MayUse"), Loc.F("Bus_MayUse_Hint", consumer.DisplayName), null),
        };
        if (byProvider.Count == 0)
        {
            rows.Add(Hint(Loc.Get("Bus_NothingPublished")));
        }

        foreach (var provider in byProvider)
        {
            var granted = _host.Settings.GetBusGrant(consumer.Id, provider.Key);
            var list = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            foreach (var (kind, label) in BusKinds)
            {
                var items = provider.Where(p => p.Kind == kind).OrderBy(p => p.Id, StringComparer.Ordinal).ToList();
                if (items.Count == 0)
                {
                    continue;
                }

                var detail = string.Join(Environment.NewLine, items.Select(p => string.IsNullOrWhiteSpace(p.Description) ? p.Id : $"{p.Id} – {p.Description}"));
                var providerId = provider.Key;
                var row = SwitchRow(Loc.Get(label), detail, (granted & kind) == kind, on =>
                {
                    var now = _host.Settings.GetBusGrant(consumer.Id, providerId);
                    _host.Settings.SetBusGrant(consumer.Id, providerId, on ? now | kind : now & ~kind);
                    ShowPage(_currentPage); // the plugin's own fields may offer what it may use now
                });
                row.Margin = new Thickness(0, 2, 0, 2);
                list.Children.Add(row);
            }

            rows.Add(Row(DisplayNameOf(provider.Key), null, list, controlBelow: true));
        }

        page.Children.Add(Card([.. rows]));
    }

    /// <summary>Requests of all plugins (overview page "Plugins").</summary>
    private void AddBusRequests(StackPanel page)
    {
        foreach (var request in _host.Settings.BusPolicy.PendingRequests)
        {
            page.Children.Add(RequestCard(request));
        }
    }

    private IEnumerable<BusPermissionRequest> RequestsOf(string consumer) =>
        _host.Settings.BusPolicy.PendingRequests.Where(r => string.Equals(r.ConsumerModuleId, consumer, StringComparison.OrdinalIgnoreCase));

    private Border RequestCard(BusPermissionRequest request)
    {
        var kinds = string.Join(", ", BusKinds.Where(k => (request.Access & k.Kind) == k.Kind).Select(k => Loc.Get(k.Label)));
        var text = Loc.F("Bus_Request", DisplayNameOf(request.ConsumerModuleId), kinds, DisplayNameOf(request.ProviderModuleId));
        var allow = Button(Loc.Get("Bus_Allow"), () =>
        {
            var now = _host.Settings.GetBusGrant(request.ConsumerModuleId, request.ProviderModuleId);
            _host.Settings.SetBusGrant(request.ConsumerModuleId, request.ProviderModuleId, now | request.Access);
            ShowPage(_currentPage);
        }, accent: true);
        var decline = Button(Loc.Get("Bus_Decline"), () =>
        {
            _host.Settings.BusPolicy.SettleRequests(request.ConsumerModuleId, request.ProviderModuleId);
            ShowPage(_currentPage);
        });
        decline.Margin = new Thickness(8, 0, 0, 0);
        return Card(Row(text, request.Reason, Buttons(allow, decline), controlBelow: true));
    }

    private string DisplayNameOf(string moduleId) => _host.Registry.Find(moduleId)?.DisplayName ?? moduleId;
}
