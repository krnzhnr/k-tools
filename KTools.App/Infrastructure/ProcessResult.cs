// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Globalization;

using KTools_App.Diagnostics;
using KTools_App.Models;

namespace KTools_App.Infrastructure;

public sealed record ProcessResult
{
    public const string ErrorBinaryMissing = "binary-missing";
    public const string ErrorStartFailed = "start-failed";
    public const string ErrorCancelled = "cancelled";
    public const string ErrorTerminationUnverified = "termination-unverified";
    public const string ErrorNonZeroExit = "nonzero-exit";
    public const string ErrorArtifactMissing = "artifact-missing";
    public const string ErrorOutputEmpty = "output-empty";
    public const string ErrorParseInvalid = "parse-invalid";
    public const string ErrorReadFailed = "output-read-failed";
    public const string ErrorWarningsOnly = "warnings-only";
    public const string MessageSucceeded = "Внешний процесс завершён успешно";
    public const string MessageCancelled = "Выполнение внешнего процесса отменено";
    public const string MessageUnverifiedTermination = "Отмена запрошена, но факт выхода процесса не подтверждён";
    public const string MessageArtifactMissing = "Процесс завершён без кода ошибки, но ожидаемый артефакт не найден";

    private ProcessResult(
        string processId,
        int? pid,
        ProcessExecutionContext context,
        int? exitCode,
        double durationMs,
        bool? outputExists,
        string? outputTail,
        bool outputTruncated,
        int warningCount,
        int errorLineCount,
        LogStatus status,
        string? errorCode,
        Exception? exception,
        string message,
        bool terminationVerified)
    {
        ProcessId = string.IsNullOrWhiteSpace(processId) ? ProcessExecutionContext.ProcessPrefix + "unknown" : processId;
        Pid = pid is > 0 ? pid : null;
        Context = context;
        ExitCode = exitCode is >= 0 ? exitCode : null;
        DurationMs = double.IsFinite(durationMs) && durationMs > 0 ? Math.Round(durationMs, 3) : 0d;
        OutputExists = outputExists;
        OutputTail = string.IsNullOrEmpty(outputTail) ? null : outputTail;
        OutputTruncated = outputTruncated;
        WarningCount = Math.Max(0, warningCount);
        ErrorLineCount = Math.Max(0, errorLineCount);
        Status = status;
        ErrorCode = string.IsNullOrWhiteSpace(errorCode) ? null : errorCode;
        Exception = exception;
        Message = string.IsNullOrWhiteSpace(message) ? MessageSucceeded : message;
        TerminationVerified = terminationVerified;
    }

    public string ProcessId { get; init; }

    public int? Pid { get; init; }

    public ProcessExecutionContext Context { get; init; }

    public string OperationId => Context.OperationId;

    public string? ItemId => Context.ItemId;

    public string Tool => Context.Tool;

    public string? ToolVersion => Context.ToolVersion;

    public int Attempt => Context.Attempt;

    public string? ExpectedArtifact => Context.ExpectedArtifact;

    public int? ExitCode { get; init; }

    public double DurationMs { get; init; }

    public bool? OutputExists { get; init; }

    public string? OutputTail { get; init; }

    public bool OutputTruncated { get; init; }

    public int WarningCount { get; init; }

    public int ErrorLineCount { get; init; }

    public LogStatus Status { get; init; }

    public string? ErrorCode { get; init; }

    public Exception? Exception { get; init; }

    public string Message { get; init; }

    public bool TerminationVerified { get; init; }

    /// <summary>
    /// Состояние уборки внешнего процесса: подтверждает, что завершённое или отменённое
    /// выполнение не оставило процесс, артефакты или исходные данные в неопределённом состоянии.
    /// </summary>
    public CleanupState CleanupState { get; init; } = CleanupState.NotRequired;

    /// <summary>
    /// Счётчики буфера вывода процесса. Без них потеря диагностического хвоста
    /// (усечение кольцевого буфера, отсечение баннеров) оставалась невидимой в журнале.
    /// </summary>
    public ProcessOutputMetrics OutputMetrics { get; init; } = ProcessOutputMetrics.Empty;

    public bool IsSuccess => Status is LogStatus.Succeeded or LogStatus.PartiallySucceeded;

    public bool IsCleanSuccess => Status == LogStatus.Succeeded;

    public bool IsPartiallySucceeded => Status == LogStatus.PartiallySucceeded;

    public bool IsCancelled => Status == LogStatus.Cancelled;

    public bool IsFailed => Status == LogStatus.Failed;

    public bool HasWarnings => WarningCount > 0;

    public bool HasException => Exception is not null;

    public bool IsTerminal => Status is LogStatus.Succeeded
        or LogStatus.Failed
        or LogStatus.Cancelled
        or LogStatus.Skipped
        or LogStatus.PartiallySucceeded;

