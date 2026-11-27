// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Diagnostics;
using System.Globalization;

namespace UeBar.Bar;

/// <summary>
/// How a hang dump is written (P2-7): by a second, hidden start of the own exe (<c>ueBar.exe --hang-dump &lt;pid&gt;
/// &lt;file&gt;</c>), which writes the dump of the hanging process from outside and ends. Before, the runtime started its
/// console program <c>createdump.exe</c> for that – and Windows opened a terminal window for it (seen after waking up,
/// 25./26.11.). The exe is a Windows program (no console) and is started with <see cref="ProcessStartInfo.CreateNoWindow"/>
/// and a hidden window, so nothing appears on the screen.
/// </summary>
public static class HangDumpCommand
{
    public const string Argument = "--hang-dump";

    /// <summary>The start of the helper – no window, no shell, nothing visible.</summary>
    public static ProcessStartInfo StartInfo(string exePath, int processId, string dumpPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(dumpPath);
        var info = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            ErrorDialog = false,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? "",
        };
        info.ArgumentList.Add(Argument);
        info.ArgumentList.Add(processId.ToString(CultureInfo.InvariantCulture));
        info.ArgumentList.Add(dumpPath);
        return info;
    }

    /// <summary>The helper's arguments (the first ones after the exe), otherwise false – a normal start.</summary>
    public static bool TryParse(IReadOnlyList<string> args, out int processId, out string dumpPath)
    {
        processId = 0;
        dumpPath = "";
        if (args is null || args.Count < 3 || !string.Equals(args[0], Argument, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out processId) || processId <= 0
            || string.IsNullOrWhiteSpace(args[2]) || !Path.IsPathFullyQualified(args[2]))
        {
            processId = 0;
            return false;
        }

        dumpPath = args[2];
        return true;
    }
}
