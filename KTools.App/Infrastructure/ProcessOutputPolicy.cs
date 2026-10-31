// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

using KTools_App.Diagnostics;

namespace KTools_App.Infrastructure;

public static class ProcessOutputPolicy
{
    public const int DefaultMaxTailLines = 64;
    public const int DefaultMaxTailBytes = 8 * 1024;
    public const int MaxSingleLineLength = 2048;
    public const int DefaultSampleRateLimit = 6;
    public const int DefaultSampleWindowMilliseconds = 1000;
    public const int MaxSingleLineBytes = 2048;
    public const int MaxRawLineLength = 1024 * 1024;

    private static readonly UTF8Encoding Utf8NoBomReplacement = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    public static Encoding ResolveEncoding() => Utf8NoBomReplacement;

    /// <summary>
    /// Отсеивает только настоящие баннеры ffmpeg. Правило намеренно узкое и
    /// позиционное: префиксы вида <c>Input #0</c>, <c>Output #0</c>, <c>Metadata:</c>,
    /// <c>encoder:</c>, <c>Stream #0:</c>, <c>libav*</c> вырезали реальные диагностические
    /// строки, в том числе <c>Input #0, mov,mp4…: Invalid data found when processing input</c>
    /// — самую частую причину отказа ffmpeg. Баннерных префиксов у сторонних утилит
    /// (mkvmerge, eac3to, qaac) нет, поэтому их вывод фильтр не затрагивает.
    /// </summary>
    public static bool IsBuildBannerLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        ReadOnlySpan<char> value = line.AsSpan().TrimStart();
        return StartsWithToken(value, "ffmpeg version")
            || StartsWithToken(value, "configuration:")
            || StartsWithToken(value, "built with")
            || StartsWithToken(value, "Stream mapping:")
            || StartsWithToken(value, "Press [q]");
    }

    /// <summary>
    /// Префиксное сравнение с обязательным разделителем после префикса: строка
    /// <c>libavutil …</c> больше не отсеивается префиксом <c>libav</c>, а
    /// <c>configuration:…</c> распознаётся как баннер.
    /// </summary>
    private static bool StartsWithToken(ReadOnlySpan<char> value, string prefix)
    {
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (value.Length == prefix.Length)
        {
            return true;
        }

        char next = value[prefix.Length];
        return char.IsWhiteSpace(next) || next == ':';
    }

    public static bool IsWarningLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        string value = line.TrimStart();
        return value.StartsWith("warning", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("warn:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("[warning]", StringComparison.OrdinalIgnoreCase)
            || value.Contains("warning:", StringComparison.OrdinalIgnoreCase)
            || value.Contains(" предупреждение", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsErrorLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        string value = line.TrimStart();
        return value.StartsWith("error", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("erro:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("fatal", StringComparison.OrdinalIgnoreCase)
            || value.Contains("error:", StringComparison.OrdinalIgnoreCase);
    }

    public static int CountWarnings(IEnumerable<string>? lines)
    {
        if (lines is null)
        {
            return 0;
        }

        int count = 0;
        try
        {
            foreach (string line in lines)
            {
                if (IsWarningLine(line))
                {
                    count++;
                }
            }
        }
        catch (Exception)
        {
        }

        return count;
    }

    public static int CountErrors(IEnumerable<string>? lines)
    {
        if (lines is null)
        {
            return 0;
        }

        int count = 0;
        try
        {
            foreach (string line in lines)
            {
                if (IsErrorLine(line))
                {
                    count++;
                }
            }
        }
        catch (Exception)
        {
        }

        return count;
    }

    public static string NormalizeLine(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        string value = LogRedactor.SingleLine(raw).Trim();
        if (ContainsRepeatedWhitespace(value))
        {
            value = CollapseWhitespace(value);
        }

        if (value.Length <= MaxSingleLineLength)
        {
            return value;
        }

        return LogRedactor.Truncate(value, MaxSingleLineBytes);
    }

    private static bool ContainsRepeatedWhitespace(string value)
    {
        for (int index = 1; index < value.Length; index++)
        {
            if (char.IsWhiteSpace(value[index]) && char.IsWhiteSpace(value[index - 1]))
            {
                return true;
            }
        }

        return false;
    }

    private static string CollapseWhitespace(string value)
    {
        System.Text.StringBuilder builder = new(value.Length);
        bool previousWasSpace = false;
        foreach (char raw in value)
        {
            if (char.IsWhiteSpace(raw))
            {
                if (previousWasSpace)
                {
                    continue;
                }

                previousWasSpace = true;
                builder.Append(' ');
                continue;
            }

            previousWasSpace = false;
            builder.Append(raw);
        }

        return builder.ToString().Trim();
    }
}

public sealed class ProcessOutputBuffer
{
    private readonly int _maxLines;
    private readonly int _maxBytes;
    private readonly Queue<string> _lines = new();
    private readonly object _gate = new();
    private readonly LogRedactor _redactor = new();

    private int _droppedLines;
    private long _droppedBytes;
    private long _totalBytes;
    private int _retainedLines;
    private int _retainedBytes;
    private int _filteredLines;
    private long _filteredBytes;

    public ProcessOutputBuffer(int? maxLines = null, int? maxBytes = null)
    {
        _maxLines = Math.Clamp(maxLines ?? ProcessOutputPolicy.DefaultMaxTailLines, 1, 4096);
        _maxBytes = Math.Clamp(maxBytes ?? ProcessOutputPolicy.DefaultMaxTailBytes, 128, 1 << 20);
    }

    public string Stream { get; init; } = "stdout";

    public bool Truncated
    {
        get
        {
            lock (_gate)
            {
                return _droppedLines > 0 || _droppedBytes > 0;
            }
        }
    }

    /// <summary>
    /// Строки, отброшенные кольцевым буфером из-за лимита хвоста.
    /// Баннерные строки сюда не входят: у них отдельный счётчик <see cref="FilteredLines"/>,
    /// иначе отсечение баннеров выглядело бы как усечение диагностики.
    /// </summary>
    public int DroppedLines
    {
        get
        {
            lock (_gate)
            {
                return _droppedLines;
            }
        }
    }

    public long DroppedBytes
    {
        get
        {
            lock (_gate)
            {
                return _droppedBytes;
            }
        }
    }

    /// <summary>
    /// Строки, отсечённые фильтром баннеров ffmpeg. Счётчик видим в журнале, поэтому
    /// пустой <c>OutputTail</c> больше не выглядит как «процесс ничего не вывел».
    /// </summary>
    public int FilteredLines
    {
        get
        {
            lock (_gate)
            {
                return _filteredLines;
            }
        }
    }

    public long FilteredBytes
    {
        get
        {
            lock (_gate)
            {
                return _filteredBytes;
            }
        }
    }

    /// <summary>
    /// Суммарное число строк, не дошедших до хвоста: усечение кольцевого буфера плюс баннеры.
    /// Именно это значение попадает в свойство <c>DroppedLines</c> события журнала.
    /// </summary>
    public int TotalDroppedLines
    {
        get
        {
            lock (_gate)
            {
                return _droppedLines + _filteredLines;
            }
        }
    }

    public long TotalDroppedBytes
    {
        get
        {
            lock (_gate)
            {
                return _droppedBytes + _filteredBytes;
            }
        }
    }

    public long TotalBytes
    {
        get
        {
            lock (_gate)
            {
                return _totalBytes;
            }
        }
    }

    public int RetainedLines
    {
        get
        {
            lock (_gate)
            {
                return _retainedLines;
            }
        }
    }

    public int RetainedBytes
    {
        get
        {
            lock (_gate)
            {
                return _retainedBytes;
            }
        }
    }

    public void Append(string? rawLine)
    {
        if (rawLine is null)
        {
            return;
        }

        string line = ProcessOutputPolicy.NormalizeLine(rawLine);
        if (line.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            _totalBytes += line.Length + 1;
            _lines.Enqueue(line);

            while (_lines.Count > _maxLines)
            {
                string removed = _lines.Dequeue();
                _droppedLines++;
                _droppedBytes += removed.Length + 1;
            }

            int size = Measure(_lines);
            while (size > _maxBytes && _lines.Count > 1)
            {
                string removed = _lines.Dequeue();
                _droppedLines++;
                _droppedBytes += removed.Length + 1;
                size = Measure(_lines);
            }

            if (size > _maxBytes && _lines.Count == 1)
            {
                string only = _lines.Peek();
                string bounded = LogRedactor.Truncate(only, _maxBytes);
                if (!string.Equals(bounded, only, StringComparison.Ordinal))
                {
                    _droppedBytes += only.Length - bounded.Length;
                    _lines.Clear();
                    _lines.Enqueue(bounded);
                }

                size = Measure(_lines);
            }

            _retainedLines = _lines.Count;
            _retainedBytes = size;
        }
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate)
        {
            return new List<string>(_lines);
        }
    }

    public string BuildTail()
    {
        List<string> snapshot;
        lock (_gate)
        {
            if (_lines.Count == 0)
            {
                return string.Empty;
            }

            snapshot = new List<string>(_lines);
        }

        try
        {
            List<string> kept = new(snapshot.Count);
            int filtered = 0;
            long filteredBytes = 0;
            for (int i = 0; i < snapshot.Count; i++)
            {
                if (ProcessOutputPolicy.IsBuildBannerLine(snapshot[i]))
                {
                    filtered++;
                    filteredBytes += snapshot[i].Length + 1;
                    continue;
                }

                kept.Add(_redactor.RedactDetailToken(snapshot[i]));
            }

            lock (_gate)
            {
                _filteredLines += filtered;
                _filteredBytes += filteredBytes;
            }

            if (kept.Count == 0)
            {
                return string.Empty;
            }

            return LogRedactor.Truncate(string.Join(" | ", kept), _maxBytes);
        }
        catch (Exception)
        {
            return LogRedactor.RedactedMarker;
        }
    }

    /// <summary>
    /// Снимок счётчиков буфера для <see cref="ProcessResult.OutputMetrics"/>:
    /// без него потеря вывода процесса оставалась невидимой в журнале.
    /// </summary>
    public ProcessOutputMetrics Metrics() => new()
    {
        Stream = Stream,
        DroppedLines = TotalDroppedLines,
        DroppedBytes = TotalDroppedBytes,
        RetainedLines = RetainedLines,
        RetainedBytes = RetainedBytes,
        TotalBytes = TotalBytes
    };

    private static int Measure(Queue<string> lines)
    {
        int total = 0;
        foreach (string line in lines)
        {
            total += line.Length + 1;
        }

        return total;
    }
}

public sealed class ProcessOutputRateLimiter
{
    private readonly int _maxSamples;
    private readonly int _windowMilliseconds;
    private readonly object _gate = new();

    private long _suppressed;
    private int _windowStart;
    private int _accepted;

    public ProcessOutputRateLimiter(
        int? maxSamples = null,
        int? windowMilliseconds = null)
    {
        _maxSamples = Math.Clamp(maxSamples ?? ProcessOutputPolicy.DefaultSampleRateLimit, 1, 1024);
        _windowMilliseconds = Math.Clamp(windowMilliseconds ?? ProcessOutputPolicy.DefaultSampleWindowMilliseconds, 50, 60_000);
        _windowStart = Environment.TickCount;
    }

    public long SuppressedCount
    {
        get
        {
            lock (_gate)
            {
                return _suppressed;
            }
        }
    }

    public bool TryAcquire()
    {
        lock (_gate)
        {
            int now = Environment.TickCount;
            if (now - _windowStart >= _windowMilliseconds || now < _windowStart)
            {
                _windowStart = now;
                _accepted = 0;
            }

            if (_accepted >= _maxSamples)
            {
                _suppressed++;
                return false;
            }

            _accepted++;
            return true;
        }
    }
}
