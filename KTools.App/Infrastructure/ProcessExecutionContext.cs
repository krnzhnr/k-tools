// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

using KTools_App.Diagnostics;

namespace KTools_App.Infrastructure;

public sealed record ProcessExecutionContext
{
    public const int DefaultAttempt = 1;
    public const string OperationPrefix = "op-";
    public const string ItemPrefix = "item-";
    public const string ToolFallback = "external";
    public const string ProcessPrefix = "proc-";
    public const string UnknownIdentifier = "unknown";
    public const int HashLength = 16;

    private ProcessExecutionContext(
        string operationId,
        string? itemId,
        string tool,
        string? toolVersion,
        int attempt,
        string? expectedArtifact)
    {
        OperationId = operationId;
        ItemId = itemId;
        Tool = tool;
        ToolVersion = toolVersion;
        Attempt = attempt < 1 ? DefaultAttempt : attempt;
        ExpectedArtifact = expectedArtifact;
    }

    public string OperationId { get; }

    public string? ItemId { get; }

    public string Tool { get; }

    public string? ToolVersion { get; }

    public int Attempt { get; }

    public string? ExpectedArtifact { get; }

    public bool HasExpectedArtifact => !string.IsNullOrEmpty(ExpectedArtifact);

    public ProcessExecutionContext WithItem(string? itemId) =>
        new(OperationId, NormalizeOptional(itemId, ItemPrefix), Tool, ToolVersion, Attempt, ExpectedArtifact);

    public ProcessExecutionContext WithToolVersion(string? toolVersion) =>
        new(OperationId, ItemId, Tool, NormalizeOptional(toolVersion, "v"), Attempt, ExpectedArtifact);

    public ProcessExecutionContext WithAttempt(int? attempt) =>
        new(OperationId, ItemId, Tool, ToolVersion, attempt is null or < 1 ? Attempt : attempt.Value, ExpectedArtifact);

    public ProcessExecutionContext WithExpectedArtifact(string? artifactPath) =>
        new(OperationId, ItemId, Tool, ToolVersion, Attempt, ToArtifactLabel(artifactPath));

    public LogContext ToLogContext() =>
        LogContext.Empty
            .WithOperation(OperationId)
            .WithItem(ItemId)
            .WithTool(Tool)
            .WithAttempt(Attempt);

    public Dictionary<string, object?> ToLogProperties()
    {
        Dictionary<string, object?> properties = new(StringComparer.Ordinal)
        {
            ["OperationId"] = OperationId,
            ["Tool"] = Tool,
            ["Attempt"] = Attempt
        };

        if (!string.IsNullOrEmpty(ItemId))
        {
            properties["ItemId"] = ItemId;
        }

        if (!string.IsNullOrEmpty(ToolVersion))
        {
            properties["ToolVersion"] = ToolVersion;
        }

        if (!string.IsNullOrEmpty(ExpectedArtifact))
        {
            properties["ExpectedArtifact"] = ExpectedArtifact;
        }

        return properties;
    }

    public static ProcessExecutionContext Create(
        string tool,
        string? operationId = null,
        string? itemId = null,
        string? toolVersion = null,
        int attempt = DefaultAttempt,
        string? expectedArtifact = null)
    {
        return new ProcessExecutionContext(
            NormalizeRequired(operationId, OperationPrefix),
            NormalizeOptional(itemId, ItemPrefix),
            NormalizeToolName(tool),
            NormalizeOptional(toolVersion, "v"),
            attempt,
            ToArtifactLabel(expectedArtifact));
    }

    public static ProcessExecutionContext FromOperation(
        string operationId,
        string tool,
        string? itemId = null,
        int attempt = DefaultAttempt)
    {
        return Create(tool, operationId, itemId, attempt: attempt);
    }

    public static ProcessExecutionContext NewOperation(string tool) => Create(tool);

    public static string CreateProcessId() => ProcessPrefix + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    public static string NormalizeToolName(string? tool)
    {
        if (string.IsNullOrWhiteSpace(tool))
        {
            return ToolFallback;
        }

        string candidate = tool.Trim();
        try
        {
            string fileName = Path.GetFileName(candidate);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                candidate = fileName;
            }
        }
        catch (Exception)
        {
        }

        if (candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate[..^4];
        }

        return NormalizeRequired(candidate, "tool");
    }

    public static string ToArtifactLabel(string? artifactPath)
    {
        if (string.IsNullOrWhiteSpace(artifactPath))
        {
            return null!;
        }

        try
        {
            string name = Path.GetFileName(artifactPath.Trim());
            return string.IsNullOrWhiteSpace(name) ? UnknownIdentifier : name;
        }
        catch (Exception)
        {
            return UnknownIdentifier;
        }
    }

    public static int CountArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return 0;
        }

        int count = 0;
        bool inQuotes = false;
        char quote = '\0';
        bool pending = false;

        for (int index = 0; index < arguments.Length; index++)
        {
            char raw = arguments[index];

            if (inQuotes)
            {
                if (raw == quote)
                {
                    inQuotes = false;
                }

                continue;
            }

            if (raw is '"' or '\'')
            {
                inQuotes = true;
                quote = raw;
                pending = true;
                continue;
            }

            if (char.IsWhiteSpace(raw))
            {
                if (pending)
                {
                    count++;
                    pending = false;
                }

                continue;
            }

            pending = true;
        }

        if (pending)
        {
            count++;
        }

        return count;
    }

    public static string HashArguments(string? arguments)
    {
        if (string.IsNullOrEmpty(arguments))
        {
            return "empty";
        }

        try
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(arguments));
            return Convert.ToHexString(hash).ToLowerInvariant()[..HashLength];
        }
        catch (Exception)
        {
            return "unavailable";
        }
    }

    public static string LabelDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return "default";
        }

        return "wd-" + HashArguments(directory.Trim());
    }

    public static string LabelFile(string? path) => ToArtifactLabel(path) ?? UnknownIdentifier;

    private static string NormalizeRequired(string? value, string prefix)
    {
        string? optional = NormalizeOptional(value, prefix);
        return optional ?? prefix + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
    }

    private static string? NormalizeOptional(string? value, string prefix)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string candidate = value.Trim();
        if (LogRedactor.LooksUnsafe(candidate) || candidate.Length > LogRedactor.MaxIdentifierLength)
        {
            return prefix + HashArguments(candidate);
        }

        StringBuilder builder = new(candidate.Length);
        foreach (char character in candidate)
        {
            if (char.IsLetterOrDigit(character) || character is '_' or '-' or '.')
            {
                builder.Append(character);
            }
        }

        return builder.Length == 0 ? prefix + HashArguments(candidate) : builder.ToString();
    }
}
