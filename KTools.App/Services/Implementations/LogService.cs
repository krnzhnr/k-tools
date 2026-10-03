// -*- coding: utf-8 -*-
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using KTools_App.Diagnostics;
using KTools_App.Services.Contracts;

namespace KTools_App.Core;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
    Fatal
}

public sealed class LogService : ILogService, IDisposable
{
    public const int SchemaVersion = 1;
    public const string ApplicationChannel = "app_diag";
    public const string ApplicationName = "K-Tools";
    public const int MaxEmergencyReports = 3;

    private const string DefaultSource = "System";
    private const int DefaultFlushTimeoutMilliseconds = 5000;
    private const int WriterBufferSize = 16 * 1024;
    private const int LineBatchBytes = 128 * 1024;
    private const int ReadChunkBytes = 64 * 1024;
    private const int BaseEventEstimate = 512;
    private const int DisposeWaitMilliseconds = 750;
    private const int LifecycleAccepting = 0;
    private const int LifecycleStopping = 1;
    private const int LifecycleDisposed = 2;

    private readonly LogServiceOptions _options;
    private readonly LogRedactor _redactor;
    private readonly LogFilePolicy _policy;
    private readonly EmergencyLogSink _emergency;
    private readonly Channel<LogWorkItem> _queue;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _writerTask;
    private readonly Task _flusherTask;
    private readonly TaskCompletionSource<bool> _disposeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _writerLock = new();
    private readonly object _stateLock = new();
    private readonly object _subscriberLock = new();
    private readonly List<SubscriberSink> _subscribers = new();
    private readonly List<LogWorkItem> _batch = new(256);
    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly int _processId = Environment.ProcessId;
    private readonly string _applicationVersion = ResolveApplicationVersion();
    private readonly string _sessionText;
    private readonly DateTimeOffset _sessionStartUtc = DateTimeOffset.UtcNow;
    private readonly int _maxLineBytes;

    private Stream? _stream;
    private byte[]? _lineBatch;
    private int _lineBatchCount;
    private Utf8JsonWriter? _json;
    private LogLineBuffer? _jsonBuffer;
    private long _writerBytes;
    private long _retentionAnchorBytes;
    private long _sequence;
    private long _queuedBytes;
    private long _currentQueuedCount;
    private long _droppedCount;
    private long _reportedDroppedCount;
    private long _writeErrorCount;
    private long _rotationErrorCount;
    private long _rejectedEventCount;
    private long _subscriberErrorCount;
    private long _subscriberDroppedCount;
    private long _disposeErrorCount;
    private long _readErrorCount;
    private long _emergencySuppressedCount;
    private long _flushScheduleFailureCount;
    private int _writerThreadId;
    private int _emergencyBudget = MaxEmergencyReports;
    private int _lastFlushTick = Environment.TickCount;
    private int _retentionCheckTick = Environment.TickCount;
    private int _minLevelValue = (int)LogLevel.Debug;
    private int _inErrorMarker;
    private int _lifecycleState;
    private int _disposeStarted;
    private int _writerStopping;
    private int _writerPoisoned;
    private bool _sessionStarted;
    private string _effectiveDirectory = string.Empty;
    private string _requestedDirectory = string.Empty;
    private string _baseFileName = string.Empty;

    public LogService()
        : this(null)
    {
    }

