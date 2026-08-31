// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KTools_App.Core;
using KTools_App.Services.Contracts;

namespace KTools_App.Infrastructure;

/// <summary>
/// Синглтон-обертка для запуска утилиты FFmpeg и зонда FFprobe.
/// Предоставляет методы для выполнения транскодирования, извлечения потоков/вложений,
/// анализа структуры медиафайлов и детекции графического оборудования NVIDIA NVENC.
/// Наследует AbstractProcessRunner и реализует все требования русскоязычной локализации.
/// </summary>
public sealed class FFmpegRunner : AbstractProcessRunner, IFFmpegRunner
{


    /// <summary>
    /// Инициализирует новый экземпляр FFmpegRunner с внедрением зависимостей.
    /// </summary>
    /// <param name="logService">Сервис логирования.</param>
    public FFmpegRunner(ILogService logService, IPathManager pathManager)
        : base(logService, pathManager)
    {
    }



    /// <summary>
    /// Запустить процесс обработки медиа через FFmpeg с отслеживанием прогресса в реальном времени.
    /// </summary>
    /// <param name="inputPath">Абсолютный путь к входному медиафайлу.</param>
    /// <param name="outputPath">Абсолютный путь к выходному медиафайлу.</param>
    /// <param name="extraArgs">Список дополнительных аргументов вывода (например, кодеки, битрейты).</param>
    /// <param name="inputArgs">Список входных параметров, указываемых перед флагом "-i".</param>
    /// <param name="overwrite">Флаг принудительной перезаписи существующего выходного файла.</param>
    /// <param name="totalDuration">Общая длительность медиафайла в секундах для расчета процента прогресса.</param>
    /// <param name="onProgress">Делегат обратного вызова для передачи информации о прогрессе.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>True, если процесс завершился успешно (код 0), иначе false.</returns>
    public async Task<bool> RunAsync(
        string inputPath,
        string? outputPath = null,
        List<string>? extraArgs = null,
        List<string>? inputArgs = null,
        bool overwrite = false,
        double totalDuration = 0.0,
        Action<ProgressInfo>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        string overwriteFlag = overwrite ? "-y" : "-n";
        
        // Формируем командную строку FFmpeg
        var argsList = new List<string>
        {
            "-hide_banner",
            "-loglevel", "info",
            "-stats",
            "-stats_period", "0.1",
            overwriteFlag
        };

        if (inputArgs != null)
        {
            argsList.AddRange(inputArgs);
        }

        argsList.Add("-i");
        argsList.Add($"\"{inputPath}\"");

        if (extraArgs != null)
        {
            argsList.AddRange(extraArgs);
        }

        if (!string.IsNullOrEmpty(outputPath))
        {
            argsList.Add($"\"{outputPath}\"");
        }

        string arguments = string.Join(" ", argsList);

        // Буфер для накопления последних строк stderr в случае ошибок
        var stderrLines = new List<string>();

        double effectiveDuration = totalDuration;
        Log.Info($"Запуск FFmpeg для '{Path.GetFileName(inputPath)}' (Начальная длительность: {effectiveDuration:F2} сек.)", "FFmpegRunner");

        var result = await RunProcessAsync(
            "ffmpeg",
            arguments,
            onOutputLine: null,
            onErrorLine: line =>
            {
                lock (stderrLines)
                {
                    stderrLines.Add(line);
                    if (stderrLines.Count > 100)
                    {
                        stderrLines.RemoveAt(0);
                    }
                }

                // Автоматическое обнаружение точной длительности из заголовочного вывода FFmpeg (Duration: HH:MM:SS.ms)
                if (onProgress != null && effectiveDuration <= 0)
                {
                    double parsedHeaderDuration = FFmpegOutputParser.ParseHeaderDuration(line, Log);
                    if (parsedHeaderDuration > 0)
                    {
                        effectiveDuration = parsedHeaderDuration;
                        Log.Info($"Длительность для '{Path.GetFileName(inputPath)}' переопределена точным заголовком FFmpeg: {effectiveDuration:F2} сек.", "FFmpegRunner");
                    }
                }

                // Парсинг прогресса из вывода (вызываем при успешном считывании строки прогресса)
                if (onProgress != null)
                {
                    var progress = FFmpegOutputParser.ParseLine(line, effectiveDuration, Log);
                    if (progress != null)
                    {
                        onProgress(progress);
                    }
                }
            },
            cancellationToken
        );

        if (!result.IsSuccess)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                string lastErrors = string.Join(Environment.NewLine, stderrLines);
                Log.Error($"Ошибка выполнения FFmpeg (Код: {result.ExitCode}). Последние строки stderr:\n{lastErrors}", "FFmpegRunner");
            }
            
