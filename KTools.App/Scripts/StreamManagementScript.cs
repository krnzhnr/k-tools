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
/// Скрипт для фильтрации и управления внутренними потоками медиафайлов (видео, аудио, субтитры).
/// Позволяет сохранять только выбранные или удалять нежелательные дорожки из контейнеров MKV и MP4.
/// Все комментарии, логирование событий и XML-документация выполнены исключительно на русском языке.
/// </summary>
public sealed class StreamManagementScript : AbstractScript
{
    private readonly IMediaProbeService _mediaProbeService;
    private readonly IFFmpegRunner _ffmpegRunner;
    private readonly IMkvmergeRunner _mkvmergeRunner;

    public StreamManagementScript(ILogService logService, ISettingsManager settingsManager, IPathManager pathManager, IMediaProbeService mediaProbeService, IFFmpegRunner ffmpegRunner, IMkvmergeRunner mkvmergeRunner)
        : base(logService, settingsManager, pathManager)
    {
        _mediaProbeService = mediaProbeService ?? throw new ArgumentNullException(nameof(mediaProbeService));
        _ffmpegRunner = ffmpegRunner ?? throw new ArgumentNullException(nameof(ffmpegRunner));
        _mkvmergeRunner = mkvmergeRunner ?? throw new ArgumentNullException(nameof(mkvmergeRunner));
    }

    /// <summary>
    /// Русское название скрипта.
    /// </summary>
    public override string Name => AppConstants.ScriptMetadata.StreamMgrName;

    /// <summary>
    /// Описание назначения скрипта на русском языке.
    /// </summary>
    public override string Description => AppConstants.ScriptMetadata.StreamMgrDesc;

    /// <summary>
    /// Категория медиаобработки.
    /// </summary>
    public override string Category => AppConstants.ScriptCategory.Containers;

    /// <summary>
    /// Название иконки для UI.
    /// </summary>
    public override string IconName => AppConstants.ScriptIcons.StreamManagement;

    /// <summary>
    /// Список поддерживаемых форматов файлов (медиа-контейнеры).
    /// </summary>
    public override string[] FileExtensions => AppConstants.VideoContainers.ToArray();

    /// <summary>
    /// Зависимости от внешних утилит.
    /// </summary>
    public override string[] RequiredDependencies => new[] { "ffmpeg", "mkvtoolnix" };

    /// <summary>
    /// Скрипт требует дерево выбора дорожек в интерфейсе приложения.
    /// </summary>
    public override bool UseCustomWidget => true;

