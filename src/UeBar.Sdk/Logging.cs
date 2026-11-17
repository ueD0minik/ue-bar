// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

namespace UeBar.Sdk;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>
/// Logger a module gets (<see cref="IModuleContext.Log"/>) – the module ID is added automatically.
/// Entries go to <c>%APPDATA%\ueBar\logs</c>. Never log secrets.
/// </summary>
public interface IModuleLogger
{
    void Log(LogLevel level, string message, Exception? exception = null);
}

/// <summary>Shortcuts for <see cref="IModuleLogger"/>.</summary>
public static class ModuleLoggerExtensions
{
    public static void Debug(this IModuleLogger log, string message) => log.Log(LogLevel.Debug, message);

    public static void Info(this IModuleLogger log, string message) => log.Log(LogLevel.Info, message);

    public static void Warn(this IModuleLogger log, string message, Exception? exception = null) => log.Log(LogLevel.Warning, message, exception);

    public static void Error(this IModuleLogger log, string message, Exception? exception = null) => log.Log(LogLevel.Error, message, exception);
}
