// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

using KTools_App.Core;

namespace KTools_App.Diagnostics;

/// <summary>
/// Читатель файлов журнала (ишью 140): выделен из LogService God-object.
/// Владеет только чтением хвостов, разбором строк и рендерингом;
/// запись, очередь и жизненный цикл остаются в LogService.
/// </summary>
internal sealed class LogFileReader
{
    private const int ReadChunkBytes = 64 * 1024;

    private readonly LogServiceOptions _options;
    private readonly LogFilePolicy _policy;
    private readonly LogRedactor _redactor;
    private readonly int _maxLineBytes;
    private readonly Func<(string Directory, string BaseName)> _getPaths;
    private readonly Func<LogEvent, LogEvent> _prepare;
    private readonly Func<LogEvent, string?> _validate;
    private readonly Func<LogEvent, string, LogEvent> _createRejected;
    private readonly Action _incrementReadError;

    public LogFileReader(
        LogServiceOptions options,
        LogFilePolicy policy,
        LogRedactor redactor,
        int maxLineBytes,
        Func<(string Directory, string BaseName)> getPaths,
        Func<LogEvent, LogEvent> prepare,
        Func<LogEvent, string?> validate,
        Func<LogEvent, string, LogEvent> createRejected,
        Action incrementReadError)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
        _maxLineBytes = maxLineBytes;
        _getPaths = getPaths ?? throw new ArgumentNullException(nameof(getPaths));
        _prepare = prepare ?? throw new ArgumentNullException(nameof(prepare));
        _validate = validate ?? throw new ArgumentNullException(nameof(validate));
        _createRejected = createRejected ?? throw new ArgumentNullException(nameof(createRejected));
        _incrementReadError = incrementReadError ?? throw new ArgumentNullException(nameof(incrementReadError));
    }

    public string ReadCurrentLog()
    {
        try
        {
            if (!TryGetSessionFiles(out IReadOnlyList<string> files))
            {
                return string.Empty;
            }

            List<string> rawLines = new();
            int budget = _options.MaxRecentReadBytes;
            for (int index = files.Count - 1; index >= 0; index--)
            {
                if (!TryReadTailLines(files[index], _options.MaxExportLines, budget, rawLines, static _ => true, out int consumed))
                {
                    return string.Empty;
                }

                budget -= consumed;
                if (rawLines.Count >= _options.MaxExportLines || budget <= 0)
                {
                    break;
                }
            }

            List<string> rendered = new(rawLines.Count);
            for (int index = rawLines.Count - 1; index >= 0; index--)
            {
                rendered.Add(RenderLine(rawLines[index]));
            }

            return string.Join(Environment.NewLine, rendered);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    public IReadOnlyList<LogEvent> ReadRecentEvents(int maxCount)
    {
        List<LogEvent> result = new();
        try
        {
            if (maxCount <= 0)
            {
                return result;
            }

            if (!TryGetSessionFiles(out IReadOnlyList<string> files))
            {
                return result;
            }

            List<string> rawLines = new();
            int budget = _options.MaxRecentReadBytes;
            for (int index = files.Count - 1; index >= 0; index--)
            {
                if (!TryReadTailLines(files[index], maxCount, budget, rawLines, IsValidLine, out int consumed))
                {
                    return new List<LogEvent>();
                }

                budget -= consumed;
                if (rawLines.Count >= maxCount || budget <= 0)
                {
                    break;
                }
            }

            for (int index = rawLines.Count - 1; index >= 0; index--)
            {
                LogEvent? parsed = LogService.LogEventJson.TryParse(rawLines[index], _validate);
                if (parsed is not null)
                {
                    result.Add(_prepare(parsed));
                }
            }

            return result;
        }
        catch (Exception)
        {
            return new List<LogEvent>();
        }
    }

    private bool TryGetSessionFiles(out IReadOnlyList<string> files)
    {
        files = Array.Empty<string>();
        (string directory, string baseName) = _getPaths();

        if (directory.Length == 0)
        {
            return false;
        }

        IReadOnlyList<string> found = _policy.EnumerateSessionFiles(directory, baseName, out bool completed);
        files = found;
        if (!completed)
        {
            _incrementReadError();
        }

        return completed;
    }

    private string RenderLine(string rawLine)
    {
        LogEvent? parsed = LogService.LogEventJson.TryParse(rawLine);
        if (parsed is not null)
        {
            string? error = _validate(parsed);
            return LogEvent.Format(
                _prepare(error is null ? parsed : _createRejected(parsed, error)),
                true);
        }

        try
        {
            return LogRedactor.Truncate(_redactor.RedactToken(rawLine), _options.MaxMessageLength);
        }
        catch (Exception)
        {
            return LogRedactor.RedactedMarker;
        }
    }

    private bool IsValidLine(string line) =>
        LogService.LogEventJson.TryParse(line, _validate) is not null;

    private bool TryReadTailLines(
        string path,
        int maxLines,
        int byteBudget,
        List<string> sink,
        Func<string, bool> accept,
        out int consumed)
    {
        consumed = 0;
        if (maxLines <= 0 || byteBudget <= 0)
        {
            return true;
        }

        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long position = stream.Length;
            byte[] chunk = new byte[ReadChunkBytes];
            List<byte> reversed = new(1024);
            int limit = Math.Max(_maxLineBytes, ReadChunkBytes);

            while (position > 0 && consumed < byteBudget && sink.Count < maxLines)
            {
                int remaining = byteBudget - consumed;
                int want = (int)Math.Min(Math.Min(chunk.Length, position), remaining);
                position -= want;
                stream.Seek(position, SeekOrigin.Begin);
                int read = FillBuffer(stream, chunk, want);
                consumed += read;

                for (int index = read - 1; index >= 0; index--)
                {
                    byte current = chunk[index];
                    if (current == (byte)'\n')
                    {
                        if (AppendLine(reversed, sink, maxLines, accept))
                        {
                            break;
                        }
                    }
                    else if (current != (byte)'\r')
                    {
                        reversed.Add(current);
                        if (reversed.Count > limit)
                        {
                            reversed.Clear();
                        }
                    }
                }
            }

            if (sink.Count < maxLines && position == 0 && reversed.Count > 0)
            {
                AppendLine(reversed, sink, maxLines, accept);
            }
        }
        catch (Exception)
        {
            _incrementReadError();
            return false;
        }

        return true;
    }

    private static int FillBuffer(FileStream stream, byte[] buffer, int count)
    {
        int total = 0;
        while (total < count)
        {
            int read = stream.Read(buffer, total, count - total);
            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static bool AppendLine(List<byte> reversed, List<string> sink, int maxLines, Func<string, bool> accept)
    {
        if (reversed.Count == 0)
        {
            return false;
        }

        byte[] bytes = new byte[reversed.Count];
        for (int index = 0; index < reversed.Count; index++)
        {
            bytes[index] = reversed[reversed.Count - 1 - index];
        }

        reversed.Clear();
        string line = Encoding.UTF8.GetString(bytes);
        if (accept(line))
        {
            sink.Add(line);
        }

        return sink.Count >= maxLines;
    }
}