    public LogService(LogServiceOptions? options)
    {
        _options = (options ?? new LogServiceOptions()).Clone().Normalize();
        _redactor = new LogRedactor(_options);
        _policy = new LogFilePolicy(_options);
        _emergency = new EmergencyLogSink(_options.EmergencyDirectory, _options);
        _baseFileName = _policy.BuildBaseName(_sessionStartUtc, _processId, _sessionId);
        _sessionText = _sessionId.ToString("D", CultureInfo.InvariantCulture);
        _maxLineBytes = ComputeMaxLineBytes(_options);
        _queue = Channel.CreateBounded<LogWorkItem>(new BoundedChannelOptions(_options.QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait
        });

        InitializeLogFile(_options.CustomLogDirectory);
        _writerTask = Task.Factory.StartNew(
            WriterLoop,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        _flusherTask = Task.Factory.StartNew(
            FlusherLoop,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    public event EventHandler<LogEvent>? LogReceived
    {
        add
        {
            if (value is null)
            {
                return;
            }

            try
            {
                if (!IsAccepting())
                {
                    return;
                }

                lock (_subscriberLock)
                {
                    _subscribers.Add(new SubscriberSink(this, value, _options.SubscriberQueueCapacity, _options.MaxQueuedBytes / 4));
                }
            }
            catch (Exception)
            {
            }
        }
        remove
        {
            if (value is null)
            {
                return;
            }

            SubscriberSink? sink = null;
            try
            {
                lock (_subscriberLock)
                {
                    int index = _subscribers.FindIndex(candidate => candidate.Matches(value));
                    if (index >= 0)
                    {
                        sink = _subscribers[index];
                        _subscribers.RemoveAt(index);
                    }
                }
            }
            catch (Exception)
            {
            }

            if (sink is not null)
            {
                sink.Complete();
            }
        }
    }

    public LogLevel MinLevel
    {
        get => (LogLevel)Volatile.Read(ref _minLevelValue);
        set => Volatile.Write(ref _minLevelValue, Math.Clamp((int)value, (int)LogLevel.Debug, (int)LogLevel.Fatal));
    }

    public string? EffectiveLogDirectory
    {
        get
        {
            lock (_stateLock)
            {
                return _effectiveDirectory.Length == 0 ? null : _effectiveDirectory;
            }
        }
    }

    public string? CurrentLogFile
    {
        get
        {
            lock (_stateLock)
            {
                return _effectiveDirectory.Length == 0 || _baseFileName.Length == 0
                    ? null
                    : Path.Combine(_effectiveDirectory, _baseFileName + LogFilePolicy.FileNameExtension);
            }
        }
    }

    public Guid SessionId => _sessionId;

    public LogServiceStatus Status => new(
        MinLevel,
        Interlocked.Read(ref _currentQueuedCount),
        Interlocked.Read(ref _queuedBytes),
        Interlocked.Read(ref _droppedCount),
        Interlocked.Read(ref _writeErrorCount),
        Interlocked.Read(ref _rotationErrorCount),
        Interlocked.Read(ref _rejectedEventCount),
        Interlocked.Read(ref _subscriberErrorCount),
        Interlocked.Read(ref _subscriberDroppedCount),
        Interlocked.Read(ref _disposeErrorCount),
        Interlocked.Read(ref _readErrorCount))
    {
        EmergencyWritten = _emergency.WrittenCount,
        EmergencyDropped = _emergency.DroppedCount,
        EmergencySuppressed = Interlocked.Read(ref _emergencySuppressedCount),
        FlushScheduleFailures = Interlocked.Read(ref _flushScheduleFailureCount)
    };

    public long QueuedEventCount => Interlocked.Read(ref _currentQueuedCount);

    public long QueuedEventBytes => Interlocked.Read(ref _queuedBytes);

    public long DroppedEventCount => Interlocked.Read(ref _droppedCount);

    public long WriteErrorCount => Interlocked.Read(ref _writeErrorCount);

    public long RotationErrorCount => Interlocked.Read(ref _rotationErrorCount);

    public long RejectedEventCount => Interlocked.Read(ref _rejectedEventCount);

    public long SubscriberErrorCount => Interlocked.Read(ref _subscriberErrorCount);

    public long SubscriberDroppedCount => Interlocked.Read(ref _subscriberDroppedCount);

    public long DisposeErrorCount => Interlocked.Read(ref _disposeErrorCount);

    public long ReadErrorCount => Interlocked.Read(ref _readErrorCount);

    public void InitializeLogFile()
    {
        InitializeLogFile(null);
    }

    public void InitializeLogFile(string? customLogDir)
    {
        if (!IsAccepting())
        {
            return;
        }

        try
        {
            string? rejection = null;
            lock (_stateLock)
            {
                if (customLogDir is not null)
                {
                    _requestedDirectory = customLogDir;
                }

                if (_sessionStarted)
                {
                    return;
                }

                string requested = _requestedDirectory;
                if (string.IsNullOrWhiteSpace(requested))
                {
                    requested = _options.CustomLogDirectory ?? string.Empty;
                }

                string? resolved = _policy.ResolveDirectory(requested, out rejection);
                if (resolved is null)
                {
                    _effectiveDirectory = string.Empty;
                }
                else
                {
                    _effectiveDirectory = resolved;
                    ApplyRetentionInternal();
                }
            }

            if (rejection is not null && _effectiveDirectory.Length > 0)
            {
                RecordWriteFailure("Каталог логов недоступен, применён резервный путь", null);
            }
        }
        catch (Exception ex)
        {
            RecordWriteFailure("Не удалось инициализировать каталог логов", ex);
        }
    }

    public void Log(LogLevel level, string message, string source = DefaultSource)
    {
        try
        {
            Write(new LogEvent
            {
                EventId = BuildLegacyEventId(source, level),
                Level = level,
                Status = level >= LogLevel.Error ? LogStatus.Failed : LogStatus.None,
                Source = source ?? DefaultSource,
                Message = message ?? string.Empty
            });
        }
        catch (Exception)
        {
            WriteLegacyFallback(level, LogStatus.None, message, source);
        }
    }

    public void DebugLog(string message, string source = DefaultSource) => Log(LogLevel.Debug, message, source);

    public void Info(string message, string source = DefaultSource) => Log(LogLevel.Info, message, source);

    public void Warn(string message, string source = DefaultSource) => Log(LogLevel.Warning, message, source);

    public void Error(string message, string source = DefaultSource) => Log(LogLevel.Error, message, source);

    public void Fatal(string message, string source = DefaultSource) => Log(LogLevel.Fatal, message, source);

    public void Exception(Exception ex, string message, string source = DefaultSource)
    {
        try
        {
            ExceptionInfo? info = null;
            try
            {
                if (ex is not null)
                {
                    info = ExceptionInfo.FromException(ex, _options.MaxExceptionDepth);
                }
            }
            catch (Exception)
            {
                info = null;
            }

            Write(new LogEvent
            {
                EventId = BuildLegacyEventId(source, LogLevel.Error),
                Level = LogLevel.Error,
                Status = LogStatus.Failed,
                Source = source ?? DefaultSource,
                Message = message ?? string.Empty,
                Exception = info
            });
        }
        catch (Exception)
        {
            WriteLegacyFallback(LogLevel.Error, LogStatus.Failed, message, source);
        }
    }

    public void Write(LogEvent logEvent)
    {
        if (logEvent is null)
        {
            return;
        }

        try
        {
            if ((int)logEvent.Level < Volatile.Read(ref _minLevelValue))
            {
                return;
            }

            LogEvent candidate = Normalize(logEvent);
            int estimate = EstimateEventBytes(candidate);

            if (!IsAccepting() || estimate > _options.MaxQueuedBytes)
            {
                DropEvent();
                return;
            }

            Enqueue(candidate, estimate);
        }
        catch (Exception ex)
        {
            try
            {
                RecordWriteFailure("Не удалось поставить событие в очередь журнала", ex);
            }
            catch (Exception)
            {
            }
        }
    }

    public void Write(
        string eventId,
        LogLevel level,
        string message,
        string source = DefaultSource,
        LogStatus status = LogStatus.None,
        LogContext? context = null,
        IReadOnlyDictionary<string, object?>? properties = null,
        Exception? exception = null)
    {
        try
        {
            Write(BuildEvent(eventId, level, status, message, exception, source, context, properties));
        }
        catch (Exception)
        {
            WriteLegacyFallback(level, status, message, source);
        }
    }

    public void Write(
        string eventId,
        LogLevel level,
        LogStatus status,
        string message,
        Exception? exception = null,
        string source = DefaultSource,
        LogContext? context = null,
        IReadOnlyDictionary<string, object?>? properties = null)
    {
        try
        {
            Write(BuildEvent(eventId, level, status, message, exception, source, context, properties));
        }
        catch (Exception)
        {
            WriteLegacyFallback(level, status, message, source);
        }
    }

    public void Flush()
    {
        if (!Flush(TimeSpan.FromMilliseconds(DefaultFlushTimeoutMilliseconds)))
        {
            Interlocked.Increment(ref _writeErrorCount);
        }
    }

    public bool Flush(TimeSpan timeout)
    {
        TimeSpan effective = timeout <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : timeout;
        try
        {
            if (!IsAccepting())
            {
                return false;
            }

            if (Environment.CurrentManagedThreadId == _writerThreadId)
            {
                bool markerPersisted = PersistDroppedMarker();
                return markerPersisted & FlushWriter();
            }

            TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            LogWorkItem probe = new(LogWorkKind.Flush, null, 0, completion);
            Stopwatch deadline = Stopwatch.StartNew();

            while (true)
            {
                if (_queue.Writer.TryWrite(probe))
                {
                    return WaitForCompletion(completion, Remaining(deadline, effective));
                }

                if (!IsAccepting())
                {
                    return false;
                }

                if (deadline.Elapsed >= effective)
                {
                    completion.TrySetResult(false);
                    return false;
                }

                Thread.Sleep(2);
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    public bool ClearCurrentLog()
    {
        try
        {
            if (!IsAccepting())
            {
                return false;
            }

            if (Environment.CurrentManagedThreadId == _writerThreadId)
            {
                return ClearPersisted();
            }

            TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            LogWorkItem request = new(LogWorkKind.Clear, null, 0, completion);
            Stopwatch deadline = Stopwatch.StartNew();
            TimeSpan effective = TimeSpan.FromMilliseconds(DefaultFlushTimeoutMilliseconds);

            while (!_queue.Writer.TryWrite(request))
            {
                if (!IsAccepting())
                {
                    return false;
                }

                if (deadline.Elapsed >= effective)
                {
                    return false;
                }

                Thread.Sleep(2);
            }

            return WaitForCompletion(completion, Remaining(deadline, effective));
        }
        catch (Exception)
        {
            return false;
        }
    }

    public string ReadCurrentLog()
    {
        try
        {
            if (!PrepareForRead())
            {
                return string.Empty;
            }

            if (!TryGetSessionFiles(out IReadOnlyList<string> files))
            {
                return string.Empty;
            }

            List<string> rawLines = new();
            int budget = _options.MaxRecentReadBytes;
            for (int index = files.Count - 1; index >= 0; index--)
            {
                if (!TryReadTailLines(files[index], _options.MaxExportLines, budget, rawLines, static _ => true, out int consumed))
                {
                    return string.Empty;
                }

                budget -= consumed;
                if (rawLines.Count >= _options.MaxExportLines || budget <= 0)
                {
                    break;
                }
            }

            List<string> rendered = new(rawLines.Count);
            for (int index = rawLines.Count - 1; index >= 0; index--)
            {
                rendered.Add(RenderLine(rawLines[index]));
            }

            return string.Join(Environment.NewLine, rendered);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    public IReadOnlyList<LogEvent> ReadRecentEvents(int maxCount)
    {
        List<LogEvent> result = new();
        try
        {
            if (maxCount <= 0 || !PrepareForRead())
            {
                return result;
            }

            if (!TryGetSessionFiles(out IReadOnlyList<string> files))
            {
                return result;
            }

            List<string> rawLines = new();
            int budget = _options.MaxRecentReadBytes;
            for (int index = files.Count - 1; index >= 0; index--)
            {
                if (!TryReadTailLines(files[index], maxCount, budget, rawLines, IsValidLine, out int consumed))
                {
                    return new List<LogEvent>();
                }

                budget -= consumed;
                if (rawLines.Count >= maxCount || budget <= 0)
                {
                    break;
                }
            }

            for (int index = rawLines.Count - 1; index >= 0; index--)
            {
                LogEvent? parsed = LogEventJson.TryParse(rawLines[index], ValidateConditionalSchemaForRead);
                if (parsed is not null)
                {
                    result.Add(Prepare(parsed));
                }
            }

            return result;
        }
        catch (Exception)
        {
            return new List<LogEvent>();
        }
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposeStarted, 1, 0) != 0)
        {
            WaitForDisposeCompletion();
            return;
        }

        bool onWriterThread = Environment.CurrentManagedThreadId == _writerThreadId;
        try
        {
            if (!onWriterThread && !Flush(TimeSpan.FromMilliseconds(DefaultFlushTimeoutMilliseconds)))
            {
                Interlocked.Increment(ref _disposeErrorCount);
            }

            Interlocked.CompareExchange(ref _lifecycleState, LifecycleStopping, LifecycleAccepting);
            try
            {
                _queue.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                RecordDisposeError(ex);
            }

            try
            {
                _shutdown.Cancel();
            }
            catch (Exception ex)
            {
                RecordDisposeError(ex);
            }

            DisposeSubscribers();
            if (!onWriterThread)
            {
                WaitBounded(_writerTask);
            }

            TryCloseWriterBestEffort();
            if (!onWriterThread)
            {
                WaitBounded(_flusherTask);
            }

            try
            {
                _shutdown.Dispose();
            }
            catch (Exception ex)
            {
                RecordDisposeError(ex);
            }
        }
        catch (Exception ex)
        {
            RecordDisposeError(ex);
        }
        finally
        {
            Interlocked.Exchange(ref _lifecycleState, LifecycleDisposed);
            _disposeCompletion.TrySetResult(true);
        }
    }

    public static string BuildLegacyEventId(string? source, LogLevel level)
    {
        try
        {
            return LogEvent.LegacyEventIdPrefix + LogRedactor.CompactSafeToken(source) + "." + LevelToken(level);
        }
        catch (Exception)
        {
            return LogEvent.LegacyEventIdPrefix + "unknown.info";
        }
    }

    public static int EstimateEventBytes(LogEvent logEvent)
    {
        if (logEvent is null)
        {
            return BaseEventEstimate;
        }

        long estimate = BaseEventEstimate;
        estimate += Size(logEvent.Message);
        estimate += Size(logEvent.EventId);
        estimate += Size(logEvent.Source);
        estimate += Size(logEvent.OperationId);
        estimate += Size(logEvent.ItemId);
        estimate += Size(logEvent.ProcessId);
        estimate += Size(logEvent.Tool);
        estimate += Size(logEvent.ToolVersion);
        estimate += Size(logEvent.Input);
        estimate += Size(logEvent.Output);
        estimate += Size(logEvent.StatusCode);
        estimate += Size(logEvent.ErrorCode);

        if (logEvent.Pid.HasValue) estimate += 16;
        if (logEvent.Attempt.HasValue) estimate += 8;
        if (logEvent.ExitCode.HasValue) estimate += 16;
        if (logEvent.DurationMs.HasValue) estimate += 24;

        try
        {
            if (logEvent.Properties is LogPropertyCollection snapshot)
            {
                LogPropertyCollection.Enumerator entries = snapshot.GetEnumerator();
                while (entries.MoveNext())
                {
                    KeyValuePair<string, object?> pair = entries.Current;
                    estimate += Size(pair.Key) + 8 + SizeOfValue(pair.Value);
                }
            }
            else if (logEvent.Properties is not null)
            {
                foreach (KeyValuePair<string, object?> pair in logEvent.Properties)
                {
                    estimate += Size(pair.Key) + 8 + SizeOfValue(pair.Value);
                }
            }

            if (logEvent.Exception is not null)
            {
                foreach (ExceptionInfo node in logEvent.Exception.EnumerateChain())
                {
                    estimate += 128 + Size(node.Type) + Size(node.Message) + Size(node.StackTrace);
                }
            }
        }
        catch (Exception)
        {
            estimate += 4096;
        }

        return (int)Math.Min(estimate, int.MaxValue);
    }

    private static int ComputeMaxLineBytes(LogServiceOptions options)
    {
        long payload = (long)options.MaxMessageLength + options.MaxPropertiesLength + (2L * options.MaxDetailLength) + 8192;
        long bound = Math.Max(4L * payload, 256L * 1024);
        return (int)Math.Min(bound, 64L * 1024 * 1024);
    }

    private static int Size(string? value) => value is null ? 0 : (value.Length * 2) + 8;

    private static int SizeOfValue(object? value) => value switch
    {
        null => 8,
        string text => (text.Length * 2) + 8,
        bool => 8,
        int or long or short or byte => 24,
        double or float or decimal => 32,
        _ => 64
    };

    private static string LevelToken(LogLevel level)
    {
        return level switch
        {
            LogLevel.Debug => "debug",
            LogLevel.Info => "info",
            LogLevel.Warning => "warning",
            LogLevel.Error => "error",
            LogLevel.Fatal => "fatal",
            _ => "info"
        };
    }

    private static TimeSpan Remaining(Stopwatch deadline, TimeSpan effective)
    {
        TimeSpan left = effective - deadline.Elapsed;
        return left <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : left;
    }

    private static bool WaitForCompletion(TaskCompletionSource<bool> completion, TimeSpan timeout)
    {
        if (!completion.Task.Wait(timeout))
        {
            return false;
        }

        try
        {
            return completion.Task.Result;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsDefined<TEnum>(TEnum value) where TEnum : struct, Enum => Enum.IsDefined(value);

    private bool IsAccepting() => Volatile.Read(ref _lifecycleState) == LifecycleAccepting;

    private static string ResolveApplicationVersion()
    {
        try
        {
            Assembly assembly = typeof(LogService).Assembly;
            string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrEmpty(informational))
            {
                int plus = informational.IndexOf('+', StringComparison.Ordinal);
                return plus > 0 ? informational[..plus] : informational;
            }

            return assembly.GetName().Version?.ToString() ?? "0.0.0";
        }
        catch (Exception)
        {
            return "0.0.0";
        }
    }

    private void WriteLegacyFallback(LogLevel level, LogStatus status, string message, string source)
    {
        try
        {
            Write(new LogEvent
            {
                EventId = BuildLegacyEventId(source, level),
                Level = level,
                Status = status,
                Source = LogEventMarkerNames.LogSource,
                Message = string.Empty
            });
        }
        catch (Exception)
        {
        }
    }

    private LogEvent BuildEvent(
        string eventId,
        LogLevel level,
        LogStatus status,
        string message,
        Exception? exception,
        string source,
        LogContext? context,
        IReadOnlyDictionary<string, object?>? properties)
    {
        ExceptionInfo? info = null;
        try
        {
            if (exception is not null)
            {
                info = ExceptionInfo.FromException(exception, _options.MaxExceptionDepth);
            }
        }
        catch (Exception)
        {
            info = null;
        }

        Dictionary<string, object?>? merged = null;
        try
        {
            if (context is not null && !context.IsEmpty)
            {
                merged = new Dictionary<string, object?>(context.ToProperties(), StringComparer.Ordinal);
            }
        }
        catch (Exception)
        {
            merged = null;
        }

        if (properties is not null)
        {
            Dictionary<string, object?> snapshot = new(StringComparer.Ordinal);
            try
            {
                foreach (KeyValuePair<string, object?> pair in properties)
                {
                    if (pair.Key is not null)
                    {
                        snapshot[pair.Key] = pair.Value;
                    }
                }
            }
            catch (Exception)
            {
                snapshot.Clear();
                snapshot[LogEventMarkerNames.UnlistedSnapshot] = LogRedactor.RedactedMarker;
            }

            merged ??= new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, object?> pair in snapshot)
            {
                merged[pair.Key] = pair.Value;
            }
        }

        return new LogEvent
        {
            EventId = eventId ?? string.Empty,
            Level = level,
            Status = status,
            Source = source ?? DefaultSource,
            Message = message ?? string.Empty,
            Properties = merged is null || merged.Count == 0 ? LogEvent.EmptyProperties : merged,
            Exception = info,
            OperationId = SafeResolve(context, static item => item.ResolveOperationId()),
            ItemId = SafeResolve(context, static item => item.ResolveItemId()),
            ProcessId = SafeResolve(context, static item => item.ResolveProcessId()),
            Attempt = SafeResolve(context, static item => item.ResolveAttempt()),
            Tool = SafeResolve(context, static item => item.ResolveTool())
        };
    }

    private static T? SafeResolve<T>(LogContext? context, Func<LogContext, T?> resolver) where T : struct
    {
        if (context is null)
        {
            return null;
        }

        try
        {
            return resolver(context);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? SafeResolve(LogContext? context, Func<LogContext, string?> resolver)
    {
        if (context is null)
        {
            return null;
        }

        try
        {
            return resolver(context);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private string? ValidateStructure(LogEvent logEvent)
    {
        string eventId = logEvent.EventId ?? string.Empty;

        try
        {
            if (string.IsNullOrWhiteSpace(eventId))
            {
                return null;
            }

            if (!LogRedactor.IsValidEventId(eventId))
            {
                return "invalid-event-id";
            }

            if (!IsDefined(logEvent.Level))
            {
                return "invalid-level";
            }

            if (!IsDefined(logEvent.Status))
            {
                return "invalid-status";
            }

            if (string.IsNullOrWhiteSpace(logEvent.Source))
            {
                return "invalid-source";
            }

            if (logEvent.Message is null)
            {
                return "invalid-message";
            }

            if (logEvent.Attempt is int attempt && attempt < 1)
            {
                return "invalid-attempt";
            }

            if (logEvent.DurationMs is double duration && (!double.IsFinite(duration) || duration < 0))
            {
                return "invalid-duration";
            }

            if (logEvent.ExitCode is int exitCode && exitCode < 0)
            {
                return "invalid-exit-code";
            }

            return ValidateConditionalSchema(logEvent);
        }
        catch (Exception)
        {
            return "validation-failed";
        }
    }

    private string? ValidateConditionalSchema(LogEvent logEvent)
    {
        return ValidateConditionalSchema(logEvent, HasSafeIdentifier);
    }

    /// <summary>
    /// Проверяет событие по той же условной схеме, что и реальная запись в журнал.
    /// Возвращает код отказа или null, если структура события допустима.
    /// Используется тестовыми двойниками <see cref="ILogService"/>, чтобы отклонённые
    /// события не проходили проверки качества незаметно.
    /// </summary>
    public static string? ValidateEventSchema(LogEvent logEvent)
    {
        if (logEvent is null)
        {
            return "invalid-event";
        }

        return ValidateConditionalSchema(logEvent, HasSafeIdentifierStatic);
    }

    private static bool HasSafeIdentifierStatic(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            return !string.Equals(LogRedactor.Default.SanitizeIdentifier(value), LogRedactor.UnknownIdentifier, StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static string? ValidateConditionalSchema(LogEvent logEvent, Func<string?, bool> hasSafeIdentifier)
    {
        try
        {
            if (logEvent.Attempt is int attempt && attempt < 1)
            {
                return "invalid-attempt";
            }

            string eventId = logEvent.EventId ?? string.Empty;
            if (eventId.StartsWith("exec.item.", StringComparison.OrdinalIgnoreCase))
            {
                if (!hasSafeIdentifier(logEvent.OperationId))
                {
                    return "exec-item-missing-operation-id";
                }

                return hasSafeIdentifier(logEvent.ItemId) ? null : "exec-item-missing-item-id";
            }

            if (eventId.StartsWith("process.", StringComparison.OrdinalIgnoreCase))
            {
                if (!hasSafeIdentifier(logEvent.OperationId))
                {
                    return "process-missing-operation-id";
                }

                if (!hasSafeIdentifier(logEvent.Tool))
                {
                    return "process-missing-tool";
                }

                if (!hasSafeIdentifier(logEvent.ProcessId))
                {
                    return "process-missing-internal-id";
                }

                if (eventId.Equals("process.started", StringComparison.OrdinalIgnoreCase))
                {
                    if (!logEvent.Pid.HasValue)
                    {
                        return "process-missing-os-pid";
                    }

                    if (logEvent.Pid.Value <= 0)
                    {
                        return "process-invalid-os-pid";
                    }

                    return logEvent.Status == LogStatus.Running ? null : "process-started-invalid-status";
                }

                if (!eventId.Equals("process.exit", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                if (!logEvent.Pid.HasValue)
                {
                    return "process-missing-os-pid";
                }

                if (logEvent.Pid.Value <= 0)
                {
                    return "process-invalid-os-pid";
                }

                if (!logEvent.ExitCode.HasValue)
                {
                    return "process-exit-missing-exit-code";
                }

                if (!logEvent.DurationMs.HasValue)
                {
                    return "process-exit-missing-duration";
                }

                if (!double.IsFinite(logEvent.DurationMs.Value) || logEvent.DurationMs.Value < 0d)
                {
                    return "process-exit-invalid-duration";
                }

                return IsTerminalStatus(logEvent.Status) ? null : "process-exit-invalid-status";
            }

            if (eventId.StartsWith("exec.queue.", StringComparison.OrdinalIgnoreCase)
                || eventId.StartsWith("batch.", StringComparison.OrdinalIgnoreCase))
            {
                return hasSafeIdentifier(logEvent.OperationId) ? null : "batch-missing-operation-id";
            }

            return null;
        }
        catch (Exception)
        {
            return "conditional-validation-failed";
        }
    }

    private string? ValidateConditionalSchemaForRead(LogEvent logEvent)
    {
        return ValidateConditionalSchema(logEvent, HasSafeIdentifier);
    }

    private static bool IsTerminalStatus(LogStatus status)
    {
        return status is LogStatus.Succeeded
            or LogStatus.Failed
            or LogStatus.Cancelled
            or LogStatus.Skipped
            or LogStatus.PartiallySucceeded;
    }

    private bool HasSafeIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            return !string.Equals(_redactor.SanitizeIdentifier(value), LogRedactor.UnknownIdentifier, StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private LogEvent CreateRejectedEvent(LogEvent source, string errorCode)
    {
        string safeSource = LogRedactor.UnknownIdentifier;
        try
        {
            safeSource = _redactor.SanitizeIdentifier(source.Source);
        }
        catch (Exception)
        {
        }

        return new LogEvent
        {
            EventId = LogEventMarkerNames.RejectedEvent,
            Level = LogLevel.Warning,
            Status = LogStatus.Failed,
            Source = LogEventMarkerNames.LogSource,
            SessionId = _sessionId,
            TimestampUtc = DateTimeOffset.UtcNow,
            Sequence = 0,
            Message = "Событие журнала отклонено проверкой структуры",
            ErrorCode = errorCode,
            Properties = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["ErrorCode"] = errorCode,
                ["Source"] = safeSource,
                ["DroppedCount"] = 1L
            }
        };
    }

    private LogEvent CreateReadRejectedEvent(LogEvent source, string errorCode)
    {
        string safeSource = LogRedactor.UnknownIdentifier;
        try
        {
            safeSource = _redactor.SanitizeIdentifier(source.Source);
        }
        catch (Exception)
        {
        }

        return new LogEvent
        {
            TimestampUtc = source.TimestampUtc,
            Sequence = source.Sequence,
            SessionId = source.SessionId,
            EventId = LogEventMarkerNames.RejectedEvent,
            Level = LogLevel.Warning,
            Status = LogStatus.Failed,
            Source = LogEventMarkerNames.LogSource,
            Message = "Событие журнала отклонено проверкой структуры",
            ErrorCode = errorCode,
            Properties = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["ErrorCode"] = errorCode,
                ["Source"] = safeSource
            }
        };
    }

    private void Enqueue(LogEvent candidate, int estimate)
    {
        if (!ReserveQueueBytes(estimate))
        {
            DropEvent();
            return;
        }

        Interlocked.Increment(ref _currentQueuedCount);
        try
        {
            if (_queue.Writer.TryWrite(new LogWorkItem(LogWorkKind.Event, candidate, estimate, null)))
            {
                return;
            }
        }
        catch (Exception)
        {
        }

        Interlocked.Decrement(ref _currentQueuedCount);
        ReleaseQueueBytes(estimate);
        DropEvent();
    }

    private bool ReserveQueueBytes(int estimate)
    {
        long current = Interlocked.Read(ref _queuedBytes);
        while (true)
        {
            long next = current + estimate;
            if (next > _options.MaxQueuedBytes)
            {
                return false;
            }

            long actual = Interlocked.CompareExchange(ref _queuedBytes, next, current);
            if (actual == current)
            {
                return true;
            }

            current = actual;
        }
    }

    private void ReleaseQueueBytes(int estimate)
    {
        if (estimate > 0)
        {
            Interlocked.Add(ref _queuedBytes, -estimate);
        }
    }

    private void DropEvent()
    {
        Interlocked.Increment(ref _droppedCount);
    }

    private long NextSequence() => Interlocked.Increment(ref _sequence);

    private void WriterLoop()
    {
        _writerThreadId = Environment.CurrentManagedThreadId;
        try
        {
            ChannelReader<LogWorkItem> reader = _queue.Reader;
            while (reader.WaitToReadAsync(_shutdown.Token).AsTask().GetAwaiter().GetResult())
            {
                DrainBatch(reader);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
        }
        finally
        {
            DrainBatch(_queue.Reader);
            if (!CloseWriter())
            {
                lock (_writerLock)
                {
                    HandleWriteFailureNoLock("Ошибка закрытия файла журнала", null, false);
                }
            }
        }
    }

    /// <summary>
    /// Забирает из очереди всю доступную пачку и обрабатывает её одним проходом:
    /// последовательность, сериализация, доставка подписчикам и файловая запись
    /// выполняются пакетами, а не по одному событию. Порядок по <c>Sequence</c>,
    /// порядок элементов Flush/Clear и все счётчики сохраняются.
    /// </summary>
    private void DrainBatch(ChannelReader<LogWorkItem> reader)
    {
        List<LogWorkItem> batch = _batch;
        batch.Clear();
        while (reader.TryRead(out LogWorkItem? item))
        {
            DequeueAccounting(item);
            batch.Add(item);
            if (batch.Count >= 4096)
            {
                break;
            }
        }

        if (batch.Count == 0)
        {
            return;
        }

        ProcessBatch(batch);
        batch.Clear();
    }

    /// <summary>
    /// Обрабатывает батч одним проходом: каждому событию сразу после присвоения
    /// <c>Sequence</c> и сериализации в общий буфер строк отдаётся доставка подписчикам,
    /// и только после этого буфер дописывается на диск. Подписчик (вкладка «Логи»)
    /// поэтому не ждёт ни файловой записи, ни файловой системы: ожидание диска
    /// остаётся только у самого последнего события батча.
    /// Порядок доставки совпадает с порядком присвоения <c>Sequence</c> и с порядком
    /// строк в файле, потому что всё три действия выполняются в одном цикле
    /// на единственном потоке записи.
    /// </summary>
    private void ProcessBatch(List<LogWorkItem> batch)
    {
        int count = batch.Count;

        try
        {
            PersistDroppedMarker();
        }
        catch (Exception ex)
        {
            RecordWriteFailure("Не удалось записать маркер потерь очереди журнала", ex);
        }

        try
        {
            for (int position = 0; position < count; position++)
            {
                ProcessWorkItem(batch[position]);
            }
        }
        finally
        {
            try
            {
                DrainLineBatch();
            }
            catch (Exception ex)
            {
                RecordWriteFailure("Не удалось сбросить буфер строк журнала", ex);
            }

            try
            {
                PersistDroppedMarker();
            }
            catch (Exception ex)
            {
                RecordWriteFailure("Не удалось записать маркер потерь очереди журнала", ex);
            }
        }
    }

    /// <summary>
    /// Обрабатывает один элемент батча под собственным try/catch: отказ одного события
    /// (нехватка памяти при расширении буфера строк, сбой сериализации) больше не
    /// уничтожает остаток батча — потеря учитывается счётчиком, остальные события
    /// продолжают доходить до файла и подписчиков, а ожидающие Flush/Clear всегда
    /// получают завершение. Номер <c>Sequence</c> расходуется до попытки записи,
    /// поэтому дыра возможна, но она никогда не молчаливая: на каждый потерянный элемент
    /// приходится маркер <c>log.queue.dropped</c> со счётчиком потерь.
    /// </summary>
    private void ProcessWorkItem(LogWorkItem item)
    {
        if (item.Kind == LogWorkKind.Flush)
        {
            bool flushed;
            try
            {
                flushed = PersistDroppedMarker() & FlushWriter();
            }
            catch (Exception ex)
            {
                RecordWriteFailure("Не удалось выполнить сброс файла журнала по требованию Flush", ex);
                flushed = false;
            }

            item.Completion?.TrySetResult(flushed);
            return;
        }

        if (item.Kind == LogWorkKind.Clear)
        {
            bool cleared;
            try
            {
                cleared = ClearPersisted();
            }
            catch (Exception ex)
            {
                RecordWriteFailure("Не удалось очистить файлы журнала по требованию Clear", ex);
                cleared = false;
            }

            item.Completion?.TrySetResult(cleared);
            return;
        }

        LogEvent? logEvent = item.Event;
        if (logEvent is null)
        {
            return;
        }

        LogEvent? sequenced = null;
        try
        {
            sequenced = logEvent with { Sequence = NextSequence() };
            AppendSerializedLine(sequenced);
        }
        catch (Exception ex)
        {
            sequenced = null;
            DropEvent();
            RecordWriteFailure("Не удалось обработать событие журнала", ex);
        }

        if (sequenced is null)
        {
            return;
        }

        try
        {
            Publish(sequenced, item.EstimatedBytes);
        }
        catch (Exception ex)
        {
            DropEvent();
            RecordWriteFailure("Не удалось доставить событие журнала подписчикам", ex);
        }
    }

    private void DequeueAccounting(LogWorkItem item)
    {
        if (item.Kind != LogWorkKind.Event)
        {
            return;
        }

        ReleaseQueueBytes(item.EstimatedBytes);
        Interlocked.Decrement(ref _currentQueuedCount);
    }

    /// <summary>
    /// Периодический сброс не является критичным для целостности журнала: сбой одной
    /// постановки Flush-запроса учитывается счётчиком и цикл продолжает работу, иначе
    /// flusher погибал бы навсегда и последующие сбросы не выполнялись бы до конца процесса.
    /// </summary>
    private void FlusherLoop()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                if (WaitForShutdown(_options.FlushIntervalMilliseconds))
                {
                    return;
                }

                bool scheduled;
                try
                {
                    scheduled = _queue.Writer.TryWrite(new LogWorkItem(LogWorkKind.Flush, null, 0, null));
                }
                catch (Exception)
                {
                    return;
                }

                if (!scheduled)
                {
                    Interlocked.Increment(ref _flushScheduleFailureCount);
                }
            }
        }
        catch (Exception)
        {
        }
    }

    private bool WaitForShutdown(int milliseconds)
    {
        try
        {
            return _shutdown.Token.WaitHandle.WaitOne(milliseconds);
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>
    /// Проверка структуры и redaction за один проход: раньше это были два последовательных
    /// вызова с двумя копиями <see cref="LogEvent"/>, теперь создаётся ровно один.
    /// </summary>
    private LogEvent Normalize(LogEvent logEvent)
    {
        string? rejection = ValidateStructure(logEvent);
        if (rejection is null)
        {
            return Prepare(
                logEvent,
                string.IsNullOrWhiteSpace(logEvent.EventId)
                    ? BuildLegacyEventId(logEvent.Source, logEvent.Level)
                    : logEvent.EventId!,
                resetSequence: true);
        }

        Interlocked.Increment(ref _rejectedEventCount);
        return Prepare(CreateRejectedEvent(logEvent, rejection), LogEventMarkerNames.RejectedEvent, resetSequence: true);
    }

    private LogEvent Prepare(LogEvent logEvent) =>
        Prepare(logEvent, logEvent.EventId ?? string.Empty, resetSequence: false);

    private LogEvent Prepare(LogEvent logEvent, string candidateEventId, bool resetSequence)
    {
        bool degraded = false;
        string message;
        IReadOnlyDictionary<string, object?> properties;
        ExceptionInfo? exceptionInfo;
        string eventId;
        string source;

        try
        {
            message = _redactor.RedactMessage(logEvent.Message);
        }
        catch (Exception)
        {
            message = LogRedactor.RedactedMarker;
            degraded = true;
        }

        try
        {
            properties = _redactor.RedactProperties(logEvent.Properties);
        }
        catch (Exception)
        {
            properties = LogEvent.EmptyProperties;
            degraded = true;
        }

        try
        {
            exceptionInfo = _redactor.RedactException(logEvent.Exception);
            properties = AddExceptionTruncationMarkers(properties, exceptionInfo);
        }
        catch (Exception)
        {
            exceptionInfo = null;
            degraded = true;
        }

        try
        {
            eventId = _redactor.SanitizeEventId(candidateEventId, LogEventMarkerNames.RejectedEvent);
        }
        catch (Exception)
        {
            eventId = LogRedactor.UnknownIdentifier;
            degraded = true;
        }

        try
        {
            source = _redactor.SanitizeIdentifier(logEvent.Source);
        }
        catch (Exception)
        {
            source = LogRedactor.UnknownIdentifier;
            degraded = true;
        }

        if (degraded)
        {
            return new LogEvent
            {
                TimestampUtc = logEvent.TimestampUtc == default ? _sessionStartUtc : logEvent.TimestampUtc,
                Sequence = 0,
                SessionId = _sessionId,
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

        return new LogEvent
        {
            TimestampUtc = logEvent.TimestampUtc == default
                ? DateTimeOffset.UtcNow
                : logEvent.TimestampUtc.ToUniversalTime(),
            Sequence = resetSequence ? 0 : logEvent.Sequence,
            SessionId = _sessionId,
            EventId = eventId,
            Level = logEvent.Level,
            Status = logEvent.Status,
            Source = source,
            OperationId = SafeIdentifier(logEvent.OperationId),
            ItemId = SafeIdentifier(logEvent.ItemId),
            ProcessId = SafeIdentifier(logEvent.ProcessId),
            Pid = logEvent.Pid,
            Attempt = logEvent.Attempt,
            Message = message,
            Properties = properties,
            Exception = exceptionInfo,
            Tool = SafeIdentifier(logEvent.Tool),
            ToolVersion = SafeIdentifier(logEvent.ToolVersion),
            ExitCode = logEvent.ExitCode,
            DurationMs = logEvent.DurationMs,
            Input = SafeToken(logEvent.Input),
            Output = SafeToken(logEvent.Output),
            StatusCode = SafeIdentifier(logEvent.StatusCode),
            ErrorCode = SafeIdentifier(logEvent.ErrorCode)
        };
    }

    private static IReadOnlyDictionary<string, object?> AddExceptionTruncationMarkers(
        IReadOnlyDictionary<string, object?> properties,
        ExceptionInfo? exception)
    {
        if (exception is null || !exception.ExceptionTruncated)
        {
            return properties;
        }

        return LogPropertyCollection.WithEntries(
            properties,
            LogEventMarkerNames.ExceptionTruncated,
            true,
            LogEventMarkerNames.OriginalExceptionCount,
            exception.OriginalChainLength,
            LogEventMarkerNames.KeptExceptionCount,
            exception.KeptChainLength);
    }

    private string? SafeIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            string safe = _redactor.SanitizeIdentifier(value);
            return safe == LogRedactor.UnknownIdentifier ? null : safe;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private string? SafeToken(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        try
        {
            return _redactor.RedactToken(value);
        }
        catch (Exception)
        {
            return LogRedactor.RedactedMarker;
        }
    }

    /// <summary>
    /// Сериализует событие прямо в общий буфер строк и дописывает его одной записью
    /// на пачку событий вместо одной записи (и одной строки UTF-16) на событие.
    /// </summary>
    private void AppendSerializedLine(LogEvent logEvent)
    {
        if (!_options.WriteFileEvents)
        {
            return;
        }

        lock (_writerLock)
        {
            LineWriteResult result = AppendLineNoLock(logEvent, true);
            if (result == LineWriteResult.Oversized)
            {
                AppendOversizedMarkerNoLock();
            }
        }
    }

    private LineWriteResult AppendLineNoLock(LogEvent logEvent, bool writeErrorMarker)
    {
        int start = _lineBatchCount;
        int lineBytes;
        try
        {
            lineBytes = SerializeIntoLineBatch(logEvent);
        }
        catch (Exception ex)
        {
            _lineBatchCount = start;
            RecordWriteFailure("Не удалось сериализовать событие журнала", ex);
            return LineWriteResult.Failed;
        }

        if (lineBytes + 1 > _options.MaxFileBytes)
        {
            _lineBatchCount = start;
            return LineWriteResult.Oversized;
        }

        if (!PrepareRotationNoLock(_lineBatchCount - start + lineBytes + 1, writeErrorMarker))
        {
            _lineBatchCount = start;
            return LineWriteResult.Failed;
        }

        if (EnsureLineBatchCapacityNoLock(lineBytes + 1))
        {
            _lineBatchCount = start;
            return LineWriteResult.Failed;
        }

        _lineBatch[_lineBatchCount++] = (byte)'\n';
        _writerBytes += lineBytes + 1;

        if (_lineBatchCount >= LineBatchBytes)
        {
            WriteLineBatchNoLock();
            ApplyRetentionIfBudgetAged();
        }

        return LineWriteResult.Written;
    }

    private void AppendOversizedMarkerNoLock()
    {
        LogEvent marker = CreateOversizedEventMarker();
        int start = _lineBatchCount;
        SerializeIntoLineBatch(marker);
        bool ready = _lineBatchCount - start + 1 <= _options.MaxFileBytes
            && !EnsureLineBatchCapacityNoLock(1)
            && PrepareRotationNoLock(_lineBatchCount + 1, true);

        if (!ready)
        {
            _lineBatchCount = start;
            DropEvent();
            RecordWriteFailure("Маркер отклонения oversized-события не записан", null);
            return;
        }

        int length = _lineBatchCount - start + 1;
        _lineBatch[_lineBatchCount++] = (byte)'\n';
        _writerBytes += length;

        WriteLineBatchNoLock();
    }

    /// <summary>
    /// Гарантирует, что в буфер строк можно дописать <paramref name="pending"/> байт:
    /// при необходимости сбрасывает накопленное, при достижении порога ротирует файл
    /// и открывает новый сегмент.
    /// </summary>
    private bool PrepareRotationNoLock(int pending, bool writeErrorMarker)
    {
        if (_stream is null && !EnsureWriterNoLock(writeErrorMarker))
        {
            return false;
        }

        if (_stream is null)
        {
            return false;
        }

        if (_writerBytes <= 0 || _writerBytes + pending <= _policy.RotationTriggerBytes)
        {
            return true;
        }

        if (!WriteLineBatchNoLock() || !RotateNoLock(writeErrorMarker))
        {
            return false;
        }

        return EnsureWriterNoLock(writeErrorMarker);
    }

    private int SerializeIntoLineBatch(LogEvent logEvent)
    {
        if (_options.FileFormat == LogFileFormat.Jsonl)
        {
            _jsonBuffer ??= new LogLineBuffer(this);
            _json ??= new Utf8JsonWriter(_jsonBuffer, LogEventJson.WriterOptions);
            _jsonBuffer.BeginLine();
            _json.Reset(_jsonBuffer);
            LogEventJson.WriteEvent(_json, logEvent, _applicationVersion, _sessionText);
            _json.Flush();
            return _jsonBuffer.EndLine();
        }

        return FormatTextIntoLineBatch(logEvent);
    }

    private int FormatTextIntoLineBatch(LogEvent logEvent)
    {
        _jsonBuffer ??= new LogLineBuffer(this);
        string line = LogEvent.Format(logEvent, includeDetail: true);
        int byteCount = Encoding.UTF8.GetByteCount(line);
        _jsonBuffer.BeginLine();
        Span<byte> span = _jsonBuffer.GetSpan(byteCount);
        int written = Encoding.UTF8.GetBytes(line, span);
        _jsonBuffer.Advance(written);
        return _jsonBuffer.EndLine();
    }

    private bool EnsureLineBatchCapacityNoLock(int required)
    {
        byte[]? batch = _lineBatch;
        if (batch is null)
        {
            batch = new byte[LineBatchBytes];
            _lineBatch = batch;
        }

        if (batch.Length - _lineBatchCount >= required)
        {
            return false;
        }

        if (batch.Length > int.MaxValue - required)
        {
            HandleWriteFailureNoLock("Строка журнала не помещается в буфер записи", null, true);
            return true;
        }

        Array.Resize(ref batch, Math.Max(batch.Length * 2, _lineBatchCount + required));
        _lineBatch = batch;
        return false;
    }

    /// <summary>
    /// Сбрасывает накопленные строки в файл одной операцией записи.
    /// </summary>
    private bool WriteLineBatchNoLock()
    {
        Stream? stream = _stream;
        if (stream is null)
        {
            _lineBatchCount = 0;
            return Volatile.Read(ref _writerStopping) == 0;
        }

        if (_lineBatchCount == 0)
        {
            return true;
        }

        int count = _lineBatchCount;
        byte[]? buffer = _lineBatch;
        if (buffer is null)
        {
            _lineBatchCount = 0;
            return Volatile.Read(ref _writerStopping) == 0;
        }

        try
        {
            stream.Write(buffer, 0, count);
            _lineBatchCount = 0;

            return true;
        }
        catch (Exception ex)
        {
            _lineBatchCount = 0;
            HandleWriteFailureNoLock("Ошибка записи события журнала", ex, true);
            return false;
        }
    }

    /// <summary>
    /// Сбрасывает буфер строк в файл и в файловую систему.
    /// </summary>
    private void DrainLineBatch(bool force = false)
    {
        if (_lineBatchCount == 0)
        {
            return;
        }

        if (!force && _lineBatchCount < LineBatchBytes && Environment.TickCount - _lastFlushTick < _options.FlushIntervalMilliseconds)
        {
            return;
        }

        WriteLineBatchNoLock();
        _lastFlushTick = Environment.TickCount;
    }

    private LogEvent CreateOversizedEventMarker()
    {
        return new LogEvent
        {
            EventId = LogEventMarkerNames.RejectedEvent,
            Level = LogLevel.Warning,
            Status = LogStatus.Failed,
            Source = LogEventMarkerNames.LogSource,
            SessionId = _sessionId,
            TimestampUtc = DateTimeOffset.UtcNow,
            Sequence = NextSequence(),
            Message = "Событие журнала отклонено из-за превышения размера файла",
            ErrorCode = "LINE_TOO_LARGE",
            Properties = LogPropertyCollection.Create(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["ErrorCode"] = "LINE_TOO_LARGE"
            })
        };
    }

    private void HandleWriteFailureNoLock(string reason, Exception? exception, bool writeErrorMarker)
    {
        bool closed = CloseWriterNoLock();
        bool repaired = RepairTrailingPartialLineNoLock();
        if (!closed || !repaired)
        {
            Interlocked.Exchange(ref _writerPoisoned, 1);
            Interlocked.Exchange(ref _writerStopping, 1);
        }

        RecordWriteFailure(reason, exception);
        if (writeErrorMarker && Volatile.Read(ref _writerStopping) == 0)
        {
            WriteErrorMarker(LogEventMarkerNames.WriteFailure);
        }
    }

    private void HandleMarkerFailure(Exception? exception)
    {
        bool closed = CloseWriterNoLock();
        bool repaired = RepairTrailingPartialLineNoLock();
        if (!closed || !repaired)
        {
            Interlocked.Exchange(ref _writerStopping, 1);
        }

        Interlocked.Exchange(ref _writerPoisoned, 1);
        Interlocked.Increment(ref _writeErrorCount);
        RecordEmergency("Ошибка записи маркера файлового канала журнала", exception);
    }

    private bool RepairTrailingPartialLineNoLock()
    {
        try
        {
            string? path = CurrentLogFile;
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            if (Directory.Exists(path))
            {
                return false;
            }

            if (!File.Exists(path))
            {
                return true;
            }

            using FileStream stream = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length == 0)
            {
                return true;
            }

            long fileLength = stream.Length;
            long contentEnd = fileLength;
            bool hasTerminator = false;
            stream.Seek(-1, SeekOrigin.End);
            if (stream.ReadByte() == '\n')
            {
                hasTerminator = true;
                contentEnd--;
                if (contentEnd > 0)
                {
                    stream.Seek(contentEnd - 1, SeekOrigin.Begin);
                    if (stream.ReadByte() == '\r')
                    {
                        contentEnd--;
                    }
                }
            }

            long lineStart = FindPreviousNewline(stream, contentEnd);
            if (IsCompletePhysicalLine(stream, lineStart, contentEnd))
            {
                if (!hasTerminator)
                {
                    stream.Seek(0, SeekOrigin.End);
                    stream.WriteByte((byte)'\n');
                    stream.Flush();
                }

                return true;
            }

            stream.SetLength(lineStart);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool IsCompletePhysicalLine(FileStream stream, long start, long end)
    {
        long length = end - start;
        if (length <= 0 || length > _maxLineBytes || length > int.MaxValue)
        {
            return false;
        }

        byte[] bytes = new byte[(int)length];
        stream.Seek(start, SeekOrigin.Begin);
        int read = FillBuffer(stream, bytes, bytes.Length);
        if (read != bytes.Length)
        {
            return false;
        }

        string line = Encoding.UTF8.GetString(bytes, 0, read);
        return LogEventJson.TryParse(line) is not null;
    }

    private static long FindPreviousNewline(FileStream stream, long position)
    {
        byte[] buffer = new byte[4096];
        long cursor = position;
        while (cursor > 0)
        {
            int count = (int)Math.Min(buffer.Length, cursor);
            cursor -= count;
            stream.Seek(cursor, SeekOrigin.Begin);
            int read = FillBuffer(stream, buffer, count);
            for (int index = read - 1; index >= 0; index--)
            {
                if (buffer[index] == (byte)'\n')
                {
                    return cursor + index + 1;
                }
            }
        }

        return 0;
    }

    private bool EnsureWriterNoLock(bool reportFailure = true)
    {
        if (Volatile.Read(ref _writerStopping) != 0)
        {
            return false;
        }

        if (_stream is null && Volatile.Read(ref _writerPoisoned) != 0 && !RepairTrailingPartialLineNoLock())
        {
            Interlocked.Exchange(ref _writerPoisoned, 1);
            Interlocked.Exchange(ref _writerStopping, 1);
            RecordWriteFailure("Не удалось восстановить файл журнала после сбоя записи", null);
            return false;
        }

        if (_stream is not null)
        {
            if (Volatile.Read(ref _writerPoisoned) != 0)
            {
                bool closed = CloseWriterNoLock();
                bool repaired = RepairTrailingPartialLineNoLock();
                if (!closed || !repaired)
                {
                    Interlocked.Exchange(ref _writerPoisoned, 1);
                    Interlocked.Exchange(ref _writerStopping, 1);
                    RecordWriteFailure("Не удалось восстановить файл журнала после сбоя записи", null);
                }

                return false;
            }

            return true;
        }

        string directory;
        string baseName;
        lock (_stateLock)
        {
            directory = _effectiveDirectory;
            baseName = _baseFileName;
        }

        if (directory.Length == 0 || baseName.Length == 0)
        {
            if (reportFailure)
            {
                RecordWriteFailure("Каталог файла журнала недоступен: запись пропущена", null);
            }

            return false;
        }

        _sessionStarted = true;
        string path = Path.Combine(directory, baseName + LogFilePolicy.FileNameExtension);

        try
        {
            Directory.CreateDirectory(directory);
            long existing = SafeFileLength(path);
            if (existing >= _policy.RotationTriggerBytes)
            {
                string? rotated = _policy.Rotate(path, out string? rotateError);
                if (rotated is null || rotateError is not null)
                {
                    if (reportFailure)
                    {
                        Interlocked.Increment(ref _rotationErrorCount);
                        Interlocked.Increment(ref _writeErrorCount);
                        RecordWriteFailure("Не удалось ротировать переполненный файл журнала", null);
                    }

                    return false;
                }

                existing = SafeFileLength(path);
            }

            FileStream stream = new(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete,
                WriterBufferSize,
                FileOptions.SequentialScan);

            _stream = stream;
            Interlocked.Exchange(ref _writerPoisoned, 0);
            _writerBytes = existing;
            _lastFlushTick = Environment.TickCount;
            return true;
        }
        catch (Exception ex)
        {
            _stream = null;
            if (reportFailure)
            {
                RecordWriteFailure("Не удалось открыть файл журнала", ex);
            }

            return false;
        }
    }

    private bool RotateNoLock(bool writeErrorMarker)
    {
        if (!FlushWriterNoLock(writeErrorMarker))
        {
            Interlocked.Increment(ref _rotationErrorCount);
            return false;
        }

        if (!CloseWriterNoLock())
        {
            Interlocked.Increment(ref _rotationErrorCount);
            HandleWriteFailureNoLock("Ошибка закрытия файла журнала при ротации", null, writeErrorMarker);
            return false;
        }

        _writerBytes = 0;

        string? live = CurrentLogFile;
        if (string.IsNullOrEmpty(live))
        {
            Interlocked.Increment(ref _rotationErrorCount);
            RecordWriteFailure("Ротация невозможна: путь файла журнала неизвестен", null);
            return false;
        }

        string? error;
        string? result = _policy.Rotate(live, out error);
        if (result is null || error is not null)
        {
            Interlocked.Increment(ref _rotationErrorCount);
            if (writeErrorMarker)
            {
                RecordWriteFailure("Не удалось выполнить ротацию файла журнала", null);
                WriteErrorMarker(LogEventMarkerNames.RotationFailure);
            }

            return false;
        }

        ApplyRetentionInternal();
        return true;
    }

    private void ApplyRetentionInternal()
    {
        string directory = _effectiveDirectory;
        if (directory.Length == 0)
        {
            return;
        }

        _policy.ApplyRetention(directory, CurrentLogFile);
        _retentionAnchorBytes = _writerBytes;
    }

    /// <summary>
    /// Суммарный бюджет каталога проверяется не только на ротации: живой файл растёт
    /// между ротациями, и без промежуточной проверки каталог мог бы превысить
    /// MaxDirectoryBytes на величину, набранную после последнего прохода retention.
    /// Проверка выполняется по факту накопленной после последнего прохода половины
    /// порога ротации и не чаще, чем раз в интервал периодического сброса: иначе
    /// обход каталога выполнялся бы на каждой записи и подписывал файл сам у себя.
    /// </summary>
    private void ApplyRetentionIfBudgetAged()
    {
        long step = _policy.RotationTriggerBytes / 2;
        if (step <= 0 || _writerBytes < _retentionAnchorBytes + step)
        {
            return;
        }

        int now = Environment.TickCount;
        if (now - _retentionCheckTick < _options.FlushIntervalMilliseconds)
        {
            return;
        }

        _retentionCheckTick = now;
        ApplyRetentionInternal();
    }

    private bool PersistDroppedMarker()
    {
        long dropped = Interlocked.Read(ref _droppedCount);
        if (dropped <= Interlocked.Read(ref _reportedDroppedCount))
        {
            return true;
        }

        if (!_options.WriteFileEvents)
        {
            Interlocked.Exchange(ref _reportedDroppedCount, dropped);
            return true;
        }

        LogEvent marker = new()
        {
            EventId = LogEventMarkerNames.DroppedMarker,
            Level = LogLevel.Warning,
            Status = LogStatus.PartiallySucceeded,
            Source = LogEventMarkerNames.LogSource,
            SessionId = _sessionId,
            TimestampUtc = DateTimeOffset.UtcNow,
            Sequence = NextSequence(),
            Message = "События журнала отброшены из-за переполнения очереди записи",
            Properties = LogPropertyCollection.Create(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["DroppedCount"] = dropped,
                ["Reason"] = "QUEUE_OVERFLOW",
                ["Total"] = _options.QueueCapacity
            })
        };

        lock (_writerLock)
        {
            LineWriteResult result = AppendLineNoLock(marker, false);
            if (result == LineWriteResult.Written)
            {
                DrainLineBatch(force: true);
                FlushStreamNoLock(false);
                Interlocked.Exchange(ref _reportedDroppedCount, dropped);
                return true;
            }

            if (result == LineWriteResult.Oversized)
            {
                Interlocked.Increment(ref _writeErrorCount);
                RecordEmergency("Маркер переполнения превышает лимит файла журнала", null);
            }
            else
            {
                HandleMarkerFailure(null);
            }

            return false;
        }
    }

    private void WriteErrorMarker(string eventId)
    {
        if (!_options.WriteFileEvents || Interlocked.CompareExchange(ref _inErrorMarker, 1, 0) != 0)
        {
            return;
        }

        Exception? failure = null;
        try
        {
            LogEvent marker = new()
            {
                EventId = _redactor.SanitizeEventId(eventId, LogEventMarkerNames.RejectedEvent),
                Level = LogLevel.Error,
                Status = LogStatus.Failed,
                Source = LogEventMarkerNames.LogSource,
                SessionId = _sessionId,
                TimestampUtc = DateTimeOffset.UtcNow,
                Sequence = NextSequence(),
                Message = "Ошибка файлового канала журнала",
                ErrorCode = "FILE_CHANNEL_FAILURE",
                Properties = LogPropertyCollection.Create(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["ErrorCode"] = "FILE_CHANNEL_FAILURE",
                    ["Stage"] = LogRedactor.CompactSafeToken(eventId)
                })
            };

            lock (_writerLock)
            {
                if (Volatile.Read(ref _writerPoisoned) != 0)
                {
                    bool closed = CloseWriterNoLock();
                    bool repaired = RepairTrailingPartialLineNoLock();
                    if (!closed || !repaired)
                    {
                        Interlocked.Exchange(ref _writerPoisoned, 1);
                        Interlocked.Exchange(ref _writerStopping, 1);
                        failure = new IOException("Error marker could not be prepared");
                    }
                }

                if (failure is null)
                {
                    LineWriteResult result = AppendLineNoLock(marker, false);
                    if (result == LineWriteResult.Written)
                    {
                        DrainLineBatch(force: true);
                        if (!FlushStreamNoLock(false))
                        {
                            failure = new IOException("Error marker was not written");
                        }
                    }
                    else
                    {
                        failure = new IOException("Error marker was not written");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            if (failure is not null)
            {
                HandleMarkerFailure(failure);
            }

            Interlocked.Exchange(ref _inErrorMarker, 0);
        }
    }

    /// <summary>
    /// Доставка подписчикам не бросает исключений: сбой одного подписчика не должен
    /// прерывать ни сериализацию, ни файловую запись остальных событий батча.
    /// </summary>
    private void Publish(LogEvent logEvent, int estimatedBytes)
    {
        try
        {
            SubscriberSink[] sinks;
            lock (_subscriberLock)
            {
                if (_subscribers.Count == 0)
                {
                    return;
                }

                sinks = _subscribers.ToArray();
            }

            LogEvent published = FreezeForSubscribers(logEvent);
            foreach (SubscriberSink sink in sinks)
            {
                sink.TryEnqueue(new SubscriberItem(published, estimatedBytes));
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// Подписчики получают один и тот же неизменяемый экземпляр события:
    /// свойства после redaction уже иммутабельны, поэтому копировать словарь
    /// на каждого подписчика не требуется.
    /// </summary>
    private static LogEvent FreezeForSubscribers(LogEvent logEvent) =>
        logEvent.Properties is LogPropertyCollection
            ? logEvent
            : logEvent with { Properties = LogPropertyCollection.Freeze(logEvent.Properties) };

    private void ReportSubscriberFailure()
    {
        Interlocked.Increment(ref _subscriberErrorCount);
    }

    private void ReportSubscriberDrop()
    {
        Interlocked.Increment(ref _subscriberDroppedCount);
    }

    private bool PrepareForRead()
    {
        if (!IsAccepting())
        {
            return _writerTask.IsCompleted;
        }

        bool flushed = Flush(TimeSpan.FromMilliseconds(DefaultFlushTimeoutMilliseconds));
        if (flushed || Environment.CurrentManagedThreadId == _writerThreadId)
        {
            return true;
        }

        Interlocked.Increment(ref _writeErrorCount);
        return false;
    }

    private bool TryGetSessionFiles(out IReadOnlyList<string> files)
    {
        files = Array.Empty<string>();
        string directory;
        string baseName;
        lock (_stateLock)
        {
            directory = _effectiveDirectory;
            baseName = _baseFileName;
        }

        if (directory.Length == 0)
        {
            return false;
        }

        IReadOnlyList<string> found = _policy.EnumerateSessionFiles(directory, baseName, out bool completed);
        files = found;
        if (!completed)
        {
            Interlocked.Increment(ref _readErrorCount);
        }

        return completed;
    }

    private bool ClearPersisted()
    {
        lock (_writerLock)
        {
            return ClearPersistedNoLock();
        }
    }

    private bool ClearPersistedNoLock()
    {
        if (!FlushWriterNoLock())
        {
            return false;
        }

        string directory;
        string baseName;
        lock (_stateLock)
        {
            directory = _effectiveDirectory;
            baseName = _baseFileName;
        }

        if (!CloseWriterNoLock())
        {
            HandleWriteFailureNoLock("Ошибка закрытия файла журнала при очистке", null, false);
            return false;
        }

        _writerBytes = 0;
        if (directory.Length == 0 || baseName.Length == 0)
        {
            return false;
        }

        bool success = true;
        try
        {
            IReadOnlyList<string> files = _policy.EnumerateSessionFiles(directory, baseName, out bool completed);
            if (!completed)
            {
                RecordWriteFailure("Не удалось получить список файлов сессии для очистки", null);
                return false;
            }

            foreach (string file in files)
            {
                if (!TryDeleteFile(file))
                {
                    success = false;
                }
            }

            string live = Path.Combine(directory, baseName + LogFilePolicy.FileNameExtension);
            if (File.Exists(live))
            {
                using FileStream stream = new(live, FileMode.Truncate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                stream.SetLength(0);
            }
        }
        catch (Exception ex)
        {
            RecordWriteFailure("Не удалось очистить текущий файл журнала", ex);
            return false;
        }

        return success;
    }

    private bool FlushWriter()
    {
        lock (_writerLock)
        {
            return FlushWriterNoLock();
        }
    }

    private bool FlushWriterNoLock(bool reportFailure = true)
    {
        Stream? stream = _stream;
        if (stream is null)
        {
            _lineBatchCount = 0;
            return Volatile.Read(ref _writerStopping) == 0;
        }

        try
        {
            if (!WriteLineBatchNoLock())
            {
                return false;
            }

            stream.Flush();
            _lastFlushTick = Environment.TickCount;

            return true;
        }
        catch (Exception ex)
        {
            HandleWriteFailureNoLock("Не удалось сбросить буфер файла журнала", ex, reportFailure);
            return false;
        }
    }

    private bool FlushStreamNoLock(bool reportFailure)
    {
        Stream? stream = _stream;
        if (stream is null)
        {
            return Volatile.Read(ref _writerStopping) == 0;
        }

        try
        {
            stream.Flush();
            _lastFlushTick = Environment.TickCount;
            return true;
        }
        catch (Exception ex)
        {
            HandleWriteFailureNoLock("Не удалось сбросить буфер файла журнала", ex, reportFailure);
            return false;
        }
    }

    private bool CloseWriter()
    {
        lock (_writerLock)
        {
            return CloseWriterNoLock();
        }
    }

    private bool CloseWriterNoLock()
    {
        Stream? stream = _stream;
        _stream = null;
        if (stream is null)
        {
            _lineBatchCount = 0;
            return true;
        }

        bool success = true;
        try
        {
            if (WriteLineBatchNoLock())
            {
                stream.Flush();
            }
            else
            {
                success = false;
            }
        }
        catch (Exception ex)
        {
            success = false;
            RecordDisposeError(ex);
        }

        try
        {
            stream.Dispose();
        }
        catch (Exception ex)
        {
            success = false;
            RecordDisposeError(ex);
        }

        _lineBatchCount = 0;
        Interlocked.Exchange(ref _writerPoisoned, success ? 0 : 1);
        return success;
    }

    private void DisposeSubscribers()
    {
        SubscriberSink[] sinks;
        try
        {
            lock (_subscriberLock)
            {
                sinks = _subscribers.ToArray();
                _subscribers.Clear();
            }
        }
        catch (Exception ex)
        {
            RecordDisposeError(ex);
            return;
        }

        List<Task> pumps = new(sinks.Length);
        foreach (SubscriberSink sink in sinks)
        {
            sink.Complete();
            pumps.Add(sink.Pump);
        }

        if (pumps.Count == 0 || Environment.CurrentManagedThreadId == _writerThreadId)
        {
            return;
        }

        try
        {
            if (!Task.WaitAll(pumps.ToArray(), DisposeWaitMilliseconds))
            {
                Interlocked.Increment(ref _disposeErrorCount);
            }
        }
        catch (Exception ex)
        {
            RecordDisposeError(ex);
        }
    }

    private void RecordDisposeError(Exception? exception)
    {
        Interlocked.Increment(ref _disposeErrorCount);
        if (exception is not null)
        {
            RecordEmergency("Ошибка освобождения ресурса журнала", null);
        }
    }

    private void RecordWriteFailure(string reason, Exception? exception)
    {
        Interlocked.Increment(ref _writeErrorCount);
        RecordEmergency(reason, exception);
    }

    private void RecordEmergency(string reason, Exception? exception)
    {
        if (Interlocked.Decrement(ref _emergencyBudget) < 0)
        {
            Interlocked.Increment(ref _emergencySuppressedCount);
            return;
        }

        try
        {
            _emergency.Write(
                _redactor.RedactToken(reason),
                exception,
                LogEventMarkerNames.LogSource,
                "log-" + Guid.NewGuid().ToString("N"),
                _sessionId,
                LogLevel.Error);
        }
        catch (Exception)
        {
        }
    }

    private void RecordSubscriberError(SinkErrorKind kind)
    {
        if (kind == SinkErrorKind.Dropped)
        {
            ReportSubscriberDrop();
        }
        else
        {
            ReportSubscriberFailure();
        }
    }

    private string RenderLine(string rawLine)
    {
        LogEvent? parsed = LogEventJson.TryParse(rawLine);
        if (parsed is not null)
        {
            string? error = ValidateConditionalSchemaForRead(parsed);
            return LogEvent.Format(
                Prepare(error is null ? parsed : CreateReadRejectedEvent(parsed, error)),
                true);
        }

        try
        {
            return LogRedactor.Truncate(_redactor.RedactToken(rawLine), _options.MaxMessageLength);
        }
        catch (Exception)
        {
            return LogRedactor.RedactedMarker;
        }
    }

    private bool IsValidLine(string line) => LogEventJson.TryParse(line, ValidateConditionalSchemaForRead) is not null;

    private bool TryReadTailLines(
        string path,
        int maxLines,
        int byteBudget,
        List<string> sink,
        Func<string, bool> accept,
        out int consumed)
    {
        consumed = 0;
        if (maxLines <= 0 || byteBudget <= 0)
        {
            return true;
        }

        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long position = stream.Length;
            byte[] chunk = new byte[ReadChunkBytes];
            List<byte> reversed = new(1024);
            int limit = Math.Max(_maxLineBytes, ReadChunkBytes);

            while (position > 0 && consumed < byteBudget && sink.Count < maxLines)
            {
                int remaining = byteBudget - consumed;
                int want = (int)Math.Min(Math.Min(chunk.Length, position), remaining);
                position -= want;
                stream.Seek(position, SeekOrigin.Begin);
                int read = FillBuffer(stream, chunk, want);
                consumed += read;

                for (int index = read - 1; index >= 0; index--)
                {
                    byte current = chunk[index];
                    if (current == (byte)'\n')
                    {
                        if (AppendLine(reversed, sink, maxLines, accept))
                        {
                            break;
                        }
                    }
                    else if (current != (byte)'\r')
                    {
                        reversed.Add(current);
                        if (reversed.Count > limit)
                        {
                            reversed.Clear();
                        }
                    }
                }
            }

            if (sink.Count < maxLines && position == 0 && reversed.Count > 0)
            {
                AppendLine(reversed, sink, maxLines, accept);
            }
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _readErrorCount);
            return false;
        }

        return true;
    }

    private static int FillBuffer(FileStream stream, byte[] buffer, int count)
    {
        int total = 0;
        while (total < count)
        {
            int read = stream.Read(buffer, total, count - total);
            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static bool AppendLine(List<byte> reversed, List<string> sink, int maxLines, Func<string, bool> accept)
    {
        if (reversed.Count == 0)
        {
            return false;
        }

        byte[] bytes = new byte[reversed.Count];
        for (int index = 0; index < reversed.Count; index++)
        {
            bytes[index] = reversed[reversed.Count - 1 - index];
        }

        reversed.Clear();
        string line = Encoding.UTF8.GetString(bytes);
        if (accept(line))
        {
            sink.Add(line);
        }

        return sink.Count >= maxLines;
    }

    private static long SafeFileLength(string path)
    {
        try
        {
            FileInfo info = new(path);
            return info.Exists ? info.Length : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static bool TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool TryCloseWriterBestEffort()
    {
        bool entered = Monitor.TryEnter(_writerLock, DisposeWaitMilliseconds);
        if (!entered)
        {
            Interlocked.Exchange(ref _writerStopping, 1);
            Interlocked.Increment(ref _disposeErrorCount);
            return false;
        }

        try
        {
            Interlocked.Exchange(ref _writerStopping, 1);
            bool closed = CloseWriterNoLock();
            if (!closed)
            {
                HandleWriteFailureNoLock("Ошибка закрытия файла журнала", null, false);
            }

            return closed;
        }
        finally
        {
            Monitor.Exit(_writerLock);
        }
    }

    private void WaitForDisposeCompletion()
    {
        try
        {
            if (!_disposeCompletion.Task.Wait(DisposeWaitMilliseconds))
            {
                Interlocked.Increment(ref _disposeErrorCount);
            }
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _disposeErrorCount);
        }
    }

    private void WaitBounded(Task task)
    {
        try
        {
            if (!task.Wait(DisposeWaitMilliseconds))
            {
                Interlocked.Increment(ref _disposeErrorCount);
            }
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _disposeErrorCount);
        }
    }

    private enum LineWriteResult
    {
        Written = 0,
        Oversized = 1,
        Failed = 2
    }

    private enum LogWorkKind
    {
        Event = 0,
        Flush = 1,
        Clear = 2
    }

    internal enum SinkErrorKind
    {
        Error = 0,
        Dropped = 1
    }

    private sealed class LogWorkItem
    {
        public LogWorkItem(LogWorkKind kind, LogEvent? logEvent, int estimatedBytes, TaskCompletionSource<bool>? completion)
        {
            Kind = kind;
            Event = logEvent;
            EstimatedBytes = estimatedBytes;
            Completion = completion;
        }

        public LogWorkKind Kind { get; }

        public LogEvent? Event { get; }

        public int EstimatedBytes { get; }

        public TaskCompletionSource<bool>? Completion { get; }
    }

    private readonly struct SubscriberItem
    {
        public SubscriberItem(LogEvent logEvent, int estimatedBytes)
        {
            LogEvent = logEvent;
            EstimatedBytes = estimatedBytes;
        }

        public LogEvent LogEvent { get; }

        public int EstimatedBytes { get; }
    }

    /// <summary>
    /// Приёмник байтов сериализатора, пишущий прямо в общий буфер строк файла:
    /// между JSON-строками не создаётся ни строки UTF-16, ни отдельного буфера.
    /// </summary>
    private sealed class LogLineBuffer : IBufferWriter<byte>
    {
        private readonly LogService _owner;
        private int _lineStart;

        public LogLineBuffer(LogService owner) => _owner = owner;

        public void BeginLine() => _lineStart = _owner._lineBatchCount;

        public int EndLine() => _owner._lineBatchCount - _lineStart;

        public void Advance(int count) => _owner._lineBatchCount += count;

        public Memory<byte> GetMemory(int sizeHint)
        {
            EnsureCapacity(sizeHint);
            return new Memory<byte>(_owner._lineBatch!, _owner._lineBatchCount, _owner._lineBatch!.Length - _owner._lineBatchCount);
        }

        public Span<byte> GetSpan(int sizeHint)
        {
            EnsureCapacity(sizeHint);
            return new Span<byte>(_owner._lineBatch!, _owner._lineBatchCount, _owner._lineBatch!.Length - _owner._lineBatchCount);
        }

        public void Clear() => _owner._lineBatchCount = 0;

        private void EnsureCapacity(int sizeHint)
        {
            if (sizeHint < 1)
            {
                sizeHint = 1;
            }

            byte[]? batch = _owner._lineBatch;
            if (batch is null)
            {
                batch = new byte[LineBatchBytes];
                _owner._lineBatch = batch;
            }

            if (batch.Length - _owner._lineBatchCount >= sizeHint)
            {
                return;
            }

            int required = _owner._lineBatchCount + sizeHint;
            Array.Resize(ref batch, Math.Max(batch.Length * 2, required));
            _owner._lineBatch = batch;
        }
    }

    private sealed class SubscriberSink : IDisposable
    {
        private readonly LogService _owner;
        private readonly EventHandler<LogEvent> _handler;
        private readonly Channel<SubscriberItem> _channel;
        private readonly long _maxBytes;
        private long _queuedBytes;
        private int _disposed;

        public SubscriberSink(LogService owner, EventHandler<LogEvent> handler, int capacity, long maxBytes)
        {
            _owner = owner;
            _handler = handler;
            _maxBytes = Math.Max(64 * 1024, maxBytes);
            _channel = Channel.CreateBounded<SubscriberItem>(new BoundedChannelOptions(Math.Max(16, capacity))
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
                FullMode = BoundedChannelFullMode.Wait
            });
            Pump = Task.Factory.StartNew(
                Run,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        public Task Pump { get; }

        public bool Matches(EventHandler<LogEvent> handler) => _handler.Equals(handler);

        public bool TryEnqueue(SubscriberItem item)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return false;
            }

            int estimate = item.EstimatedBytes;
            if (!Reserve(estimate))
            {
                _owner.RecordSubscriberError(SinkErrorKind.Dropped);
                return false;
            }

            try
            {
                if (_channel.Writer.TryWrite(item))
                {
                    return true;
                }
            }
            catch (Exception)
            {
            }

            Release(estimate);
            _owner.RecordSubscriberError(SinkErrorKind.Dropped);
            return false;
        }

        public void Complete()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                _channel.Writer.TryComplete();
            }
            catch (Exception)
            {
            }
        }

        public void Dispose()
        {
            Complete();
        }

        private bool Reserve(int estimate)
        {
            long current = Interlocked.Read(ref _queuedBytes);
            while (true)
            {
                long next = current + estimate;
                if (next > _maxBytes)
                {
                    return false;
                }

                long actual = Interlocked.CompareExchange(ref _queuedBytes, next, current);
                if (actual == current)
                {
                    return true;
                }

                current = actual;
            }
        }

        private void Release(int estimate)
        {
            if (estimate > 0)
            {
                Interlocked.Add(ref _queuedBytes, -estimate);
            }
        }

        private void Run()
        {
            try
            {
                ChannelReader<SubscriberItem> reader = _channel.Reader;
                while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
                {
                    while (reader.TryRead(out SubscriberItem item))
                    {
                        Release(item.EstimatedBytes);
                        if (Volatile.Read(ref _disposed) != 0)
                        {
                            continue;
                        }

                        try
                        {
                            _handler(_owner, item.LogEvent);
                        }
                        catch (Exception)
                        {
                            _owner.RecordSubscriberError(SinkErrorKind.Error);
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
        }
    }

    internal static class LogEventJson
    {
        internal static JsonWriterOptions WriterOptions { get; } = new()
        {
            Indented = false,
            SkipValidation = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private static readonly JsonSerializerOptions ReadOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        /// <summary>
        /// Пишет одно событие в JSONL напрямую в переиспользуемый приёмник байтов.
        /// Контракт строки (одна строка на событие, UTF-8 без BOM, \n) задаёт вызывающий:
        /// он же дописывает перевод строки и считает байты для ротации.
        /// </summary>
        public static void WriteEvent(Utf8JsonWriter writer, LogEvent logEvent, string applicationVersion, string sessionText)
        {
            Span<char> timestamp = stackalloc char[24];
            int timestampLength = FormatTimestamp(logEvent.TimestampUtc.UtcDateTime, timestamp);

            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteString("channel", ApplicationChannel);
            writer.WriteString("app", ApplicationName);
            writer.WriteString("appVersion", applicationVersion);
            writer.WriteString("session", sessionText);
            writer.WritePropertyName("timestampUtc");
            if (timestampLength == 0)
            {
                writer.WriteStringValue(logEvent.TimestampUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
            }
            else
            {
                writer.WriteStringValue(timestamp[..timestampLength]);
            }

            writer.WriteNumber("sequence", logEvent.Sequence);
            writer.WriteString("eventId", logEvent.EventId);
            writer.WriteString("level", LevelText(logEvent.Level));
            writer.WriteString("status", StatusText(logEvent.Status));
            writer.WriteString("source", logEvent.Source);
            writer.WriteString("message", logEvent.Message);
            writer.WritePropertyName("properties");
            WriteProperties(writer, logEvent.Properties);
            WriteException(writer, logEvent.Exception);
            WriteOptional(writer, "operation", logEvent.OperationId);
            WriteOptional(writer, "item", logEvent.ItemId);
            WriteOptional(writer, "process", logEvent.ProcessId);
            if (logEvent.Pid.HasValue) writer.WriteNumber("pid", logEvent.Pid.Value);
            if (logEvent.Attempt.HasValue) writer.WriteNumber("attempt", logEvent.Attempt.Value);
            WriteOptional(writer, "tool", logEvent.Tool);
            WriteOptional(writer, "toolVersion", logEvent.ToolVersion);
            if (logEvent.ExitCode.HasValue) writer.WriteNumber("exitCode", logEvent.ExitCode.Value);
            if (logEvent.DurationMs.HasValue && double.IsFinite(logEvent.DurationMs.Value)) writer.WriteNumber("durationMs", logEvent.DurationMs.Value);
            WriteOptional(writer, "input", logEvent.Input);
            WriteOptional(writer, "output", logEvent.Output);
            WriteOptional(writer, "statusCode", logEvent.StatusCode);
            WriteOptional(writer, "errorCode", logEvent.ErrorCode);
            writer.WriteEndObject();
        }

        private static readonly string[] LevelTexts =
        {
            "Debug", "Info", "Warning", "Error", "Fatal"
        };

        private static readonly string[] StatusTexts =
        {
            "None", "Running", "Succeeded", "Failed", "Cancelled", "Skipped",
            "PartiallySucceeded", "RetryScheduled", "Changed"
        };

        private static string LevelText(LogLevel level)
        {
            int index = (int)level;
            return index >= 0 && index < LevelTexts.Length ? LevelTexts[index] : LevelTexts[(int)LogLevel.Info];
        }

        private static string StatusText(LogStatus status)
        {
            int index = (int)status;
            return index >= 0 && index < StatusTexts.Length ? StatusTexts[index] : StatusTexts[0];
        }

        /// <summary>
        /// Форматирует метку времени UTC в фиксированный буфер без выделения строки.
        /// Возвращает 0, если год не укладывается в схему yyyy.
        /// </summary>
        private static int FormatTimestamp(DateTime value, Span<char> buffer)
        {
            int year = value.Year;
            if (year < 1 || year > 9999)
            {
                return 0;
            }

            WriteDigits(buffer, 0, year, 4);
            buffer[4] = '-';
            WriteDigits(buffer, 5, value.Month, 2);
            buffer[7] = '-';
            WriteDigits(buffer, 8, value.Day, 2);
            buffer[10] = 'T';
            WriteDigits(buffer, 11, value.Hour, 2);
            buffer[13] = ':';
            WriteDigits(buffer, 14, value.Minute, 2);
            buffer[16] = ':';
            WriteDigits(buffer, 17, value.Second, 2);
            buffer[19] = '.';
            WriteDigits(buffer, 20, value.Millisecond, 3);
            buffer[23] = 'Z';
            return 24;
        }

        private static void WriteDigits(Span<char> buffer, int offset, int value, int width)
        {
            for (int position = width - 1; position >= 0; position--)
            {
                buffer[offset + position] = (char)('0' + (value % 10));
                value /= 10;
            }
        }

        public static string Serialize(LogEvent logEvent, string applicationVersion)
        {
            System.Buffers.ArrayBufferWriter<byte> buffer = new(512);
            using (Utf8JsonWriter writer = new(buffer, WriterOptions))
            {
                WriteEvent(writer, logEvent, applicationVersion, logEvent.SessionId.ToString("D", CultureInfo.InvariantCulture));
            }

            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }

        public static LogEvent? TryParse(
            string line,
            Func<LogEvent, string?>? conditionalValidator = null)
        {
            if (string.IsNullOrWhiteSpace(line) || line.Length > int.MaxValue)
            {
                return null;
            }

            string trimmed = line.Trim();
            if (!trimmed.StartsWith('{'))
            {
                if (LogEvent.TryParseText(trimmed, out LogEvent? textEvent) && textEvent is not null)
                {
                    if (conditionalValidator?.Invoke(textEvent) is not null)
                    {
                        return null;
                    }

                    return textEvent;
                }

                return null;
            }

            try
            {
                LogEventEnvelope? envelope = JsonSerializer.Deserialize<LogEventEnvelope>(line, ReadOptions);
                LogEvent? logEvent = envelope?.ToLogEvent();
                if (logEvent is null || conditionalValidator?.Invoke(logEvent) is not null)
                {
                    return null;
                }

                return logEvent;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void WriteProperties(Utf8JsonWriter writer, IReadOnlyDictionary<string, object?> properties)
        {
            writer.WriteStartObject();
            if (properties is LogPropertyCollection snapshot)
            {
                LogPropertyCollection.Enumerator entries = snapshot.GetEnumerator();
                while (entries.MoveNext())
                {
                    KeyValuePair<string, object?> pair = entries.Current;
                    writer.WritePropertyName(pair.Key);
                    WriteValue(writer, pair.Value);
                }
            }
            else if (properties is not null)
            {
                foreach (KeyValuePair<string, object?> pair in properties)
                {
                    writer.WritePropertyName(pair.Key);
                    WriteValue(writer, pair.Value);
                }
            }

            writer.WriteEndObject();
        }

        private static void WriteValue(Utf8JsonWriter writer, object? value)
        {
            switch (value)
            {
                case null:
                    writer.WriteNullValue();
                    break;
                case string text:
                    writer.WriteStringValue(text);
                    break;
                case bool flag:
                    writer.WriteBooleanValue(flag);
                    break;
                case int number:
                    writer.WriteNumberValue(number);
                    break;
                case long number:
                    writer.WriteNumberValue(number);
                    break;
                case short number:
                    writer.WriteNumberValue(number);
                    break;
                case byte number:
                    writer.WriteNumberValue(number);
                    break;
                case double number:
                    WriteDouble(writer, number);
                    break;
                case float number:
                    WriteDouble(writer, number);
                    break;
                case decimal number:
                    writer.WriteNumberValue(number);
                    break;
                case DateTimeOffset timestamp:
                    writer.WriteStringValue(timestamp.ToString("O", CultureInfo.InvariantCulture));
                    break;
                case Guid guid:
                    writer.WriteStringValue(guid.ToString("D", CultureInfo.InvariantCulture));
                    break;
                case TimeSpan span:
                    writer.WriteNumberValue(span.TotalMilliseconds);
                    break;
                default:
                    writer.WriteStringValue(LogRedactor.Truncate(LogRedactor.SingleLine(value.ToString() ?? string.Empty), 512));
                    break;
            }
        }

        private static void WriteDouble(Utf8JsonWriter writer, double value)
        {
            if (double.IsFinite(value))
            {
                writer.WriteNumberValue(value);
            }
            else
            {
                writer.WriteNullValue();
            }
        }

        private static void WriteOptional(Utf8JsonWriter writer, string name, string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                writer.WriteNull(name);
            }
            else
            {
                writer.WriteString(name, value);
            }
        }

        private static void WriteException(Utf8JsonWriter writer, ExceptionInfo? exception)
        {
            if (exception is null)
            {
                writer.WriteNull("exception");
                return;
            }

            writer.WritePropertyName("exception");
            writer.WriteStartObject();
            writer.WriteString("type", exception.Type);
            writer.WriteString("message", exception.Message);
            writer.WriteNumber("hresult", exception.HResult);
            if (string.IsNullOrEmpty(exception.StackTrace))
            {
                writer.WriteNull("stack");
            }
            else
            {
                writer.WriteString("stack", exception.StackTrace);
            }

            writer.WriteBoolean("exceptionTruncated", exception.ExceptionTruncated);
            if (exception.ExceptionTruncated)
            {
                writer.WriteNumber("originalExceptionCount", exception.OriginalChainLength);
                writer.WriteNumber("keptExceptionCount", exception.KeptChainLength);
            }

            WriteException(writer, exception.Inner);
            writer.WriteEndObject();
        }
    }

    private sealed class LogEventEnvelope
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; }

        [JsonPropertyName("channel")]
        public string? Channel { get; set; }

        [JsonPropertyName("app")]
        public string? App { get; set; }

        [JsonPropertyName("appVersion")]
        public string? AppVersion { get; set; }

        [JsonPropertyName("session")]
        public string? Session { get; set; }

        [JsonPropertyName("timestampUtc")]
        public string? TimestampUtc { get; set; }

        [JsonPropertyName("sequence")]
        public long Sequence { get; set; }

        [JsonPropertyName("eventId")]
        public string? EventId { get; set; }

        [JsonPropertyName("level")]
        public string? Level { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("source")]
        public string? Source { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("properties")]
        public Dictionary<string, JsonElement>? Properties { get; set; }

        [JsonPropertyName("exception")]
        public ExceptionEnvelope? Exception { get; set; }

        [JsonPropertyName("operation")]
        public string? Operation { get; set; }

        [JsonPropertyName("item")]
        public string? Item { get; set; }

        [JsonPropertyName("process")]
        public string? Process { get; set; }

        [JsonPropertyName("pid")]
        public int? Pid { get; set; }

        [JsonPropertyName("attempt")]
        public int? Attempt { get; set; }

        [JsonPropertyName("tool")]
        public string? Tool { get; set; }

        [JsonPropertyName("toolVersion")]
        public string? ToolVersion { get; set; }

        [JsonPropertyName("exitCode")]
        public int? ExitCode { get; set; }

        [JsonPropertyName("durationMs")]
        public double? DurationMs { get; set; }

        [JsonPropertyName("input")]
        public string? Input { get; set; }

        [JsonPropertyName("output")]
        public string? Output { get; set; }

        [JsonPropertyName("statusCode")]
        public string? StatusCode { get; set; }

        [JsonPropertyName("errorCode")]
        public string? ErrorCode { get; set; }

        public LogEvent? ToLogEvent()
        {
            if (SchemaVersion != LogService.SchemaVersion)
            {
                return null;
            }

            if (!string.Equals(Channel, LogService.ApplicationChannel, StringComparison.Ordinal))
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(EventId) || !LogRedactor.IsValidEventId(EventId))
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(Source) || string.IsNullOrWhiteSpace(App))
            {
                return null;
            }

            if (Message is null || Sequence <= 0)
            {
                return null;
            }

            if (!Guid.TryParse(Session, out Guid session) || session == Guid.Empty)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(TimestampUtc) ||
                !DateTimeOffset.TryParse(
                    TimestampUtc,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                    out DateTimeOffset timestamp))
            {
                return null;
            }

            if (!TryParseStrict(Level, out LogLevel level) || !TryParseStrict(Status, out LogStatus status))
            {
                return null;
            }

            return new LogEvent
            {
                TimestampUtc = timestamp,
                Sequence = Sequence,
                SessionId = session,
                EventId = EventId!,
                Level = level,
                Status = status,
                Source = Source,
                OperationId = Operation,
                ItemId = Item,
                ProcessId = Process,
                Pid = Pid,
                Attempt = Attempt,
                Message = Message,
                Properties = ToProperties(Properties),
                Exception = Exception?.ToExceptionInfo(),
                Tool = Tool,
                ToolVersion = ToolVersion,
                ExitCode = ExitCode,
                DurationMs = DurationMs,
                Input = Input,
                Output = Output,
                StatusCode = StatusCode,
                ErrorCode = ErrorCode
            };
        }

        private static bool TryParseStrict<TEnum>(string? value, out TEnum result) where TEnum : struct, Enum
        {
            result = default;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            if (!Enum.TryParse(value, true, out result))
            {
                return false;
            }

            return Enum.IsDefined(result) && string.Equals(value, result.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        private static IReadOnlyDictionary<string, object?> ToProperties(Dictionary<string, JsonElement>? properties)
        {
            if (properties is null || properties.Count == 0)
            {
                return LogEvent.EmptyProperties;
            }

            Dictionary<string, object?> result = new(properties.Count, StringComparer.Ordinal);
            foreach (KeyValuePair<string, JsonElement> pair in properties)
            {
                result[pair.Key] = ToClrValue(pair.Value);
            }

            return result;
        }

        private static object? ToClrValue(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.TryGetInt64(out long integral) ? integral : element.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => element.ToString()
            };
        }
    }

    private sealed class ExceptionEnvelope
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("hresult")]
        public int HResult { get; set; }

        [JsonPropertyName("stack")]
        public string? Stack { get; set; }

        [JsonPropertyName("exceptionTruncated")]
        public bool ExceptionTruncated { get; set; }

        [JsonPropertyName("originalExceptionCount")]
        public int OriginalExceptionCount { get; set; }

        [JsonPropertyName("keptExceptionCount")]
        public int KeptExceptionCount { get; set; }

        [JsonPropertyName("exception")]
        public ExceptionEnvelope? Inner { get; set; }

        public ExceptionInfo ToExceptionInfo()
        {
            return ExceptionInfo.Create(
                Type ?? "Exception",
                Message ?? string.Empty,
                HResult,
                Stack,
                Inner?.ToExceptionInfo(),
                ExceptionTruncated,
                OriginalExceptionCount,
                KeptExceptionCount);
        }
    }
}