    public ExecutionStatus ToExecutionStatus() => Status switch
    {
        LogStatus.Succeeded => ExecutionStatus.Succeeded,
        LogStatus.Cancelled => ExecutionStatus.Cancelled,
        LogStatus.Skipped => ExecutionStatus.Skipped,
        LogStatus.PartiallySucceeded => ExecutionStatus.PartiallySucceeded,
        _ => ExecutionStatus.Failed
    };

    public LogContext ToLogContext() =>
        Context.ToLogContext()
            .WithProcess(ProcessId);

    public Dictionary<string, object?> ToLogProperties(bool includeOutputTail = true)
    {
        Dictionary<string, object?> properties = Context.ToLogProperties();
        properties["ProcessId"] = ProcessId;

        if (Pid.HasValue)
        {
            properties["Pid"] = Pid.Value;
        }

        if (ExitCode.HasValue)
        {
            properties["ExitCode"] = ExitCode.Value;
        }

        if (DurationMs > 0)
        {
            properties["DurationMs"] = DurationMs;
        }

        if (OutputExists.HasValue)
        {
            properties["OutputExists"] = OutputExists.Value;
        }

        if (WarningCount > 0)
        {
            properties["WarningCount"] = WarningCount;
        }

        if (OutputTruncated)
        {
            properties["OutputTruncated"] = true;
        }

        if (includeOutputTail && !string.IsNullOrEmpty(OutputTail))
        {
            properties["OutputTail"] = OutputTail;
        }

        if (OutputMetrics.DroppedLines > 0)
        {
            properties["DroppedLines"] = OutputMetrics.DroppedLines;
        }

        if (OutputMetrics.DroppedBytes > 0)
        {
            properties["DroppedBytes"] = OutputMetrics.DroppedBytes;
        }

        if (OutputMetrics.RetainedLines > 0)
        {
            properties["RetainedLines"] = OutputMetrics.RetainedLines;
        }

        if (OutputMetrics.RetainedBytes > 0)
        {
            properties["RetainedBytes"] = OutputMetrics.RetainedBytes;
        }

        if (OutputMetrics.SuppressedCount > 0)
        {
            properties["SuppressedCount"] = OutputMetrics.SuppressedCount;
        }

        properties["CleanupState"] = CleanupState.ToString();

        if (!string.IsNullOrEmpty(ErrorCode))
        {
            properties["ErrorCode"] = ErrorCode;
        }

        return properties;
    }

    public static ProcessResult Succeeded(
        ProcessExecutionContext context,
        string? processId = null,
        int? pid = null,
        int? exitCode = null,
        double durationMs = 0,
        bool? outputExists = null,
        string? outputTail = null,
        bool outputTruncated = false,
        int warningCount = 0,
        int errorLineCount = 0,
        string? message = null)
    {
        return Create(
            context,
            processId,
            pid,
            exitCode,
            durationMs,
            outputExists,
            outputTail,
            outputTruncated,
            warningCount,
            errorLineCount,
            LogStatus.Succeeded,
            warningCount > 0 ? ErrorWarningsOnly : null,
            null,
            message ?? MessageSucceeded,
            terminationVerified: true);
    }

    public static ProcessResult Failed(
        ProcessExecutionContext context,
        string errorCode,
        string message,
        string? processId = null,
        int? pid = null,
        int? exitCode = null,
        double durationMs = 0,
        bool? outputExists = null,
        string? outputTail = null,
        bool outputTruncated = false,
        int warningCount = 0,
        int errorLineCount = 0,
        Exception? exception = null)
    {
        return Create(
            context,
            processId,
            pid,
            exitCode,
            durationMs,
            outputExists,
            outputTail,
            outputTruncated,
            warningCount,
            errorLineCount,
            LogStatus.Failed,
            errorCode,
            exception,
            message,
            terminationVerified: true);
    }

    public static ProcessResult PartiallySucceeded(
        ProcessExecutionContext context,
        string message,
        string? processId = null,
        int? pid = null,
        int? exitCode = null,
        double durationMs = 0,
        bool? outputExists = null,
        string? outputTail = null,
        bool outputTruncated = false,
        int warningCount = 0,
        int errorLineCount = 0)
    {
        return Create(
            context,
            processId,
            pid,
            exitCode,
            durationMs,
            outputExists,
            outputTail,
            outputTruncated,
            warningCount,
            errorLineCount,
            LogStatus.PartiallySucceeded,
            ErrorWarningsOnly,
            null,
            message,
            terminationVerified: true);
    }

