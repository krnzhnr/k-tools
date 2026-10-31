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
/// Информация о файле-замене для конкретной дорожки.
/// </summary>
public sealed class ReplacementInfo
{
    /// <summary>
    /// Путь к файлу-замене.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Идентификатор дорожки внутри файла-замены (0 для простых файлов).
    /// </summary>
    public int SrcId { get; set; }
}

/// <summary>
/// Скрипт для подмены отдельных дорожек в медиа-контейнерах на внешние файлы.
/// Поддерживает сборку MKV через mkvmerge и MP4 через FFmpeg.
/// Все комментарии и сообщения логов выполнены исключительно на русском языке с исчерпывающей полнотой.
/// </summary>
public sealed class StreamReplacementScript : AbstractScript
{
    private readonly IMediaProbeService _mediaProbeService;
    private readonly IFFmpegRunner _ffmpegRunner;
    private readonly IMkvmergeRunner _mkvmergeRunner;

    public StreamReplacementScript(ILogService logService, ISettingsManager settingsManager, IPathManager pathManager, IMediaProbeService mediaProbeService, IFFmpegRunner ffmpegRunner, IMkvmergeRunner mkvmergeRunner)
        : base(logService, settingsManager, pathManager)
    {
        _mediaProbeService = mediaProbeService ?? throw new ArgumentNullException(nameof(mediaProbeService));
        _ffmpegRunner = ffmpegRunner ?? throw new ArgumentNullException(nameof(ffmpegRunner));
        _mkvmergeRunner = mkvmergeRunner ?? throw new ArgumentNullException(nameof(mkvmergeRunner));
    }

    public override string Name => AppConstants.ScriptMetadata.StreamReplName;
    public override string Description => AppConstants.ScriptMetadata.StreamReplDesc;
    public override string Category => AppConstants.ScriptCategory.Containers;
    public override string IconName => AppConstants.ScriptIcons.StreamReplacement;
    public override string[] FileExtensions => AppConstants.VideoContainers.ToArray();
    public override string[] RequiredDependencies => new[] { "ffmpeg", "mkvtoolnix" };
    public override bool UseCustomWidget => true;

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

        _logService.Write("script.stream_replacement.started", LogLevel.Debug, LogStatus.Running, $"Начата подмена дорожек файла '{LogProps.FileName(filePath)}'", source: Name, properties: LogProps.Create("InputName", LogProps.FileName(filePath)));

        // 1. Считываем назначения замен из настроек
        var rawReplacements = GetSettingValue<Dictionary<string, object>?>(settings, "replacements", null);

        var replacements = new Dictionary<int, ReplacementInfo>();

        if (rawReplacements != null && rawReplacements.TryGetValue(filePath, out var fileRepsObj) && fileRepsObj is IDictionary<string, object> fileReps)
        {
            // Парсим замены в типизированный словарь для текущего файла
            foreach (var kvp in fileReps)
            {
                if (int.TryParse(kvp.Key, out int trackId))
                {
                    string? path = null;
                    int srcId = 0;

                    if (kvp.Value is System.Text.Json.JsonElement elem)
                    {
                        if (elem.TryGetProperty("path", out var pathProp)) path = pathProp.GetString();
                        if (elem.TryGetProperty("src_id", out var srcIdProp)) srcId = srcIdProp.GetInt32();
                    }
                    else if (kvp.Value is Dictionary<string, object> dict)
                    {
                        if (dict.TryGetValue("path", out var pathVal)) path = pathVal?.ToString();
                        if (dict.TryGetValue("src_id", out var srcIdVal)) srcId = Convert.ToInt32(srcIdVal);
                    }

                    if (!string.IsNullOrEmpty(path))
                    {
                        replacements[trackId] = new ReplacementInfo { Path = path, SrcId = srcId };
                    }
                }
            }
        }

        if (replacements.Count == 0)
        {
            string err = $"❌ Ошибка: не назначено ни одной замены для подмены дорожек в файле '{Path.GetFileName(filePath)}'.";
            _logService.Write("script.stream_replacement.failed", LogLevel.Error, LogStatus.Failed, err, source: Name, properties: LogProps.Create("ErrorCode", "STREAM_REPLACEMENT_FAILED").With("InputName", LogProps.FileName(filePath)).With("Retryable", true));
            progressCallback(fileIndex, totalCount, "Ошибка: нет замен", 0.0);
            results.Add(err);
            return ExecutionResult.Failed(
                context,
                results,
                errorCode: "no-replacements");
        }

