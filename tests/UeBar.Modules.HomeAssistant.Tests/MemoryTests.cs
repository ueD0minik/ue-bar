// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Text.Json;
using System.Text.Json.Nodes;
using UeBar.Core.Layout;

namespace UeBar.Modules.HomeAssistant.Tests;

/// <summary>M5-Fix 1: large answers are read directly into lean records.</summary>
public class MemoryTests
{
    [Fact]
    public async Task Large_registry_is_read_completely_across_buffer_growth()
    {
        await using var server = new FakeHaServer();
        for (var i = 0; i < 2500; i++)
        {
            server.AddState($"light.lampe_{i:0000}", "off", $"Lampe mit einem recht langen Namen Nummer {i:0000}");
        }

        server.RegistryOverrides["light.lampe_0007"] = new JsonObject { ["area_id"] = "zimmer", ["entity_category"] = "config", ["hidden_by"] = "user" };
        server.RegistryOverrides["light.lampe_0008"] = new JsonObject { ["disabled_by"] = "user" };
        await using var client = new RunningClient(new HaSettings(server.BaseUrl, null, "good-token", true));

        Assert.True(await client.WaitForAsync(HaConnectionState.ConnectedLocal));

        var index = client.Client.Index;
        Assert.Equal(2499, index.Count); // disabled ones are missing
        var entry = index.Get("light.lampe_0007")!;
        Assert.Equal("Office", entry.Area);
        Assert.Equal("zimmer", entry.AreaId);
        Assert.Equal("config", entry.EntityCategory);
        Assert.True(entry.Hidden);
        Assert.Null(index.Get("light.lampe_0008"));
        Assert.Equal("Lampe mit einem recht langen Namen Nummer 2499", index.Get("light.lampe_2499")!.Name);
    }

    [Fact]
    public void Registry_parsing_keeps_only_needed_fields_and_composes_device_names()
    {
        using var entities = JsonDocument.Parse("""
            [
              { "entity_id": "sensor.t", "name": null, "original_name": "Temperatur", "has_entity_name": true, "device_id": "d1",
                "area_id": null, "icon": null, "original_icon": "mdi:thermometer", "disabled_by": null, "hidden_by": null,
                "entity_category": null, "options": { "sensor": { "suggested_display_precision": 1 } }, "unique_id": "x", "platform": "y" },
              { "entity_id": "switch.s", "name": "Eigener Name", "original_name": "egal", "has_entity_name": true, "device_id": "d1",
                "area_id": "kueche", "disabled_by": null, "hidden_by": null, "entity_category": "diagnostic" }
            ]
            """);
        using var devices = JsonDocument.Parse("""[ { "id": "d1", "name": "Sensor", "name_by_user": "Fenster", "area_id": "zimmer", "manufacturer": "x" } ]""");
        using var areas = JsonDocument.Parse("""[ { "area_id": "zimmer", "name": "Zimmer" }, { "area_id": "kueche", "name": "Küche", "icon": "mdi:stove" } ]""");
        var index = new EntityIndex();

        index.Load(EntityIndex.ParseRegistry(entities.RootElement), EntityIndex.ParseDevices(devices.RootElement), EntityIndex.ParseAreas(areas.RootElement), EntityIndexSource.Registry);

        var temp = index.Get("sensor.t")!;
        Assert.Equal("Fenster Temperatur", temp.Name); // device name + entity name
        Assert.Equal("Zimmer", temp.Area); // area through the device
        Assert.Equal(1, temp.DisplayPrecision);
        Assert.Equal("mdi:thermometer", temp.Icon);
        var plug = index.Get("switch.s")!;
        Assert.Equal("Eigener Name", plug.Name); // own name wins
        Assert.Equal("Küche", plug.Area);
        Assert.Equal("diagnostic", plug.EntityCategory);
        Assert.Equal("mdi:stove", index.Areas.Single(a => a.Id == "kueche").Icon);
    }

    [Fact]
    public async Task Command_results_are_detached_not_copied_and_errors_still_arrive()
    {
        await using var server = new FakeHaServer();
        server.AddState("light.desk", "on", "Desk");
        server.Unauthorized.Add("config/device_registry/list");
        await using var client = new RunningClient(new HaSettings(server.BaseUrl, null, "good-token", true));
        Assert.True(await client.WaitForAsync(HaConnectionState.ConnectedLocal));

        var states = await client.Client.SendCommandAsync(new JsonObject { ["type"] = "get_states" }, CancellationToken.None);
        var denied = await client.Client.SendCommandAsync(new JsonObject { ["type"] = "config/device_registry/list" }, CancellationToken.None);

        Assert.True(states.Success);
        Assert.Null(states.Result!.Parent); // taken out of the message
        Assert.Equal("light.desk", states.Result[0]!["entity_id"]!.ToString());
        Assert.False(denied.Success);
        Assert.Equal("unauthorized", denied.ErrorCode);
        Assert.Contains(client.Log.Lines, l => l.Contains("Device registry not readable (unauthorized)", StringComparison.Ordinal));
    }
}
