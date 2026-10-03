using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;

using KTools_App.Diagnostics;

namespace KTools_App.Models;

public enum ExecutionStatus
{
    Succeeded,
    Failed,
    Cancelled,
    Skipped,
    PartiallySucceeded
}

public enum CleanupState
{
    NotRequired,
    NotStarted,
    Completed,
    Failed,
    Partial,

    /// <summary>
    /// Исходный файл сохранён: замена не выполнена либо не подтверждена, данные не потеряны.
    /// </summary>
    SourcePreserved,

    Unknown
}

public class ItemResult
{
    public ItemResult(
        string itemId,
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
        double durationMs = 0,
        string? operationId = null)
    {
        ItemId = NormalizeIdentifier(itemId, "item");
        OperationId = NormalizeIdentifier(operationId, "operation");
        Status = status;
        Messages = ExecutionMessageBounds.Create(messages);
        ErrorCode = ExecutionMessageBounds.BoundedToken(errorCode, 128);
        ExitCode = exitCode;
        OutputFile = string.IsNullOrWhiteSpace(outputFile) ? null : outputFile;
        OutputExists = outputExists ?? (!string.IsNullOrWhiteSpace(outputFile) && File.Exists(outputFile));
        ExceptionInfo = exceptionInfo ?? (exception is null ? null : SafeExceptionInfo(exception));
        Exception = exception;
        Retryable = retryable;
        CleanupState = cleanupState;
        DurationMs = ExecutionMessageBounds.BoundedDuration(durationMs);
    }

    public string ItemId { get; }

    public string OperationId { get; }

    public ExecutionStatus Status { get; }

    public IReadOnlyList<string> Messages { get; }

    public string? ErrorCode { get; }

    public int? ExitCode { get; }

    public string? OutputFile { get; }

    public bool OutputExists { get; }

    public string? Output => OutputFile;

    public bool HasOutput => OutputExists;

    public ExceptionInfo? ExceptionInfo { get; }

    public ExceptionInfo? Error => ExceptionInfo;

    public Exception? Exception { get; }

    public bool Retryable { get; }

    public CleanupState CleanupState { get; }

    public double DurationMs { get; }

    public bool IsTerminal => Status is ExecutionStatus.Succeeded
        or ExecutionStatus.Failed
        or ExecutionStatus.Cancelled
        or ExecutionStatus.Skipped
        or ExecutionStatus.PartiallySucceeded;

    public bool IsSuccess => Status == ExecutionStatus.Succeeded;

    public bool IsFailure => Status is ExecutionStatus.Failed or ExecutionStatus.PartiallySucceeded;

    public bool IsPartial => Status == ExecutionStatus.PartiallySucceeded;

    public int MessageCount => Messages.Count;

    public LogStatus LogStatus => Status switch
    {
        ExecutionStatus.Succeeded => LogStatus.Succeeded,
        ExecutionStatus.Failed => LogStatus.Failed,
        ExecutionStatus.Cancelled => LogStatus.Cancelled,
        ExecutionStatus.Skipped => LogStatus.Skipped,
        ExecutionStatus.PartiallySucceeded => LogStatus.PartiallySucceeded,
        _ => LogStatus.None
    };

    public static ItemResult Create(
        string itemId,
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
        double durationMs = 0,
        string? operationId = null)
    {
        return new ItemResult(
            itemId,
            status,
            messages,
            errorCode,
            exitCode,
            outputFile,
            outputExists,
            exceptionInfo,
            exception,
            retryable,
            cleanupState,
            durationMs,
            operationId);
    }

    public ItemResult ToItemResult() => this;

    private static string NormalizeIdentifier(string? value, string prefix)
    {
        string candidate = value?.Trim() ?? string.Empty;
        if (candidate.Length == 0)
        {
            return CreateIdentifier(prefix);
        }

        if (LogRedactor.LooksUnsafe(candidate) || candidate.Length > LogRedactor.MaxIdentifierLength)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(candidate));
            return $"{prefix}-{Convert.ToHexString(hash)[..24].ToLowerInvariant()}";
        }

        StringBuilder result = new(candidate.Length);
        foreach (char character in candidate)
        {
            if (char.IsLetterOrDigit(character) || character is '_' or '-' or '.')
            {
                result.Append(character);
            }
        }

        return result.Length == 0 ? CreateIdentifier(prefix) : result.ToString();
    }

    internal static string CreateIdentifier(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private static ExceptionInfo? SafeExceptionInfo(Exception exception)
    {
        try
        {
            return ExceptionInfo.FromException(exception);
        }
        catch
        {
            return null;
        }
    }
}

internal static class ExecutionMessageBounds
{
    public const int MaxMessages = 100;
    public const int MaxMessageLength = 2048;
    public const int MaxTotalLength = 16000;

    public static IReadOnlyList<string> Create(IEnumerable<string>? messages)
    {
        if (messages is null)
        {
            return Array.Empty<string>();
        }

        List<string> result = new(Math.Min(MaxMessages, 32));
        int totalLength = 0;
        try
        {
            foreach (string? raw in messages)
            {
                if (result.Count >= MaxMessages || totalLength >= MaxTotalLength)
                {
                    break;
                }

                string message = raw ?? string.Empty;
                if (message.Length > MaxMessageLength)
                {
                    message = message[..MaxMessageLength];
                }

                result.Add(message);
                totalLength += message.Length;
            }
        }
        catch
        {
        }

        return result.Count == 0 ? Array.Empty<string>() : new ReadOnlyCollection<string>(result);
    }

    public static string? BoundedToken(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string token = value.Trim();
        return token.Length > maxLength ? token[..maxLength] : token;
    }

    public static double BoundedDuration(double durationMs)
    {
        return double.IsFinite(durationMs) && durationMs > 0 ? durationMs : 0;
    }
}
