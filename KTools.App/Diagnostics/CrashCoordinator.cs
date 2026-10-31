// -*- coding: utf-8 -*-
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using KTools_App.Core;
using KTools_App.Services.Contracts;

namespace KTools_App.Diagnostics;

public sealed class CrashCoordinator : ICrashCoordinator, IDisposable
{
    public const string WinUiUnhandledEventId = "app.unhandled_exception";
    public const string AppDomainTerminatingEventId = "app.domain_terminating";
    public const string UnobservedTaskEventId = "app.unobserved_task_exception";
    public const string ControlledShutdownEventId = "app.controlled_shutdown";
    public const string ControlledShutdownCompletedEventId = "app.controlled_shutdown.completed";
    public const string ControlledShutdownOutcomeEventId = "app.controlled_shutdown.outcome";
    public const string ControlledShutdownFailedEventId = "app.controlled_shutdown.failed";
    public const int MaxCrashIdLength = 32;
    public const int MaxReasonBytes = 512;
    public const string ErrorCoordinatorDisposed = "coordinator-disposed";
    public const string ErrorDisposedFallbackWritten = "coordinator-disposed-fallback-written";
    public const string ErrorDisposedFallbackFailed = "coordinator-disposed-fallback-failed";

    private static readonly TimeSpan StructuredFlushTimeout = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan ShutdownStageTimeout = TimeSpan.FromSeconds(2);

    private readonly EmergencyLogSink _emergencySink;
    private readonly ILogService? _logService;
    private readonly LogRedactor _redactor;
    private readonly Guid _sessionId;
    private readonly ConcurrentDictionary<string, CrashRecord> _records = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CrashWriteResult> _outcomes = new(StringComparer.Ordinal);
    private readonly ConditionalWeakTable<Exception, CrashIdHolder> _exceptionIds = new();
    private readonly object _writeGate = new();
    private int _disposed;
    private int _shutdownState;
    private int _redeliveryDisabled;
    private string? _lastCrashId;
    private string? _controlledShutdownCrashId;
    private long _emergencyAttempts;
    private long _emergencyFailures;
    private long _structuredAttempts;
    private long _structuredFailures;
    private long _duplicateCount;
    private long _retryCount;
    private long _disposedFallbackCount;
    private long _disposedFallbackFailures;
    private long _outcomeDroppedCount;

    public CrashCoordinator()
        : this(EmergencyLogSink.Shared, null)
    {
    }

    public CrashCoordinator(
        EmergencyLogSink emergencySink,
        ILogService? logService = null,
        Guid? sessionId = null)
    {
        _emergencySink = emergencySink ?? throw new ArgumentNullException(nameof(emergencySink));
        _logService = logService;
        _redactor = new LogRedactor();
        _sessionId = sessionId is { } value && value != Guid.Empty ? value : Guid.NewGuid();
    }

    public Guid SessionId => _sessionId;

    public long EmergencyAttempts => Interlocked.Read(ref _emergencyAttempts);

    public long EmergencyFailures => Interlocked.Read(ref _emergencyFailures);

    public long StructuredAttempts => Interlocked.Read(ref _structuredAttempts);

    public long StructuredFailures => Interlocked.Read(ref _structuredFailures);

    public long DuplicateCount => Interlocked.Read(ref _duplicateCount);

    public long RetryCount => Interlocked.Read(ref _retryCount);

    public long DisposedFallbackCount => Interlocked.Read(ref _disposedFallbackCount);

    public long DisposedFallbackFailureCount => Interlocked.Read(ref _disposedFallbackFailures);

    public long ShutdownOutcomeDroppedCount => Interlocked.Read(ref _outcomeDroppedCount);

    public long EmergencyWrittenCount => _emergencySink.WrittenCount;

    public long EmergencyDroppedCount => _emergencySink.DroppedCount;

    public string? LastCrashId => Volatile.Read(ref _lastCrashId);

    public string? LastControlledShutdownCrashId => Volatile.Read(ref _controlledShutdownCrashId);

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public bool IsControlledShutdownStarted => Volatile.Read(ref _shutdownState) != 0;

    public bool IsRedeliveryDisabled => Volatile.Read(ref _redeliveryDisabled) != 0;

    public CrashRecord CreateRecord(
        string source,
        Exception? exception,
        string? reason,
        LogLevel level,
        CrashEventKind kind,
        bool emergencyRequired,
        string? crashId = null)
    {
        return CreateCore(source, exception, reason, level, kind, emergencyRequired, crashId, null);
    }

