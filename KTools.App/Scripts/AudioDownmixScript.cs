// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Infrastructure;
using KTools_App.Models;
using KTools_App.Services.Contracts;
using ExecutionContext = KTools_App.Models.ExecutionContext;

namespace KTools_App.Scripts;

/// <summary>
/// Скрипт для сведения многоканального аудио в стерео (Stereo 2.0).
/// Поддерживает Dolby Encoding Engine (DEE) и FFmpeg с нормализацией EBU R128.
/// </summary>
public sealed class AudioDownmixScript : AbstractScript
{
    private readonly IDependencyManager _dependencyManager;
    private readonly IMediaProbeService _mediaProbeService;
    private readonly IFFmpegRunner _ffmpegRunner;
    private readonly DeeRunner _deeRunner;

    public AudioDownmixScript(ILogService logService, ISettingsManager settingsManager, IPathManager pathManager, IDependencyManager dependencyManager, IMediaProbeService mediaProbeService, IFFmpegRunner ffmpegRunner, DeeRunner deeRunner)
        : base(logService, settingsManager, pathManager)
    {
        _dependencyManager = dependencyManager ?? throw new ArgumentNullException(nameof(dependencyManager));
        _mediaProbeService = mediaProbeService ?? throw new ArgumentNullException(nameof(mediaProbeService));
        _ffmpegRunner = ffmpegRunner ?? throw new ArgumentNullException(nameof(ffmpegRunner));
        _deeRunner = deeRunner ?? throw new ArgumentNullException(nameof(deeRunner));
    }

    /// <summary>
    /// Русское название скрипта.
    /// </summary>
    public override string Name => AppConstants.ScriptMetadata.AudioDownmixName;

    /// <summary>
    /// Русское описание возможностей скрипта для UI.
    /// </summary>
    public override string Description => AppConstants.ScriptMetadata.AudioDownmixDesc;

    /// <summary>
    /// Категория медиаобработки.
    /// </summary>
    public override string Category => AppConstants.ScriptCategory.Audio;

    /// <summary>
    /// Название системной Fluent-иконки.
    /// </summary>
    public override string IconName => AppConstants.ScriptIcons.AudioDownmix;

    /// <summary>
    /// Поддерживаемые расширения медиафайлов.
    /// </summary>
    public override string[] FileExtensions => AppConstants.AudioContainers
        .Concat(AppConstants.AudioStreams)
        .Concat(AppConstants.VideoContainers)
        .ToArray();

    /// <summary>
    /// Зависимости скрипта (FFmpeg нужен в обоих режимах).
    /// </summary>
    public override string[] RequiredDependencies => new[] { "ffmpeg" };

    /// <summary>
    /// Поддержка параллельной обработки файлов.
    /// </summary>
    public override bool SupportsParallel => true;

    /// <summary>
    /// Декларативная схема настроек скрипта.
    /// </summary>
    public override List<SettingField> SettingsSchema => new()
    {
        new SettingField(
            "DownmixMode",
            "Режим сведения в стерео",
            SettingType.Combo,
            "FFmpeg",
            "Параметры сведения в стерео",
            options: new List<string> {
                "FFmpeg",
                "Dolby Encoding Engine (DEE)"
            }),

        new SettingField(
            "OutputFormat",
            "Формат вывода",
            SettingType.Combo,
            "E-AC3",
            "Параметры сведения в стерео",
            options: new List<string> {
                "E-AC3",
                "AC3",
                "AAC",
                "FLAC"
            }),

        new SettingField(
            "Bitrate",
            "Битрейт (кбит/с)",
            SettingType.Combo,
            "256",
            "Параметры сведения в стерео",
            options: new List<string> {
                "128", "192", "224", "256", "320", "384", "448", "640"
            },
            visibleIfKey: "OutputFormat",
            visibleIfValues: new List<string> { "E-AC3", "AC3", "AAC" }),

        new SettingField(
            "Suffix",
            "Суффикс файла",
            SettingType.Text,
            "_stereo",
            "Общие"),

        new SettingField(
            "DeleteOriginal",
            "Удалить исходный файл",
            SettingType.Checkbox,
            false,
            "Общие")
    };

