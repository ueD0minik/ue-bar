// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.ComponentModel;
using System.Runtime.InteropServices;

namespace UeBar.Modules.Clipboard;

/// <summary>Encrypts what the history stores (testable: tests can count calls, the real one is DPAPI).</summary>
internal interface IClipProtector
{
    byte[] Protect(byte[] data);

    /// <summary>Throws <see cref="CryptographicDataException"/> for data from another user/PC or broken data.</summary>
    byte[] Unprotect(byte[] data);
}

/// <summary>Data that could not be decrypted (another Windows user, another PC, broken file).</summary>
internal sealed class CryptographicDataException(string message) : Exception(message);

/// <summary>
/// DPAPI for the current Windows user (<c>CryptProtectData</c>, no extra package): only this user on this PC can read
/// the files. An own entropy keeps other apps of the same user from decrypting them by accident.
/// </summary>
internal sealed class DpapiProtector : IClipProtector
{
    private const int CryptProtectUiForbidden = 0x1;
    private static readonly byte[] Entropy = "ueBar.clipboard.v1"u8.ToArray();

    public byte[] Protect(byte[] data) => Run(data, protect: true);

    public byte[] Unprotect(byte[] data) => Run(data, protect: false);

    private static byte[] Run(byte[] data, bool protect)
    {
        var input = Blob.From(data);
        var entropy = Blob.From(Entropy);
        var output = default(Blob);
        try
        {
            var ok = protect
                ? CryptProtectData(ref input, null, ref entropy, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref output);
            if (!ok)
            {
                var error = Marshal.GetLastWin32Error();
                throw protect ? new Win32Exception(error) : new CryptographicDataException($"DPAPI error {error}");
            }

            var result = new byte[output.Length];
            if (output.Length > 0)
            {
                Marshal.Copy(output.Data, result, 0, output.Length);
            }

            return result;
        }
        finally
        {
            input.Free();
            entropy.Free();
            if (output.Data != IntPtr.Zero)
            {
                LocalFree(output.Data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob
    {
        public int Length;
        public IntPtr Data;

        public static Blob From(byte[] bytes)
        {
            var blob = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(Math.Max(1, bytes.Length)) };
            Marshal.Copy(bytes, 0, blob.Data, bytes.Length);
            return blob;
        }

        public void Free()
        {
            if (Data != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(Data);
                Data = IntPtr.Zero;
            }
        }
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob data, string? description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, ref Blob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob data, IntPtr description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, ref Blob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