    /// <summary>
    /// Декларативная схема параметров настроек скрипта.
    /// </summary>
    public override List<SettingField> SettingsSchema => new()
    {
        new SettingField(
            "mode",
            "Режим работы",
            SettingType.Combo,
            "Удалить выбранные",
            "Режим",
            options: new List<string> { "Удалить выбранные", "Сохранить только выбранные" }
        ),
        new SettingField(
            "use_m4a",
            "Упаковать аудио в M4A (только при сохранении одной дорожки)",
            SettingType.Checkbox,
            false,
            "Аудио"
        ),
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

    /// <summary>
    /// Асинхронно выполняет фильтрацию дорожек для отдельного файла.
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

        _logService.Write("script.stream_management.started", LogLevel.Debug, LogStatus.Running, $"Начато управление дорожками файла '{LogProps.FileName(filePath)}'", source: Name, properties: LogProps.Create("InputName", LogProps.FileName(filePath)));

        // 1. Считываем выбранные пользователем дорожки для текущего файла
        var tracksPerFile = GetSettingValue<Dictionary<string, List<int>>?>(settings, "selected_tracks_per_file", null);
        List<int>? selectedTrackIds = null;
        tracksPerFile?.TryGetValue(filePath, out selectedTrackIds);

        if (selectedTrackIds == null || selectedTrackIds.Count == 0)
        {
            string skipMsg = $"Обработка пропущена: не выбраны дорожки для '{LogProps.FileName(filePath)}'";
            _logService.Write("script.stream_management.skipped", LogLevel.Info, LogStatus.Skipped, skipMsg, source: Name, properties: LogProps.Create("InputName", LogProps.FileName(filePath)));
            progressCallback(fileIndex, totalCount, $"Пропуск (нет выбора): {Path.GetFileName(filePath)}", 100.0);
            results.Add(skipMsg);
            return ExecutionResult.Skipped(
                context,
                results,
                errorCode: "no-track-selection");
        }

        // 2. Выполняем зондирование структуры файла
        MediaStructure? structure;
        try
        {
            structure = await _mediaProbeService.ProbeAsync(filePath);
        }
        catch (Exception ex)
        {
            string probeErr = $"❌ Ошибка анализа метаданных файла: {ex.Message}";
            _logService.Write("script.stream_management.probe_failed", LogLevel.Warning, LogStatus.Skipped, $"Метаданные файла '{LogProps.FileName(filePath)}' не прочитаны, анализ дорожек пропущен", ex, Name, properties: LogProps.Create("ErrorCode", "PROBE_FAILED").With("InputName", LogProps.FileName(filePath)));
            progressCallback(fileIndex, totalCount, "Ошибка ffprobe", 0.0);
            results.Add(probeErr);
            return ExecutionResult.FromException(
                context,
                ex,
                results,
                errorCode: "probe-failed",
                cleanupState: CleanupState.NotStarted);
        }

        if (structure == null)
        {
            string err = $"❌ ОШИБКА анализа: {Path.GetFileName(filePath)}";
            _logService.Write("script.stream_management.failed", LogLevel.Error, LogStatus.Failed, err, source: Name, properties: LogProps.Create("ErrorCode", "STREAM_MANAGEMENT_FAILED").With("InputName", LogProps.FileName(filePath)).With("Retryable", true));
            progressCallback(fileIndex, totalCount, "Ошибка ffprobe", 0.0);
            results.Add(err);
            return ExecutionResult.Failed(
                context,
                results,
                errorCode: "probe-empty",
                retryable: true);
        }

        // 3. Вычисляем ID сохраняемых дорожек на основе режима работы
        string mode = GetSettingValue(settings, "mode", "Удалить выбранные");
        var allTrackIds = structure.Tracks.Select(t => t.TrackId).ToList();
        HashSet<int> keepIds;

        if (mode == "Сохранить только выбранные")
        {
            keepIds = new HashSet<int>(selectedTrackIds);
        }
        else
        {
            keepIds = new HashSet<int>(allTrackIds.Except(selectedTrackIds));
        }

        var keptTracks = structure.Tracks.Where(t => keepIds.Contains(t.TrackId)).ToList();
        _logService.Write("script.stream_management.tracks_selected", LogLevel.Debug, LogStatus.Succeeded, $"К сохранению определено дорожек: {keptTracks.Count} из {allTrackIds.Count}", source: Name, properties: LogProps.Create("Count", keptTracks.Count).With("Total", allTrackIds.Count));

        // 4. Подготавливаем параметры запуска
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        bool isMp4 = ext == ".mp4";
        bool useFfmpeg = isMp4;
        var ffmpegArgs = new List<string>();
        string targetExt = ext;

        var keptTypes = keptTracks.Select(t => t.TrackType).ToHashSet();
        bool useM4a = GetSettingValue(settings, "use_m4a", false);

        if (isMp4)
        {
            foreach (int tid in keepIds.OrderBy(id => id))
            {
                ffmpegArgs.Add("-map");
                ffmpegArgs.Add($"0:{tid}");
            }
            ffmpegArgs.Add("-c");
            ffmpegArgs.Add("copy");

            if (keptTypes.Count == 1 && keptTypes.Contains("audio"))
            {
                if (useM4a)
                {
                    targetExt = ".m4a";
                }
                else
                {
                    targetExt = keptTracks.Count == 1
                        ? GetRawExtension(keptTracks[0].Codec, ".mka")
                        : ".mka";
                }
            }
        }
        else if (keptTracks.Count == 1 && keptTracks[0].TrackType == "audio")
        {
            var track = keptTracks[0];
            useFfmpeg = true;

            if (useM4a)
            {
                targetExt = ".m4a";
            }
            else
            {
                targetExt = GetRawExtension(track.Codec, ".mka");
            }

            var audioTracks = structure.Tracks.Where(t => t.TrackType == "audio").ToList();
            int audioIdx = audioTracks.IndexOf(track);
            if (audioIdx >= 0)
            {
                ffmpegArgs.Add("-map");
                ffmpegArgs.Add($"0:a:{audioIdx}");
                ffmpegArgs.Add("-c");
                ffmpegArgs.Add("copy");
            }
            else
            {
                useFfmpeg = false;
            }
        }
        else if (keptTypes.Count == 1 && keptTypes.Contains("audio"))
        {
            targetExt = ".mka";
        }

        // 5. Вычисляем выходной путь и подготавливаем безопасное имя
        string targetDir = string.IsNullOrEmpty(outputPath)
            ? Path.GetDirectoryName(filePath) ?? AppContext.BaseDirectory
            : outputPath;

        string stem = Path.GetFileNameWithoutExtension(filePath);
        string targetFile = Path.Combine(targetDir, $"{stem}{targetExt}");
        string finalOutputFile = GetSafeOutputPath(filePath, targetFile, settings);

        // Проверка флага перезаписи существующего файла
        bool overwrite = _settingsManager.GetSetting("General", "OverwriteExisting", false);
        if (File.Exists(finalOutputFile) && !overwrite)
        {
            string skipExist = $"⏭ ПРОПУСК (файл существует): {Path.GetFileName(finalOutputFile)}";
            _logService.Write("script.stream_management.output_exists", LogLevel.Info, LogStatus.Skipped, skipExist, source: Name, properties: LogProps.Create("OutputName", LogProps.FileName(finalOutputFile)).With("ArtifactExists", true));
            progressCallback(fileIndex, totalCount, $"Пропуск (существует): {Path.GetFileName(finalOutputFile)}", 100.0);
            results.Add(skipExist);
            return ExecutionResult.Skipped(
                context,
                results,
                errorCode: "output-exists",
                outputFile: finalOutputFile,
                outputExists: true);
        }

        // 6. Запуск процесса сборки
        progressCallback(fileIndex, totalCount, $"Обработка: {stem}...", 0.0);

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
            if (useFfmpeg)
            {
                _logService.Write("script.stream_management.ffmpeg_started", LogLevel.Debug, LogStatus.Running, $"Запущен FFmpeg для фильтрации дорожек '{LogProps.FileName(filePath)}' -> '{LogProps.FileName(finalOutputFile)}'", source: Name, properties: LogProps.Create("Tool", "ffmpeg").With("InputName", LogProps.FileName(filePath)).With("OutputName", LogProps.FileName(finalOutputFile)));
                success = await _ffmpegRunner.RunAsync(
                    inputPath: filePath,
                    outputPath: finalOutputFile,
                    extraArgs: ffmpegArgs,
                    overwrite: overwrite,
                    totalDuration: structure.Duration,
                    onProgress: progress =>
                    {
                        progressCallback(fileIndex, totalCount, $"Обработка (FFmpeg)... {progress.Percent:F1}%", progress.Percent, progress.Fps, progress.Bitrate);
                    },
                    cancellationToken: cts.Token
                );
            }
            else
            {
                _logService.Write("script.stream_management.mkvmerge_started", LogLevel.Debug, LogStatus.Running, $"Запущен mkvmerge для фильтрации дорожек '{LogProps.FileName(filePath)}' -> '{LogProps.FileName(finalOutputFile)}'", source: Name, properties: LogProps.Create("Tool", "mkvmerge").With("InputName", LogProps.FileName(filePath)).With("OutputName", LogProps.FileName(finalOutputFile)));
                var mkvmergeArgs = BuildTrackArgs(structure.Tracks, keepIds);

                var mkvInputs = new List<MkvInputSource>
                {
                    new MkvInputSource(filePath, mkvmergeArgs)
                };

                progressCallback(fileIndex, totalCount, "Фильтрация (mkvmerge)...", 30.0);

                success = await _mkvmergeRunner.RunAsync(
                    outputPath: finalOutputFile,
                    inputs: mkvInputs,
                    cancellationToken: cts.Token
                );
            }
        }
        catch (Exception ex)
        {
            string runErr = $"Критическая ошибка при обработке потоков для '{stem}'";
            _logService.Write("script.stream_management.filter_failed", LogLevel.Error, LogStatus.Failed, $"Фильтрация дорожек для '{LogProps.FileName(filePath)}' не выполнена", ex, Name, properties: LogProps.Create("ErrorCode", "TRACK_FILTER_FAILED").With("InputName", LogProps.FileName(filePath)).With("Retryable", true));
            results.Add(runErr);
        }
        finally
        {
            cts.Cancel();
            await cancelMonitorTask;
        }