    public CrashRecord CreateRelatedRecord(
        string source,
        Exception? exception,
        string? reason,
        LogLevel level,
        CrashEventKind kind,
        bool emergencyRequired,
        string? relatedCrashId)
    {
        return CreateCore(source, exception, reason, level, kind, emergencyRequired, null, relatedCrashId, true);
    }

    public CrashWriteResult Record(CrashRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return RecordCore(record, flushStructured: false);
    }

    public CrashWriteResult Record(
        CrashRecord record,
        IReadOnlyDictionary<string, object?>? properties)
    {
        ArgumentNullException.ThrowIfNull(record);
        return RecordCore(record, flushStructured: false, properties);
    }

    public CrashWriteResult RecordWinUiUnhandled(Exception? exception, string? reason = null)
    {
        CrashRecord record = CreateRecord(
            "App.WinUi",
            exception,
            reason ?? "Unhandled UI exception",
            LogLevel.Fatal,
            CrashEventKind.WinUiUnhandled,
            emergencyRequired: true);
        return RecordCore(record, flushStructured: true);
    }

    public CrashWriteResult RecordAppDomainTerminating(
        Exception? exception,
        bool isTerminating,
        string? crashId = null,
        IReadOnlyDictionary<string, object?>? properties = null)
    {
        string? relatedCrashId = crashId;
        CrashRecord record = isTerminating
            ? CreateRelatedRecord(
                "App.Domain",
                exception,
                "Terminating AppDomain exception",
                LogLevel.Fatal,
                CrashEventKind.AppDomainTerminating,
                emergencyRequired: true,
                relatedCrashId)
            : CreateRecord(
                "App.Domain",
                exception,
                "AppDomain exception",
                LogLevel.Fatal,
                CrashEventKind.AppDomainTerminating,
                emergencyRequired: true);
        Dictionary<string, object?> merged = BuildProperties(record);
        if (properties is not null)
        {
            foreach (KeyValuePair<string, object?> pair in properties)
            {
                if (!string.IsNullOrEmpty(pair.Key))
                {
                    merged[pair.Key] = pair.Value;
                }
            }
        }

        merged["ErrorCode"] = isTerminating ? "TERMINATING" : "NON_TERMINATING";
        return RecordCore(record, flushStructured: true, merged);
    }

    public bool TryRecordUnobservedTaskException(
        AggregateException exception,
        out CrashWriteResult result)
    {
        if (exception is null)
        {
            result = new CrashWriteResult(
                new CrashRecord(
                    CreateCrashId(),
                    _sessionId,
                    "App.TaskScheduler",
                    DateTimeOffset.UtcNow,
                    "Unobserved task exception",
                    LogLevel.Error,
                    CrashEventKind.UnobservedTask,
                    null,
                    emergencyRequired: false),
                false,
                false,
                false,
                false,
                "exception-missing");
            return false;
        }

        CrashRecord record = CreateRecord(
            "App.TaskScheduler",
            exception,
            "Unobserved task exception",
            LogLevel.Error,
            CrashEventKind.UnobservedTask,
            emergencyRequired: false);
        result = RecordCore(record, flushStructured: true, BuildProperties(record));
        return result.StructuredWriteSucceeded;
    }

    public bool TryHandleWinUiUnhandled(
        Exception? exception,
        string source,
        Func<bool> initiateShutdown,
        out CrashWriteResult? result,
        string? reason = null)
    {
        if (initiateShutdown is null)
        {
            result = null;
            return false;
        }

        CrashRecord record = CreateRecord(
            source,
            exception,
            reason ?? "Unhandled UI exception",
            LogLevel.Fatal,
            CrashEventKind.WinUiUnhandled,
            emergencyRequired: true);
        bool firstDelivery = Interlocked.Exchange(ref _redeliveryDisabled, 1) == 0;
        CrashWriteResult writeResult = RecordCore(record, flushStructured: true);
        bool initiated = false;
        if (firstDelivery)
        {
            try
            {
                initiated = initiateShutdown();
            }
            catch (Exception)
            {
                initiated = false;
            }

            if (initiated)
            {
                Volatile.Write(ref _controlledShutdownCrashId, record.CrashId);
            }
        }

        result = new CrashWriteResult(
            writeResult.Record,
            writeResult.EmergencyWritten,
            writeResult.StructuredWriteSucceeded,
            writeResult.Duplicate,
            initiated,
            initiated ? writeResult.ErrorCode : "controlled-shutdown-not-initiated",
            writeResult.Retried);
        return initiated;
    }

    public bool TryHandleWinUiUnhandled(
        Exception? exception,
        string source,
        Func<bool> initiateShutdown,
        string? reason,
        out CrashWriteResult? result)
    {
        return TryHandleWinUiUnhandled(exception, source, initiateShutdown, out result, reason);
    }

