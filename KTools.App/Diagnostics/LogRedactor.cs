// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace KTools_App.Diagnostics;

public sealed class LogRedactor
{
    public const string RedactedMarker = "[redacted]";
    public const string TruncationMarker = "...[truncated]";
    public const string UnknownIdentifier = "Unknown";
    public const string DroppedPropertyPrefix = "DroppedProperty";
    public const string RedactedPropertyPrefix = "RedactedProperty";
    public const string RedactedCountProperty = "redactedCount";
    public const string DroppedPropertiesProperty = "droppedProperties";
    public const string MarkedPropertiesProperty = "markedProperties";
    public const string RedactionReasonProperty = "redactionReason";
    public const string SensitiveKeyCategory = "SENSITIVE_KEY";
    public const int DefaultMaxMessageLength = LogServiceOptions.DefaultMaxMessageLength;
    public const int DefaultMaxPropertiesLength = LogServiceOptions.DefaultMaxPropertiesLength;
    public const int DefaultMaxDetailLength = LogServiceOptions.DefaultMaxDetailLength;
    public const int MaxIdentifierLength = 128;
    public const int MaxKeyLength = 64;
    public const int MaxEventIdLength = 64;
    public const int MaxMarkerEntries = 64;

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    private static readonly System.Buffers.SearchValues<char> QueryFragmentMarkers =
        System.Buffers.SearchValues.Create("?#");

    private static readonly System.Buffers.SearchValues<char> PathSeparators =
        System.Buffers.SearchValues.Create("\\/");

