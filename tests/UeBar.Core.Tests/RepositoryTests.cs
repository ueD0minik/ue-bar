// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Text.RegularExpressions;

namespace UeBar.Core.Tests;

/// <summary>Rules for the public repository: license headers, license file, no company names.</summary>
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
