// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

using KTools_App.Core;

namespace KTools_App.Diagnostics;

public sealed class EmergencyLogSink
{
    public const string FileNamePrefix = "emergency_";
    public const string FileNameExtension = ".json";
    public const string FileSearchPattern = FileNamePrefix + "*" + FileNameExtension;
    public const string EmergencyFolderName = "emergency";
    public const int DefaultReasonLimit = 1024;
    public const int DefaultStackLimit = 4096;
    public const int DefaultMaxInnerDepth = 3;
    public const int RememberedCrashIds = 16;

    private const string ApplicationName = "K-Tools";

    private readonly LogRedactor _redactor;
    private readonly int _maxFileBytes;
    private readonly int _maxFiles;
    private readonly int _retentionDays;
    private readonly int _reasonLimit;
    private readonly int _stackLimit;
    private readonly int _maxInnerDepth;
    private readonly string? _directory;
    private readonly HashSet<string> _seenCrashIds = new(StringComparer.Ordinal);
    private readonly Queue<string> _seenOrder = new();
    private readonly object _lock = new();

    private long _writtenCount;
    private long _droppedCount;

    public EmergencyLogSink(string? directory = null, LogServiceOptions? options = null)
    {
        LogServiceOptions effective = (options ?? new LogServiceOptions()).Clone().Normalize();
        _redactor = new LogRedactor(effective);
        _maxFileBytes = effective.MaxEmergencyFileBytes;
        _maxFiles = effective.MaxEmergencyFiles;
        _retentionDays = effective.EmergencyRetentionDays;
        _reasonLimit = effective.EmergencyReasonLength;
        _stackLimit = effective.EmergencyStackLength;
        _maxInnerDepth = effective.EmergencyMaxInnerDepth;
        _directory = string.IsNullOrWhiteSpace(directory) ? null : directory.Trim();
    }

    public static EmergencyLogSink Shared { get; } = new();

    public long WrittenCount => Interlocked.Read(ref _writtenCount);

    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    public string? TargetDirectory => _directory;

    public int MaxFileBytes => _maxFileBytes;

    public bool Write(
        string? reason,
        Exception? exception,
        string? source,
        string? crashId,
        Guid sessionId,
        LogLevel level = LogLevel.Error)
    {
        try
        {
            string safeCrashId = SafeCrashId(crashId);
            if (string.IsNullOrWhiteSpace(safeCrashId) || string.Equals(safeCrashId, LogRedactor.UnknownIdentifier, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _droppedCount);
                return false;
            }

            if (!TryRemember(safeCrashId))
            {
                Interlocked.Increment(ref _droppedCount);
                return false;
            }

            string? target = ResolveDirectory();
            if (target is null)
            {
                Interlocked.Increment(ref _droppedCount);
                return false;
            }

            string? payload = BuildPayload(reason, exception, source, safeCrashId, sessionId, level);
            if (payload is null || Encoding.UTF8.GetByteCount(payload) > _maxFileBytes)
            {
                Interlocked.Increment(ref _droppedCount);
                return false;
            }

            string fileName = string.Create(
                CultureInfo.InvariantCulture,
                $"{FileNamePrefix}{DateTime.UtcNow:yyyyMMdd'T'HHmmss.fffffff'Z'}_{Safe(safeCrashId, 32)}{FileNameExtension}");
            string path = Path.Combine(target, fileName);

            File.WriteAllText(path, payload, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Interlocked.Increment(ref _writtenCount);
            ApplyRetention(target);
            return true;
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _droppedCount);
            return false;
        }
    }

