// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;

namespace KTools_App.Diagnostics;

public sealed class ExceptionInfo
{
    private const int DefaultMaxDepth = 8;
    private const int MaxObservedDepth = 256;

    private ExceptionInfo(
        string type,
        string message,
        int hResult,
        string? stackTrace,
        ExceptionInfo? inner,
        bool exceptionTruncated,
        int originalChainLength,
        int keptChainLength)
    {
        Type = type;
        Message = message;
        HResult = hResult;
        StackTrace = stackTrace;
        Inner = inner;
        ChainLength = 1 + (inner?.ChainLength ?? 0);
        ExceptionTruncated = exceptionTruncated;
        OriginalChainLength = originalChainLength > 0 ? originalChainLength : ChainLength;
        KeptChainLength = keptChainLength > 0 ? keptChainLength : ChainLength;
    }

    public string Type { get; }

    public string Message { get; }

    public int HResult { get; }

    public string? StackTrace { get; }

    public ExceptionInfo? Inner { get; }

    public int ChainLength { get; }

    public bool ExceptionTruncated { get; }

    public bool Truncated => ExceptionTruncated;

    public int OriginalChainLength { get; }

    public int KeptChainLength { get; }

    public int OriginalCount => OriginalChainLength;

    public int KeptCount => KeptChainLength;

    public static ExceptionInfo Create(
        string type,
        string message,
        int hResult,
        string? stackTrace,
        ExceptionInfo? inner = null,
        bool exceptionTruncated = false,
        int originalChainLength = 0,
        int keptChainLength = 0)
    {
        return new ExceptionInfo(
            string.IsNullOrEmpty(type) ? "Exception" : type,
            message ?? string.Empty,
            hResult,
            string.IsNullOrEmpty(stackTrace) ? null : stackTrace,
            inner,
            exceptionTruncated,
            originalChainLength,
            keptChainLength);
    }

    public static ExceptionInfo FromException(Exception? exception, int maxDepth = DefaultMaxDepth)
    {
        ArgumentNullException.ThrowIfNull(exception);

        int depth = maxDepth <= 0 ? 1 : maxDepth;
        List<Exception> chain = new(Math.Min(depth, MaxObservedDepth));
        chain.Add(exception);
        Exception? current = exception;
        while (chain.Count < MaxObservedDepth)
        {
            Exception? next = SafeGetInner(current!);
            if (next is null)
            {
                break;
            }

            chain.Add(next);
            current = next;
        }

        int originalCount = chain.Count;
        bool countTruncated = chain.Count == MaxObservedDepth && SafeGetInner(chain[^1]) is not null;
        int keptCount = Math.Min(originalCount, depth);
        List<ExceptionInfo> nodes = new(Math.Max(0, keptCount - 1));
        for (int index = 1; index < keptCount; index++)
        {
            Exception node = chain[index];
            nodes.Add(Create(
                node.GetType().FullName ?? node.GetType().Name,
                node.Message ?? string.Empty,
                node.HResult,
                node.StackTrace));
        }

        ExceptionInfo? inner = null;
        for (int index = nodes.Count - 1; index >= 0; index--)
        {
            ExceptionInfo node = nodes[index];
            inner = Create(node.Type, node.Message, node.HResult, node.StackTrace, inner);
        }

        bool truncated = countTruncated || originalCount > keptCount;
        return Create(
            exception.GetType().FullName ?? exception.GetType().Name,
            exception.Message ?? string.Empty,
            exception.HResult,
            exception.StackTrace,
            inner,
            truncated,
            originalCount,
            keptCount);
    }

    public IReadOnlyList<ExceptionInfo> EnumerateChain()
    {
        List<ExceptionInfo> chain = new(ChainLength);
        ExceptionInfo? current = this;
        while (current is not null)
        {
            chain.Add(current);
            current = current.Inner;
        }

        return chain;
    }

    public override string ToString() => Type + ": " + Message;

    private static Exception? SafeGetInner(Exception exception)
    {
        try
        {
            return exception.InnerException;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
