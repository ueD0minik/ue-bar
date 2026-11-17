// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Text.Json.Nodes;

namespace UeBar.Modules.HomeAssistant.Tests;

/// <summary>
/// States/attributes of the example home, shaped like real get_states answers. The
/// proxy token in the cover link is replaced by a placeholder – never put real tokens into the repo.
/// </summary>
internal static class SampleStates
{
    public const string FakeProxyToken = "0000proxytoken0000";

    public static void AddAll(FakeHaServer server)
    {
        server.AddState("light.office_desk", "on", "Desk", Desk());
        server.AddState("light.office_ceiling", "off", "Deckenlampe", Json("""
            { "min_color_temp_kelvin": 2202, "max_color_temp_kelvin": 6535, "supported_color_modes": ["color_temp", "hs", "xy"],
              "color_mode": null, "brightness": null, "color_temp_kelvin": null, "supported_features": 32 }
            """));
        server.AddState("switch.office_ac_power", "off", "Office AC Power");
        server.AddState("number.office_ac_target", "18.0", "Office AC Target", Json("""
            { "min": 16, "max": 30, "step": 1, "mode": "auto", "unit_of_measurement": "°C" }
            """));
        server.AddState("select.office_ac_fan", "Auto", "Office AC Fan", Json("""
            { "options": ["Auto", "Low", "Medium", "High"] }
            """));
        server.AddState("media_player.monitor", "on", "Office Monitor", Json("""
            { "source_list": ["hdmi1", "hdmi2"], "volume_level": 0.4, "is_volume_muted": false, "source": "hdmi2", "supported_features": 23997 }
            """));
        foreach (var (id, name, icon, last) in new[]
        {
            ("script.focus", "Focus", "mdi:desk", "2026-08-09T19:05:19.217673+00:00"),
            ("script.movie_night", "Movie night", "mdi:movie", "2026-08-22T18:16:35.322666+00:00"),
            ("script.good_night", "Good night", "mdi:weather-night", "2026-11-04T21:48:27.492165+00:00"),
            ("script.leave_home", "Leave home", "mdi:exit-run", "2026-09-24T04:55:02.058218+00:00"),
            ("script.lock_pc", "Lock PC", "mdi:laptop-off", (string?)null),
        })
        {
            server.AddState(id, "off", name, new JsonObject { ["last_triggered"] = last, ["mode"] = "single", ["current"] = 0, ["icon"] = icon });
        }

        server.AddState("vacuum.robot", "docked", "Robot", Json("""
            { "software_version": "3.5.62+95", "status": null, "bin_present": true, "bin_full": false, "supported_features": 13084 }
            """));
        server.AddState("sensor.robot_battery", "90", "Robot Battery", Json("""
            { "unit_of_measurement": "%", "device_class": "battery", "state_class": "measurement" }
            """));
        server.AddState("media_player.living_room_tv", "off", "Living Room TV", Json("""
            { "source_list": ["HDMI 1", "Netflix", "YouTube"], "device_class": "tv", "supported_features": 24509 }
            """));
        server.AddState("media_player.office_pc", "playing", "OFFICE-PC", OfficePc());
        server.AddState("sensor.office_temperature", "20.2", "Office Temperature", Json("""
            { "state_class": "measurement", "unit_of_measurement": "°C", "device_class": "temperature" }
            """));
        server.AddState("weather.forecast_home", "clear-night", "Weather", Json("""
            { "temperature": 6.3, "dew_point": 3.4, "temperature_unit": "°C", "humidity": 82, "cloud_coverage": 8.6,
              "pressure": 1029.2, "pressure_unit": "hPa", "wind_speed": 11.2, "wind_speed_unit": "km/h", "supported_features": 3 }
            """));
    }

    public static JsonObject Desk() => Json("""
        { "min_color_temp_kelvin": 2702, "max_color_temp_kelvin": 5000, "supported_color_modes": ["color_temp", "hs", "xy"],
          "color_mode": "color_temp", "brightness": 102, "color_temp_kelvin": 2702, "hs_color": [28.391, 65.659],
          "rgb_color": [255, 167, 88], "xy_color": [0.524, 0.388], "supported_features": 32 }
        """);

    public static JsonObject OfficePc() => Json($$"""
        { "volume_level": 0.3, "is_volume_muted": false, "media_content_type": "music", "media_duration": 182.125,
          "media_position": 40.591, "media_position_updated_at": "2026-11-05T20:13:38.474025+00:00",
          "media_title": "Example Song", "media_artist": "Example Artist", "media_album_name": "Example Song",
          "device_class": "speaker",
          "entity_picture": "/api/media_player_proxy/media_player.office_pc?token={{FakeProxyToken}}&cache=0123456789abcdef",
          "supported_features": 153151 }
        """);

    public static JsonObject Json(string json) => (JsonObject)JsonNode.Parse(json)!;
}