        // 2. Зондируем структуру исходного файла
        MediaStructure? structure;
        try
        {
            structure = await _mediaProbeService.ProbeAsync(filePath);
        }
        catch (Exception ex)
        {
            string probeErr = $"❌ Ошибка анализа метаданных файла: {ex.Message}";
            _logService.Write("script.stream_replacement.probe_failed", LogLevel.Warning, LogStatus.Skipped, $"Метаданные файла '{LogProps.FileName(filePath)}' не прочитаны, подмена дорожек пропущена", ex, Name, properties: LogProps.Create("ErrorCode", "PROBE_FAILED").With("InputName", LogProps.FileName(filePath)));
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
            _logService.Write("script.stream_replacement.failed", LogLevel.Error, LogStatus.Failed, err, source: Name, properties: LogProps.Create("ErrorCode", "STREAM_REPLACEMENT_FAILED").With("InputName", LogProps.FileName(filePath)).With("Retryable", true));
            progressCallback(fileIndex, totalCount, "Ошибка ffprobe", 0.0);
            results.Add(err);
            return ExecutionResult.Failed(
                context,
                results,
                errorCode: "probe-empty",
                retryable: true);
        }

        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        bool isMp4 = ext == ".mp4";
        string targetDir = string.IsNullOrEmpty(outputPath)
            ? Path.GetDirectoryName(filePath) ?? AppContext.BaseDirectory
            : outputPath;

        string stem = Path.GetFileNameWithoutExtension(filePath);
        string targetFile = Path.Combine(targetDir, $"{stem}{ext}");
        string finalOutputFile = GetSafeOutputPath(filePath, targetFile, settings);

        bool overwrite = _settingsManager.GetSetting("General", "OverwriteExisting", false);
        if (File.Exists(finalOutputFile) && !overwrite)
        {
            string skipExist = $"⏭ ПРОПУСК (файл существует): {Path.GetFileName(finalOutputFile)}";
            _logService.Write("script.stream_replacement.output_exists", LogLevel.Info, LogStatus.Skipped, skipExist, source: Name, properties: LogProps.Create("OutputName", LogProps.FileName(filePath)).With("ArtifactExists", true));
            progressCallback(fileIndex, totalCount, $"Пропущен (существует): {Path.GetFileName(finalOutputFile)}", 100.0);
            results.Add(skipExist);
            return ExecutionResult.Skipped(
                context,
                results,
                errorCode: "output-exists",
                outputFile: finalOutputFile,
                outputExists: true);
        }

