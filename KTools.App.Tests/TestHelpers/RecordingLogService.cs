// -*- coding: utf-8 -*-
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Services.Contracts;

namespace KTools_App.Tests.TestHelpers;

/// <summary>
/// Захваченное структурированное событие журнала для проверок контракта persistence-слоя.
/// </summary>
public sealed record RecordedLogEvent(
    string EventId,
    LogLevel Level,
    LogStatus Status,
    string Message,
    Exception? Exception,
    IReadOnlyDictionary<string, object?>? Properties)
{
    /// <summary>
    /// Получить типизированное свойство события.
    /// </summary>
    public T? GetProperty<T>(string name)
    {
        if (Properties is null)
        {
            return default;
        }

        return Properties.TryGetValue(name, out object? value) && value is T typed ? typed : default;
    }
}

/// <summary>
/// Событие, отклонённое условной схемой журнала, и код отказа.
/// </summary>
public sealed record RecordedRejection(string EventId, string ErrorCode);

/// <summary>
/// Тестовый двойник ILogService, фиксирующий все структурированные события
/// без реальной записи на диск. Повторяет поведение production-сервиса в тех
/// аспектах, которые влияют на достоверность проверок приватности и качества:
/// редактирование через <see cref="LogRedactor"/>, возврат без учёта потерь при
/// фильтре по минимальному уровню и заполнение <c>SessionId</c>/<c>TimestampUtc</c>
/// у отклонённых событий. Иначе тесты приватности проверяли бы мок, а не контракт.
/// </summary>
public sealed class RecordingLogService : ILogService
{
    private const string LegacyEventId = "legacy.log";

    private static readonly Guid RecordingSessionId = Guid.NewGuid();

    private readonly ConcurrentQueue<RecordedLogEvent> _events = new();
    private readonly ConcurrentQueue<RecordedRejection> _rejections = new();
    private readonly ConcurrentQueue<string?> _initializeLogFileCalls = new();
    private readonly ConcurrentQueue<string> _callOrder = new();
    private readonly LogRedactor _redactor = new();
    private long _rejectedEventCount;
    private long _droppedEventCount;

    /// <inheritdoc />
    public event EventHandler<LogEvent>? LogReceived;

    /// <summary>
    /// Все зафиксированные события в порядке записи.
    /// </summary>
    public IReadOnlyList<RecordedLogEvent> Events => _events.ToArray();

    /// <summary>
    /// События, отклонённые условной схемой журнала, вместе с кодом отказа.
    /// </summary>
    public IReadOnlyList<RecordedRejection> Rejections => _rejections.ToArray();

    /// <summary>
    /// Общая очередь вызовов в формате «init:&lt;путь&gt;» для InitializeLogFile
    /// и «event:&lt;идентификатор&gt;» для записей журнала.
    /// Позволяет проверить, что ни одна запись не выполнена до выбора каталога.
    /// </summary>
    public IReadOnlyList<string> CallOrder => _callOrder.ToArray();

    /// <summary>
    /// Аргументы всех вызовов InitializeLogFile в порядке вызова.
    /// </summary>
    public IReadOnlyList<string?> InitializeLogFileCalls => _initializeLogFileCalls.ToArray();

    /// <summary>
    /// События с указанным идентификатором.
    /// </summary>
    public IReadOnlyList<RecordedLogEvent> EventsById(string eventId) =>
        _events.Where(e => string.Equals(e.EventId, eventId, StringComparison.Ordinal)).ToArray();

    /// <summary>
    /// Удалить все зафиксированные события (например, чтобы изолировать проверку
    /// событий, порождённых конкретным вызовом).
    /// </summary>
    public void Clear()
    {
        while (_events.TryDequeue(out _))
        {
        }

        while (_rejections.TryDequeue(out _))
        {
        }

        while (_callOrder.TryDequeue(out _))
        {
        }

        Interlocked.Exchange(ref _rejectedEventCount, 0);
        Interlocked.Exchange(ref _droppedEventCount, 0);
    }

    /// <inheritdoc />
    public void Write(
        string eventId,
        LogLevel level,
        string message,
        string source = "System",
        LogStatus status = LogStatus.None,
        LogContext? context = null,
        IReadOnlyDictionary<string, object?>? properties = null,
        Exception? exception = null)
    {
        Record(
            new LogEvent
            {
                EventId = eventId ?? string.Empty,
                Level = level,
                Status = status,
                Message = message ?? string.Empty,
                Source = source,
                OperationId = context?.ResolveOperationId(),
                ItemId = context?.ResolveItemId(),
                ProcessId = context?.ResolveProcessId(),
                Tool = context?.ResolveTool(),
                Properties = MergeContextProperties(context, properties),
                Exception = exception is null ? null : ExceptionInfo.FromException(exception)
            },
            exception);
    }

