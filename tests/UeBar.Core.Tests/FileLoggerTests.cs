// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Core.Logging;

namespace UeBar.Core.Tests;

public class FileLoggerTests
{
    [Fact]
    public void Writes_line_with_module_id()
    {
        using var dir = new TempDirectory();
        var logger = new FileLogger(dir.Path);

        logger.Info("dummy", "Hallo");

        var file = Assert.Single(Directory.GetFiles(dir.Path, "uebar-*.log"));
        Assert.Contains("[INFO] [dummy] Hallo", File.ReadAllText(file));
    }

    [Fact]
    public void Locked_main_file_falls_back_to_process_file_instead_of_losing_lines()
    {
        using var dir = new TempDirectory();
        var logger = new FileLogger(dir.Path);
        logger.Info(null, "vorher");
        var main = Assert.Single(Directory.GetFiles(dir.Path, "uebar-*.log"));

        // Another program keeps the file open and only allows reading (like a locking editor).
        using (new FileStream(main, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            logger.Info(null, "während gesperrt");
        }

        var fallback = Assert.Single(Directory.GetFiles(dir.Path, $"uebar-*-p{Environment.ProcessId}.log"));
        var text = File.ReadAllText(fallback);
        Assert.Contains("während gesperrt", text);
        Assert.Contains("not writable", text);
        Assert.NotNull(logger.LastWriteError);

        logger.Info(null, "danach");
        Assert.Contains("danach", File.ReadAllText(main));
    }

    [Fact]
    public void Below_minimum_level_is_not_written()
    {
        using var dir = new TempDirectory();
        var logger = new FileLogger(dir.Path, LogLevel.Info);

        logger.Debug(null, "geheim");

        Assert.Empty(Directory.GetFiles(dir.Path));
    }
}
