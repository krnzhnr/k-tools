// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Globalization;

namespace KTools_App.Diagnostics;

public sealed class LogContext
{
    private static readonly IReadOnlyDictionary<string, object?> EmptyProperties =
        new Dictionary<string, object?>(0, StringComparer.Ordinal);

    private LogContext(LogContext? parent, string? operationId, string? itemId, string? processId, int? attempt, string? tool)
    {
        Parent = parent;
        OperationId = operationId;
        ItemId = itemId;
        ProcessId = processId;
        Attempt = attempt;
        Tool = tool;
    }

    public static LogContext Empty { get; } = new(null, null, null, null, null, null);

    public LogContext? Parent { get; }

    public string? OperationId { get; }

    public string? ItemId { get; }

    public string? ProcessId { get; }

    public int? Attempt { get; }

    public string? Tool { get; }

    public bool IsEmpty =>
        OperationId is null && ItemId is null && ProcessId is null && Attempt is null && Tool is null;

    public LogContext WithOperation(string? operationId)
    {
        string? safe = Normalize(operationId);
        return new(this, IsSafeToken(safe) ? safe : null, ItemId, ProcessId, Attempt, Tool);
    }

    public LogContext WithItem(string? itemId)
    {
        string? safe = Normalize(itemId);
        return new(this, OperationId, IsSafeToken(safe) ? safe : null, ProcessId, Attempt, Tool);
    }

    public LogContext WithProcess(string? processId)
    {
        string? safe = Normalize(processId);
        return new(this, OperationId, ItemId, IsSafeToken(safe) ? safe : null, Attempt, Tool);
    }

    public LogContext WithAttempt(int? attempt) =>
        new(this, OperationId, ItemId, ProcessId, attempt is null or < 1 ? null : attempt, Tool);

    public LogContext WithTool(string? tool)
    {
        string? safe = Normalize(tool);
        return new(this, OperationId, ItemId, ProcessId, Attempt, IsSafeToken(safe) ? safe : null);
    }

    public LogContext ToChild() => new(this, null, null, null, null, null);

    public string? ResolveOperationId() => OperationId ?? Parent?.ResolveOperationId();

    public string? ResolveItemId() => ItemId ?? Parent?.ResolveItemId();

    public string? ResolveProcessId() => ProcessId ?? Parent?.ResolveProcessId();

    public int? ResolveAttempt() => Attempt ?? Parent?.ResolveAttempt();

    public string? ResolveTool() => Tool ?? Parent?.ResolveTool();

    public IReadOnlyDictionary<string, object?> ToProperties()
    {
        Dictionary<string, object?>? result = null;

        Add(result = new Dictionary<string, object?>(StringComparer.Ordinal), "OperationId", ResolveOperationId());
        Add(result!, "ItemId", ResolveItemId());
        Add(result!, "ProcessId", ResolveProcessId());
        Add(result!, "Tool", ResolveTool());

        int? attempt = ResolveAttempt();
        if (attempt.HasValue)
        {
            result!["Attempt"] = attempt.Value;
        }

        return result is null || result.Count == 0 ? EmptyProperties : result;

        static void Add(Dictionary<string, object?> target, string key, string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                target[key] = value;
            }
        }
    }

    public string Describe()
    {
        List<string> parts = new(5);
        string? operation = ResolveOperationId();
        string? item = ResolveItemId();
        string? process = ResolveProcessId();
        int? attempt = ResolveAttempt();

        if (!string.IsNullOrEmpty(operation)) parts.Add("op=" + operation);
        if (!string.IsNullOrEmpty(item)) parts.Add("item=" + item);
        if (!string.IsNullOrEmpty(process)) parts.Add("process=" + process);
        if (attempt.HasValue) parts.Add("attempt=" + attempt.Value.ToString(CultureInfo.InvariantCulture));

        return string.Join(",", parts);
    }

    public override string ToString() => Describe();

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }

    private static bool IsSafeToken(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > LogRedactor.MaxIdentifierLength)
        {
            return false;
        }

        return !LogRedactor.LooksUnsafe(value);
    }
}