    /// <summary>
    /// Выполнить сведение аудио в стерео для одного файла.
    /// </summary>
    public override async Task<ExecutionResult> ExecuteSingleAsync(
        string filePath,
        Dictionary<string, object> settings,
        string? outputPath,
        ScriptProgressCallback progressCallback,
        int fileIndex,
        int totalCount,
        ExecutionContext context)
    {
        var results = new List<string>();

        // Извлекаем настройки
        string mode = GetSettingValue(
            settings,
            "DownmixMode",
            "FFmpeg");
        string format = GetSettingValue(
            settings,
            "OutputFormat",
            "E-AC3");

        string bitrate = GetSettingValue(
            settings,
            "Bitrate",
            "256");
        string suffix = GetSettingValue(
            settings,
            "Suffix",
            "_stereo");
        bool deleteOriginal = GetSettingValue(
            settings,
            "DeleteOriginal",
            false);

        string originalName = Path.GetFileNameWithoutExtension(filePath);

        // 1. Валидация формата для DEE
        if (mode == "Dolby Encoding Engine (DEE)")
        {
            if (format != "E-AC3" &&
                format != "AC3")
            {
                string errMsg = "❌ Ошибка: Dolby Encoding Engine " +
                                "поддерживает только форматы E-AC3 и AC3.";
                results.Add(errMsg);
                return ExecutionResult.Failed(
                    context,
                    results,
                    errorCode: "unsupported-format");
            }

            // Динамическая проверка зависимости dee
            if (!_dependencyManager.IsInstalled("dee"))
            {
                string errMsg = "❌ Ошибка: Для работы в режиме DEE " +
                                "необходима установленная утилита 'dee'.";
                results.Add(errMsg);
                return ExecutionResult.Failed(
                    context,
                    results,
                    errorCode: "missing-dependency",
                    retryable: true,
                    cleanupState: CleanupState.NotRequired);
            }
        }

        // Определение расширения выходного файла
        string ext = ".eac3";
        if (format == "AC3")
        {
            ext = ".ac3";
        }
        else if (format == "AAC")
        {
            ext = ".m4a";
        }
        else if (format == "FLAC")
        {
            ext = ".flac";
        }

        string outputName = $"{originalName}{suffix}{ext}";

        // Определение директории сохранения
        string targetDir = string.IsNullOrEmpty(outputPath)
            ? Path.GetDirectoryName(filePath) ?? AppContext.BaseDirectory
            : outputPath;

        string outputFilePath = Path.Combine(targetDir, outputName);
        outputFilePath = GetSafeOutputPath(filePath, outputFilePath, settings);

        // Проверяем, существует ли файл и нужно ли его перезаписать
        bool overwrite = _settingsManager.GetSetting(
            "General",
            "OverwriteExisting",
            false);

        if (File.Exists(outputFilePath) && !overwrite)
        {
            string msg = $"Пропуск (существует): {outputName}";
            progressCallback(fileIndex, totalCount, msg, 100.0);
            results.Add($"⏭ ПРОПУСК (файл существует): {outputName}");
            return ExecutionResult.Skipped(
                context,
                results,
                errorCode: "output-exists",
                outputFile: outputFilePath,
                outputExists: true);
        }

        // Получаем длительность для прогресса
        double duration = 0.0;
        try
        {
            var structure = await _mediaProbeService.ProbeAsync(
                filePath);
            if (structure != null)
            {
                duration = structure.Duration;
            }
        }
        catch (Exception ex)
        {
            _logService.Write(
            "script.audio_downmix.duration_read_failed",
            LogLevel.Warning,
            LogStatus.PartiallySucceeded,
            $"Длительность файла '{originalName}' не прочитана, сведение в стерео продолжится с начальными параметрами",
            ex,
            Name,
            properties: LogProps
                .Create("InputName", LogProps.FileName(filePath))
                .With("ErrorCode", "DURATION_READ_FAILED")
                .With("Retryable", false));
        }

        using var cts = new CancellationTokenSource();
        ProcessResult? success = null;

        if (mode == "Dolby Encoding Engine (DEE)")
        {
            progressCallback(
                fileIndex,
                totalCount,
                "Запуск Dolby Encoding Engine...",
                0.0);

            string outputFormat = format == "E-AC3"
                ? "ddp"
                : "dd";

            // Следим за отменой
            var deeTask = _deeRunner.RunAsync(
                inputPath: filePath,
                outputPath: outputFilePath,
                bitrate: bitrate,
                outputFormat: outputFormat,
                downmixChannels: 2,
                onProgress: pct =>
                {
                    string msg = $"Сведение в стерео (DEE) | {pct:F1}%";
                    progressCallback(
                        fileIndex,
                        totalCount,
                        msg,
                        pct);
                },
                cancellationToken: cts.Token);

            while (!deeTask.IsCompleted)
            {
                if (IsCancelled)
                {
                    cts.Cancel();
                    break;
                }
                await Task.Delay(200);
            }

            try
            {
                success = await deeTask;
            }
            catch (Exception ex)
            {
                _logService.Write(
                "script.audio_downmix.dee_failed",
                LogLevel.Error,
                LogStatus.Failed,
                $"Сведение в стерео через DEE для '{originalName}' не выполнено",
                ex,
                Name,
                properties: LogProps
                    .Create("Tool", "DEE")
                    .With("InputName", LogProps.FileName(filePath))
                    .With("ErrorCode", "DEE_FAILED")
                    .With("Retryable", true));
            }
        }
        else
        {
            // Режим FFmpeg EBU R128
            progressCallback(fileIndex, totalCount, "Запуск FFmpeg...", 0.0);

            string codec = "aac";
            bool isLossless = false;

            if (format == "E-AC3")
            {
                codec = "eac3";
            }
            else if (format == "AC3")
            {
                codec = "ac3";
            }
            else if (format == "FLAC")
            {
                codec = "flac";
                isLossless = true;
            }

            // Сведение в стерео с коэффициентами HandBrake (center/surround = 0.7071, LFE отброшен).
            // Фильтр pan обходит автоматическую нормализацию матрицы (rematrix_maxval) в libswresample,
            // сохраняя оригинальную громкость фронтальных каналов.
            var extraArgs = new List<string>
            {
                "-af", AppConstants.FFmpegAudio.StereoDownmixPanFilter,
                "-c:a", codec
            };

            if (!isLossless)
            {
                extraArgs.Add("-b:a");
                extraArgs.Add($"{bitrate}k");
            }

            var ffmpegTask = _ffmpegRunner.RunAsync(
                inputPath: filePath,
                outputPath: outputFilePath,
                extraArgs: extraArgs,
                overwrite: overwrite,
                totalDuration: duration,
                onProgress: progressInfo =>
                {
                    string speedStr = progressInfo.Speed > 0
                        ? $"{progressInfo.Speed:F1}x"
                        : "н/д";
                    string progressLabel;
                    if (progressInfo.Percent > 0)
                    {
                        progressLabel = $"{progressInfo.Percent:F1}%";
                    }
                    else
                    {
                        TimeSpan ts = TimeSpan.FromSeconds(progressInfo.TimeSeconds);
                        progressLabel = ts.Hours > 0
                            ? $"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"
                            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
                    }

                    string msg = $"Сведение в стерео | {progressLabel} | Скорость: {speedStr}";
                    progressCallback(
                        fileIndex,
                        totalCount,
                        msg,
                        progressInfo.Percent,
                        progressInfo.Fps,
                        progressInfo.Bitrate);
                },
                cancellationToken: cts.Token);

            while (!ffmpegTask.IsCompleted)
            {
                if (IsCancelled)
                {
                    cts.Cancel();
                    break;
                }
                await Task.Delay(200);
            }

            try
            {
                success = await ffmpegTask;
            }
            catch (Exception ex)
            {
                _logService.Write(
                "script.audio_downmix.ffmpeg_failed",
                LogLevel.Error,
                LogStatus.Failed,
                $"Сведение в стерео через FFmpeg для '{originalName}' не выполнено",
                ex,
                Name,
                properties: LogProps
                    .Create("Tool", "ffmpeg")
                    .With("InputName", LogProps.FileName(filePath))
                    .With("ErrorCode", "FFMPEG_FAILED")
                    .With("Retryable", true));
            }
        }

        if (IsCancelled)
        {
            CleanupIfCancelled(outputFilePath);
            results.Add($"⚠ Отменено: {outputName}");
            return ExecutionResult.Cancelled(
                context,
                results,
                errorCode: "cancelled",
                outputFile: outputFilePath,
                outputExists: File.Exists(outputFilePath),
                cleanupState: CleanupState.Completed);
        }

        try
        {
            if (success?.IsSuccess == true && File.Exists(outputFilePath))
            {
                progressCallback(
                    fileIndex,
                    totalCount,
                    "Успешно завершено!",
                    100.0);
                results.Add($"✅ Сведение в стерео выполнено: {outputName}");

                if (deleteOriginal)
                {
                    await DeleteSourceAsync(filePath, results);
                }
            }
            else
            {
                await CleanupFailedOutputFileAsync(outputFilePath);
                results.Add($"❌ Ошибка обработки для {Path.GetFileName(filePath)}");
            }
        }
        catch (Exception ex)
        {
            await CleanupFailedOutputFileAsync(outputFilePath);
            string errorMsg = $"❌ Ошибка выполнения скрипта для {Path.GetFileName(filePath)}: {ex.Message}";
            results.Add(errorMsg);
            _logService.Write(
                "script.audio_downmix.failed",
                LogLevel.Error,
                LogStatus.Failed,
                $"Сведение в стерео для '{originalName}' не выполнено",
                ex,
                Name,
                properties: LogProps
                    .Create("InputName", LogProps.FileName(filePath))
                    .With("ErrorCode", "DOWNMIX_FAILED")
                    .With("Retryable", true));
        }

        bool outputReady = success?.IsSuccess == true && File.Exists(outputFilePath);
        bool sourceCleanupFailed = deleteOriginal && outputReady && File.Exists(filePath);
        if (outputReady && sourceCleanupFailed)
        {
            return ExecutionResult.PartiallySucceeded(
                context,
                results,
                errorCode: "source-cleanup-failed",
                outputFile: outputFilePath,
                outputExists: true,
                cleanupState: CleanupState.Failed);
        }
        if (outputReady)
        {
            return ExecutionResult.Succeeded(
                context,
                results,
                outputFile: outputFilePath,
                outputExists: true,
                cleanupState: deleteOriginal ? CleanupState.Completed : CleanupState.NotRequired);
        }
        return ExecutionResult.Failed(
            context,
            results,
            errorCode: "output-missing",
            outputFile: outputFilePath,
            outputExists: File.Exists(outputFilePath));
    }
}
