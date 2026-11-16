// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Security.Cryptography;
using System.Text;
using UeBar.Core.Settings;

namespace UeBar.App.Infrastructure;

/// <summary>Encrypts secrets with DPAPI (only the current Windows user can read them).</summary>
internal sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = "ueBar.v1"u8.ToArray();

    public string Protect(string plainText)
    {
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plainText), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(data);
    }

    public string Unprotect(string protectedText)
    {
        var data = ProtectedData.Unprotect(Convert.FromBase64String(protectedText), Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(data);
    }
}
