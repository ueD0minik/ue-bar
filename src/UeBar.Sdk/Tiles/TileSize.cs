// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UeBar.Sdk;

/// <summary>Tile size in the grid, in JSON as "2x1" (columns x rows).</summary>
[JsonConverter(typeof(TileSizeJsonConverter))]
public readonly record struct TileSize(int Columns, int Rows)
{
    public static readonly TileSize Small = new(1, 1);
    public static readonly TileSize Wide = new(2, 1);
    public static readonly TileSize Large = new(2, 2);

    public static IReadOnlyList<TileSize> All { get; } = [Small, Wide, Large];

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Columns}x{Rows}");

    public static bool TryParse(string? text, out TileSize size)
    {
        size = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Trim().ToLowerInvariant().Split('x');
        if (parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var c)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var r)
            && c > 0 && r > 0)
        {
            size = new TileSize(c, r);
            return true;
        }

        return false;
    }

    public static TileSize Parse(string text) =>
        TryParse(text, out var size) ? size : throw new FormatException($"Invalid tile size: '{text}'");
}

public sealed class TileSizeJsonConverter : JsonConverter<TileSize>
{
    public override TileSize Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString();
        return TileSize.TryParse(text, out var size) ? size : TileSize.Small;
    }

    public override void Write(Utf8JsonWriter writer, TileSize value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
