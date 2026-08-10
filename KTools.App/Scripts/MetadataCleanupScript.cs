// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KTools_App.Core;
using KTools_App.Services.Contracts;

namespace KTools_App.Scripts;

/// <summary>
/// Скрипт очистки метаданных и тегов из любых медиафайлов через FFmpeg.
/// Полностью удаляет все глобальные теги и метаданные потоков без перекодирования исходного содержимого.
/// </summary>
public sealed class MetadataCleanupScript : AbstractScript
{
    private readonly IFFmpegRunner _ffmpegRunner;
    private readonly IMediaProbeService _mediaProbeService;

    /// <summary>
    /// Русское название скрипта.
    /// </summary>
    public override string Name => AppConstants.ScriptMetadata.MetadataCleanName;

    /// <summary>
    /// Русское описание возможностей скрипта для UI.
    /// </summary>
    public override string Description => AppConstants.ScriptMetadata.MetadataCleanDesc;

    /// <summary>
    /// Категория медиаобработки.
    /// </summary>
    public override string Category => AppConstants.ScriptCategory.Video;

    /// <summary>
    /// Название системной Fluent-иконки.
    /// </summary>
    public override string IconName => AppConstants.ScriptIcons.MetadataCleanup;

    /// <summary>
    /// Поддерживаемые расширения всех видео и аудио медиафайлов, поддерживаемых FFmpeg.
    /// </summary>
    public override string[] FileExtensions => AppConstants.AllMediaExtensions.ToArray();

    /// <summary>
    /// Обязательные зависимости скрипта.
    /// </summary>
    public override string[] RequiredDependencies => new[] { "ffmpeg" };

    /// <summary>
    /// Декларативная схема настроек скрипта.
    /// </summary>
    public override List<SettingField> SettingsSchema => new()
    {
        new SettingField(
            "overwrite_source",
            "Подменить оригинал финальным файлом",
            SettingType.Checkbox,
            false,
            "Вывод"
        ),
        new SettingField(
            "delete_source",
            "Удалить оригинал после обработки",
            SettingType.Checkbox,
            false,
            "Вывод",
            visibleIfKey: "overwrite_source",
            visibleIfValues: new List<string> { "False" }
        )
    };

    public MetadataCleanupScript(
        ILogService logService,
        ISettingsManager settingsManager,
        IPathManager pathManager,
        IFFmpegRunner ffmpegRunner,
        IMediaProbeService mediaProbeService)
        : base(logService, settingsManager, pathManager)
    {
        _ffmpegRunner = ffmpegRunner ?? throw new ArgumentNullException(nameof(ffmpegRunner));
        _mediaProbeService = mediaProbeService ?? throw new ArgumentNullException(nameof(mediaProbeService));
    }

    /// <summary>
    /// Асинхронное выполнение очистки метаданных для одного файла.
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

        _logService.Info($"Начало очистки метаданных для файла '{Path.GetFileName(filePath)}'", "MetadataCleanupScript");

        string originalName = Path.GetFileNameWithoutExtension(filePath);
        string ext = Path.GetExtension(filePath);

        // Определяем целевую директорию
        string targetDir = string.IsNullOrEmpty(outputPath)
            ? Path.GetDirectoryName(filePath) ?? AppContext.BaseDirectory
            : outputPath;

        string targetOutputFile = Path.Combine(targetDir, $"{originalName}{ext}");
        string finalOutputFile = GetSafeOutputPath(filePath, targetOutputFile, settings);
        string outputName = Path.GetFileName(finalOutputFile);

        bool overwrite = _settingsManager.GetSetting("General", "OverwriteExisting", false);
        if (File.Exists(finalOutputFile) && !overwrite)
        {
            string skipMsg = $"⏭ ПРОПУСК (файл существует): {outputName}";
            _logService.Info(skipMsg, "MetadataCleanupScript");
            progressCallback(fileIndex, totalCount, $"Пропущен (существует): {outputName}", 100.0);
            results.Add(skipMsg);
            return results;
        }