    public static ProcessResult Cancelled(
        ProcessExecutionContext context,
        string message,
        string? processId = null,
        int? pid = null,
        int? exitCode = null,
        double durationMs = 0,
        bool? outputExists = null,
        string? outputTail = null,
        bool outputTruncated = false,
        int warningCount = 0,
        int errorLineCount = 0,
        bool terminationVerified = true,
        string? errorCode = null,
        CleanupState cleanupState = CleanupState.NotRequired,
        ProcessOutputMetrics? outputMetrics = null)
    {
        return Create(
            context,
            processId,
            pid,
            exitCode,
            durationMs,
            outputExists,
            outputTail,
            outputTruncated,
            warningCount,
            errorLineCount,
            LogStatus.Cancelled,
            errorCode ?? ErrorCancelled,
            null,
            message,
            terminationVerified) with
        {
            CleanupState = cleanupState,
            OutputMetrics = outputMetrics ?? ProcessOutputMetrics.Empty
        };
    }

    public static ProcessResult NotStarted(
        ProcessExecutionContext context,
        string errorCode,
        string message,
        Exception? exception = null,
        string? processId = null)
    {
        return Create(
            context,
            processId,
            pid: null,
            exitCode: null,
            durationMs: 0,
            outputExists: null,
            outputTail: null,
            outputTruncated: false,
            warningCount: 0,
            errorLineCount: 0,
            LogStatus.Failed,
            errorCode,
            exception,
            message,
            terminationVerified: true);
    }

    public ProcessResult(bool isSuccess, int exitCode, string message)
        : this(
            ProcessExecutionContext.ProcessPrefix + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            null,
            ProcessExecutionContext.Create(ProcessExecutionContext.ToolFallback),
            exitCode >= 0 ? exitCode : null,
            0,
            null,
            null,
            false,
            0,
            0,
            isSuccess ? LogStatus.Succeeded : LogStatus.Failed,
            isSuccess ? null : ErrorNonZeroExit,
            null,
            message,
            terminationVerified: true)
    {
    }

    private static ProcessResult Create(
        ProcessExecutionContext context,
        string? processId,
        int? pid,
        int? exitCode,
        double durationMs,
        bool? outputExists,
        string? outputTail,
        bool outputTruncated,
        int warningCount,
        int errorLineCount,
        LogStatus status,
        string? errorCode,
        Exception? exception,
        string message,
        bool terminationVerified)
    {
        ProcessExecutionContext safeContext = context ?? ProcessExecutionContext.Create(ProcessExecutionContext.ToolFallback);
        return new ProcessResult(
            processId ?? ProcessExecutionContext.CreateProcessId(),
            pid,
            safeContext,
            exitCode,
            durationMs,
            outputExists,
            outputTail,
            outputTruncated,
            warningCount,
            errorLineCount,
            status,
            errorCode,
            exception,
            message,
            terminationVerified);
    }

    public string ToSummary()
    {
        string exit = ExitCode.HasValue
            ? ExitCode.Value.ToString(CultureInfo.InvariantCulture)
            : "none";
        string duration = DurationMs > 0
            ? DurationMs.ToString("F0", CultureInfo.InvariantCulture) + "ms"
            : "0ms";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Tool} status={Status} exit={exit} duration={duration} warnings={WarningCount} outputExists={OutputExists?.ToString() ?? "n/a"} cleanup={CleanupState} errorCode={ErrorCode ?? "none"}");
    }
}

/// <summary>
/// Счётчики буфера вывода внешнего процесса, попадающие в свойства события журнала.
/// Значения вычислялись и раньше, но не доходили до <c>ProcessResult.ToLogProperties</c>,
/// поэтому усечение вывода и отсечение баннеров были не видны при разборе инцидента.
/// </summary>
public sealed record ProcessOutputMetrics
{
    public static ProcessOutputMetrics Empty { get; } = new();

    public string Stream { get; init; } = string.Empty;

    public int DroppedLines { get; init; }

    public long DroppedBytes { get; init; }

    public int RetainedLines { get; init; }

    public int RetainedBytes { get; init; }

    public long TotalBytes { get; init; }

    public long SuppressedCount { get; init; }

    public bool HasLoss => DroppedLines > 0 || DroppedBytes > 0;

    /// <summary>
    /// Суммирует метрики нескольких буферов (stdout и stderr) одного процесса.
    /// </summary>
    public static ProcessOutputMetrics Combine(
        ProcessOutputMetrics first,
        ProcessOutputMetrics second,
        long suppressedCount = 0)
    {
        return new ProcessOutputMetrics
        {
            Stream = string.IsNullOrEmpty(first.Stream) ? second.Stream : first.Stream,
            DroppedLines = first.DroppedLines + second.DroppedLines,
            DroppedBytes = first.DroppedBytes + second.DroppedBytes,
            RetainedLines = first.RetainedLines + second.RetainedLines,
            RetainedBytes = first.RetainedBytes + second.RetainedBytes,
            TotalBytes = first.TotalBytes + second.TotalBytes,
            SuppressedCount = first.SuppressedCount + second.SuppressedCount + suppressedCount
        };
    }
}
