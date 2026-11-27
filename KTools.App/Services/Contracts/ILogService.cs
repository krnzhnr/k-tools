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

    void Write(LogEvent logEvent);

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
