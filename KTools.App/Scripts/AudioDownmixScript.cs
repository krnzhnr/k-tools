using KTools_App.Services.Contracts;
// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KTools_App.Core;
using KTools_App.Infrastructure;

namespace KTools_App.Scripts;

/// <summary>
/// Скрипт для даунмикса многоканального аудио в стерео (Stereo 2.0).
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
    public override string IconName => "volume2";

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
            "Режим даунмикса",
            SettingType.Combo,
            "Dolby Encoding Engine (DEE)",
            "Параметры даунмикса",
            options: new List<string> {
                "Dolby Encoding Engine (DEE)",
                "FFmpeg (EBU R128)"
            }),

        new SettingField(
            "OutputFormat",
            "Формат вывода",
            SettingType.Combo,
            "E-AC3",
            "Параметры даунмикса",
            options: new List<string> {
                "E-AC3",
                "AC3",
                "AAC",
                "FLAC"
            }),

        new SettingField(
            "Bitrate",
            "Битрейт (kbps)",
            SettingType.Combo,
            "256",
            "Параметры даунмикса",
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
    /// Выполнить даунмикс для одного файла.
    /// </summary>
    public override async Task<List<string>> ExecuteSingleAsync(
        string filePath,
        Dictionary<string, object> settings,
        string? outputPath,
        ScriptProgressCallback progressCallback,
        int fileIndex,
        int totalCount)
    {
        ResetCancellation();
        var results = new List<string>();

        // Извлекаем настройки
        string mode = GetSettingValue(
            settings, 
            "DownmixMode", 
            "Dolby Encoding Engine (DEE)");
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
                return results;
            }

            // Динамическая проверка зависимости dee
            if (!_dependencyManager.IsInstalled("dee"))
            {
                string errMsg = "❌ Ошибка: Для работы в режиме DEE " +
                                "необходима установленная утилита 'dee'.";
                results.Add(errMsg);
                return results;
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
            return results;
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
            _logService.Exception(
                ex,
                $"Не удалось прочесть длительность для '{originalName}'",
                "AudioDownmixScript");
        }

        using var cts = new CancellationTokenSource();
        bool success = false;

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
                    string msg = $"Даунмикс (DEE) | {pct:F1}%";
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
                _logService.Exception(
                    ex,
                    $"Ошибка работы DEE для '{originalName}': {ex.Message}",
                    "AudioDownmixScript");
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

            // Настройка фильтра EBU R128 в один проход
            var extraArgs = new List<string>
            {
                "-ac", "2",
                "-af", "loudnorm=I=-24:LRA=7:TP=-2.0",
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
                    string msg = $"Даунмикс | {progressInfo.Percent:F1}% " +
                                 $"| Скорость: {speedStr}";
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
                _logService.Exception(
                    ex,
                    $"Ошибка работы FFmpeg для '{originalName}': {ex.Message}",
                    "AudioDownmixScript");
            }
        }

        if (IsCancelled)
        {
            CleanupIfCancelled(outputFilePath);
            results.Add($"⚠ Отменено: {outputName}");
            return results;
        }

        try
        {
            if (success && File.Exists(outputFilePath))
            {
                progressCallback(
                    fileIndex, 
                    totalCount, 
                    "Успешно завершено!", 
                    100.0);
                results.Add($"✅ Даунмикс выполнен: {outputName}");

                if (deleteOriginal)
                {
                    DeleteSource(filePath, results);
                }
            }
            else
            {
                CleanupFailedOutputFile(outputFilePath);
                results.Add($"❌ Ошибка обработки для {Path.GetFileName(filePath)}");
            }
        }
        catch (Exception ex)
        {
            CleanupFailedOutputFile(outputFilePath);
            string errorMsg = $"❌ Ошибка выполнения скрипта для {Path.GetFileName(filePath)}: {ex.Message}";
            results.Add(errorMsg);
            _logService.Exception(ex, $"Ошибка при выполнении даунмикса для '{originalName}': {ex.Message}", "AudioDownmixScript");
        }

        return results;
    }
}
