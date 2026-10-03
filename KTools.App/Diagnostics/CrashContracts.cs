using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using KTools_App.Core;

namespace KTools_App.Diagnostics;

public interface ICrashCoordinator
{
    Guid SessionId { get; }

    bool IsControlledShutdownStarted { get; }

    string? LastControlledShutdownCrashId { get; }

    CrashRecord CreateRecord(
        string source,
        Exception? exception,
        string? reason,
        LogLevel level,
        CrashEventKind kind,
        bool emergencyRequired,
        string? crashId = null);

    CrashWriteResult Record(CrashRecord record);

    CrashWriteResult RecordAppDomainTerminating(
        Exception? exception,
        bool isTerminating,
        string? crashId = null,
        IReadOnlyDictionary<string, object?>? properties = null);

    bool TryRecordUnobservedTaskException(AggregateException exception, out CrashWriteResult result);

    bool TryHandleWinUiUnhandled(
        Exception? exception,
        string source,
        Func<bool> initiateShutdown,
        out CrashWriteResult? result,
        string? reason = null);

    bool TryBeginControlledShutdown(
        string reason,
        Func<bool> initiateShutdown,
        out CrashShutdownStartResult result);

    Task<CrashShutdownResult> RunControlledShutdownStagesAsync(
        string reason,
        Func<CancellationToken, Task<bool>>? stopOperations,
        Func<CancellationToken, Task<bool>>? stopWatcher,
        Func<CancellationToken, Task<ProcessTerminationSummary>>? terminateProcesses,
        Func<CancellationToken, Task<bool>>? flush,
        Func<Task<bool>>? disposeServices,
        Func<Task<bool>>? requestExit,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<SettingsPersistenceResult>>? persistSettings,
        Func<CrashShutdownResult, CancellationToken, Task>? writeTerminalEvent);

    void RecordShutdownOutcome(CrashShutdownResult result);
}
