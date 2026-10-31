// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

using KTools_App.Core;

namespace KTools_App.Diagnostics;

public sealed record LogEvent
{
    public const string LegacyEventIdPrefix = "legacy.";
    public const string LevelPadding = "       ";
    public const int SourceDisplayWidth = 20;

    public static IReadOnlyDictionary<string, object?> EmptyProperties { get; } = LogPropertyCollection.Empty;

    public DateTimeOffset TimestampUtc { get; init; }

    public long Sequence { get; init; }

    public Guid SessionId { get; init; }

    public string EventId { get; init; } = string.Empty;

    public LogLevel Level { get; init; }

    public LogStatus Status { get; init; }

    public string Source { get; init; } = string.Empty;

    public string? OperationId { get; init; }

    public string? ItemId { get; init; }

    public string? ProcessId { get; init; }

    public int? Pid { get; init; }

    public int? Attempt { get; init; }

    public string Message { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, object?> Properties { get; init; } = EmptyProperties;

    public ExceptionInfo? Exception { get; init; }

    public string? Tool { get; init; }

    public string? ToolVersion { get; init; }

    public int? ExitCode { get; init; }

    public double? DurationMs { get; init; }

    public string? Input { get; init; }

    public string? Output { get; init; }

    public string? StatusCode { get; init; }

    public string? ErrorCode { get; init; }

    public bool IsLegacy => EventId.StartsWith(LegacyEventIdPrefix, StringComparison.Ordinal);

    public string CorrelationKey =>
        SessionId.ToString("N", CultureInfo.InvariantCulture) + ":" + Sequence.ToString(CultureInfo.InvariantCulture);

    public string ToDisplayString() => Format(this, true);

    private static readonly string[] LevelLabels =
    {
        "DEBUG  ", "INFO   ", "WARNING", "ERROR  ", "FATAL  "
    };

    private static readonly string[] StatusLabels =
    {
        "None", "Running", "Succeeded", "Failed", "Cancelled",
        "Skipped", "PartiallySucceeded", "RetryScheduled", "Changed"
    };

    public static string Format(LogEvent logEvent, bool includeDetail)
    {
        StringBuilder builder = new(512);
        Span<char> timestamp = stackalloc char[24];
        int length = FormatTimestamp(logEvent.TimestampUtc.UtcDateTime, timestamp);
        if (length == 0)
        {
            builder.Append(logEvent.TimestampUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
        }
        else
        {
            builder.Append(timestamp[..length]);
        }

        builder.Append(" | ");
        builder.Append(LevelLabel(logEvent.Level));
        builder.Append(" | ");
        AppendPaddedSingleLine(builder, logEvent.Source, SourceDisplayWidth);
        builder.Append(" | ");
        builder.Append(logEvent.Sequence.ToString(CultureInfo.InvariantCulture));
        builder.Append(" | ");
        AppendSingleLine(builder, logEvent.EventId);
        LogStatus status = logEvent.Status;
        if (status != LogStatus.None)
        {
            builder.Append(" | ");
            builder.Append(StatusLabel(status));
        }

        builder.Append(" | ");
        AppendSingleLine(builder, logEvent.Message);

        if (includeDetail)
        {
            AppendContext(builder, logEvent);
            AppendProperties(builder, logEvent);
            AppendException(builder, logEvent.Exception);
        }

        return builder.ToString();
    }

    private static string LevelLabel(LogLevel level)
    {
        int index = (int)level;
        return index >= 0 && index < LevelLabels.Length ? LevelLabels[index] : LevelLabels[1];
    }

    private static string StatusLabel(LogStatus status)
    {
        int index = (int)status;
        return index >= 0 && index < StatusLabels.Length ? StatusLabels[index] : StatusLabels[0];
    }

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

    private static void AppendPaddedSingleLine(StringBuilder builder, string value, int width)
    {
        int index = 0;
        int length = value.Length;
        for (; index < length; index++)
        {
            char raw = value[index];
            if (raw is '\r' or '\n' or '\t')
            {
                break;
            }
        }

        if (index == length)
        {
            builder.Append(value.AsSpan(0, Math.Min(length, width)));
            for (int pad = Math.Min(length, width); pad < width; pad++)
            {
                builder.Append(' ');
            }

            return;
        }

        for (int position = 0; position < length && position < width; position++)
        {
            char raw = value[position];
            builder.Append(raw is '\r' or '\n' or '\t' ? ' ' : raw);
        }

        for (int pad = Math.Min(length, width); pad < width; pad++)
        {
            builder.Append(' ');
        }
    }

    private static void AppendSingleLine(StringBuilder builder, string value)
    {
        int length = value.Length;
        int index = 0;
        for (; index < length; index++)
        {
            if (value[index] is '\r' or '\n' or '\t')
            {
                break;
            }
        }

        if (index == length)
        {
            builder.Append(value.AsSpan());
            return;
        }

        builder.Append(value.AsSpan(0, index));
        for (; index < length; index++)
        {
            char raw = value[index];
            builder.Append(raw is '\r' or '\n' or '\t' ? ' ' : raw);
        }
    }

    private static void AppendContext(StringBuilder builder, LogEvent logEvent)
    {
        int count = 0;
        if (!string.IsNullOrEmpty(logEvent.OperationId)) count++;
        if (!string.IsNullOrEmpty(logEvent.ItemId)) count++;
        if (!string.IsNullOrEmpty(logEvent.ProcessId)) count++;
        if (logEvent.Pid.HasValue) count++;
        if (logEvent.Attempt.HasValue) count++;
        if (!string.IsNullOrEmpty(logEvent.Tool)) count++;
        if (logEvent.ExitCode.HasValue) count++;
        if (logEvent.DurationMs.HasValue) count++;
        if (!string.IsNullOrEmpty(logEvent.Input)) count++;
        if (!string.IsNullOrEmpty(logEvent.Output)) count++;
        if (!string.IsNullOrEmpty(logEvent.StatusCode)) count++;
        if (!string.IsNullOrEmpty(logEvent.ErrorCode)) count++;

        if (count == 0)
        {
            return;
        }

        builder.Append(" | ");
        bool first = true;
        AppendContextPart(builder, ref first, logEvent.OperationId, "op=");
        AppendContextPart(builder, ref first, logEvent.ItemId, "item=");
        AppendContextPart(builder, ref first, logEvent.ProcessId, "process=");
        if (logEvent.Pid.HasValue)
        {
            AppendSeparator(builder, ref first);
            builder.Append("pid=").Append(logEvent.Pid.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (logEvent.Attempt.HasValue)
        {
            AppendSeparator(builder, ref first);
            builder.Append("attempt=").Append(logEvent.Attempt.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrEmpty(logEvent.Tool))
        {
            AppendSeparator(builder, ref first);
            builder.Append("tool=").Append(logEvent.Tool);
            if (!string.IsNullOrEmpty(logEvent.ToolVersion))
            {
                builder.Append('@').Append(logEvent.ToolVersion);
            }
        }

        if (logEvent.ExitCode.HasValue)
        {
            AppendSeparator(builder, ref first);
            builder.Append("exit=").Append(logEvent.ExitCode.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (logEvent.DurationMs.HasValue)
        {
            AppendSeparator(builder, ref first);
            builder.Append("durationMs=").Append(logEvent.DurationMs.Value.ToString(CultureInfo.InvariantCulture));
        }

        AppendContextPart(builder, ref first, logEvent.Input, "input=");
        AppendContextPart(builder, ref first, logEvent.Output, "output=");
        AppendContextPart(builder, ref first, logEvent.StatusCode, "statusCode=");
        AppendContextPart(builder, ref first, logEvent.ErrorCode, "errorCode=");
    }

    private static void AppendContextPart(StringBuilder builder, ref bool first, string? value, string prefix)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        AppendSeparator(builder, ref first);
        builder.Append(prefix).Append(value);
    }

    private static void AppendSeparator(StringBuilder builder, ref bool first)
    {
        if (first)
        {
            first = false;
            return;
        }

        builder.Append(' ');
    }

    private static void AppendProperties(StringBuilder builder, LogEvent logEvent)
    {
        IReadOnlyDictionary<string, object?> properties = logEvent.Properties;
        if (properties.Count == 0)
        {
            return;
        }

        builder.Append(" | props{");
        bool first = true;
        if (properties is LogPropertyCollection snapshot)
        {
            LogPropertyCollection.Enumerator entries = snapshot.GetEnumerator();
            while (entries.MoveNext())
            {
                KeyValuePair<string, object?> pair = entries.Current;
                if (!first)
                {
                    builder.Append(", ");
                }

                first = false;
                builder.Append(pair.Key);
                builder.Append('=');
                AppendValue(builder, pair.Value);
            }
        }
        else
        {
            foreach (KeyValuePair<string, object?> pair in properties)
            {
                if (!first)
                {
                    builder.Append(", ");
                }

                first = false;
                builder.Append(pair.Key);
                builder.Append('=');
                AppendValue(builder, pair.Value);
            }
        }

        builder.Append('}');
    }

    private static void AppendException(StringBuilder builder, ExceptionInfo? exception)
    {
        ExceptionInfo? current = exception;
        bool first = true;
        while (current is not null)
        {
            builder.Append(first ? " | ex{Type=" : " <- ex{Type=");
            first = false;
            builder.Append(current.Type);
            builder.Append(", HResult=0x");
            builder.Append(current.HResult.ToString("X8", CultureInfo.InvariantCulture));
            builder.Append(", Message=");
            AppendSingleLine(builder, current.Message);
            if (!string.IsNullOrEmpty(current.StackTrace))
            {
                builder.Append(", Стек вызовов: ");
                AppendSingleLine(builder, current.StackTrace!);
            }

            builder.Append('}');
            current = current.Inner;
        }
    }

    private static void AppendValue(StringBuilder builder, object? value)
    {
        switch (value)
        {
            case null:
                builder.Append("null");
                break;
            case bool flag:
                builder.Append(flag ? "true" : "false");
                break;
            case double number:
                builder.Append(number.ToString(CultureInfo.InvariantCulture));
                break;
            case float number:
                builder.Append(number.ToString(CultureInfo.InvariantCulture));
                break;
            case decimal number:
                builder.Append(number.ToString(CultureInfo.InvariantCulture));
                break;
            case IFormattable formattable:
                builder.Append(formattable.ToString(null, CultureInfo.InvariantCulture));
                break;
            default:
                builder.Append(value.ToString() ?? string.Empty);
                break;
        }
    }

    public static bool TryParseText(string line, out LogEvent? logEvent)
    {
        logEvent = null;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        string[] parts = line.Split(" | ");
        if (parts.Length < 5)
        {
            return false;
        }

        if (!DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset timestamp))
        {
            return false;
        }

        LogLevel level = parts[1].Trim() switch
        {
            "DEBUG" => LogLevel.Debug,
            "INFO" => LogLevel.Info,
            "WARNING" => LogLevel.Warning,
            "ERROR" => LogLevel.Error,
            "FATAL" => LogLevel.Fatal,
            _ => LogLevel.Info
        };

        string source = parts[2].Trim();

        if (!long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out long sequence))
        {
            return false;
        }

        string eventId = parts[4].Trim();

        int nextIndex = 5;
        LogStatus status = LogStatus.None;
        if (nextIndex < parts.Length && Enum.TryParse<LogStatus>(parts[nextIndex].Trim(), out LogStatus parsedStatus))
        {
            status = parsedStatus;
            nextIndex++;
        }

        string message = string.Empty;
        if (nextIndex < parts.Length)
        {
            message = parts[nextIndex];
            nextIndex++;
        }

        string? operationId = null;
        string? itemId = null;
        string? processId = null;
        int? pid = null;
        int? attempt = null;
        string? tool = null;
        string? toolVersion = null;
        int? exitCode = null;
        double? durationMs = null;
        string? input = null;
        string? output = null;
        string? statusCode = null;
        string? errorCode = null;
        Dictionary<string, object?> properties = new(StringComparer.Ordinal);
        ExceptionInfo? exception = null;

        for (int i = nextIndex; i < parts.Length; i++)
        {
            string part = parts[i];
            if (part.StartsWith("props{", StringComparison.Ordinal) && part.EndsWith('}'))
            {
                ParseProperties(part.Substring(6, part.Length - 7), properties);
            }
            else if (part.StartsWith("ex{", StringComparison.Ordinal) && part.EndsWith('}'))
            {
                exception = ParseException(part.Substring(3, part.Length - 4));
            }
            else
            {
                ParseContextTokens(part, ref operationId, ref itemId, ref processId, ref pid, ref attempt, ref tool, ref toolVersion, ref exitCode, ref durationMs, ref input, ref output, ref statusCode, ref errorCode);
            }
        }

        logEvent = new LogEvent
        {
            TimestampUtc = timestamp,
            Sequence = sequence,
            EventId = eventId,
            Level = level,
            Status = status,
            Source = source,
            Message = message,
            OperationId = operationId,
            ItemId = itemId,
            ProcessId = processId,
            Pid = pid,
            Attempt = attempt,
            Tool = tool,
            ToolVersion = toolVersion,
            ExitCode = exitCode,
            DurationMs = durationMs,
            Input = input,
            Output = output,
            StatusCode = statusCode,
            ErrorCode = errorCode,
            Properties = properties.Count > 0 ? LogPropertyCollection.Create(properties) : EmptyProperties,
            Exception = exception
        };

        return true;
    }

    private static void ParseContextTokens(
        string part,
        ref string? operationId,
        ref string? itemId,
        ref string? processId,
        ref int? pid,
        ref int? attempt,
        ref string? tool,
        ref string? toolVersion,
        ref int? exitCode,
        ref double? durationMs,
        ref string? input,
        ref string? output,
        ref string? statusCode,
        ref string? errorCode)
    {
        string[] tokens = part.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (string token in tokens)
        {
            int eq = token.IndexOf('=');
            if (eq <= 0) continue;
            string key = token[..eq];
            string val = token[(eq + 1)..];
            switch (key)
            {
                case "op": operationId = val; break;
                case "item": itemId = val; break;
                case "process": processId = val; break;
                case "pid": if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int p)) pid = p; break;
                case "attempt": if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int a)) attempt = a; break;
                case "tool":
                    int at = val.IndexOf('@');
                    if (at > 0)
                    {
                        tool = val[..at];
                        toolVersion = val[(at + 1)..];
                    }
                    else
                    {
                        tool = val;
                    }
                    break;
                case "exit": if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ex)) exitCode = ex; break;
                case "durationMs": if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) durationMs = d; break;
                case "input": input = val; break;
                case "output": output = val; break;
                case "statusCode": statusCode = val; break;
                case "errorCode": errorCode = val; break;
            }
        }
    }

    private static void ParseProperties(string content, Dictionary<string, object?> target)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        int start = 0;
        while (start < content.Length)
        {
            int eq = content.IndexOf('=', start);
            if (eq < 0) break;
            string key = content[start..eq].Trim();

            int nextStart = content.Length;
            int searchPos = eq + 1;
            while (searchPos < content.Length)
            {
                int comma = content.IndexOf(", ", searchPos, StringComparison.Ordinal);
                if (comma < 0) break;
                int afterComma = comma + 2;
                int nextEq = content.IndexOf('=', afterComma);
                if (nextEq > afterComma && !content[afterComma..nextEq].Contains(' '))
                {
                    nextStart = comma;
                    break;
                }
                searchPos = comma + 2;
            }

            string valStr = content[(eq + 1)..nextStart].Trim();
            object? typedVal = valStr;
            if (bool.TryParse(valStr, out bool b))
            {
                typedVal = b;
            }
            else if (int.TryParse(valStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int num))
            {
                typedVal = num;
            }
            else if (long.TryParse(valStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l))
            {
                typedVal = l;
            }
            target[key] = typedVal;
            start = nextStart < content.Length ? nextStart + 2 : content.Length;
        }
    }

    private static ExceptionInfo? ParseException(string content)
    {
        string type = "Exception";
        int hresult = 0;
        string message = string.Empty;
        string? stack = null;

        string[] tokens = content.Split(", ");
        foreach (string token in tokens)
        {
            int eq = token.IndexOf('=');
            if (eq > 0)
            {
                string k = token[..eq].Trim();
                string v = token[(eq + 1)..].Trim();
                if (k == "Type") type = v;
                else if (k == "HResult" && v.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && int.TryParse(v[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int hr)) hresult = hr;
                else if (k == "Message") message = v;
            }
            else if (token.StartsWith("Стек вызовов: ", StringComparison.Ordinal))
            {
                stack = token.Substring("Стек вызовов: ".Length);
            }
        }

        return ExceptionInfo.Create(type, message, hresult, stack);
    }
}
