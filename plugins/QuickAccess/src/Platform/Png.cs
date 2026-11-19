// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Buffers.Binary;
using System.IO.Compression;

namespace UeBar.Modules.QuickAccess.Platform;

/// <summary>
/// Minimal PNG encoder for icons (RGBA, no extras) – the app decodes the image, the plugin has no
/// image library and needs none for this.
/// </summary>
internal static class Png
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>
    /// 32-bit BGRA pixels (top-down) → PNG. Icons from the shell come premultiplied when they have soft
    /// edges; if no pixel is brighter than its alpha, the colors are divided back.
    /// </summary>
    public static byte[] FromBgra(byte[] bgra, int width, int height)
    {
        var premultiplied = true;
        var anyAlpha = false;
        for (var i = 0; i < bgra.Length; i += 4)
        {
            var a = bgra[i + 3];
            anyAlpha |= a != 0;
            if (bgra[i] > a || bgra[i + 1] > a || bgra[i + 2] > a)
            {
                premultiplied = false;
            }
        }

        var raw = new byte[height * ((width * 4) + 1)];
        var o = 0;
        for (var y = 0; y < height; y++)
        {
            raw[o++] = 0; // filter: none
            for (var x = 0; x < width; x++)
            {
                var i = ((y * width) + x) * 4;
                var a = anyAlpha ? bgra[i + 3] : (byte)255; // no alpha at all = an opaque bitmap
                byte r = bgra[i + 2], g = bgra[i + 1], b = bgra[i];
                if (premultiplied && anyAlpha && a is > 0 and < 255)
                {
                    r = (byte)Math.Min(255, r * 255 / a);
                    g = (byte)Math.Min(255, g * 255 / a);
                    b = (byte)Math.Min(255, b * 255 / a);
                }

                raw[o++] = r;
                raw[o++] = g;
                raw[o++] = b;
                raw[o++] = a;
            }
        }

        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 6; // RGBA
        Chunk(output, "IHDR", ihdr);
        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(raw);
            }

            Chunk(output, "IDAT", compressed.ToArray());
        }

        Chunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void Chunk(Stream output, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        output.Write(number);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);
        var crc = 0xFFFFFFFFu;
        foreach (var b in typeBytes.Concat(data))
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        BinaryPrimitives.WriteUInt32BigEndian(number, crc ^ 0xFFFFFFFFu);
        output.Write(number);
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (var n = 0u; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