            // Физически удаляем поврежденный выходной файл при сбое выполнения процесса
            if (!string.IsNullOrEmpty(outputPath))
            {
                for (int attempt = 0; attempt < 6; attempt++)
                {
                    if (!File.Exists(outputPath)) break;
                    try
                    {
                        File.Delete(outputPath);
                        Log.DebugLog($"Удален поврежденный выходной файл после остановки FFmpeg: '{Path.GetFileName(outputPath)}'", "FFmpegRunner");
                        break;
                    }
                    catch (Exception deleteEx)
                    {
                        if (attempt == 5)
                        {
                            Log.Warn($"Не удалось удалить поврежденный выходной файл '{outputPath}' после остановки FFmpeg: {deleteEx.Message}", "FFmpegRunner");
                        }
                        else
                        {
                            await Task.Delay(150);
                        }
                    }
                }
            }
            
            return false;
        }

        return true;
    }

    /// <summary>
    /// Получить подробную техническую информацию о структуре медиафайла в формате JSON с помощью FFprobe.
    /// </summary>
    /// <param name="filePath">Абсолютный путь к исследуемому файлу.</param>
    /// <returns>Документ JsonDocument со свойствами потоков, или null при сбоях.</returns>
    public async Task<JsonDocument?> GetVideoInfoAsync(string filePath)
    {
        string arguments = $"-v error -analyzeduration 100M -probesize 100M -show_entries format=duration,bit_rate:stream=index,codec_name,codec_type,duration,bit_rate,disposition,pix_fmt,width,height,channels:stream_tags -of json \"{filePath}\"";
        
        var outputLines = new List<string>();
        var errorLines = new List<string>();

        var result = await RunProcessAsync(
            "ffprobe",
            arguments,
            onOutputLine: line => outputLines.Add(line),
            onErrorLine: line => errorLines.Add(line),
            CancellationToken.None
        );

        if (!result.IsSuccess)
        {
            string errText = string.Join(" ", errorLines);
            Log.Error($"Ошибка вызова ffprobe для файла '{filePath}': {errText}", "FFmpegRunner");
            return null;
        }

        string fullOutput = string.Join("", outputLines);
        if (string.IsNullOrWhiteSpace(fullOutput))
        {
            Log.Error($"ffprobe вернул пустой вывод для файла '{filePath}'", "FFmpegRunner");
            return null;
        }

        try
        {
            return JsonDocument.Parse(fullOutput);
        }
        catch (JsonException ex)
        {
            Log.Exception(ex, $"Ошибка парсинга JSON от ffprobe для файла '{filePath}'", "FFmpegRunner");
            return null;
        }
    }

    /// <summary>
    /// Извлечь выбранную дорожку субтитров и перекодировать в формат ASS.
    /// </summary>
    public async Task<bool> ExtractSubtitleAsync(string inputFile, int streamIndex, string outputPath, bool relative = false)
    {
        string mapVal = relative ? $"0:s:{streamIndex}" : $"0:{streamIndex}";
        string arguments = $"-y -hide_banner -loglevel error -i \"{inputFile}\" -map {mapVal} -c:s ass \"{outputPath}\"";

        var result = await RunProcessAsync("ffmpeg", arguments, null, null, CancellationToken.None);
        return result.IsSuccess && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
    }

    /// <summary>
    /// Извлечь встроенное вложение (например, файл шрифта) из видеофайла.
    /// </summary>
    public async Task<bool> ExtractAttachmentAsync(string inputFile, int streamIndex, string outputPath)
    {
        // Папка вывода должна существовать
        string? dir = Path.GetDirectoryName(outputPath);
        if (dir != null && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string arguments = $"-y -hide_banner -loglevel error -dump_attachment:{streamIndex} \"{outputPath}\" -i \"{inputFile}\"";
        
        var result = await RunProcessAsync("ffmpeg", arguments, null, null, CancellationToken.None);
        return result.IsSuccess && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
    }

    /// <summary>
    /// Проверить поддержку аппаратного кодирования NVENC со стороны FFmpeg и видеокарты NVIDIA.
    /// </summary>
    public async Task<bool> CheckNvencSupportAsync()
    {
        // 1. Проверяем наличие кодировщика hevc_nvenc в FFmpeg
        bool ffmpegSupports = false;
        var result = await RunProcessAsync(
            "ffmpeg",
            "-encoders",
            onOutputLine: line =>
            {
                if (line.Contains("hevc_nvenc", StringComparison.OrdinalIgnoreCase))
                {
                    ffmpegSupports = true;
                }
            },
            onErrorLine: null,
            CancellationToken.None
        );

        if (!result.IsSuccess || !ffmpegSupports)
        {
            Log.Warn("Аппаратный энкодер 'hevc_nvenc' не поддерживается сборкой FFmpeg", "FFmpegRunner");
            return false;
        }

        // 2. Проверяем наличие видеокарты NVIDIA через nvidia-smi
        string winPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe");
        string smiName = File.Exists(winPath) ? winPath : "nvidia-smi";

        bool hasNvidia = false;
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = smiName,
                    Arguments = "-L",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                }
            };
            process.Start();
            string output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            hasNvidia = process.ExitCode == 0 && output.Contains("GPU", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // nvidia-smi может отсутствовать
        }

        if (hasNvidia)
        {
            Log.Info("Обнаружено аппаратное обеспечение NVIDIA с поддержкой NVENC", "FFmpegRunner");
            return true;
        }

        Log.Warn("Аппаратная видеокарта NVIDIA не обнаружена в системе через утилиту nvidia-smi", "FFmpegRunner");
        return false;
    }

    /// <summary>
    /// Проверить поддержку параметра Temporal AQ для NVENC через вывод помощи FFmpeg.
    /// </summary>
    public async Task<bool> CheckNvencTemporalAqSupportAsync()
    {
        bool supported = false;
        var result = await RunProcessAsync(
            "ffmpeg",
            "-h encoder=hevc_nvenc",
            onOutputLine: line =>
            {
                if (line.Contains("-temporal-aq", StringComparison.OrdinalIgnoreCase))
                {
                    supported = true;
                }
            },
            onErrorLine: null,
            CancellationToken.None
        );

        return result.IsSuccess && supported;
    }

    /// <summary>
    /// Выполнить зондирование и автоматическое определение обрезки черных полос (cropdetect).
    /// </summary>
    public async Task<string?> DetectCropAsync(
        string filePath,
        double skipSeconds = 0,
        int probeFrames = 25,
        double limit = 0.0941176,
        int round = 16,
        int skip = 2,
        int reset = 0,
        string mode = "black",
        CancellationToken cancellationToken = default)
    {
        int safeSkip = skip;
        if (safeSkip >= probeFrames)
        {
            safeSkip = Math.Max(0, Math.Min(2, probeFrames - 1));
        }

        string limitStr = limit.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
        string cropdetectFilter = $"cropdetect=limit={limitStr}:round={round}:skip={safeSkip}:reset={reset}:mode={mode}";
        
        var argsList = new List<string>
        {
            "-hide_banner",
            "-nostats"
        };

        if (skipSeconds > 0)
        {
            argsList.Add("-ss");
            argsList.Add(skipSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        }

        argsList.AddRange(new[]
        {
            "-i", $"\"{filePath}\"",
            "-vframes", probeFrames.ToString(),
            "-vf", cropdetectFilter,
            "-f", "null",
            "-"
        });

        string arguments = string.Join(" ", argsList);
        string? lastDetectedCrop = null;
        var cropRegex = new System.Text.RegularExpressions.Regex(@"crop=([0-9]+:[0-9]+:[0-9]+:[0-9]+)", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        var result = await RunProcessAsync(
            "ffmpeg",
            arguments,
            onOutputLine: line =>
            {
                var match = cropRegex.Match(line);
                if (match.Success)
                {
                    lastDetectedCrop = match.Groups[1].Value;
                }
            },
            onErrorLine: line =>
            {
                var match = cropRegex.Match(line);
                if (match.Success)
                {
                    lastDetectedCrop = match.Groups[1].Value;
                }
            },
            cancellationToken
        );

        if (!result.IsSuccess && lastDetectedCrop == null)
        {
            Log.Warn($"cropdetect не смог определить параметры обрезки для '{Path.GetFileName(filePath)}' (Код выхода: {result.ExitCode})", "FFmpegRunner");
            return null;
        }

        return lastDetectedCrop;
    }
}
