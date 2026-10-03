// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;

using KTools_App.Core;
using KTools_App.Diagnostics;

namespace KTools_App.Services.Contracts;

public interface ILogService : IDisposable
{
    event EventHandler<LogEvent>? LogReceived;

    void InitializeLogFile();

    void InitializeLogFile(string? customLogDir);

    void Log(LogLevel level, string message, string source = "System");

    void DebugLog(string message, string source = "System");

    void Info(string message, string source = "System");

    void Warn(string message, string source = "System");

    void Error(string message, string source = "System");

    void Fatal(string message, string source = "System");

    void Exception(Exception ex, string message, string source = "System");

    void Write(LogEvent logEvent);

    void Write(
        string eventId,
        LogLevel level,
        string message,
        string source = "System",
        LogStatus status = LogStatus.None,
        LogContext? context = null,
        IReadOnlyDictionary<string, object?>? properties = null,
        Exception? exception = null);

    void Write(
        string eventId,
        LogLevel level,
        LogStatus status,
        string message,
        Exception? exception = null,
        string source = "System",
        LogContext? context = null,
        IReadOnlyDictionary<string, object?>? properties = null);

    string ReadCurrentLog();

    IReadOnlyList<LogEvent> ReadRecentEvents(int maxCount);

    bool ClearCurrentLog();

    void Flush();

    bool Flush(TimeSpan timeout);

    LogLevel MinLevel { get; set; }

    LogServiceStatus Status { get; }

    string? EffectiveLogDirectory { get; }

    string? CurrentLogFile { get; }

    long QueuedEventCount { get; }

    long QueuedEventBytes { get; }

    long DroppedEventCount { get; }

    long WriteErrorCount { get; }

    long RotationErrorCount { get; }

    long RejectedEventCount { get; }

    long SubscriberErrorCount { get; }

    long SubscriberDroppedCount { get; }

    long DisposeErrorCount { get; }

    long ReadErrorCount { get; }
}
