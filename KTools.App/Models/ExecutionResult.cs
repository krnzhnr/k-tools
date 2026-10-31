using System;
using System.Collections;
using System.Collections.Generic;

using KTools_App.Diagnostics;

namespace KTools_App.Models;

public sealed class ExecutionResult : ItemResult, IReadOnlyList<string>
{
    public ExecutionResult(
        ExecutionContext context,
        ExecutionStatus status,
        IEnumerable<string>? messages = null,
        string? errorCode = null,
        int? exitCode = null,
        string? outputFile = null,
        bool? outputExists = null,
        ExceptionInfo? exceptionInfo = null,
        Exception? exception = null,
        bool retryable = false,
        CleanupState cleanupState = CleanupState.Unknown,
        double durationMs = 0)
        : base(
            context.ItemId,
            status,
            messages,
            // При Succeeded код отказа не выдумывается: потребитель, ищущий отказы
            // по errorCode != null, иначе получал бы ложное срабатывание на успехе.
            errorCode ?? DefaultErrorCode(status),
            exitCode,
            outputFile,
            outputExists,
            exceptionInfo,
            exception,
            retryable,
            cleanupState,
            durationMs,
            context.OperationId)
    {
        Context = context;
    }

    public ExecutionContext Context { get; }

    public int Count => Messages.Count;

    public string this[int index] => Messages[index];

    public ExecutionResult ToExecutionResult() => this;

    public override string ToString() => $"{Status}:{ItemId}";

    public static ExecutionResult Create(
        ExecutionContext context,
        ExecutionStatus status,
        IEnumerable<string>? messages = null,
        string? errorCode = null,
        int? exitCode = null,
        string? outputFile = null,
        bool? outputExists = null,
        ExceptionInfo? exceptionInfo = null,
        Exception? exception = null,
        bool retryable = false,
        CleanupState cleanupState = CleanupState.Unknown,
        double durationMs = 0)
    {
        return new ExecutionResult(
            context,
            status,
            messages,
            errorCode ?? DefaultErrorCode(status),
            exitCode,
            outputFile,
            outputExists,
            exceptionInfo,
            exception,
            retryable,
            cleanupState,
            durationMs);
    }

    public static ExecutionResult FromItemResult(ExecutionContext context, ItemResult item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new ExecutionResult(
            context,
            item.Status,
            item.Messages,
            item.ErrorCode,
            item.ExitCode,
            item.OutputFile,
            item.OutputExists,
            item.ExceptionInfo,
            item.Exception,
            item.Retryable,
            item.CleanupState,
            item.DurationMs);
    }

    public static ExecutionResult Succeeded(
        ExecutionContext context,
        IEnumerable<string>? messages = null,
        string? outputFile = null,
        bool? outputExists = null,
        int? exitCode = null,
        double durationMs = 0,
        CleanupState cleanupState = CleanupState.NotRequired)
    {
        return Create(
            context,
            ExecutionStatus.Succeeded,
            messages,
            outputFile: outputFile,
            outputExists: outputExists,
            exitCode: exitCode,
            cleanupState: cleanupState,
            durationMs: durationMs);
    }

    public static ExecutionResult Succeeded(
        ExecutionContext context,
        string message,
        string? outputFile = null,
        bool? outputExists = null,
        int? exitCode = null,
        double durationMs = 0,
        CleanupState cleanupState = CleanupState.NotRequired)
    {
        return Succeeded(context, new[] { message }, outputFile, outputExists, exitCode, durationMs, cleanupState);
    }

    public static ExecutionResult Failed(
        ExecutionContext context,
        IEnumerable<string>? messages = null,
        string? errorCode = null,
        int? exitCode = null,
        string? outputFile = null,
        bool? outputExists = null,
        ExceptionInfo? exceptionInfo = null,
        Exception? exception = null,
        bool retryable = false,
        CleanupState cleanupState = CleanupState.NotRequired,
        double durationMs = 0)
    {
        return Create(
            context,
            ExecutionStatus.Failed,
            messages,
            errorCode ?? "execution-failed",
            exitCode,
            outputFile,
            outputExists,
            exceptionInfo,
            exception,
            retryable,
            cleanupState,
            durationMs);
    }

    public static ExecutionResult Failed(
        ExecutionContext context,
        string message,
        string? errorCode = null,
        int? exitCode = null,
        string? outputFile = null,
        bool? outputExists = null,
        ExceptionInfo? exceptionInfo = null,
        Exception? exception = null,
        bool retryable = false,
        CleanupState cleanupState = CleanupState.NotRequired,
        double durationMs = 0)
    {
        return Failed(
            context,
            new[] { message },
            errorCode ?? "execution-failed",
            exitCode,
            outputFile,
            outputExists,
            exceptionInfo,
            exception,
            retryable,
            cleanupState,
            durationMs);
    }

    public static ExecutionResult Cancelled(
        ExecutionContext context,
        IEnumerable<string>? messages = null,
        string? errorCode = null,
        int? exitCode = null,
        string? outputFile = null,
        bool? outputExists = null,
        CleanupState cleanupState = CleanupState.Completed,
        double durationMs = 0)
    {
        return Create(
            context,
            ExecutionStatus.Cancelled,
            messages,
            errorCode ?? "cancelled",
            exitCode,
            outputFile,
            outputExists,
            cleanupState: cleanupState,
            durationMs: durationMs);
    }

    public static ExecutionResult Cancelled(
        ExecutionContext context,
        string message,
        string? errorCode = null,
        int? exitCode = null,
        string? outputFile = null,
        bool? outputExists = null,
        CleanupState cleanupState = CleanupState.Completed,
        double durationMs = 0)
    {
        return Cancelled(context, new[] { message }, errorCode, exitCode, outputFile, outputExists, cleanupState, durationMs);
    }

