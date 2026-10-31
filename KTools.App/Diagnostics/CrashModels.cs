// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

using KTools_App.Core;

namespace KTools_App.Diagnostics;

public enum CrashEventKind
{
    WinUiUnhandled,
    AppDomainTerminating,
    UnobservedTask,
    ControlledShutdown
}

public sealed record CrashRecord
{
    public CrashRecord(
        string crashId,
        Guid sessionId,
        string source,
        DateTimeOffset timestampUtc,
        string reason,
        LogLevel level,
        CrashEventKind kind,
        Exception? exception,
        bool emergencyRequired,
        string? relatedCrashId = null)
    {
        CrashId = crashId;
        SessionId = sessionId;
        Source = source;
        TimestampUtc = timestampUtc;
        Reason = reason;
        Level = level;
        Kind = kind;
        Exception = exception;
        EmergencyRequired = emergencyRequired;
        RelatedCrashId = string.IsNullOrWhiteSpace(relatedCrashId) ? null : relatedCrashId;
    }

    public string CrashId { get; }

    public Guid SessionId { get; }

    public string Source { get; }

    public DateTimeOffset TimestampUtc { get; }

    public string Reason { get; }

    public LogLevel Level { get; }

    public CrashEventKind Kind { get; }

    public Exception? Exception { get; }

    public bool EmergencyRequired { get; }

    public string? RelatedCrashId { get; }
}

public sealed record CrashWriteResult
{
    public CrashWriteResult(
        CrashRecord record,
        bool emergencyWritten,
        bool structuredWriteSucceeded,
        bool duplicate,
        bool controlledShutdownInitiated,
        string? errorCode = null,
        bool retried = false)
    {
        Record = record;
        EmergencyWritten = emergencyWritten;
        StructuredWriteSucceeded = structuredWriteSucceeded;
        Duplicate = duplicate;
        ControlledShutdownInitiated = controlledShutdownInitiated;
        ErrorCode = errorCode;
        Retried = retried;
    }

    public CrashRecord Record { get; }

    public bool EmergencyWritten { get; }

    public bool StructuredWriteSucceeded { get; }

    public bool Duplicate { get; }

    public bool ControlledShutdownInitiated { get; }

    public string? ErrorCode { get; }

    public bool Retried { get; }

    public bool RequiredWriteSucceeded => Record.EmergencyRequired
        ? EmergencyWritten
        : StructuredWriteSucceeded;

    public bool Succeeded => RequiredWriteSucceeded;
}

public sealed record CrashShutdownStartResult
{
    public CrashShutdownStartResult(
        bool initiated,
        bool alreadyInProgress,
        string? errorCode = null)
    {
        Initiated = initiated;
        AlreadyInProgress = alreadyInProgress;
        ErrorCode = errorCode;
    }

    public bool Initiated { get; }

    public bool AlreadyInProgress { get; }

    public bool Accepted => Initiated || AlreadyInProgress;

    public string? ErrorCode { get; }
}

public sealed record CrashShutdownResult
{
    public const string ErrorExitRequestPending = "exit-request-pending";
    public const string ErrorStageUnavailable = "shutdown-stage-unavailable";
    public const string ErrorCancelled = "shutdown-cancelled";
    public const string ErrorStageFailed = "shutdown-stage-failed";
    public const string ErrorOperationStopFailed = "operation-stop-failed";
    public const string ErrorSettingsPersistenceFailed = "settings-persistence-failed";
    public const string ErrorProcessTerminationFailed = "process-termination-failed";
    public const string ErrorWatcherStopFailed = "watcher-stop-failed";
    public const string ErrorLogFlushFailed = "log-flush-failed";
    public const string ErrorServiceDisposeFailed = "service-dispose-failed";
    public const string ErrorExitRequestFailed = "exit-request-failed";

    public CrashShutdownResult(
        bool shutdownRequested,
        bool operationsStopped,
        bool settingsPersisted,
        bool processesVerified,
        bool watcherStopped,
        bool flushSucceeded,
        bool servicesDisposed,
        bool exitRequested,
        string? errorCode = null,
        ProcessTerminationSummary? processSummary = null,
        IReadOnlyList<string>? executedStages = null)
    {
        ShutdownRequested = shutdownRequested;
        OperationsStopped = operationsStopped;
        SettingsPersisted = settingsPersisted;
        ProcessesVerified = processesVerified;
        WatcherStopped = watcherStopped;
        FlushSucceeded = flushSucceeded;
        ServicesDisposed = servicesDisposed;
        ExitRequested = exitRequested;
        ErrorCode = errorCode;
        ProcessSummary = processSummary;
        ExecutedStages = executedStages is null || executedStages.Count == 0
            ? Array.Empty<string>()
            : new ReadOnlyCollection<string>(executedStages.ToArray());
    }

    public bool ShutdownRequested { get; }

    public bool OperationsStopped { get; }

    public bool SettingsPersisted { get; }

    public bool ProcessesVerified { get; }

    public bool WatcherStopped { get; }

    public bool FlushSucceeded { get; }

    public bool ServicesDisposed { get; }

    public bool ExitRequested { get; }

    public ProcessTerminationSummary? ProcessSummary { get; }

    public IReadOnlyList<string> ExecutedStages { get; }

    public bool StagesSucceeded => ShutdownRequested
        && OperationsStopped
        && SettingsPersisted
        && ProcessesVerified
        && WatcherStopped
        && FlushSucceeded;

    public bool ExitRequestPending => !ExitRequested
        && string.Equals(ErrorCode, ErrorExitRequestPending, StringComparison.Ordinal);

    public bool Succeeded => StagesSucceeded && ServicesDisposed && ExitRequested;

    public string? ErrorCode { get; }
}

public sealed record SettingsPersistenceResult
{
    public SettingsPersistenceResult(bool persisted, bool isKnown, string? errorCode = null)
    {
        Persisted = persisted;
        IsKnown = isKnown;
        ErrorCode = errorCode;
    }

    public bool Persisted { get; }

    public bool IsKnown { get; }

    public string? ErrorCode { get; }

    public static SettingsPersistenceResult Unknown { get; } = new(false, false, "result-unavailable");

    public static SettingsPersistenceResult StageUnavailable { get; } =
        new(false, false, CrashShutdownResult.ErrorStageUnavailable);

    public static SettingsPersistenceResult Success { get; } = new(true, true);

    public static SettingsPersistenceResult Failure(string? errorCode = null) =>
        new(false, true, errorCode ?? "persistence-failed");
}

public sealed record MainWindowShutdownResult
{
    public MainWindowShutdownResult(
        bool settingsPersisted,
        bool processesVerified,
        bool flushSucceeded,
        bool watcherStopped,
        LogLevel level,
        LogStatus status)
    {
        SettingsPersisted = settingsPersisted;
        ProcessesVerified = processesVerified;
        FlushSucceeded = flushSucceeded;
        WatcherStopped = watcherStopped;
        Level = level;
        Status = status;
    }

    public bool SettingsPersisted { get; }

    public bool ProcessesVerified { get; }

    public bool FlushSucceeded { get; }

    public bool WatcherStopped { get; }

    public LogLevel Level { get; }

    public LogStatus Status { get; }

    public bool Succeeded => SettingsPersisted && ProcessesVerified && FlushSucceeded && WatcherStopped;
}