    /// <inheritdoc />
    public void Write(
        string eventId,
        LogLevel level,
        LogStatus status,
        string message,
        Exception? exception = null,
        string source = "System",
        LogContext? context = null,
        IReadOnlyDictionary<string, object?>? properties = null)
    {
        Record(
            new LogEvent
            {
                EventId = eventId ?? string.Empty,
                Level = level,
                Status = status,
                Message = message ?? string.Empty,
                Source = source,
                OperationId = context?.ResolveOperationId(),
                ItemId = context?.ResolveItemId(),
                ProcessId = context?.ResolveProcessId(),
                Tool = context?.ResolveTool(),
                Properties = MergeContextProperties(context, properties),
                Exception = exception is null ? null : ExceptionInfo.FromException(exception)
            },
            exception);
    }

    /// <summary>
    /// Объединяет свойства контекста и явные свойства так же, как это делает реальный сервис,
    /// чтобы проверки видели корреляцию одинаково при любой форме вызова.
    /// </summary>
    private static IReadOnlyDictionary<string, object?> MergeContextProperties(
        LogContext? context,
        IReadOnlyDictionary<string, object?>? properties)
    {
        Dictionary<string, object?>? merged = null;
        if (context is not null && !context.IsEmpty)
        {
            merged = new Dictionary<string, object?>(context.ToProperties(), StringComparer.Ordinal);
        }

        if (properties is not null)
        {
            merged ??= new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, object?> pair in properties)
            {
                merged[pair.Key] = pair.Value;
            }
        }

