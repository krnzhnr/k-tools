// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KTools_App.Tests.Integration;

/// <summary>
/// Хост для совестимостных тестов на реальном FFmpeg: поиск проверенного бинарника,
/// запуск с таймаутом, генерация эталонного источника, разбор характеристик через ffprobe.
/// Тесты, использующие хост, помечаются категорией RequiresFfmpeg и завершаются
/// Inconclusive (а не падением), если рабочий FFmpeg на машине отсутствует —
/// поэтому набор безопасен для CI без предустановленных зависимостей.
/// Все комментарии написаны на русском языке.
/// </summary>
internal static class FfmpegTestHost
{
    /// <summary>
    /// Результат запуска процесса FFmpeg.
    /// </summary>
    internal sealed record RunResult(int ExitCode, bool TimedOut, string ErrorTail);

    /// <summary>
    /// Ищет рабочий бинарник FFmpeg: проверяет каждого кандидата реальным запуском -version.
    /// Порядок: PATH, пользовательская папка KTools, папка bin репозитория.
    /// </summary>
    internal static string? ResolveFfmpeg()
    {
        var candidates = new List<string>();

        foreach (var fromPath in LocateOnPath("ffmpeg.exe"))
        {
            candidates.Add(fromPath);
        }

        string localAppData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KTools", "bin", "ffmpeg", "kt-ffmpeg.exe");
        candidates.Add(localAppData);

        string? repoBin = LocateRepoBin();
        if (repoBin != null)
        {
            candidates.Add(Path.Combine(repoBin, "ffmpeg", "kt-ffmpeg.exe"));
        }

        foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (IsWorkingFfmpeg(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Ищет ffprobe рядом с выбранным ffmpeg, иначе в PATH. Возвращает null, если не найден.
    /// </summary>
    internal static string? ResolveFfprobe(string ffmpegPath)
    {
        string? sibling = Path.Combine(
            Path.GetDirectoryName(ffmpegPath) ?? string.Empty,
            Path.GetFileName(ffmpegPath).Contains("kt-ffmpeg") ? "kt-ffprobe.exe" : "ffprobe.exe");
        if (File.Exists(sibling) && IsWorkingFfmpeg(sibling))
        {
            return sibling;
        }

        foreach (var fromPath in LocateOnPath("ffprobe.exe"))
        {
            if (IsWorkingFfmpeg(fromPath))
            {
                return fromPath;
            }
        }

        return null;
    }

    /// <summary>
    /// Запускает FFmpeg с аргументами и ждёт завершения не дольше таймаута.
    /// Оба потока вывода дренируются асинхронно, чтобы не упереться в переполнение
    /// буфера (классический дедлок ReadToEnd + WaitForExit). Ввод отключён (-nostdin),
    /// кроме вызовов ffprobe, у которого такого флага нет.
    /// </summary>
    internal static RunResult Run(string ffmpegPath, string arguments, int timeoutMs, bool disableStdin = true)
    {
        string effectiveArgs = disableStdin && !IsFfprobeBinary(ffmpegPath)
            ? "-nostdin " + arguments
            : arguments;
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = effectiveArgs,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        var stdout = new System.Text.StringBuilder();
        var stderr = new System.Text.StringBuilder();
        using var stdoutDone = new System.Threading.ManualResetEventSlim(false);
        using var stderrDone = new System.Threading.ManualResetEventSlim(false);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) { stdoutDone.Set(); return; }
            lock (stdout) { stdout.AppendLine(e.Data); }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) { stderrDone.Set(); return; }
            lock (stderr) { stderr.AppendLine(e.Data); }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        bool exited = process.WaitForExit(timeoutMs);
        if (!exited)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* процесс уже завершается */ }
            process.WaitForExit(5000);
        }
        stdoutDone.Wait(5000);
        stderrDone.Wait(5000);