    public string? BuildPayload(
        string? reason,
        Exception? exception,
        string? source,
        string? crashId,
        Guid sessionId,
        LogLevel level)
    {
        try
        {
            string payload = RenderFull(reason, exception, source, crashId, sessionId, level);
            if (Encoding.UTF8.GetByteCount(payload) <= _maxFileBytes)
            {
                return payload;
            }
        }
        catch (Exception)
        {
        }

        try
        {
            return RenderMinimal(reason, source, crashId, sessionId, level);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public int ApplyRetention(string directory)
    {
        int deleted = 0;
        try
        {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return 0;
            }

            string[] paths = Directory.GetFiles(directory, FileSearchPattern);
            List<FileInfo> files = new(paths.Length);
            foreach (string path in paths)
            {
                try
                {
                    files.Add(new FileInfo(path));
                }
                catch (Exception)
                {
                }
            }

            files.Sort(static (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
            DateTime cutoff = DateTime.UtcNow.AddDays(-_retentionDays);
            int keepCount = Math.Min(files.Count, _maxFiles);

            int index = 0;
            while (index < files.Count)
            {
                bool tooOld = files[index].LastWriteTimeUtc < cutoff;
                bool beyondLimit = index < files.Count - keepCount;
                if (!tooOld && !beyondLimit)
                {
                    break;
                }

                if (!TryDelete(files[index]))
                {
                    index++;
                    continue;
                }

                files.RemoveAt(index);
                deleted++;
            }
        }
        catch (Exception)
        {
            return deleted;
        }

        return deleted;
    }

    private string RenderFull(
        string? reason,
        Exception? exception,
        string? source,
        string? crashId,
        Guid sessionId,
        LogLevel level)
    {
        System.Buffers.ArrayBufferWriter<byte> buffer = new(512);
        using (Utf8JsonWriter writer = CreateWriter(buffer))
        {
            string safeReason = Bounded(reason, _reasonLimit);
            bool truncated = IsBounded(reason, _reasonLimit) || HasTruncatedException(exception);
            writer.WriteStartObject();
            WriteEnvelopeHeader(writer, source, crashId, sessionId, level, truncated, null);
            writer.WriteString("reason", safeReason);
            if (exception is not null)
            {
                WriteException(writer, exception);
            }
            else
            {
                writer.WriteNull("exception");
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private string RenderMinimal(string? reason, string? source, string? crashId, Guid sessionId, LogLevel level)
    {
        int reasonBudget = Math.Max(32, Math.Min(_reasonLimit, _maxFileBytes / 3));
        for (int attempt = 0; attempt < 8; attempt++)
        {
            string payload = RenderMinimal(reason, source, crashId, sessionId, level, reasonBudget);
            if (Encoding.UTF8.GetByteCount(payload) <= _maxFileBytes)
            {
                return payload;
            }

            reasonBudget = Math.Max(16, reasonBudget / 2);
        }

        return RenderMinimal(reason, source, crashId, sessionId, level, 16);
    }

    private string RenderMinimal(
        string? reason,
        string? source,
        string? crashId,
        Guid sessionId,
        LogLevel level,
        int reasonBudget)
    {
        System.Buffers.ArrayBufferWriter<byte> buffer = new(256);
        using (Utf8JsonWriter writer = CreateWriter(buffer))
        {
            writer.WriteStartObject();
            WriteEnvelopeHeader(writer, source, crashId, sessionId, level, true, null);
            writer.WriteString("reason", Bounded(reason, reasonBudget));
            writer.WriteNull("exception");
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private void WriteEnvelopeHeader(
        Utf8JsonWriter writer,
        string? source,
        string? crashId,
        Guid sessionId,
        LogLevel level,
        bool truncated,
        DateTimeOffset? timestamp)
    {
        writer.WriteString("channel", "crash_emergency");
        writer.WriteString("app", ApplicationName);
        writer.WriteString("crashId", SafeCrashId(crashId));
        writer.WriteString(
            "timestampUtc",
            (timestamp ?? DateTimeOffset.UtcNow).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
        writer.WriteString("session", sessionId.ToString("D", CultureInfo.InvariantCulture));
        writer.WriteString("source", SafeSource(source));
        writer.WriteString("level", level.ToString());
        writer.WriteBoolean("truncated", truncated);
    }

    private void WriteException(Utf8JsonWriter writer, Exception exception)
    {
        writer.WritePropertyName("exception");
        writer.WriteStartObject();
        writer.WriteString("type", _redactor.SanitizeIdentifier(exception.GetType().FullName ?? exception.GetType().Name));
        writer.WriteString("message", Bounded(exception.Message, Math.Max(64, _reasonLimit / 2)));
        writer.WriteNumber("hresult", exception.HResult);
        writer.WriteString("stack", Bounded(exception.StackTrace ?? string.Empty, _stackLimit));

        writer.WritePropertyName("inner");
        writer.WriteStartArray();
        int depth = 0;
        Exception? current = SafeInner(exception);
        while (current is not null && depth < _maxInnerDepth)
        {
            writer.WriteStartObject();
            writer.WriteString("type", _redactor.SanitizeIdentifier(current.GetType().FullName ?? current.GetType().Name));
            writer.WriteString("message", Bounded(current.Message, Math.Max(32, _reasonLimit / 4)));
            writer.WriteEndObject();
            current = SafeInner(current);
            depth++;
        }

        writer.WriteEndArray();
        writer.WriteBoolean("innerTruncated", current is not null);
        writer.WriteEndObject();
    }

    private static Utf8JsonWriter CreateWriter(System.Buffers.ArrayBufferWriter<byte> buffer) =>
        new(buffer, new JsonWriterOptions
        {
            Indented = false,
            SkipValidation = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });

    private string Bounded(string? value, int maxBytes)
    {
        try
        {
            return LogRedactor.Truncate(_redactor.RedactToken(value), Math.Max(16, maxBytes));
        }
        catch (Exception)
        {
            return LogRedactor.RedactedMarker;
        }
    }

    private bool IsBounded(string? value, int maxBytes)
    {
        try
        {
            return Encoding.UTF8.GetByteCount(_redactor.RedactToken(value)) > maxBytes;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private bool HasTruncatedException(Exception? exception)
    {
        if (exception is null)
        {
            return false;
        }

        try
        {
            if (Encoding.UTF8.GetByteCount(exception.Message) > Math.Max(64, _reasonLimit / 2)
                || Encoding.UTF8.GetByteCount(exception.StackTrace ?? string.Empty) > _stackLimit)
            {
                return true;
            }

            int depth = 0;
            Exception? current = SafeInner(exception);
            while (current is not null)
            {
                if (depth >= _maxInnerDepth)
                {
                    return true;
                }

                if (Encoding.UTF8.GetByteCount(current.Message) > Math.Max(32, _reasonLimit / 4))
                {
                    return true;
                }

                current = SafeInner(current);
                depth++;
            }

            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private string SafeCrashId(string? value)
    {
        try
        {
            string safe = _redactor.SanitizeIdentifier(value);
            return string.Equals(safe, LogRedactor.UnknownIdentifier, StringComparison.Ordinal)
                ? LogRedactor.UnknownIdentifier
                : LogRedactor.Truncate(safe, 64);
        }
        catch (Exception)
        {
            return LogRedactor.UnknownIdentifier;
        }
    }

    private string SafeSource(string? value)
    {
        try
        {
            string safe = _redactor.SanitizeIdentifier(value);
            return LogRedactor.Truncate(safe, 128);
        }
        catch (Exception)
        {
            return LogRedactor.UnknownIdentifier;
        }
    }

    private static Exception? SafeInner(Exception exception)
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

    private string? ResolveDirectory()
    {
        if (_directory is not null)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                return _directory;
            }
            catch (Exception)
            {
                return null;
            }
        }

        try
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(localAppData))
            {
                localAppData = Path.GetTempPath();
            }

            string baseDirectory = Path.Combine(
                localAppData,
                LogFilePolicy.ApplicationFolderName,
                LogFilePolicy.LogsFolderName,
                EmergencyFolderName);
            Directory.CreateDirectory(baseDirectory);
            return baseDirectory;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private bool TryRemember(string crashId)
    {
        lock (_lock)
        {
            if (!_seenCrashIds.Add(crashId))
            {
                return false;
            }

            _seenOrder.Enqueue(crashId);
            while (_seenOrder.Count > RememberedCrashIds)
            {
                _seenCrashIds.Remove(_seenOrder.Dequeue());
            }

            return true;
        }
    }

    private static bool TryDelete(FileInfo file)
    {
        try
        {
            if (file.Exists)
            {
                file.Delete();
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string Safe(string value, int length)
    {
        char[] buffer = new char[length];
        int count = 0;
        foreach (char raw in value)
        {
            if (count == length)
            {
                break;
            }

            buffer[count++] = char.IsLetterOrDigit(raw) ? raw : '-';
        }

        return new string(buffer, 0, count);
    }
}