        // 7. Обработка результатов
        if (IsCancelled)
        {
            CleanupIfCancelled(finalOutputFile);
            string cancelMsg = $"⚠ Обработка отменена пользователем: {Path.GetFileName(finalOutputFile)}";
            _logService.Write("script.stream_management.cancelled", LogLevel.Info, LogStatus.Cancelled, cancelMsg, source: Name, properties: LogProps.Create("Reason", "UserRequested").With("CleanupState", "NotStarted"));
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
                string successMsg = $"✅ ОБРАБОТАНО: {Path.GetFileName(finalOutputFile)}";
                _logService.Write("script.stream_management.completed", LogLevel.Info, LogStatus.Succeeded, successMsg, source: Name, properties: LogProps.Create("OutputName", LogProps.FileName(finalOutputFile)).With("Verified", true));
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
                string failMsg = $"❌ ОШИБКА обработки файла: {Path.GetFileName(filePath)}";
                _logService.Write("script.stream_management.failed", LogLevel.Error, LogStatus.Failed, failMsg, source: Name, properties: LogProps.Create("ErrorCode", "TRACK_FILTER_FAILED").With("InputName", LogProps.FileName(filePath)).With("Retryable", true));
                results.Add(failMsg);
            }
        }
        catch (Exception ex)
        {
            await CleanupFailedOutputFileAsync(finalOutputFile);
            string errorMsg = $"❌ Ошибка выполнения скрипта для {Path.GetFileName(filePath)}: {ex.Message}";
            results.Add(errorMsg);
            _logService.Write("script.stream_management.failed", LogLevel.Error, LogStatus.Failed, $"Фильтрация дорожек для '{stem}' не выполнена", ex, Name, properties: LogProps.Create("ErrorCode", "TRACK_FILTER_FAILED").With("Retryable", true));
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

            string replacementFailMsg = $"❌ Исходный файл не заменён готовым результатом обработки дорожек: '{Path.GetFileName(filePath)}', исходник сохранён";
            _logService.Write("script.source.replaced_failed", LogLevel.Error, LogStatus.PartiallySucceeded, replacementFailMsg, source: Name, properties: LogProps.Create("ErrorCode", "SOURCE_REPLACE_FAILED").With("InputName", LogProps.FileName(filePath)).With("CleanupState", "SourcePreserved"));
            return File.Exists(filePath)
                ? ExecutionResult.PartiallySucceeded(
                    context,
                    results,
                    errorCode: "source-replacement-failed",
                    outputFile: finalOutputFile,
                    outputExists: outputReady,
                    cleanupState: CleanupState.Failed)
                : ExecutionResult.Failed(
                    context,
                    results,
                    errorCode: "source-replacement-failed",
                    outputFile: finalOutputFile,
                    outputExists: outputReady,
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

    /// <summary>
    /// Безопасно сопоставляет имя кодека с расширением сырого потока из констант.
    /// </summary>
    private static string GetRawExtension(string codec, string defaultExt)
    {
        string ext = AppConstants.ResolveRawExtension(codec);
        return ext.StartsWith(".") && ext != ".bin" ? ext : defaultExt;
    }

    /// <summary>
    /// Генерирует аргументы mkvmerge для фильтрации дорожек по типам.
    /// </summary>
    private static List<string> BuildTrackArgs(List<MediaTrack> allTracks, HashSet<int> keepIds)
    {
        var typeMap = new Dictionary<string, List<int>>
        {
            { "video", new List<int>() },
            { "audio", new List<int>() },
            { "subtitles", new List<int>() }
        };

        foreach (var track in allTracks)
        {
            if (typeMap.ContainsKey(track.TrackType))
            {
                typeMap[track.TrackType].Add(track.TrackId);
            }
        }

        var args = new List<string>();

        var flagMap = new Dictionary<string, string>
        {
            { "video", "--video-tracks" },
            { "audio", "--audio-tracks" },
            { "subtitles", "--subtitle-tracks" }
        };

        var noFlagMap = new Dictionary<string, string>
        {
            { "video", "--no-video" },
            { "audio", "--no-audio" },
            { "subtitles", "--no-subtitles" }
        };

        foreach (var pair in typeMap)
        {
            string trackType = pair.Key;
            var allTypeIds = pair.Value;
            if (allTypeIds.Count == 0) continue;

            var kept = allTypeIds.Where(tid => keepIds.Contains(tid)).ToList();

            if (kept.Count == allTypeIds.Count)
            {
                // Если остаются все дорожки данного типа, mkvmerge сохранит их по умолчанию
                continue;
            }

            if (kept.Count == 0)
            {
                args.Add(noFlagMap[trackType]);
            }
            else
            {
                args.Add(flagMap[trackType]);
                args.Add(string.Join(",", kept));
            }
        }

        return args;
    }
}