        string err = stderr.ToString();
        string tail = err.Length > 3000 ? err.Substring(err.Length - 3000) : err;
        return new RunResult(process.ExitCode, !exited, tail);
    }

    /// <summary>
    /// Проверяет, что энкодер перечислен в сборке FFmpeg (ffmpeg -h encoder=...).
    /// </summary>
    internal static bool IsEncoderListed(string ffmpegPath, string encoderName)
    {
        var result = Run(ffmpegPath, $"-hide_banner -h encoder={encoderName}", 30000);
        return !result.TimedOut && result.ExitCode == 0;
    }

    /// <summary>
    /// Определяет по хвосту stderr, что неуспех NVENC вызван отсутствием/недоступностью GPU,
    /// а не нашими аргументами. Список намеренно узкий: ресурсные и инициализационные ошибки
    /// при параллельной нагрузке сюда не входят и честно роняют тест.
    /// </summary>
    internal static bool LooksLikeMissingGpu(string errorTail)
    {
        string[] markers =
        {
            "No NVENC", "no NVENC", "Cannot load", "Driver does not",
            "no capable", "No capable", "FunctionNotFound", "GetNumberAdapters", "not supported"
        };
        return markers.Any(m => errorTail.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Разбирает характеристики первой видеодорожки через ffprobe в словарь (codec_name, pix_fmt, ...).
    /// </summary>
    internal static Dictionary<string, string> ProbeVideoStream(string? ffprobePath, string ffmpegPath, string filePath)
    {
        // Фолбэк: сам ffmpeg умеет зондировать через -i (информация уходит в stderr).
        if (ffprobePath == null)
        {
            var dump = Run(ffmpegPath, $"-hide_banner -i \"{filePath}\"", 30000);
            return ParseFfmpegInputDump(dump.ErrorTail);
        }

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = ffprobePath,
            Arguments = $"-v error -select_streams v:0 -show_entries stream=codec_name,pix_fmt,width,height -of default=noprint_wrappers=1 \"{filePath}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        process.Start();
        string output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit(30000);

        var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = line.IndexOf('=');
            if (eq > 0)
            {
                parsed[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
        }
        return parsed;
    }

    private static Dictionary<string, string> ParseFfmpegInputDump(string dump)
    {
        // Строка вида: Stream #0:0: Video: h264 (High) (avc1 / ...), yuv420p(progressive), 320x240 ...
        // Суффиксы в скобках (progressive, tv, bt709...) отрезаются — нужен только базовый токен.
        var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in dump.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = line.Trim();
            if (!trimmed.StartsWith("Stream #", StringComparison.Ordinal) || !trimmed.Contains("Video:"))
            {
                continue;
            }
            string afterVideo = trimmed.Substring(trimmed.IndexOf("Video:", StringComparison.Ordinal) + "Video:".Length).Trim();
            string[] parts = afterVideo.Split(',');
            if (parts.Length > 0)
            {
                parsed["codec_name"] = LeadingToken(parts[0]);
            }
            if (parts.Length > 1)
            {
                parsed["pix_fmt"] = LeadingToken(parts[1]);
            }
            break;
        }
        return parsed;
    }

    private static string LeadingToken(string fragment)
    {
        var match = System.Text.RegularExpressions.Regex.Match(fragment.Trim(), @"^[A-Za-z0-9_]+");
        return match.Success ? match.Value : fragment.Trim();
    }

    private static bool IsFfprobeBinary(string path)
    {
        return Path.GetFileName(path).Contains("ffprobe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWorkingFfmpeg(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate))
        {
            return false;
        }
        try
        {
            var result = Run(candidate, "-version", 30000);
            return !result.TimedOut && result.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<string> LocateOnPath(string fileName)
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
        {
            yield break;
        }
        foreach (string dir in pathEnv.Split(Path.PathSeparator))
        {
            string candidate;
            try
            {
                candidate = Path.Combine(dir.Trim().Trim('"'), fileName);
            }
            catch
            {
                continue;
            }
            if (File.Exists(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static string? LocateRepoBin()
    {
        // От каталога тестового рантайма идём вверх до KTools.sln — там рядом папка bin.
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 12 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir, "KTools.sln")))
            {
                string bin = Path.Combine(dir, "bin");
                return Directory.Exists(bin) ? bin : null;
            }
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}
