using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

using KTools_App.Diagnostics;

namespace KTools_App.Models;

public sealed class ExecutionContext
{
    public ExecutionContext(
        string operationId,
        string itemId,
        int itemIndex,
        int total,
        string scriptId)
    {
        OperationId = operationId;
        ItemId = itemId;
        ItemIndex = itemIndex;
        Total = total;
        ScriptId = scriptId;
    }

    public string OperationId { get; }

    public string ItemId { get; }

    public int ItemIndex { get; }

    public int Total { get; }

    public string ScriptId { get; }

    public bool IsBatchContext => ItemIndex < 0;

    public int ItemNumber => IsBatchContext ? 0 : ItemIndex + 1;

    public static ExecutionContext CreateBatch(string scriptId, int total, string? operationId = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(total);

        return new ExecutionContext(
            NormalizeIdentifier(operationId, "operation"),
            string.Empty,
            -1,
            total,
            NormalizeIdentifier(scriptId, "script"));
    }

    public static ExecutionContext CreateBatchContext(string scriptId, int total, string? operationId = null)
    {
        return CreateBatch(scriptId, total, operationId);
    }

    public ExecutionContext ForItem(int itemIndex, string? itemId = null)
    {
        if (itemIndex < 0 || itemIndex >= Total)
        {
            throw new ArgumentOutOfRangeException(nameof(itemIndex));
        }

        return new ExecutionContext(
            OperationId,
            NormalizeIdentifier(itemId, "item"),
            itemIndex,
            Total,
            ScriptId);
    }

    public ExecutionContext WithItem(int itemIndex, string? itemId = null)
    {
        return ForItem(itemIndex, itemId);
    }

    public static ExecutionContext CreateItem(
        string operationId,
        int itemIndex,
        int total,
        string scriptId,
        string? itemId = null)
    {
        return new ExecutionContext(
            NormalizeIdentifier(operationId, "operation"),
            NormalizeIdentifier(itemId, "item"),
            itemIndex,
            total,
            NormalizeIdentifier(scriptId, "script"));
    }

    public LogContext ToLogContext()
    {
        LogContext context = LogContext.Empty.WithOperation(OperationId);
        return string.IsNullOrEmpty(ItemId) ? context : context.WithItem(ItemId);
    }

    public IReadOnlyDictionary<string, object?> ToLogProperties()
    {
        Dictionary<string, object?> properties = new(StringComparer.Ordinal)
        {
            ["OperationId"] = OperationId,
            ["ScriptId"] = ScriptId,
            ["Total"] = Total
        };

        if (!IsBatchContext)
        {
            properties["ItemId"] = ItemId;
            properties["Index"] = ItemIndex;
        }

        return properties;
    }

    public override string ToString() => $"op={OperationId};item={ItemId};index={ItemIndex};total={Total};script={ScriptId}";

    private static string NormalizeIdentifier(string? value, string prefix)
    {
        string candidate = value?.Trim() ?? string.Empty;
        if (candidate.Length == 0)
        {
            return $"{prefix}-{Guid.NewGuid():N}";
        }

        if (LogRedactor.LooksUnsafe(candidate) || candidate.Length > LogRedactor.MaxIdentifierLength)
        {
            byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(candidate));
            return $"{prefix}-{Convert.ToHexString(bytes)[..24].ToLowerInvariant()}";
        }

        StringBuilder result = new(candidate.Length);
        foreach (char character in candidate)
        {
            if (char.IsLetterOrDigit(character) || character is '_' or '-' or '.')
            {
                result.Append(character);
            }
        }

        return result.Length == 0 ? $"{prefix}-{Guid.NewGuid():N}" : result.ToString();
    }
}

public static class ExecutionResultStatus
{
    public const ExecutionStatus Succeeded = ExecutionStatus.Succeeded;
    public const ExecutionStatus Failed = ExecutionStatus.Failed;
    public const ExecutionStatus Cancelled = ExecutionStatus.Cancelled;
    public const ExecutionStatus Skipped = ExecutionStatus.Skipped;
    public const ExecutionStatus PartiallySucceeded = ExecutionStatus.PartiallySucceeded;
}
