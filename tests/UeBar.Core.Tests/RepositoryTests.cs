// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Text.RegularExpressions;

namespace UeBar.Core.Tests;

/// <summary>Rules for the public repository: license headers, license file, no company names.</summary>
[Trait("Category", "Slow")]
public sealed class RepositoryTests
{
    private static IEnumerable<string> AllFiles(params string[] extensions) =>
        Directory.EnumerateFiles(SourceTree.AppRoot, "*.*", SearchOption.AllDirectories)
            .Where(f => extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Where(f => !Regex.IsMatch(f, @"[\\/](bin|obj|dist|\.git)[\\/]"));

    [Fact]
    public void Every_source_file_has_the_license_header()
    {
        var missing = AllFiles(".cs", ".xaml", ".ps1", ".iss")
            .Where(f => !File.ReadAllText(f).AsSpan(0, Math.Min(400, (int)new FileInfo(f).Length)).Contains("SPDX-License-Identifier: GPL-3.0-or-later", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(SourceTree.AppRoot, f))
            .ToList();

        Assert.True(missing.Count == 0, "Without license header: " + string.Join(Environment.NewLine, missing));
    }

    /// <summary>
    /// P3-1: every message of the app goes through its own dialog (hidden from screen captures like every window of the
    /// app). The Windows message box is left only for the moments before the settings are loaded (<c>EarlyMessage</c>).
    /// </summary>
    [Fact]
    public void No_windows_message_box_outside_the_early_messages()
    {
        var allowed = Path.Combine("src", "UeBar.App", "Infrastructure", "UeDialog.cs");
        var found = AllFiles(".cs")
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"\bMessageBox\s*\.\s*Show\b"))
            .Select(f => Path.GetRelativePath(SourceTree.AppRoot, f))
            .Where(f => !string.Equals(f, allowed, StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(found.Count == 0, "MessageBox.Show outside EarlyMessage: " + string.Join(Environment.NewLine, found));
        var dialog = File.ReadAllText(Path.Combine(SourceTree.AppRoot, allowed));
        Assert.Single(Regex.Matches(dialog, @"\bMessageBox\.Show\b"));
        Assert.True(dialog.IndexOf("MessageBox.Show", StringComparison.Ordinal) > dialog.IndexOf("class EarlyMessage", StringComparison.Ordinal));
    }

    [Fact]
    public void License_file_is_the_gpl_3()
    {
        var text = File.ReadAllText(Path.Combine(SourceTree.AppRoot, "LICENSE"));
        Assert.Contains("GNU GENERAL PUBLIC LICENSE", text, StringComparison.Ordinal);
        Assert.Contains("Version 3, 29 June 2007", text, StringComparison.Ordinal);
        Assert.Contains("END OF TERMS AND CONDITIONS", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_name_is_never_used_as_a_company()
    {
        var company = new Regex(@"(Company|Publisher|AppPublisher|Copyright|manufacturer)[^\n]{0,20}ueniverse", RegexOptions.IgnoreCase);
        var found = AllFiles(".cs", ".props", ".csproj", ".iss", ".resx")
            .Where(f => Path.GetFileName(f) != "RepositoryTests.cs" && company.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(SourceTree.AppRoot, f))
            .ToList();

        Assert.Empty(found);
    }
}