    public bool HandleWinUiUnhandled(
        Exception? exception,
        Func<bool> initiateShutdown,
        out CrashWriteResult? result)
    {
        return TryHandleWinUiUnhandled(
            exception,
            "App.WinUi",
            initiateShutdown,
            out result);
    }

    public bool TryBeginControlledShutdown(
        string reason,
        Func<bool> initiateShutdown,
        out CrashShutdownStartResult result)
    {
        if (string.IsNullOrWhiteSpace(reason) || initiateShutdown is null)
        {
            result = new CrashShutdownStartResult(false, false, "shutdown-request-invalid");
            return false;
        }

        lock (_writeGate)
        {
            if (Volatile.Read(ref _shutdownState) != 0)
            {
                result = new CrashShutdownStartResult(false, true);
                return true;
            }

            try
            {
                if (!initiateShutdown())
                {
                    result = new CrashShutdownStartResult(false, false, "shutdown-start-failed");
                    return false;
                }
            }
            catch (Exception)
            {
                result = new CrashShutdownStartResult(false, false, "shutdown-start-failed");
                return false;
            }

            Volatile.Write(ref _shutdownState, 1);
            result = new CrashShutdownStartResult(true, false);
            return true;
        }
    }

    public async Task<CrashShutdownResult> RunControlledShutdownStagesAsync(
        string reason,
        Func<CancellationToken, Task<bool>>? stopOperations,
        Func<CancellationToken, Task<bool>>? stopWatcher,
        Func<CancellationToken, Task<ProcessTerminationSummary>>? terminateProcesses,
        Func<CancellationToken, Task<bool>>? flush,
        Func<Task<bool>>? disposeServices,
        Func<Task<bool>>? requestExit,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<SettingsPersistenceResult>>? persistSettings,
        Func<CrashShutdownResult, CancellationToken, Task>? writeTerminalEvent)
    {
        bool shutdownRequested = !string.IsNullOrWhiteSpace(reason);
        ShutdownStageTracker tracker = new();
        try
        {
            bool operationsStopped = await RunStageAsync(
                ShutdownStages.OperationIntake,
                stopOperations,
                tracker,
                cancellationToken).ConfigureAwait(false);
            SettingsPersistenceResult settingsPersistence = await RunSettingsStageAsync(
                persistSettings,
                tracker,
                cancellationToken).ConfigureAwait(false);
            bool settingsPersisted = settingsPersistence.Persisted;
            ProcessTerminationSummary processSummary = await RunProcessStageAsync(
                terminateProcesses,
                tracker,
                cancellationToken).ConfigureAwait(false);
            bool processesVerified = processSummary.AllVerified;
            bool watcherStopped = await RunStageAsync(
                ShutdownStages.Watcher,
                stopWatcher,
                tracker,
                cancellationToken).ConfigureAwait(false);
            bool flushSucceeded = await RunStageAsync(
                ShutdownStages.LogFlush,
                flush,
                tracker,
                cancellationToken).ConfigureAwait(false);
            if (writeTerminalEvent is not null && !cancellationToken.IsCancellationRequested)
            {
                tracker.MarkExecuted(ShutdownStages.TerminalEvent);
            }

            CrashShutdownResult provisional = new(
                shutdownRequested,
                operationsStopped,
                settingsPersisted,
                processesVerified,
                watcherStopped,
                flushSucceeded,
                servicesDisposed: false,
                exitRequested: false,
                GetStageErrorCode(
                    shutdownRequested,
                    operationsStopped,
                    settingsPersisted,
                    processesVerified,
                    watcherStopped,
                    flushSucceeded,
                    tracker.HasMissing,
                    cancellationToken.IsCancellationRequested) ?? CrashShutdownResult.ErrorExitRequestPending,
                processSummary,
                tracker.Executed);
            if (writeTerminalEvent is not null)
            {
                await RunTerminalEventAsync(
                    writeTerminalEvent,
                    provisional,
                    cancellationToken).ConfigureAwait(false);
            }

            RecordShutdownOutcome(provisional);

            bool servicesDisposed = await RunStageAsync(
                ShutdownStages.DisposeServices,
                disposeServices is null ? null : _ => disposeServices(),
                tracker,
                cancellationToken).ConfigureAwait(false);
            bool exitRequested = await RunStageAsync(
                ShutdownStages.ExitRequest,
                requestExit is null ? null : _ => requestExit(),
                tracker,
                cancellationToken).ConfigureAwait(false);
            return new CrashShutdownResult(
                shutdownRequested,
                operationsStopped,
                settingsPersisted,
                processesVerified,
                watcherStopped,
                flushSucceeded,
                servicesDisposed,
                exitRequested,
                GetShutdownErrorCode(
                    shutdownRequested,
                    operationsStopped,
                    settingsPersisted,
                    processesVerified,
                    watcherStopped,
                    flushSucceeded,
                    servicesDisposed,
                    exitRequested,
                    tracker.HasMissing,
                    cancellationToken.IsCancellationRequested),
                processSummary,
                tracker.Executed);
        }
        catch (Exception)
        {
            return new CrashShutdownResult(
                shutdownRequested: shutdownRequested,
                operationsStopped: false,
                settingsPersisted: false,
                processesVerified: false,
                watcherStopped: false,
                flushSucceeded: false,
                servicesDisposed: false,
                exitRequested: false,
                CrashShutdownResult.ErrorStageFailed,
                ProcessTerminationSummary.Unavailable(CrashShutdownResult.ErrorStageFailed),
                tracker.Executed);
        }
    }