        // Пробуем получить длительность для расчета прогресса
        double? duration = null;
        try
        {
            var mediaInfo = await _mediaProbeService.ProbeAsync(filePath);
            if (mediaInfo != null && mediaInfo.Duration > 0)
            {
                duration = mediaInfo.Duration;
            }
        }
        catch (Exception ex)
        {
            _logService.Warn($"Не удалось заранее определить длительность медиафайла: {ex.Message}", "MetadataCleanupScript");
        }

        progressCallback(fileIndex, totalCount, $"Очистка метаданных {originalName}...", 0.0);

        using var cts = new CancellationTokenSource();
        var cancelMonitorTask = Task.Run(async () =>
        {
            while (!IsCancelled && !cts.IsCancellationRequested)
            {
                await Task.Delay(100);
            }
            if (IsCancelled)
            {
                cts.Cancel();
            }
        });

        bool success = false;
        try
        {
            // Формируем аргументы FFmpeg для очистки всех тегов и копирования всех потоков (-map 0 -map_metadata -1 -c copy)
            var ffmpegArgs = new List<string>
            {
                "-map", "0",
                "-map_metadata", "-1",
                "-map_metadata:s", "-1",
                "-c", "copy"
            };

            success = await _ffmpegRunner.RunAsync(
                inputPath: filePath,
                outputPath: finalOutputFile,
                extraArgs: ffmpegArgs,
                overwrite: overwrite,
                totalDuration: duration ?? 0.0,
                onProgress: progress =>
                {
                    progressCallback(fileIndex, totalCount, $"Очистка метаданных | {progress.Percent:F1}% | Скорость: {(progress.Speed.HasValue ? $"{progress.Speed.Value:F1}x" : "н/д")}", progress.Percent, progress.Fps, progress.Bitrate);
                },
                cancellationToken: cts.Token
            );
        }
        catch (Exception ex)
        {
            string runErr = $"❌ Критическая ошибка при очистке метаданных для '{originalName}': {ex.Message}";
            _logService.Exception(ex, $"Исключение при очистке метаданных для '{filePath}': {ex.Message}", "MetadataCleanupScript");
            results.Add(runErr);
        }
        finally
        {
            cts.Cancel();
            await cancelMonitorTask;
        }

        if (IsCancelled)
        {
            CleanupIfCancelled(finalOutputFile);
            string cancelMsg = $"⚠ Обработка отменена пользователем: {outputName}";
            _logService.Info(cancelMsg, "MetadataCleanupScript");
            results.Add(cancelMsg);
            return results;
        }

        try
        {
            if (success)
            {
                progressCallback(fileIndex, totalCount, "Завершено!", 100.0);
                string successMsg = $"✅ Очищены метаданные: {outputName}";
                _logService.Info(successMsg, "MetadataCleanupScript");
                results.Add(successMsg);

                bool overwriteSource = GetSettingValue(settings, "overwrite_source", false);
                bool deleteSource = GetSettingValue(settings, "delete_source", false);

                if (overwriteSource && string.IsNullOrEmpty(outputPath))
                {
                    ReplaceSourceWithResult(filePath, finalOutputFile, results);
                }
                else if (deleteSource)
                {
                    DeleteSource(filePath, results);
                }
            }
            else
            {
                CleanupFailedOutputFile(finalOutputFile);
                string failMsg = $"❌ ОШИБКА очистки метаданных: {Path.GetFileName(filePath)}";
                _logService.Error(failMsg, "MetadataCleanupScript");
                results.Add(failMsg);
            }
        }
        catch (Exception ex)
        {
            CleanupFailedOutputFile(finalOutputFile);
            string errorMsg = $"❌ Ошибка выполнения скрипта для {Path.GetFileName(filePath)}: {ex.Message}";
            results.Add(errorMsg);
            _logService.Exception(ex, $"Ошибка при очистке метаданных для '{originalName}': {ex.Message}", "MetadataCleanupScript");
        }

        return results;
    }
}

