// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Globalization;
using System.Text;
using UeBar.Sdk;

namespace UeBar.Core.Logging;

/// <summary>
/// Rolling file log: one file per day (uebar-YYYYMMDD.log), files older than
/// <see cref="RetentionDays"/> days are deleted. Thread-safe, no background timer.
/// </summary>
public sealed class FileLogger : IUeLogger
{
    public const int RetentionDays = 7;

    private readonly string _directory;
    private readonly TimeProvider _time;
    private readonly LogLevel _minimumLevel;
    private readonly Lock _lock = new();
    private DateOnly _currentDay;

    public FileLogger(string directory, LogLevel minimumLevel = LogLevel.Info, TimeProvider? time = null)
    {
        _directory = directory;
        _minimumLevel = minimumLevel;
        _time = time ?? TimeProvider.System;
        Directory.CreateDirectory(_directory);
    }

    public void Log(LogLevel level, string? moduleId, string message, Exception? exception = null)
    {
        if (level < _minimumLevel)
        {
            return;
        }

        var now = _time.GetLocalNow();
        var line = new StringBuilder()
            .Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
            .Append(" [").Append(level.ToString().ToUpperInvariant()).Append("] [")
            .Append(moduleId ?? "core").Append("] ")
            .Append(message);
        if (exception is not null)
        {
            line.AppendLine().Append(exception);
        }

        line.AppendLine();

        lock (_lock)
        {
            var day = DateOnly.FromDateTime(now.DateTime);
            try
            {
                if (day != _currentDay)
                {
                    _currentDay = day;
                    CleanupOldFiles(day);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            var text = line.ToString();
            var main = Path.Combine(_directory, $"uebar-{day:yyyyMMdd}.log");
            if (TryAppend(main, text, attempts: 3))
            {
                return;
            }

            // Main file locked (e.g. opened by another program) – do not lose entries silently,
            // switch to a separate file for this process instead.
            var fallback = Path.Combine(_directory, $"uebar-{day:yyyyMMdd}-p{Environment.ProcessId}.log");
            if (!_usingFallback)
            {
                _usingFallback = true;
                TryAppend(fallback, $"{now:yyyy-MM-dd HH:mm:ss.fff} [WARNING] [core] main log {Path.GetFileName(main)} not writable ({LastWriteError}) – writing here{Environment.NewLine}", 1);
            }

            TryAppend(fallback, text, 2);
        }
    }

    /// <summary>Last write error (for diagnostics), null if everything works.</summary>
    public string? LastWriteError { get; private set; }

    private bool _usingFallback;

    private bool TryAppend(string path, string text, int attempts)
    {
        for (var i = 0; i < attempts; i++)
        {
            try
            {
                File.AppendAllText(path, text);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging must never take the app down.
                LastWriteError = $"{ex.GetType().Name}: {ex.Message}";
                if (i < attempts - 1)
                {
                    Thread.Sleep(15);
                }
            }
        }

        return false;
    }

    private void CleanupOldFiles(DateOnly today)
    {
        var cutoff = today.AddDays(-(RetentionDays - 1));
        foreach (var file in Directory.EnumerateFiles(_directory, "uebar-*.log"))
        {
            // uebar-YYYYMMDD.log or uebar-YYYYMMDD-p<PID>.log
            var name = Path.GetFileNameWithoutExtension(file);
            if (name.Length >= 14
                && DateOnly.TryParseExact(name[6..14], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fileDay)
                && fileDay < cutoff)
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                }
            }
        }
    }
}