    private static readonly Regex UrlRegex = new(
        @"(?<scheme>[a-zA-Z][a-zA-Z0-9+.\-]{1,15})://(?<body>[^\s""'<>\\|]+)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        MatchTimeout);

    private static readonly Regex ExtendedPathRegex = new(
        @"(?<![A-Za-z0-9_\\])(?<prefix>\\\\[?.][\\/]+)(?<body>[^\s""'<>|]*)",
        RegexOptions.CultureInvariant,
        MatchTimeout);

    private static readonly Regex DrivePathRegex = new(
        @"(?<![A-Za-z0-9_./\\:])(?<path>[A-Za-z]:(?:[\\/]+[^\\/:*?""<>|\r\n]+)+[\\/]*)",
        RegexOptions.CultureInvariant,
        MatchTimeout);

    private static readonly Regex DriveRelativePathRegex = new(
        @"(?<![A-Za-z0-9_\-])(?<path>[A-Za-z]:[^\\/:*?""<>|\r\n\s,;()\[\]{}]+)",
        RegexOptions.CultureInvariant,
        MatchTimeout);

    private static readonly Regex UncPathRegex = new(
        @"(?<![A-Za-z0-9_\\])(?<path>\\\\[\\/]*[^\\/:*?""<>|\r\n]+(?:[\\/]+[^\\/:*?""<>|\r\n]+)*)",
        RegexOptions.CultureInvariant,
        MatchTimeout);

    private static readonly Regex SensitiveHeaderRegex = new(
        @"(?<key>\b(?:authorization|proxy-authorization|cookie|set-cookie|x-api-key|x-auth-token)\b)(?<sep>\s*:\s*)(?<value>[^\r\n]+)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        MatchTimeout);

    private static readonly Regex SensitiveAssignmentRegex = new(
        @"(?<key>\b(?:tokens?|access[_-]?tokens?|refresh[_-]?tokens?|id[_-]?tokens?|api[_-]?keys?|apisecret|client[_-]?secrets?|secrets?|passwords?|passwd|pwd|passphrase|credentials?|session[_-]?ids?|signatures?|private[_-]?keys?|auth[_-]?tokens?)[""']?)(?<sep>\s*[:=]\s*)(?<value>""[^""]*""|'[^']*'|[^\s,;)\]}]+)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        MatchTimeout);

    private static readonly Regex EventIdRegex = new(
        @"^[A-Za-z][A-Za-z0-9]*([._][A-Za-z0-9]+)*$",
        RegexOptions.CultureInvariant,
        MatchTimeout);

    private static readonly HashSet<string> AllowedPropertyKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "ArgumentCount", "Attempt", "BitrateKbps", "Bytes", "CancelReason", "Changed", "Channel", "CommandHash",
        "Count", "CrashId", "DroppedCount", "DurationMs", "ElapsedMs", "ErrorCode", "Errors", "EventId", "ExitCode",
        "ExitRequested", "Extension", "FileName", "Fingerprint", "Fps", "Group", "HttpMethod", "Index",
        "InputFingerprint", "InputName", "IsAdmin", "ItemCount", "Key", "Level", "Lines", "MaxAttempts", "OutputName",
        "OutputTail", "OutputTruncated", "Percent", "Persisted", "Pid", "Platform", "ProcessId", "QueueSize", "Reason",
        "RelatedCrashId", "Retryable", "Schema", "ScriptId", "Succeeded", "Failed", "Cancelled", "Skipped", "Total",
        "TotalBytes", "Tool", "ToolVersion", "ValueHash", "Verified", "Version", "Status", "Source", "OperationId",
        "ItemId",         "SessionId", "ExecutedStages", "AlreadyExited", "Language", "Theme", "Page", "Control", "Stage",
        "ExitRequestPending", "StreamName", "Container", "Codec", "Resolution", "AudioChannels", "SampleRate", "FrameRate",
        "DroppedProperties", "RedactedCount", "RedactionReason", "Truncated", "MarkedProperties", "UnlistedSnapshot",
        "exceptionTruncated", "originalExceptionCount", "keptExceptionCount", "OutputExists", "CleanupState",
        "MessageCount", "PartiallySucceeded", "Attempted", "WarningCount", "DroppedLines", "DroppedBytes",
        "RetainedLines", "RetainedBytes", "WorkingDirLabel", "SuppressedCount", "ExpectedArtifact", "ArgumentCount",
        "ArtifactVerified", "ParserVerified", "StdoutBytes", "StderrBytes", "HostLabel", "MediaId",
        "MaxAttempts", "AttemptCount", "TopicCount", "QueueLength",
        "Architecture", "ArtifactExists", "Mode", "StatusCode", "TargetTag",
        "RejectedCount", "WriteErrors", "RotationErrors", "SubscriberErrors", "SubscriberDropped",
        "DisposeErrors", "ReadErrors", "QueuedBytes", "CommandLine", "Value"
    };

    private static readonly HashSet<string> SensitiveKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization", "proxyauthorization", "cookie", "setcookie", "xapikey", "xauthtoken", "token",
        "accesstoken", "refreshtoken", "idtoken", "apikey", "apisecret", "clientsecret", "secret", "password",
        "passwd", "pwd", "passphrase", "credential", "credentials", "sessionid", "signature", "privatekey",
        "authtoken", "bearer", "jwt", "otp", "pin"
    };

    private readonly int _maxMessageLength;
    private readonly int _maxPropertiesLength;
    private readonly int _maxDetailLength;
    private readonly int _maxExceptionDepth;

    public LogRedactor(LogServiceOptions? options = null)
    {
        LogServiceOptions effective = (options ?? new LogServiceOptions()).Clone().Normalize();
        _maxMessageLength = effective.MaxMessageLength;
        _maxPropertiesLength = effective.MaxPropertiesLength;
        _maxDetailLength = effective.MaxDetailLength;
        _maxExceptionDepth = effective.MaxExceptionDepth;
    }

    public LogRedactor(int maxMessageLength, int maxPropertiesLength, int maxDetailLength, int maxExceptionDepth)
    {
        _maxMessageLength = Math.Clamp(maxMessageLength, 64, 1 << 20);
        _maxPropertiesLength = Math.Clamp(maxPropertiesLength, 64, 1 << 22);
        _maxDetailLength = Math.Clamp(maxDetailLength, 64, 1 << 24);
        _maxExceptionDepth = Math.Clamp(maxExceptionDepth, 1, 32);
    }

    public static LogRedactor Default { get; } = new();

    public int MaxMessageLength => _maxMessageLength;

    public int MaxPropertiesLength => _maxPropertiesLength;

    public int MaxDetailLength => _maxDetailLength;

    public int MaxExceptionDepth => _maxExceptionDepth;

    public string RedactMessage(string? value) => Bound(Pipeline(value), _maxMessageLength);

    public string RedactDetail(string? value) => Bound(Pipeline(value), _maxDetailLength);

    public string RedactToken(string? value) => SingleLine(Bound(Pipeline(value), _maxMessageLength));

    public string RedactDetailToken(string? value) => SingleLine(Bound(Pipeline(value), _maxDetailLength));

    public string SanitizeIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return UnknownIdentifier;
        }