    public static bool IsShutdownOutcomeFailed(CrashShutdownResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.StagesSucceeded)
        {
            return true;
        }

        if (result.ExitRequestPending)
        {
            return false;
        }

        return !result.ServicesDisposed || !result.ExitRequested;
    }

    public void RecordShutdownOutcome(CrashShutdownResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        bool failed = IsShutdownOutcomeFailed(result);
        bool pending = result.ExitRequestPending;
        string stateToken = ShutdownStateToken(result);
        Dictionary<string, object?> properties = BuildStageProperties(result);
        if (failed && !properties.ContainsKey("ErrorCode"))
        {
            // Итоговое событие уровня Error обязано нести машиночитаемый код, даже если
            // стадия не сообщила причину: иначе потребитель не отличит «причина неизвестна»
            // от «событие не классифицировано».
            properties["ErrorCode"] = CrashShutdownResult.ErrorStageFailed;
        }

        if (Volatile.Read(ref _disposed) != 0 || _logService is null)
        {
            CountShutdownOutcomeDropped(failed, stateToken);
            return;
        }

        try
        {
            _logService.Write(
                ControlledShutdownOutcomeEventId,
                failed ? LogLevel.Error : LogLevel.Info,
                failed ? LogStatus.Failed : LogStatus.Succeeded,
                failed
                    ? "Контролируемое завершение выполнено с ошибками"
                    : (pending
                        ? "Этапы контролируемого завершения выполнены, запрос выхода ещё не обработан"
                        : "Контролируемое завершение выполнено, запрос выхода обработан"),
                null,
                "App",
                context: null,
                properties);
            if (!_logService.Flush(StructuredFlushTimeout))
            {
                CountShutdownOutcomeDropped(failed, stateToken);
            }
        }
        catch (Exception)
        {
            CountShutdownOutcomeDropped(failed, stateToken);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        GC.SuppressFinalize(this);
    }

    private CrashRecord CreateCore(
        string source,
        Exception? exception,
        string? reason,
        LogLevel level,
        CrashEventKind kind,
        bool emergencyRequired,
        string? crashId,
        string? relatedCrashId,
        bool freshCrashId = false)
    {
        string id = NormalizeCrashId(crashId) ?? (freshCrashId ? CreateCrashId() : GetOrCreateCrashId(exception));
        string safeSource = NormalizeSource(source);
        string safeReason = NormalizeReason(reason, exception);
        return new CrashRecord(
            id,
            _sessionId,
            safeSource,
            DateTimeOffset.UtcNow,
            safeReason,
            level,
            kind,
            exception,
            emergencyRequired,
            NormalizeCrashId(relatedCrashId));
    }

    private CrashWriteResult RecordCore(
        CrashRecord record,
        bool flushStructured,
        IReadOnlyDictionary<string, object?>? properties = null)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return RecordDisposedFallback(record);
        }

        lock (_writeGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return RecordDisposedFallback(record);
            }

            string key = BuildOutcomeKey(record);
            bool retried = false;
            if (_outcomes.TryGetValue(key, out CrashWriteResult? existing))
            {
                if (existing.RequiredWriteSucceeded)
                {
                    Interlocked.Increment(ref _duplicateCount);
                    return new CrashWriteResult(
                        existing.Record,
                        existing.EmergencyWritten,
                        existing.StructuredWriteSucceeded,
                        true,
                        existing.ControlledShutdownInitiated,
                        existing.ErrorCode);
                }

                retried = true;
                Interlocked.Increment(ref _retryCount);
            }

            _records[key] = record;
            bool emergencyWritten = false;
            if (record.EmergencyRequired)
            {
                Interlocked.Increment(ref _emergencyAttempts);
                try
                {
                    emergencyWritten = _emergencySink.Write(
                        record.Reason,
                        record.Exception,
                        record.Source,
                        record.CrashId,
                        _sessionId,
                        record.Level);
                    if (!emergencyWritten)
                    {
                        Interlocked.Increment(ref _emergencyFailures);
                    }
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref _emergencyFailures);
                }
            }

            bool structuredSucceeded = WriteStructured(
                record,
                properties ?? BuildProperties(record),
                flushStructured);
            CrashWriteResult merged = MergeOutcome(record, existing, emergencyWritten, structuredSucceeded, retried);
            _outcomes[key] = merged;
            Volatile.Write(ref _lastCrashId, record.CrashId);
            return merged;
        }
    }

    private CrashWriteResult MergeOutcome(
        CrashRecord record,
        CrashWriteResult? existing,
        bool emergencyWritten,
        bool structuredSucceeded,
        bool retried)
    {
        bool emergencyOk = emergencyWritten || (existing?.EmergencyWritten ?? false);
        bool structuredOk = structuredSucceeded || (existing?.StructuredWriteSucceeded ?? false);
        string? errorCode = null;
        if (record.EmergencyRequired && !emergencyOk)
        {
            errorCode = "emergency-write-failed";
        }

        if (errorCode is null && !structuredOk)
        {
            errorCode = "structured-write-failed";
        }

        return new CrashWriteResult(
            record,
            emergencyOk,
            structuredOk,
            false,
            false,
            errorCode,
            retried);
    }

    private CrashWriteResult RecordDisposedFallback(CrashRecord record)
    {
        bool written = false;
        try
        {
            written = EmergencyLogSink.Shared.Write(
                record.Reason,
                record.Exception,
                record.Source,
                record.CrashId,
                _sessionId,
                record.Level);
        }
        catch (Exception)
        {
            written = false;
        }

        if (written)
        {
            Interlocked.Increment(ref _disposedFallbackCount);
        }
        else
        {
            Interlocked.Increment(ref _disposedFallbackFailures);
        }

        return new CrashWriteResult(
            record,
            written,
            false,
            false,
            false,
            written ? ErrorDisposedFallbackWritten : ErrorDisposedFallbackFailed);
    }

    private void WriteDisposedFallback(string reason, LogLevel level)
    {
        Interlocked.Increment(ref _disposedFallbackCount);
        try
        {
            if (!EmergencyLogSink.Shared.Write(
                reason,
                null,
                "App.Shutdown",
                "shutdown-" + Guid.NewGuid().ToString("N"),
                _sessionId,
                level))
            {
                Interlocked.Increment(ref _disposedFallbackFailures);
            }
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _disposedFallbackFailures);
        }
    }

    private bool WriteStructured(
        CrashRecord record,
        IReadOnlyDictionary<string, object?> properties,
        bool flush)
    {
        if (_logService is null)
        {
            Interlocked.Increment(ref _structuredFailures);
            return false;
        }

        Interlocked.Increment(ref _structuredAttempts);
        long droppedBefore = _logService.DroppedEventCount;
        long errorsBefore = _logService.WriteErrorCount;
        try
        {
            string eventId = GetEventId(record.Kind);
            LogLevel level = record.Kind == CrashEventKind.UnobservedTask ? LogLevel.Error : record.Level;
            LogStatus status = LogStatus.Failed;
            Exception? exception = record.Kind == CrashEventKind.UnobservedTask
                ? BuildRedactedAggregate(record.Exception as AggregateException)
                : record.Exception;
            _logService.Write(
                eventId,
                level,
                status,
                GetStructuredMessage(record.Kind),
                exception,
                "App",
                context: null,
                properties);
            bool flushed = !flush || _logService.Flush(StructuredFlushTimeout);
            bool countersUnchanged = _logService.DroppedEventCount == droppedBefore
                && _logService.WriteErrorCount == errorsBefore;
            bool result = flushed && countersUnchanged;
            if (!result)
            {
                Interlocked.Increment(ref _structuredFailures);
            }

            return result;
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _structuredFailures);
            return false;
        }
    }

    private Dictionary<string, object?> BuildProperties(CrashRecord record)
    {
        Dictionary<string, object?> properties = new(StringComparer.Ordinal)
        {
            ["CrashId"] = record.CrashId,
            ["Stage"] = record.Kind.ToString(),
            ["Reason"] = record.Reason,
            ["ErrorCode"] = GetCrashErrorCode(record.Kind)
        };
        if (record.RelatedCrashId is not null)
        {
            properties["RelatedCrashId"] = record.RelatedCrashId;
        }

        return properties;
    }

    /// <summary>
    /// Стабильный машиночитаемый код аварийного пути. Без него все Fatal-события
    /// аварийного пути выглядели одинаково: «что-то упало», но не «какой класс отказа».
    /// </summary>
    private static string GetCrashErrorCode(CrashEventKind kind)
    {
        return kind switch
        {
            CrashEventKind.WinUiUnhandled => "crash-winui-unhandled",
            CrashEventKind.AppDomainTerminating => "crash-appdomain-terminating",
            CrashEventKind.UnobservedTask => "crash-unobserved-task",
            _ => "crash-controlled-shutdown"
        };
    }

    public static Dictionary<string, object?> BuildStageProperties(CrashShutdownResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        bool failed = IsShutdownOutcomeFailed(result);
        Dictionary<string, object?> properties = new(StringComparer.Ordinal)
        {
            ["Stage"] = "controlled_shutdown",
            ["Status"] = ShutdownStateToken(result),
            ["Succeeded"] = !failed,
            ["Failed"] = failed,
            ["Persisted"] = result.SettingsPersisted,
            ["ExitRequested"] = result.ExitRequested,
            ["ExitRequestPending"] = result.ExitRequestPending,
            ["ExecutedStages"] = JoinStageNames(result.ExecutedStages),
            ["Reason"] = ShutdownStateToken(result)
        };

        // ErrorCode заполняется только при отказе: потребитель, ищущий отказы по
        // errorCode != null, не должен получать ложное срабатывание на успешном завершении.
        // Признак «выход запрошен, но ещё не подтверждён» остаётся в ExitRequestPending.
        if (failed && !string.IsNullOrEmpty(result.ErrorCode))
        {
            properties["ErrorCode"] = result.ErrorCode;
        }

        return properties;
    }

    private static string JoinStageNames(IReadOnlyList<string> stages)
    {
        if (stages.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(";", stages);
    }

    private static string ShutdownStateToken(CrashShutdownResult result)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"requested={result.ShutdownRequested};operations={result.OperationsStopped};settings={result.SettingsPersisted};processes={result.ProcessesVerified};watcher={result.WatcherStopped};flush={result.FlushSucceeded};disposed={result.ServicesDisposed};exit={result.ExitRequested}");
    }

    private Exception? BuildRedactedAggregate(AggregateException? exception)
    {
        if (exception is null)
        {
            return null;
        }

        try
        {
            AggregateException flattened = exception.Flatten();
            IReadOnlyList<Exception> items = flattened.InnerExceptions;
            int count = Math.Min(items.Count, 4);
            List<Exception> safeItems = new(count);
            for (int index = 0; index < count; index++)
            {
                Exception item = items[index];
                string type = NormalizeType(item.GetType());
                string message = NormalizeReason(item.Message, null, 256);
                safeItems.Add(new InvalidOperationException(type + ": " + message));
            }

            Exception? chain = null;
            for (int index = safeItems.Count - 1; index >= 0; index--)
            {
                chain = new InvalidOperationException(safeItems[index].Message, chain);
            }

            return chain is null
                ? new AggregateException("AggregateException")
                : new AggregateException("AggregateException", chain);
        }
        catch (Exception)
        {
            return new InvalidOperationException("AggregateException");
        }
    }

    private string GetOrCreateCrashId(Exception? exception)
    {
        if (exception is null)
        {
            return CreateCrashId();
        }

        try
        {
            return _exceptionIds.GetValue(exception, _ => new CrashIdHolder(CreateCrashId())).Value;
        }
        catch (Exception)
        {
            return CreateCrashId();
        }
    }

    private string CreateCrashId() => Guid.NewGuid().ToString("N");

    private static string BuildOutcomeKey(CrashRecord record)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{record.CrashId}|{(int)record.Kind}");
    }

    private string? NormalizeCrashId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            string safe = _redactor.SanitizeIdentifier(value);
            if (string.Equals(safe, LogRedactor.UnknownIdentifier, StringComparison.Ordinal))
            {
                return null;
            }

            return safe.Length <= MaxCrashIdLength ? safe : safe[..MaxCrashIdLength];
        }
        catch (Exception)
        {
            return null;
        }
    }

    private string NormalizeSource(string? source)
    {
        try
        {
            string safe = _redactor.SanitizeIdentifier(source);
            return string.Equals(safe, LogRedactor.UnknownIdentifier, StringComparison.Ordinal)
                ? "App"
                : safe;
        }
        catch (Exception)
        {
            return "App";
        }
    }

    private string NormalizeReason(string? reason, Exception? exception, int maxBytes = MaxReasonBytes)
    {
        string value = string.IsNullOrWhiteSpace(reason)
            ? exception?.GetType().Name ?? "Unhandled exception"
            : reason;
        try
        {
            return LogRedactor.Truncate(_redactor.RedactToken(value), Math.Max(32, maxBytes));
        }
        catch (Exception)
        {
            return "redacted";
        }
    }

    private string NormalizeType(Type type)
    {
        try
        {
            return _redactor.SanitizeIdentifier(type.FullName ?? type.Name);
        }
        catch (Exception)
        {
            return "Exception";
        }
    }

    private static string GetEventId(CrashEventKind kind)
    {
        return kind switch
        {
            CrashEventKind.WinUiUnhandled => WinUiUnhandledEventId,
            CrashEventKind.AppDomainTerminating => AppDomainTerminatingEventId,
            CrashEventKind.UnobservedTask => UnobservedTaskEventId,
            _ => ControlledShutdownEventId
        };
    }

    private static string GetStructuredMessage(CrashEventKind kind)
    {
        return kind switch
        {
            CrashEventKind.WinUiUnhandled => "Unhandled UI exception",
            CrashEventKind.AppDomainTerminating => "Terminating application exception",
            CrashEventKind.UnobservedTask => "Unobserved task exception",
            _ => "Controlled application shutdown"
        };
    }

    private static async Task<bool> RunStageAsync(
        string stage,
        Func<CancellationToken, Task<bool>>? operation,
        ShutdownStageTracker tracker,
        CancellationToken cancellationToken)
    {
        if (operation is null)
        {
            tracker.MarkMissing(stage);
            return false;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        tracker.MarkExecuted(stage);
        return await AwaitBoundedAsync(
            () => operation(cancellationToken) ?? Task.FromResult(false),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<SettingsPersistenceResult> RunSettingsStageAsync(
        Func<CancellationToken, Task<SettingsPersistenceResult>>? operation,
        ShutdownStageTracker tracker,
        CancellationToken cancellationToken)
    {
        if (operation is null)
        {
            tracker.MarkMissing(ShutdownStages.SettingsPersistence);
            return SettingsPersistenceResult.StageUnavailable;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return SettingsPersistenceResult.Failure(CrashShutdownResult.ErrorCancelled);
        }

        tracker.MarkExecuted(ShutdownStages.SettingsPersistence);
        Task<SettingsPersistenceResult>? task = null;
        try
        {
            task = operation(cancellationToken) ?? Task.FromResult(SettingsPersistenceResult.Unknown);
            Task completed = await WhenBoundedAsync(task, cancellationToken).ConfigureAwait(false);
            if (!ReferenceEquals(completed, task))
            {
                Observe(task);
                return SettingsPersistenceResult.Failure(CrashShutdownResult.ErrorCancelled);
            }

            return await task.ConfigureAwait(false) ?? SettingsPersistenceResult.Unknown;
        }
        catch (Exception)
        {
            Observe(task);
            return SettingsPersistenceResult.Failure(CrashShutdownResult.ErrorStageFailed);
        }
    }

    private static async Task<ProcessTerminationSummary> RunProcessStageAsync(
        Func<CancellationToken, Task<ProcessTerminationSummary>>? operation,
        ShutdownStageTracker tracker,
        CancellationToken cancellationToken)
    {
        if (operation is null)
        {
            tracker.MarkMissing(ShutdownStages.ProcessTermination);
            return ProcessTerminationSummary.Unavailable(CrashShutdownResult.ErrorStageUnavailable);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return ProcessTerminationSummary.Unavailable(CrashShutdownResult.ErrorCancelled);
        }

        tracker.MarkExecuted(ShutdownStages.ProcessTermination);
        Task<ProcessTerminationSummary>? task = null;
        try
        {
            task = operation(cancellationToken) ?? Task.FromResult(
                ProcessTerminationSummary.Unavailable(CrashShutdownResult.ErrorStageUnavailable));
            Task completed = await WhenBoundedAsync(task, cancellationToken).ConfigureAwait(false);
            if (!ReferenceEquals(completed, task))
            {
                Observe(task);
                return ProcessTerminationSummary.Unavailable(CrashShutdownResult.ErrorCancelled);
            }

            return await task.ConfigureAwait(false)
                ?? ProcessTerminationSummary.Unavailable(CrashShutdownResult.ErrorStageUnavailable);
        }
        catch (Exception)
        {
            Observe(task);
            return ProcessTerminationSummary.Unavailable(CrashShutdownResult.ErrorStageFailed);
        }
    }

    private static async Task RunTerminalEventAsync(
        Func<CrashShutdownResult, CancellationToken, Task>? writeTerminalEvent,
        CrashShutdownResult provisional,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        Task? task = null;
        try
        {
            task = writeTerminalEvent?.Invoke(provisional, cancellationToken) ?? Task.CompletedTask;
            Task completed = await WhenBoundedAsync(task, cancellationToken).ConfigureAwait(false);
            if (!ReferenceEquals(completed, task))
            {
                Observe(task);
                return;
            }

            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            Observe(task);
        }
    }

    private static async Task<bool> AwaitBoundedAsync(
        Func<Task<bool>> start,
        CancellationToken cancellationToken)
    {
        Task<bool> task;
        try
        {
            task = start() ?? Task.FromResult(false);
        }
        catch (Exception)
        {
            return false;
        }

        Task completed = await WhenBoundedAsync(task, cancellationToken).ConfigureAwait(false);
        if (!ReferenceEquals(completed, task))
        {
            Observe(task);
            return false;
        }

        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static Task<Task> WhenBoundedAsync(Task task, CancellationToken cancellationToken)
    {
        return Task.WhenAny(task, Task.Delay(ShutdownStageTimeout, cancellationToken));
    }

    private static void Observe(Task? task)
    {
        if (task is null)
        {
            return;
        }

        _ = task.ContinueWith(
            static observed => _ = observed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void CountShutdownOutcomeDropped(bool failed, string stateToken)
    {
        Interlocked.Increment(ref _outcomeDroppedCount);
        if (!failed)
        {
            return;
        }

        WriteDisposedFallback(
            "Controlled shutdown outcome: " + stateToken,
            LogLevel.Error);
    }

    private static string? GetStageErrorCode(
        bool shutdownRequested,
        bool operationsStopped,
        bool settingsPersisted,
        bool processesVerified,
        bool watcherStopped,
        bool flushSucceeded,
        bool stageUnavailable,
        bool cancelled)
    {
        if (!shutdownRequested)
        {
            return "shutdown-request-invalid";
        }

        if (stageUnavailable)
        {
            return CrashShutdownResult.ErrorStageUnavailable;
        }

        if (cancelled)
        {
            return CrashShutdownResult.ErrorCancelled;
        }

        if (!operationsStopped)
        {
            return CrashShutdownResult.ErrorOperationStopFailed;
        }

        if (!settingsPersisted)
        {
            return CrashShutdownResult.ErrorSettingsPersistenceFailed;
        }

        if (!processesVerified)
        {
            return CrashShutdownResult.ErrorProcessTerminationFailed;
        }

        if (!watcherStopped)
        {
            return CrashShutdownResult.ErrorWatcherStopFailed;
        }

        if (!flushSucceeded)
        {
            return CrashShutdownResult.ErrorLogFlushFailed;
        }

        return null;
    }

    private static string? GetShutdownErrorCode(
        bool shutdownRequested,
        bool operationsStopped,
        bool settingsPersisted,
        bool processesVerified,
        bool watcherStopped,
        bool flushSucceeded,
        bool servicesDisposed,
        bool exitRequested,
        bool stageUnavailable,
        bool cancelled)
    {
        string? stageError = GetStageErrorCode(
            shutdownRequested,
            operationsStopped,
            settingsPersisted,
            processesVerified,
            watcherStopped,
            flushSucceeded,
            stageUnavailable,
            cancelled);
        if (stageError is not null)
        {
            return stageError;
        }

        if (!servicesDisposed)
        {
            return CrashShutdownResult.ErrorServiceDisposeFailed;
        }

        if (!exitRequested)
        {
            return CrashShutdownResult.ErrorExitRequestFailed;
        }

        return null;
    }

    private sealed class CrashIdHolder
    {
        public CrashIdHolder(string value)
        {
            Value = value;
        }

        public string Value { get; }
    }

    private sealed class ShutdownStageTracker
    {
        private const int MaxTrackedStages = 12;

        private readonly List<string> _executed = new(8);
        private readonly List<string> _missing = new(4);

        public IReadOnlyList<string> Executed => _executed;

        public bool HasMissing => _missing.Count > 0;

        public void MarkExecuted(string stage)
        {
            if (_executed.Count < MaxTrackedStages && !_executed.Contains(stage, StringComparer.Ordinal))
            {
                _executed.Add(stage);
            }
        }

        public void MarkMissing(string stage)
        {
            if (_missing.Count < MaxTrackedStages && !_missing.Contains(stage, StringComparer.Ordinal))
            {
                _missing.Add(stage);
            }
        }
    }
}

public static class ShutdownStages
{
    public const string OperationIntake = "operation_intake";
    public const string SettingsPersistence = "settings_persistence";
    public const string ProcessTermination = "process_termination";
    public const string Watcher = "watcher";
    public const string LogFlush = "log_flush";
    public const string TerminalEvent = "terminal_event";
    public const string DisposeServices = "dispose_services";
    public const string ExitRequest = "exit_request";
}
