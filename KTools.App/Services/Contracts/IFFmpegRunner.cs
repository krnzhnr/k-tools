// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KTools_App.Infrastructure;

namespace KTools_App.Services.Contracts;

/// <summary>
/// Интерфейс обертки для запуска процессов утилит FFmpeg и FFprobe.
/// </summary>
public interface IFFmpegRunner
{
    /// <summary>
    /// Запустить процесс обработки через FFmpeg асинхронно с отслеживанием прогресса.
    /// </summary>
    Task<bool> RunAsync(
        string inputPath,
        string? outputPath = null,
        List<string>? extraArgs = null,
        List<string>? inputArgs = null,
        bool overwrite = false,
        double totalDuration = 0.0,
        Action<ProgressInfo>? onProgress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Получить техническую информацию о медиафайле в формате JSON через ffprobe.
    /// </summary>
    Task<JsonDocument?> GetVideoInfoAsync(string filePath);

    /// <summary>
    /// Извлечь выбранную дорожку субтитров и перекодировать её в формат ASS.
    /// </summary>
    Task<bool> ExtractSubtitleAsync(string inputFile, int streamIndex, string outputPath, bool relative = false);

    /// <summary>
    /// Извлечь встроенное вложение (файл шрифта) из медиафайла.
    /// </summary>
    Task<bool> ExtractAttachmentAsync(string inputFile, int streamIndex, string outputPath);

    /// <summary>
    /// Проверить поддержку кодирования с аппаратным ускорением NVIDIA NVENC.
    /// </summary>
    Task<bool> CheckNvencSupportAsync();

    /// <summary>
    /// Проверить поддержку параметра Temporal AQ для NVENC.
    /// </summary>
    Task<bool> CheckNvencTemporalAqSupportAsync();

    /// <summary>
    /// Выполнить зондирование и автоматическое определение обрезки черных полос (cropdetect).
    /// </summary>
    Task<string?> DetectCropAsync(
        string filePath,
        double skipSeconds = 0,
        int probeFrames = 25,
        double limit = 0.0941176,
        int round = 16,
        int skip = 2,
        int reset = 0,
        string mode = "black",
        CancellationToken cancellationToken = default);
}
