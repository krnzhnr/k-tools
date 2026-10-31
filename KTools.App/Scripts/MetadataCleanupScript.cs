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
            "Заменить исходный файл готовым результатом после успешной проверки",
            SettingType.Checkbox,
            false,
            "Вывод",
            requiresWarning: true,
            warningTitle: "Замена исходного файла",
            warningText: "Исходный файл заменяется готовым результатом только после успешной проверки. До подтверждения результата исходный файл сохраняется."
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

        _logService.Write("script.metadata_cleanup.started", LogLevel.Debug, LogStatus.Running, $"Начата очистка метаданных файла '{LogProps.FileName(filePath)}'", source: Name, properties: LogProps.Create("InputName", LogProps.FileName(filePath)));
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
            _logService.Write("script.metadata_cleanup.skipped", LogLevel.Info, LogStatus.Skipped, skipMsg, source: Name, properties: LogProps.Create("InputName", LogProps.FileName(filePath)));
            progressCallback(fileIndex, totalCount, $"Пропущен (существует): {outputName}", 100.0);
            results.Add(skipMsg);
            return ExecutionResult.Skipped(
                context,
                results,
                errorCode: "output-exists",
                outputFile: finalOutputFile,
                outputExists: true);
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
            _logService.Write("media.duration.probe_failed", LogLevel.Warning, LogStatus.Skipped, "Длительность медиафайла заранее не определена, очистка продолжится", ex, Name, properties: LogProps.Create("ErrorCode", "DURATION_PROBE_FAILED").With("Tool", "ffprobe").With("InputName", LogProps.FileName(filePath)));
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

        ProcessResult? success = null;
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
            string runErr = $"Критическая ошибка при очистке метаданных для '{originalName}'";
            _logService.Write("script.metadata_cleanup.failed", LogLevel.Error, LogStatus.Failed, $"Очистка метаданных файла '{LogProps.FileName(filePath)}' не выполнена", ex, Name, properties: LogProps.Create("ErrorCode", "METADATA_CLEANUP_FAILED").With("InputName", LogProps.FileName(filePath)).With("Retryable", true));
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
            _logService.Write("script.metadata_cleanup.cancelled", LogLevel.Info, LogStatus.Cancelled, cancelMsg, source: Name, properties: LogProps.Create("Reason", "UserRequested").With("CleanupState", "NotStarted"));
            results.Add(cancelMsg);
            return ExecutionResult.Cancelled(
                context,
                results,
                errorCode: "cancelled",
                outputFile: finalOutputFile,
                outputExists: File.Exists(finalOutputFile),
                cleanupState: CleanupState.Completed);
        }

        bool overwriteSource = GetSettingValue(settings, "overwrite_source", false);
        bool deleteSource = GetSettingValue(settings, "delete_source", false);
        bool sourceReplaced = false;

        try
        {
            if (success?.IsSuccess == true)
            {
                progressCallback(fileIndex, totalCount, "Завершено!", 100.0);
                string successMsg = $"✅ Очищены метаданные: {outputName}";
                _logService.Write("script.metadata_cleanup.completed", LogLevel.Info, LogStatus.Succeeded, successMsg, source: Name, properties: LogProps.Create("InputName", LogProps.FileName(filePath)).With("Verified", true));
                results.Add(successMsg);

                if (overwriteSource && string.IsNullOrEmpty(outputPath))
                {
                    sourceReplaced = await ReplaceSourceWithResultAsync(filePath, finalOutputFile, results);
                }
                else if (deleteSource)
                {
                    await DeleteSourceAsync(filePath, results);
                }
            }
            else
            {
                await CleanupFailedOutputFileAsync(finalOutputFile);
                string failMsg = $"❌ ОШИБКА очистки метаданных: {Path.GetFileName(filePath)}";
                _logService.Write("script.metadata_cleanup.failed", LogLevel.Error, LogStatus.Failed, failMsg, source: Name, properties: LogProps.Create("ErrorCode", "METADATA_CLEANUP_FAILED").With("InputName", LogProps.FileName(filePath)).With("Retryable", true));
                results.Add(failMsg);
            }
        }
        catch (Exception ex)
        {
            await CleanupFailedOutputFileAsync(finalOutputFile);
            string errorMsg = $"❌ Ошибка выполнения скрипта для {Path.GetFileName(filePath)}: {ex.Message}";
            results.Add(errorMsg);
            _logService.Write("script.metadata_cleanup.failed", LogLevel.Error, LogStatus.Failed, $"Очистка метаданных для '{originalName}' не выполнена", ex, Name, properties: LogProps.Create("ErrorCode", "METADATA_CLEANUP_FAILED").With("InputName", LogProps.FileName(filePath)).With("Retryable", true));
        }

        if (sourceReplaced)
        {
            return ExecutionResult.Succeeded(
                context,
                results,
                outputFile: filePath,
                outputExists: File.Exists(filePath),
                cleanupState: CleanupState.Completed);
        }

        bool outputReady = File.Exists(finalOutputFile);
        if (overwriteSource && string.IsNullOrEmpty(outputPath))
        {
            if (success?.IsSuccess != true)
            {
                return ExecutionResult.Failed(
                    context,
                    results,
                    errorCode: "output-missing",
                    outputFile: finalOutputFile,
                    outputExists: false,
                    cleanupState: CleanupState.Completed);
            }

            bool sourceIntact = File.Exists(filePath);
            string replacementFailMsg = $"❌ Исходный файл не заменён готовым результатом очистки метаданных: '{Path.GetFileName(filePath)}', исходник сохранён";
            _logService.Write("script.source.replaced_failed", LogLevel.Error, LogStatus.PartiallySucceeded, replacementFailMsg, source: Name, properties: LogProps.Create("ErrorCode", "SOURCE_REPLACE_FAILED").With("InputName", LogProps.FileName(filePath)).With("CleanupState", "SourcePreserved"));
            return sourceIntact
                ? ExecutionResult.PartiallySucceeded(
                    context,
                    results,
                    errorCode: "source-replacement-failed",
                    outputFile: finalOutputFile,
                    outputExists: File.Exists(finalOutputFile),
                    cleanupState: CleanupState.Failed)
                : ExecutionResult.Failed(
                    context,
                    results,
                    errorCode: "source-replacement-failed",
                    outputFile: finalOutputFile,
                    outputExists: File.Exists(finalOutputFile),
                    cleanupState: CleanupState.Failed);
        }

        if (success?.IsSuccess == true && outputReady)
        {
            if (deleteSource && File.Exists(filePath))
            {
                return ExecutionResult.PartiallySucceeded(
                    context,
                    results,
                    errorCode: "source-cleanup-failed",
                    outputFile: finalOutputFile,
                    outputExists: true,
                    cleanupState: CleanupState.Failed);
            }
            return ExecutionResult.Succeeded(
                context,
                results,
                outputFile: finalOutputFile,
                outputExists: true,
                cleanupState: deleteSource ? CleanupState.Completed : CleanupState.NotRequired);
        }
        return ExecutionResult.Failed(
            context,
            results,
            errorCode: "output-missing",
            outputFile: finalOutputFile,
            outputExists: false,
            cleanupState: CleanupState.Completed);
    }
}

