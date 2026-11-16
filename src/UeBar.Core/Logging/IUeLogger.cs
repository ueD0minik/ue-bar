// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

namespace UeBar.Core.Logging;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>Simple log with a module ID per entry (SPEC 4.6). Never log secrets.</summary>
public interface IUeLogger
{
    void Log(LogLevel level, string? moduleId, string message, Exception? exception = null);
}

public static class UeLoggerExtensions
{
    public static void Info(this IUeLogger log, string? moduleId, string message) =>
        log.Log(LogLevel.Info, moduleId, message);

    public static void Warn(this IUeLogger log, string? moduleId, string message, Exception? ex = null) =>
        log.Log(LogLevel.Warning, moduleId, message, ex);

    public static void Error(this IUeLogger log, string? moduleId, string message, Exception? ex = null) =>
        log.Log(LogLevel.Error, moduleId, message, ex);

    public static void Debug(this IUeLogger log, string? moduleId, string message) =>
        log.Log(LogLevel.Debug, moduleId, message);

    /// <summary>Logger that adds a fixed module ID to every entry.</summary>
    public static IModuleLogger ForModule(this IUeLogger log, string moduleId) => new ScopedLogger(log, moduleId);

    private sealed class ScopedLogger(IUeLogger inner, string moduleId) : IModuleLogger
    {
        public void Log(LogLevel level, string message, Exception? exception = null) =>
            inner.Log(level, moduleId, message, exception);
    }
}

/// <summary>Logger a module gets – the module ID is added automatically.</summary>
public interface IModuleLogger
{
    void Log(LogLevel level, string message, Exception? exception = null);
}

/// <summary>Discards everything. For tests.</summary>
public sealed class NullLogger : IUeLogger
{
    public static NullLogger Instance { get; } = new();

    public void Log(LogLevel level, string? moduleId, string message, Exception? exception = null)
    {
    }
}