        return merged is null || merged.Count == 0 ? LogEvent.EmptyProperties : merged;
    }

    /// <inheritdoc />
    public void Write(LogEvent logEvent)
    {
        if (logEvent == null)
        {
            return;
        }

        Record(logEvent);
    }

    /// <inheritdoc />
    public void InitializeLogFile() => RecordInitializeCall(null);

    /// <inheritdoc />
    public void InitializeLogFile(string? customLogDir) => RecordInitializeCall(customLogDir);

    private void RecordInitializeCall(string? customLogDir)
    {
        _initializeLogFileCalls.Enqueue(customLogDir);
        _callOrder.Enqueue("init:" + (customLogDir ?? string.Empty));
    }

    /// <inheritdoc />
    public void Log(LogLevel level, string message, string source = "System") =>
        Record(new LogEvent { EventId = LegacyEventId, Level = level, Message = message, Source = source });

    /// <inheritdoc />
    public void DebugLog(string message, string source = "System") =>
        Record(new LogEvent { EventId = LegacyEventId, Level = LogLevel.Debug, Message = message, Source = source });

    /// <inheritdoc />
    public void Info(string message, string source = "System") =>
        Record(new LogEvent { EventId = LegacyEventId, Level = LogLevel.Info, Message = message, Source = source });

    /// <inheritdoc />
    public void Warn(string message, string source = "System") =>
        Record(new LogEvent { EventId = LegacyEventId, Level = LogLevel.Warning, Message = message, Source = source });

    /// <inheritdoc />
    public void Error(string message, string source = "System") =>
        Record(new LogEvent { EventId = LegacyEventId, Level = LogLevel.Error, Message = message, Source = source });

    /// <inheritdoc />
    public void Fatal(string message, string source = "System") =>
        Record(new LogEvent { EventId = LegacyEventId, Level = LogLevel.Fatal, Message = message, Source = source });

    /// <inheritdoc />
    public void Exception(Exception ex, string message, string source = "System") =>
        Record(
            new LogEvent
            {
                EventId = LegacyEventId,
                Level = LogLevel.Error,
                Status = LogStatus.Failed,
                Message = message,
                Source = source,
                Exception = ex is null ? null : ExceptionInfo.FromException(ex)
            },
            ex);

    /// <inheritdoc />
    public string ReadCurrentLog() => string.Empty;

    /// <inheritdoc />
    public IReadOnlyList<LogEvent> ReadRecentEvents(int maxCount) => Array.Empty<LogEvent>();

    /// <inheritdoc />
    public bool ClearCurrentLog() => true;

    /// <inheritdoc />
    public void Flush()
    {
    }

    /// <inheritdoc />
    public bool Flush(TimeSpan timeout) => true;

    /// <inheritdoc />
    public LogLevel MinLevel { get; set; } = LogLevel.Debug;

    /// <inheritdoc />
    public LogServiceStatus Status => new(
        MinLevel,
        _events.Count,
        0,
        Interlocked.Read(ref _droppedEventCount),
        0,
        0,
        Interlocked.Read(ref _rejectedEventCount),
        0,
        0,
        0,
        0);

    /// <summary>
    /// Каталог, который сервис журналирования сообщил как фактически применённый.
    /// Позволяет проверять, что менеджер настроек не сообщает об успехе,
    /// когда запрошенный каталог не был применён.
    /// </summary>
    public string? EffectiveLogDirectory { get; set; }

    /// <inheritdoc />
    public string? CurrentLogFile => null;

    /// <inheritdoc />
    public long QueuedEventCount => _events.Count;

    /// <inheritdoc />
    public long QueuedEventBytes => 0;

    /// <inheritdoc />
    public long DroppedEventCount => Interlocked.Read(ref _droppedEventCount);

    /// <inheritdoc />
    public long WriteErrorCount => 0;

    /// <inheritdoc />
    public long RotationErrorCount => 0;

    /// <inheritdoc />
    public long RejectedEventCount => Interlocked.Read(ref _rejectedEventCount);

    /// <inheritdoc />
    public long SubscriberErrorCount => 0;

    /// <inheritdoc />
    public long SubscriberDroppedCount => 0;

    /// <inheritdoc />
    public long DisposeErrorCount => 0;

    /// <inheritdoc />
    public long ReadErrorCount => 0;

    /// <inheritdoc />
    public void Dispose()
    {
        LogReceived = null;
        GC.SuppressFinalize(this);
    }

    private void Record(LogEvent logEvent, Exception? exception = null)
    {
        // Production-сервис при фильтре по MinLevel просто возвращается, ничего не
        // записывая и не считая потерю: событие отброшено осознанно, а не потеряно.
        if ((int)logEvent.Level < (int)MinLevel)
        {
            return;
        }

        string? rejection = LogService.ValidateEventSchema(logEvent);
        if (rejection is not null)
        {
            Interlocked.Increment(ref _rejectedEventCount);
            _rejections.Enqueue(new RecordedRejection(logEvent.EventId, rejection));
            logEvent = CreateRejectedEvent(rejection);
            exception = null;
        }

        logEvent = Prepare(logEvent);

        _callOrder.Enqueue("event:" + logEvent.EventId);
        _events.Enqueue(new RecordedLogEvent(
            logEvent.EventId,
            logEvent.Level,
            logEvent.Status,
            logEvent.Message,
            exception,
            logEvent.Properties));

        try
        {
            LogReceived?.Invoke(this, logEvent);
        }
        catch (Exception)
        {
            // Подписчик не должен влиять на запись события в тесте.
        }
    }

    /// <summary>
    /// Редактирование и заполнение обязательных полей так же, как в production:
    /// без этого тесты приватности проверяли бы неотредактированные аргументы мока.
    /// </summary>
    private LogEvent Prepare(LogEvent logEvent)
    {
        try
        {
            return logEvent with
            {
                TimestampUtc = logEvent.TimestampUtc == default
                    ? DateTimeOffset.UtcNow
                    : logEvent.TimestampUtc.ToUniversalTime(),
                SessionId = RecordingSessionId,
                Source = _redactor.SanitizeIdentifier(logEvent.Source),
                Message = _redactor.RedactMessage(logEvent.Message),
                Properties = _redactor.RedactProperties(logEvent.Properties),
                Exception = _redactor.RedactException(logEvent.Exception)
            };
        }
        catch (Exception)
        {
            return logEvent with
            {
                SessionId = RecordingSessionId,
                EventId = LogEventMarkerNames.RejectedEvent,
                Level = LogLevel.Warning,
                Status = LogStatus.PartiallySucceeded,
                Source = LogEventMarkerNames.LogSource,
                Message = LogRedactor.RedactedMarker,
                Properties = LogEvent.EmptyProperties,
                Exception = null,
                ErrorCode = "REDACTION_FAILED"
            };
        }
    }

    private static LogEvent CreateRejectedEvent(string errorCode) => new()
    {
        EventId = LogEventMarkerNames.RejectedEvent,
        Level = LogLevel.Warning,
        Status = LogStatus.Failed,
        Source = LogEventMarkerNames.LogSource,
        SessionId = RecordingSessionId,
        TimestampUtc = DateTimeOffset.UtcNow,
        Message = "Событие журнала отклонено проверкой структуры",
        ErrorCode = errorCode,
        Properties = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ErrorCode"] = errorCode,
            ["DroppedCount"] = 1L
        }
    };
}
