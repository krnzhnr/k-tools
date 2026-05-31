// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KTools_App.Core;

namespace KTools_App.Infrastructure;

/// <summary>
/// Синглтон-обертка для запуска утилиты FFmpeg и зонда FFprobe.
/// Предоставляет методы для выполнения транскодирования, извлечения потоков/вложений,
/// анализа структуры медиафайлов и детекции графического оборудования NVIDIA NVENC.
/// Наследует AbstractProcessRunner и реализует все требования русскоязычной локализации.
/// </summary>
public sealed class FFmpegRunner : AbstractProcessRunner
{
    private static readonly Lazy<FFmpegRunner> LazyInstance =
        new(() => new FFmpegRunner());

    private FFmpegRunner() { }

    /// <summary>
    /// Возвращает единственный экземпляр класса FFmpegRunner.
    /// </summary>
    public static FFmpegRunner Instance => LazyInstance.Value;

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
        string outputPath,
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
            "-stats_period", "0.5",
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

        argsList.Add($"\"{outputPath}\"");

        string arguments = string.Join(" ", argsList);

        // Буфер для накопления последних строк stderr в случае ошибок
        var stderrLines = new List<string>();

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

                // Парсинг прогресса из вывода
                if (onProgress != null && totalDuration > 0)
                {
                    var progress = FFmpegOutputParser.ParseLine(line, totalDuration);
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
            string lastErrors = string.Join(Environment.NewLine, stderrLines);
            LogService.Instance.Error($"Ошибка выполнения FFmpeg (Код: {result.ExitCode}). Последние строки stderr:\n{lastErrors}", "FFmpegRunner");
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
        string arguments = $"-v error -show_entries format=duration:stream=index,codec_name,codec_type,disposition,pix_fmt,width,height:stream_tags -of json \"{filePath}\"";
        
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
            LogService.Instance.Error($"Ошибка вызова ffprobe для файла '{filePath}': {errText}", "FFmpegRunner");
            return null;
        }

        string fullOutput = string.Join("", outputLines);
        if (string.IsNullOrWhiteSpace(fullOutput))
        {
            LogService.Instance.Error($"ffprobe вернул пустой вывод для файла '{filePath}'", "FFmpegRunner");
            return null;
        }

        try
        {
            return JsonDocument.Parse(fullOutput);
        }
        catch (JsonException ex)
        {
            LogService.Instance.Exception(ex, $"Ошибка парсинга JSON от ffprobe для файла '{filePath}'", "FFmpegRunner");
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
            LogService.Instance.Warn("Аппаратный энкодер 'hevc_nvenc' не поддерживается сборкой FFmpeg", "FFmpegRunner");
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
            LogService.Instance.Info("Обнаружено аппаратное обеспечение NVIDIA с поддержкой NVENC", "FFmpegRunner");
            return true;
        }

        LogService.Instance.Warn("Аппаратная видеокарта NVIDIA не обнаружена в системе через утилиту nvidia-smi", "FFmpegRunner");
        return false;
    }
}