    public static ExecutionResult Skipped(
        ExecutionContext context,
        IEnumerable<string>? messages = null,
        string? errorCode = null,
        int? exitCode = null,
        string? outputFile = null,
        bool? outputExists = null,
        CleanupState cleanupState = CleanupState.NotRequired,
        double durationMs = 0)
    {
        return Create(
            context,
            ExecutionStatus.Skipped,
            messages,
            errorCode ?? "skipped",
            exitCode,
            outputFile,
            outputExists,
            cleanupState: cleanupState,
            durationMs: durationMs);
    }

    public static ExecutionResult Skipped(
        ExecutionContext context,
        string message,
        string? errorCode = null,
        int? exitCode = null,
        string? outputFile = null,
        bool? outputExists = null,
        CleanupState cleanupState = CleanupState.NotRequired,
        double durationMs = 0)
    {
        return Skipped(context, new[] { message }, errorCode, exitCode, outputFile, outputExists, cleanupState, durationMs);
    }

    public static ExecutionResult PartiallySucceeded(
        ExecutionContext context,
        IEnumerable<string>? messages = null,
        string? errorCode = null,
        int? exitCode = null,
        string? outputFile = null,
        bool? outputExists = null,
        bool retryable = true,
        CleanupState cleanupState = CleanupState.Partial,
        double durationMs = 0)
    {
        return Create(
            context,
            ExecutionStatus.PartiallySucceeded,
            messages,
            errorCode ?? "partial-success",
            exitCode,
            outputFile,
            outputExists,
            retryable: retryable,
            cleanupState: cleanupState,
            durationMs: durationMs);
    }

    public static ExecutionResult PartiallySucceeded(
        ExecutionContext context,
        string message,
        string? errorCode = null,
        int? exitCode = null,
        string? outputFile = null,
        bool? outputExists = null,
        bool retryable = true,
        CleanupState cleanupState = CleanupState.Partial,
        double durationMs = 0)
    {
        return PartiallySucceeded(
            context,
            new[] { message },
            errorCode ?? "execution-failed",
            exitCode,
            outputFile,
            outputExists,
            retryable,
            cleanupState,
            durationMs);
    }

    public static ExecutionResult FromException(
        ExecutionContext context,
        Exception exception,
        IEnumerable<string>? messages = null,
        string? errorCode = null,
        int? exitCode = null,
        string? outputFile = null,
        bool? outputExists = null,
        bool retryable = false,
        CleanupState cleanupState = CleanupState.Unknown,
        double durationMs = 0)
    {
        return Failed(
            context,
            messages,
            errorCode ?? "execution-failed",
            exitCode,
            outputFile,
            outputExists,
            exception: exception,
            retryable: retryable,
            cleanupState: cleanupState,
            durationMs: durationMs);
    }

    public static ExecutionResult Success(
        ExecutionContext context,
        IEnumerable<string>? messages = null,
        string? outputFile = null,
        bool? outputExists = null,
        int? exitCode = null,
        double durationMs = 0,
        CleanupState cleanupState = CleanupState.NotRequired)
    {
        return Succeeded(context, messages, outputFile, outputExists, exitCode, durationMs, cleanupState);
    }

    public static ExecutionResult Failure(
        ExecutionContext context,
        IEnumerable<string>? messages = null,
        string? errorCode = null,
        int? exitCode = null,
        string? outputFile = null,
        bool? outputExists = null,
        ExceptionInfo? exceptionInfo = null,
        Exception? exception = null,
        bool retryable = false,
        CleanupState cleanupState = CleanupState.NotRequired,
        double durationMs = 0)
    {
        return Failed(context, messages, errorCode, exitCode, outputFile, outputExists, exceptionInfo, exception, retryable, cleanupState, durationMs);
    }

    public static ExecutionResult Cancel(
        ExecutionContext context,
        IEnumerable<string>? messages = null,
        string? errorCode = null,
        int? exitCode = null,
        string? outputFile = null,
        bool? outputExists = null,
        CleanupState cleanupState = CleanupState.Completed,
        double durationMs = 0)
    {
        return Cancelled(context, messages, errorCode, exitCode, outputFile, outputExists, cleanupState, durationMs);
    }

    public static ExecutionResult Skip(
        ExecutionContext context,
        IEnumerable<string>? messages = null,
        string? errorCode = null,
        int? exitCode = null,
        string? outputFile = null,
        bool? outputExists = null,
        CleanupState cleanupState = CleanupState.NotRequired,
        double durationMs = 0)
    {
        return Skipped(context, messages, errorCode, exitCode, outputFile, outputExists, cleanupState, durationMs);
    }

    public static ExecutionResult PartialSuccess(
        ExecutionContext context,
        IEnumerable<string>? messages = null,
        string? errorCode = null,
        int? exitCode = null,
        string? outputFile = null,
        bool? outputExists = null,
        bool retryable = true,
        CleanupState cleanupState = CleanupState.Partial,
        double durationMs = 0)
    {
        return PartiallySucceeded(context, messages, errorCode, exitCode, outputFile, outputExists, retryable, cleanupState, durationMs);
    }

    private static string? DefaultErrorCode(ExecutionStatus status)
    {
        return status switch
        {
            ExecutionStatus.Failed => "execution-failed",
            ExecutionStatus.Cancelled => "cancelled",
            ExecutionStatus.Skipped => "skipped",
            ExecutionStatus.PartiallySucceeded => "partial-success",
            _ => null
        };
    }

    public IEnumerator<string> GetEnumerator() => Messages.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