        string trimmed = value.Trim();
        if (LooksUnsafe(trimmed))
        {
            return UnknownIdentifier;
        }

        StringBuilder builder = new(Math.Min(trimmed.Length, MaxIdentifierLength));
        foreach (char raw in trimmed)
        {
            if (builder.Length >= MaxIdentifierLength)
            {
                break;
            }

            builder.Append(IsIdentifierChar(raw) ? raw : '_');
        }

        return builder.Length == 0 ? UnknownIdentifier : builder.ToString();
    }

    public static bool IsValidEventId(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxEventIdLength)
        {
            return false;
        }

        try
        {
            return EventIdRegex.IsMatch(value);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public string SanitizeEventId(string? value, string fallback)
    {
        if (IsValidEventId(value))
        {
            return value!;
        }

        return IsValidEventId(fallback) ? fallback : UnknownIdentifier;
    }

    public string SanitizeKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return "key";
        }

        StringBuilder builder = new(Math.Min(key.Length, MaxKeyLength));
        for (int index = 0; index < key.Length && builder.Length < MaxKeyLength; index++)
        {
            char raw = key[index];
            if (IsIdentifierChar(raw))
            {
                builder.Append(raw);
            }
            else if (builder.Length > 0 && builder[^1] != '_')
            {
                builder.Append('_');
            }
        }

        if (builder.Length == 0)
        {
            return "key";
        }

        if (char.IsDigit(builder[0]))
        {
            builder.Insert(0, '_');
        }

        return builder.ToString();
    }

    public bool IsAllowedPropertyKey(string? key) =>
        !string.IsNullOrEmpty(key) && AllowedPropertyKeys.Contains(key);

    public static bool IsSensitiveKeyName(string? key)
    {
        try
        {
            if (string.IsNullOrEmpty(key))
            {
                return false;
            }

            string compact = Compact(key);
            if (SensitiveKeys.Contains(compact))
            {
                return true;
            }

            return compact.Contains("password", StringComparison.Ordinal)
                || compact.Contains("secret", StringComparison.Ordinal)
                || compact.Contains("token", StringComparison.Ordinal)
                || compact.Contains("cookie", StringComparison.Ordinal)
                || compact.Contains("apikey", StringComparison.Ordinal)
                || compact.Contains("authorization", StringComparison.Ordinal)
                || compact.Contains("credential", StringComparison.Ordinal)
                || compact.Contains("passphrase", StringComparison.Ordinal)
                || compact.Contains("privatekey", StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return true;
        }
    }

    public static bool LooksUnsafe(string? value)
    {
        try
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            foreach (char raw in value)
            {
                if (char.IsControl(raw))
                {
                    return true;
                }
            }

            if (value.Contains("://", StringComparison.Ordinal))
            {
                return true;
            }

            if (value.Contains('\\') || value.Contains('/'))
            {
                return true;
            }

            if (value.Contains('=') || value.Contains('?'))
            {
                return true;
            }

            return CanMatchRedactionPatterns(value)
                && (DriveRelativePathRegex.IsMatch(value)
                    || SensitiveAssignmentRegex.IsMatch(value)
                    || SensitiveHeaderRegex.IsMatch(value));
        }
        catch (Exception)
        {
            return true;
        }
    }
    public IReadOnlyDictionary<string, object?> RedactProperties(IReadOnlyDictionary<string, object?>? properties)
    {
        if (properties is null || properties.Count == 0)
        {
            return LogPropertyCollection.Empty;
        }

        Dictionary<string, object?> result = new(properties.Count, StringComparer.Ordinal);
        int used = 2;
        int redactedMarkers = 0;
        int droppedMarkers = 0;
        int redactedCount = 0;
        int droppedCount = 0;
        int droppedByLimit = 0;

        foreach (KeyValuePair<string, object?> pair in properties)
        {
            string rawKey = pair.Key ?? string.Empty;

            if (IsSensitiveKeyName(rawKey))
            {
                redactedCount++;
                if (redactedMarkers < MaxMarkerEntries)
                {
                    redactedMarkers++;
                    used = AddMarker(result, used, RedactedPropertyPrefix + redactedMarkers.ToString("D2", CultureInfo.InvariantCulture));
                }

                continue;
            }

            if (!IsAllowedPropertyKey(rawKey))
            {
                droppedCount++;
                if (droppedMarkers < MaxMarkerEntries)
                {
                    droppedMarkers++;
                    used = AddMarker(result, used, DroppedPropertyPrefix + droppedMarkers.ToString("D2", CultureInfo.InvariantCulture));
                }

                continue;
            }

            object? value;
            if (string.Equals(rawKey, "CommandLine", StringComparison.OrdinalIgnoreCase) && pair.Value is string cmd)
            {
                value = RedactToken(cmd);
            }
            else if (string.Equals(rawKey, "Value", StringComparison.OrdinalIgnoreCase) && pair.Value is string val)
            {
                value = RedactToken(val);
            }
            else
            {
                value = RedactPropertyValue(pair.Value);
            }

            string key = SanitizeKey(rawKey);
            int cost = Encoding.UTF8.GetByteCount(key) + 2 + EstimateSize(value);
            if (used + cost > _maxPropertiesLength)
            {
                droppedByLimit++;
                continue;
            }

            used += cost;
            result[key] = value;
        }

        if (redactedCount > 0)
        {
            used = AddSummary(result, used, RedactedCountProperty, redactedCount);
            used = AddSummary(result, used, RedactionReasonProperty, SensitiveKeyCategory);
        }

        if (droppedCount > 0)
        {
            used = AddSummary(result, used, MarkedPropertiesProperty, droppedCount);
        }

        if (droppedByLimit > 0)
        {
            AddSummary(result, used, DroppedPropertiesProperty, droppedByLimit);
        }

        return LogPropertyCollection.Create(result);
    }

    private int AddMarker(Dictionary<string, object?> target, int used, string key)
    {
        int cost = Encoding.UTF8.GetByteCount(key) + 2 + EstimateSize(LogRedactor.RedactedMarker);
        if (used + cost > _maxPropertiesLength)
        {
            return used;
        }

        target[key] = LogRedactor.RedactedMarker;
        return used + cost;
    }

    private int AddSummary(Dictionary<string, object?> target, int used, string key, object value)
    {
        int cost = Encoding.UTF8.GetByteCount(key) + 2 + EstimateSize(value);
        if (used + cost > _maxPropertiesLength)
        {
            return used;
        }

        target[key] = value;
        return used + cost;
    }

    public ExceptionInfo? RedactException(ExceptionInfo? exception)
    {
        if (exception is null)
        {
            return null;
        }

        IReadOnlyList<ExceptionInfo> chain = exception.EnumerateChain();
        int originalCount = exception.OriginalChainLength > 0 ? exception.OriginalChainLength : chain.Count;
        int nodeCount = chain.Count > _maxExceptionDepth ? _maxExceptionDepth : chain.Count;
        if (nodeCount < 1)
        {
            nodeCount = 1;
        }

        bool exceptionTruncated = exception.ExceptionTruncated || chain.Count > nodeCount || originalCount > nodeCount;
        int perNode = Math.Max(64, _maxDetailLength / (2 * nodeCount));
        ExceptionInfo? inner = null;

        for (int index = nodeCount - 1; index >= 0; index--)
        {
            ExceptionInfo node = chain[index];
            string message;
            string? stack = null;
            string type = SanitizeIdentifier(node.Type);

            try
            {
                message = Bound(SingleLine(Pipeline(node.Message)), perNode);
            }
            catch (Exception)
            {
                message = RedactedMarker;
            }

            try
            {
                stack = string.IsNullOrEmpty(node.StackTrace) ? null : Bound(Pipeline(node.StackTrace), perNode);
            }
            catch (Exception)
            {
                stack = null;
            }

            inner = ExceptionInfo.Create(type, message, node.HResult, stack, inner);
        }

        return inner is null
            ? exception
            : ExceptionInfo.Create(
                inner.Type,
                inner.Message,
                inner.HResult,
                inner.StackTrace,
                inner.Inner,
                exceptionTruncated,
                originalCount,
                nodeCount);
    }

    public static string CompactSafeToken(string? value)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "unknown";
            }

            string trimmed = value.Trim();
            if (LooksUnsafe(trimmed))
            {
                return "unknown";
            }

            string compact = Compact(SingleLine(trimmed));
            if (compact.Length == 0)
            {
                return "unknown";
            }

            return TruncateChars(compact, 32);
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    public static string Truncate(string value, int maxBytes) => Bound(value, maxBytes);

    /// <summary>
    /// Машиночитаемое значение с сохранёнными разделителями: версия <c>10.0.26200</c>,
    /// культура <c>ru-RU</c> и тег <c>script:имя</c> остаются разбираемыми.
    /// Значения, похожие на секрет, URL или путь, не проходят дословно: после
    /// <see cref="LooksUnsafe"/> дополнительно отсекается всё, что содержит разделитель
    /// присваивания <c>=</c>, поскольку подписанная query-строка вида
    /// <c>Expires=1893456000&amp;Policy=…</c> иначе сохранила бы секрет как есть.
    /// Поведение совпадает с <see cref="CompactSafeToken"/> по приватности, но сохраняет
    /// разделители там, где это безопасно.
    /// </summary>
    public static string ReadableMachineValue(string? value)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "unknown";
            }

            string trimmed = SingleLine(value.Trim());
            if (LooksUnsafe(trimmed) || CarriesAssignment(trimmed))
            {
                return "unknown";
            }

            StringBuilder builder = new(trimmed.Length);
            char? previous = null;
            foreach (char raw in trimmed)
            {
                bool isSpace = raw is ' ' or '\t';
                if (isSpace || IsIdentifierChar(raw))
                {
                    if (char.IsLetterOrDigit(raw))
                    {
                        builder.Append(char.ToLowerInvariant(raw));
                    }
                    else if (previous != raw)
                    {
                        builder.Append(raw);
                    }

                    previous = raw;
                }
            }

            string compact = builder.ToString();
            if (compact.Length == 0)
            {
                return "unknown";
            }

            return TruncateChars(compact, 64);
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    /// <summary>
    /// Признак «значение выглядит как пара ключ-значение». Отсекает подписанные
    /// query-параметры и любые присваивания, которые <see cref="SensitiveAssignmentRegex"/>
    /// не перечисляет (<c>Expires</c>, <c>Policy</c>, <c>AWSAccessKeyId</c> и т. п.).
    /// </summary>
    private static bool CarriesAssignment(string value)
    {
        for (int index = 0; index < value.Length; index++)
        {
            char raw = value[index];
            if (raw != '=' && raw != '?' && raw != '&')
            {
                continue;
            }

            if (raw == '=')
            {
                int left = index - 1;
                return left >= 0 && char.IsLetterOrDigit(value[left]);
            }

            return index + 1 < value.Length && value[index + 1] == '=';
        }

        return false;
    }

    /// <summary>
    /// Обрезка по числу символов без разрыва суррогатной пары: срез по <c>char</c>
    /// посередине пары даёт невалидный UTF-16 и ломает последующую сериализацию.
    /// </summary>
    public static string TruncateChars(string value, int maxChars)
    {
        if (maxChars <= 0)
        {
            return string.Empty;
        }

        if (value.Length <= maxChars)
        {
            return value;
        }

        int cut = maxChars;
        if (char.IsHighSurrogate(value[cut - 1]) && cut < value.Length && char.IsLowSurrogate(value[cut]))
        {
            cut--;
        }

        return value[..cut];
    }

    public static string SingleLine(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        bool needsWork = false;
        foreach (char raw in value)
        {
            if (raw is '\r' or '\n' or '\t')
            {
                needsWork = true;
                break;
            }
        }

        if (!needsWork)
        {
            return value;
        }

        StringBuilder builder = new(value.Length);
        foreach (char raw in value)
        {
            builder.Append(raw is '\r' or '\n' or '\t' ? ' ' : raw);
        }

        return builder.ToString();
    }

    public static int EstimateSize(object? value)
    {
        return value switch
        {
            null => 4,
            bool => 5,
            int or long or short or byte => 20,
            double or float or decimal => 24,
            string text => Encoding.UTF8.GetByteCount(text) + 2,
            _ => 64
        };
    }

    public static string Compact(string value)
    {
        StringBuilder builder = new(value.Length);
        foreach (char raw in value)
        {
            if (char.IsLetterOrDigit(raw))
            {
                builder.Append(char.ToLowerInvariant(raw));
            }
        }

        return builder.ToString();
    }

    private static bool IsIdentifierChar(char raw) =>
        char.IsLetterOrDigit(raw) || raw is '_' or '-' or '.' or ':';

    private object? RedactPropertyValue(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case bool:
            case int:
            case long:
            case short:
            case byte:
            case uint:
            case ulong:
            case ushort:
            case sbyte:
            case double:
            case float:
            case decimal:
                return value;
            case string text:
                return RedactToken(text);
            case DateTime timestamp:
                return timestamp.ToString("O", CultureInfo.InvariantCulture);
            case DateTimeOffset timestamp:
                return timestamp.ToString("O", CultureInfo.InvariantCulture);
            case Guid guid:
                return guid.ToString("N", CultureInfo.InvariantCulture);
            case TimeSpan span:
                return ((long)span.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
            default:
                try
                {
                    return RedactDetailToken(value.ToString());
                }
                catch (Exception)
                {
                    return RedactedMarker;
                }
        }
    }

    private string Pipeline(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (!CanMatchRedactionPatterns(value))
        {
            return RemoveControlCharacters(value);
        }

        string result = value;
        try
        {
            result = SensitiveHeaderRegex.Replace(result, m => m.Groups["key"].Value + m.Groups["sep"].Value + RedactedMarker);
            result = SensitiveAssignmentRegex.Replace(result, m => m.Groups["key"].Value + m.Groups["sep"].Value + RedactedMarker);
            result = UrlRegex.Replace(result, RedactUrlMatch);
            result = ExtendedPathRegex.Replace(result, RedactExtendedPathMatch);
            result = DrivePathRegex.Replace(result, m => FileNameOnly(m.Groups["path"].Value));
            result = DriveRelativePathRegex.Replace(result, m => DriveRelativeFileNameOnly(m.Groups["path"].Value));
            result = UncPathRegex.Replace(result, m => FileNameOnly(m.Groups["path"].Value));
        }
        catch (Exception)
        {
            return RedactedMarker;
        }

        return RemoveControlCharacters(result);
    }

    /// <summary>
    /// Дешёвая проверка того, может ли значение вообще содержать совпадение для
    /// любого из семи шаблонов <see cref="Pipeline"/>. Ни один шаблон не совпадёт
    /// без двоеточия, знака равенства или пары обратных косых черты:
    /// <c>SensitiveHeaderRegex</c> и <c>DrivePathRegex</c> и <c>DriveRelativePathRegex</c>
    /// требуют литерального <c>:</c>, <c>SensitiveAssignmentRegex</c> — <c>:</c> или <c>=</c>,
    /// <c>UrlRegex</c> — подстроку <c>://</c>, а <c>ExtendedPathRegex</c> и <c>UncPathRegex</c> —
    /// начало <c>\\</c>. Ответ <c>false</c> поэтому означает, что все семь замен
    /// являются тождественными, и pipeline может вернуть строку без изменений.
    /// Проверка допускает ложное срабатывание, но не ложный пропуск.
    /// </summary>
    private static bool CanMatchRedactionPatterns(string value)
    {
        for (int index = 0; index < value.Length; index++)
        {
            char raw = value[index];
            if (raw is ':' or '=')
            {
                return true;
            }

            if (raw == '\\' && index + 1 < value.Length && value[index + 1] == '\\')
            {
                return true;
            }
        }

        return false;
    }

    private static string RedactExtendedPathMatch(Match match)
    {
        string prefix = match.Groups["prefix"].Value;
        string body = match.Groups["body"].Value;
        if (prefix.Contains('.', StringComparison.Ordinal))
        {
            return RedactedMarker;
        }

        if (body.StartsWith("UNC", StringComparison.OrdinalIgnoreCase))
        {
            return FileNameOnly(body[3..]);
        }

        return FileNameOnly(body);
    }

    private static string RedactUrlMatch(Match match)
    {
        string scheme = match.Groups["scheme"].Value;
        string body = match.Groups["body"].Value;

        if (string.Equals(scheme, "file", StringComparison.OrdinalIgnoreCase))
        {
            return scheme + "://" + FileNameOnly(body);
        }

        int cut = body.Length;
        bool hasQuery = false;

        int fragment = body.IndexOf('#', StringComparison.Ordinal);
        if (fragment >= 0)
        {
            cut = fragment;
        }

        int query = body.IndexOf('?', StringComparison.Ordinal);
        if (query >= 0 && query < cut)
        {
            cut = query;
            hasQuery = true;
        }

        string head = body[..cut];
        string suffix = hasQuery ? "?" + RedactedMarker : string.Empty;

        int at = head.LastIndexOf('@');
        if (at >= 0)
        {
            head = RedactedMarker + head[at..];
        }

        int slash = head.IndexOf('/');
        if (slash < 0)
        {
            return scheme + "://" + head + suffix;
        }

        string host = head[..slash];
        string path = head[slash..];
        return scheme + "://" + host + "/" + FileNameOnly(path) + suffix;
    }

    private static string FileNameOnly(string path)
    {
        string trimmed = path.TrimEnd('\\', '/');
        int index = trimmed.AsSpan().LastIndexOfAny(PathSeparators);
        string name = index >= 0 ? trimmed[(index + 1)..] : trimmed;
        if (string.IsNullOrWhiteSpace(name) || name.AsSpan().IndexOfAny(QueryFragmentMarkers) >= 0)
        {
            return RedactedMarker;
        }

        return name;
    }

    private static string DriveRelativeFileNameOnly(string path)
    {
        if (path.Length <= 2)
        {
            return RedactedMarker;
        }

        return FileNameOnly(path[2..]);
    }

    private static string RemoveControlCharacters(string value)
    {
        StringBuilder? builder = null;
        for (int index = 0; index < value.Length; index++)
        {
            char raw = value[index];
            if (raw is '\r' or '\n' or '\t' || !char.IsControl(raw))
            {
                builder?.Append(raw);
                continue;
            }

            if (builder is null)
            {
                builder = new StringBuilder(value.Length);
                builder.Append(value, 0, index);
            }

            builder.Append(' ');
        }

        return builder?.ToString() ?? value;
    }

    private static string Bound(string value, int maxBytes)
    {
        if (maxBytes <= 0)
        {
            return string.Empty;
        }

        if (value.Length == 0)
        {
            return value;
        }

        if (Encoding.UTF8.GetByteCount(value) <= maxBytes)
        {
            return value;
        }

        int markerBytes = Encoding.UTF8.GetByteCount(TruncationMarker);
        int budget = maxBytes - markerBytes;
        if (budget <= 0)
        {
            return TruncationMarker[..Math.Min(TruncationMarker.Length, maxBytes)];
        }

        int low = 0;
        int high = Math.Min(value.Length, budget);
        while (low < high)
        {
            int middle = low + ((high - low + 1) / 2);
            if (Encoding.UTF8.GetByteCount(value.AsSpan(0, middle)) <= budget)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        int cut = low;
        if (cut > 0 && cut < value.Length && char.IsHighSurrogate(value[cut - 1]))
        {
            cut--;
        }

        return string.Concat(value.AsSpan(0, cut), TruncationMarker);
    }
}

public static class LogEventMarkerNames
{
    public const string UnlistedSnapshot = "UnlistedSnapshot";
    public const string MarkedProperties = "MarkedProperties";
    public const string DroppedMarker = "log.queue.dropped";
    public const string RejectedEvent = "log.event.rejected";
    public const string WriteFailure = "log.file.write_failed";
    public const string RotationFailure = "log.file.rotation_failed";
    public const string PersistedLogsCleared = "log.history.cleared";
    public const string PersistedLogsClearFailed = "log.history.clear_failed";
    public const string LogSource = "LogService";
    public const string ExceptionTruncated = "exceptionTruncated";
    public const string OriginalExceptionCount = "originalExceptionCount";
    public const string KeptExceptionCount = "keptExceptionCount";
}