        progressCallback(fileIndex, totalCount, $"Сборка {stem}...", 0.0);

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
            if (isMp4)
            {
                _logService.Write("script.stream_replacement.ffmpeg_started", LogLevel.Debug, LogStatus.Running, $"Запущен FFmpeg для подмены дорожек в MP4 '{LogProps.FileName(filePath)}'", source: Name, properties: LogProps.Create("Tool", "ffmpeg").With("InputName", LogProps.FileName(filePath)).With("Container", "mp4"));

                var ffmpegArgs = PrepareMp4Args(structure.Tracks, replacements, out int replacedCount);

                if (replacedCount == 0)
                {
                    string err = $"❌ Ошибка: ни одна из назначенных замен не была передана в финальную команду для '{Path.GetFileName(filePath)}'.";
                    _logService.Write("script.stream_replacement.failed", LogLevel.Error, LogStatus.Failed, err, source: Name, properties: LogProps.Create("ErrorCode", "STREAM_REPLACEMENT_FAILED").With("InputName", LogProps.FileName(filePath)).With("Retryable", true));
                    progressCallback(fileIndex, totalCount, "Ошибка: 0 замен", 0.0);
                    results.Add(err);
                    return ExecutionResult.Failed(
                        context,
                        results,
                        errorCode: "no-effective-replacements");
                }

                success = await _ffmpegRunner.RunAsync(
                    inputPath: filePath,
                    outputPath: finalOutputFile,
                    extraArgs: ffmpegArgs,
                    overwrite: overwrite,
                    totalDuration: structure.Duration,
                    onProgress: progress =>
                    {
                        progressCallback(fileIndex, totalCount, $"Сборка MP4 | {progress.Percent:F1}% | Скорость: {(progress.Speed.HasValue ? $"{progress.Speed.Value:F1}x" : "н/д")}", progress.Percent, progress.Fps, progress.Bitrate);
                    },
                    cancellationToken: cts.Token
                );
            }
            else
            {
                _logService.Write("script.stream_replacement.mkvmerge_started", LogLevel.Debug, LogStatus.Running, $"Запущен mkvmerge для подмены дорожек в MKV '{LogProps.FileName(filePath)}'", source: Name, properties: LogProps.Create("Tool", "mkvmerge").With("InputName", LogProps.FileName(filePath)).With("Container", "mkv"));

                var mkvInputs = PrepareMkvInputs(filePath, structure.Tracks, replacements, out var extraArgs, out int replacedCount);

                if (replacedCount == 0)
                {
                    string err = $"❌ Ошибка: ни одна из назначенных замен не была передана в финальную команду для '{Path.GetFileName(filePath)}'.";
                    _logService.Write("script.stream_replacement.failed", LogLevel.Error, LogStatus.Failed, err, source: Name, properties: LogProps.Create("ErrorCode", "STREAM_REPLACEMENT_FAILED").With("InputName", LogProps.FileName(filePath)).With("Retryable", true));
                    progressCallback(fileIndex, totalCount, "Ошибка: 0 замен", 0.0);
                    results.Add(err);
                    return ExecutionResult.Failed(
                        context,
                        results,
                        errorCode: "no-effective-replacements");
                }

                success = await _mkvmergeRunner.RunAsync(
                    outputPath: finalOutputFile,
                    inputs: mkvInputs,
                    extraArgs: extraArgs,
                    onProgress: progress =>
                    {
                        progressCallback(fileIndex, totalCount, $"Сборка MKV | {progress:F1}%", progress);
                    },
                    cancellationToken: cts.Token
                );
            }
        }
        catch (Exception ex)
        {
            string runErr = $"Критическая ошибка при сборке для '{stem}'";
            _logService.Write("script.stream_replacement.build_failed", LogLevel.Error, LogStatus.Failed, $"Сборка с подменой дорожек для '{LogProps.FileName(filePath)}' не выполнена", ex, Name, properties: LogProps.Create("ErrorCode", "STREAM_REPLACEMENT_FAILED").With("InputName", LogProps.FileName(filePath)).With("Retryable", true));
            results.Add(runErr);
        }
        finally
        {
            cts.Cancel();
            await cancelMonitorTask;
        }

        // 4. Обработка результатов завершения
        if (IsCancelled)
        {
            CleanupIfCancelled(finalOutputFile);
            string cancelMsg = $"⚠ Обработка отменена пользователем: {Path.GetFileName(finalOutputFile)}";
            _logService.Write("script.stream_replacement.cancelled", LogLevel.Info, LogStatus.Cancelled, cancelMsg, source: Name, properties: LogProps.Create("Reason", "UserRequested").With("CleanupState", "NotStarted"));
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
                _logService.Write("script.stream_replacement.completed", LogLevel.Info, LogStatus.Succeeded, successMsg, source: Name, properties: LogProps.Create("OutputName", LogProps.FileName(filePath)).With("Verified", true));
                results.Add(successMsg);

                if (overwriteSource)
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
                string failMsg = $"❌ ОШИБКА сборки файла: {Path.GetFileName(filePath)}";
                _logService.Write("script.stream_replacement.failed", LogLevel.Error, LogStatus.Failed, failMsg, source: Name, properties: LogProps.Create("ErrorCode", "STREAM_REPLACEMENT_FAILED").With("InputName", LogProps.FileName(filePath)).With("Retryable", true));
                results.Add(failMsg);
            }
        }
        catch (Exception ex)
        {
            await CleanupFailedOutputFileAsync(finalOutputFile);
            string errorMsg = $"❌ Ошибка выполнения скрипта для {Path.GetFileName(filePath)}";
            results.Add(errorMsg);
            _logService.Write("script.stream_replacement.failed", LogLevel.Error, LogStatus.Failed, $"Подмена дорожек для '{stem}' не выполнена", ex, Name, properties: LogProps.Create("ErrorCode", "STREAM_REPLACEMENT_FAILED").With("Retryable", true));
            return ExecutionResult.FromException(
                context,
                ex,
                results,
                errorCode: "replacement-exception",
                outputFile: finalOutputFile,
                outputExists: File.Exists(finalOutputFile),
                cleanupState: CleanupState.Completed);
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

        if (overwriteSource)
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

            string replacementFailMsg = $"❌ Исходный файл не заменён готовым результатом замены дорожек: '{Path.GetFileName(filePath)}', исходник сохранён";
            _logService.Write("script.source.replaced_failed", LogLevel.Error, LogStatus.PartiallySucceeded, replacementFailMsg, source: Name, properties: LogProps.Create("ErrorCode", "SOURCE_REPLACE_FAILED").With("InputName", LogProps.FileName(filePath)).With("CleanupState", "SourcePreserved"));
            return File.Exists(filePath)
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

        bool outputReady = File.Exists(finalOutputFile);
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
    /// Формирует аргументы FFmpeg для подмены дорожек в MP4.
    /// </summary>
    private List<string> PrepareMp4Args(List<MediaTrack> streams, Dictionary<int, ReplacementInfo> replacements, out int replacedCount)
    {
        var extraArgs = new List<string>();
        var extraInputs = new List<string>();
        int inputIdx = 1;
        int outIdx = 0;
        replacedCount = 0;

        foreach (var stream in streams)
        {
            int sid = stream.TrackId;
            if (replacements.TryGetValue(sid, out var rep))
            {
                _logService.Write("script.stream_replacement.map_applied", LogLevel.Debug, LogStatus.Succeeded, $"Замена медиапотока #{sid} на '{LogProps.FileName(rep.Path)}', идентификатор источника {rep.SrcId}", source: Name, properties: LogProps.Create("Index", sid).With("InputName", LogProps.FileName(rep.Path)).With("Container", "mp4"));
                extraInputs.Add(rep.Path);
                extraArgs.Add("-map");
                extraArgs.Add($"{inputIdx}:{rep.SrcId}");

                // Переносим метаданные языка и заголовка
                AddFfmpegMetadata(extraArgs, outIdx, stream);
                inputIdx++;
                replacedCount++;
            }
            else
            {
                extraArgs.Add("-map");
                extraArgs.Add($"0:{sid}");
            }
            outIdx++;
        }

        extraArgs.Add("-c");
        extraArgs.Add("copy");

        var inputArgs = new List<string>();
        foreach (var inp in extraInputs)
        {
            inputArgs.Add("-i");
            inputArgs.Add($"\"{inp}\"");
        }

        return inputArgs.Concat(extraArgs).ToList();
    }

    private static void AddFfmpegMetadata(List<string> args, int streamIdx, MediaTrack track)
    {
        if (!string.IsNullOrEmpty(track.Language) && track.Language != "und")
        {
            args.Add($"-metadata:s:{streamIdx}");
            args.Add($"language={track.Language}");
        }
        if (!string.IsNullOrEmpty(track.Name))
        {
            args.Add($"-metadata:s:{streamIdx}");
            args.Add($"title=\"{track.Name}\"");
        }

        var dispositions = new List<string>();
        if (track.IsDefault) dispositions.Add("default");
        if (track.IsForced) dispositions.Add("forced");
        if (track.IsHearingImpaired) dispositions.Add("hearing_impaired");
        if (track.IsCommentary) dispositions.Add("comment");
        if (track.IsOriginal) dispositions.Add("original");

        if (dispositions.Count > 0)
        {
            args.Add($"-disposition:s:{streamIdx}");
            args.Add(string.Join("+", dispositions));
        }
    }

    /// <summary>
    /// Формирует входные источники и аргументы mkvmerge для сборки MKV.
    /// </summary>
    private List<MkvInputSource> PrepareMkvInputs(
        string containerPath,
        List<MediaTrack> allTracks,
        Dictionary<int, ReplacementInfo> replacements,
        out List<string> extraArgs,
        out int replacedCount)
    {
        var inputs = new List<MkvInputSource>();
        replacedCount = 0;

        // Ограничиваем оригинальный контейнер только незаменяемыми дорожками
        var containerArgs = BuildContainerTracksArgs(allTracks, replacements.Keys.ToHashSet());
        inputs.Add(new MkvInputSource(containerPath, containerArgs));

        // Карта: оригинальный_track_id -> (номер_входа, track_id_во_входе)
        var trackMap = allTracks.ToDictionary(t => t.TrackId, t => (0, t.TrackId));

        int currentInputIdx = 1;
        foreach (var kvp in replacements.OrderBy(k => k.Key))
        {
            int originalTrackId = kvp.Key;
            var rep = kvp.Value;

            var originalTrack = allTracks.FirstOrDefault(t => t.TrackId == originalTrackId);
            if (originalTrack != null)
            {
                _logService.Write("script.stream_replacement.map_applied", LogLevel.Debug, LogStatus.Succeeded, $"Замена дорожки #{originalTrackId} на '{LogProps.FileName(rep.Path)}', идентификатор источника {rep.SrcId}", source: Name, properties: LogProps.Create("Index", originalTrackId).With("InputName", LogProps.FileName(rep.Path)).With("Container", "mkv"));

                var replacementArgs = BuildReplacementArgs(originalTrack, rep.SrcId);
                inputs.Add(new MkvInputSource(rep.Path, replacementArgs));

                trackMap[originalTrackId] = (currentInputIdx, rep.SrcId);
                currentInputIdx++;
                replacedCount++;
            }
        }

        // Вычисляем track-order на основе исходного порядка треков
        var orderParts = new List<string>();
        foreach (var t in allTracks)
        {
            if (trackMap.TryGetValue(t.TrackId, out var mapped))
            {
                orderParts.Add($"{mapped.Item1}:{mapped.Item2}");
            }
        }

        extraArgs = new List<string>
        {
            "--track-order",
            string.Join(",", orderParts)
        };

        return inputs;
    }

    private static List<string> BuildContainerTracksArgs(List<MediaTrack> allTracks, HashSet<int> replacedIds)
    {
        var args = new List<string>();

        // Видео
        var keepVideo = allTracks.Where(t => t.TrackType == "video" && !replacedIds.Contains(t.TrackId)).Select(t => t.TrackId).ToList();
        if (allTracks.Any(t => t.TrackType == "video"))
        {
            if (keepVideo.Count == 0) args.Add("--no-video");
            else
            {
                args.Add("--video-tracks");
                args.Add(string.Join(",", keepVideo));
            }
        }

        // Аудио
        var keepAudio = allTracks.Where(t => t.TrackType == "audio" && !replacedIds.Contains(t.TrackId)).Select(t => t.TrackId).ToList();
        if (allTracks.Any(t => t.TrackType == "audio"))
        {
            if (keepAudio.Count == 0) args.Add("--no-audio");
            else
            {
                args.Add("--audio-tracks");
                args.Add(string.Join(",", keepAudio));
            }
        }

        // Субтитры
        var keepSubs = allTracks.Where(t => t.TrackType == "subtitles" && !replacedIds.Contains(t.TrackId)).Select(t => t.TrackId).ToList();
        if (allTracks.Any(t => t.TrackType == "subtitles"))
        {
            if (keepSubs.Count == 0) args.Add("--no-subtitles");
            else
            {
                args.Add("--subtitle-tracks");
                args.Add(string.Join(",", keepSubs));
            }
        }

        return args;
    }

    private static List<string> BuildReplacementArgs(MediaTrack track, int srcId)
    {
        var args = new List<string>();

        // Выбираем только одну нужную дорожку
        if (track.TrackType == "video")
        {
            args.Add("--video-tracks");
            args.Add(srcId.ToString());
            args.Add("--no-audio");
            args.Add("--no-subtitles");
        }
        else if (track.TrackType == "audio")
        {
            args.Add("--audio-tracks");
            args.Add(srcId.ToString());
            args.Add("--no-video");
            args.Add("--no-subtitles");
        }
        else if (track.TrackType == "subtitles")
        {
            args.Add("--subtitle-tracks");
            args.Add(srcId.ToString());
            args.Add("--no-video");
            args.Add("--no-audio");
        }

        args.Add("--no-chapters");
        args.Add("--no-global-tags");
        args.Add("--no-track-tags");
        args.Add("--no-attachments");

        // Перенос метаданных
        if (!string.IsNullOrEmpty(track.Language) && track.Language != "und")
        {
            args.Add("--language");
            args.Add($"{srcId}:{track.Language}");
        }
        if (!string.IsNullOrEmpty(track.Name))
        {
            args.Add("--track-name");
            args.Add($"\"{srcId}:{track.Name}\"");
        }

        // Перенос флагов
        args.Add("--default-track");
        args.Add($"{srcId}:{(track.IsDefault ? "yes" : "no")}");

        args.Add("--forced-display-flag");
        args.Add($"{srcId}:{(track.IsForced ? "yes" : "no")}");

        if (track.IsHearingImpaired)
        {
            args.Add("--hearing-impaired-flag");
            args.Add($"{srcId}:yes");
        }
        if (track.IsCommentary)
        {
            args.Add("--commentary-flag");
            args.Add($"{srcId}:yes");
        }
        if (track.IsOriginal)
        {
            args.Add("--original-flag");
            args.Add($"{srcId}:yes");
        }
        if (track.IsVisualImpaired)
        {
            args.Add("--visual-impaired-flag");
            args.Add($"{srcId}:yes");
        }

        return args;
    }
}
