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
    public override string Name => AppConstants.ScriptMetadata.StreamReplName;
    public override string Description => AppConstants.ScriptMetadata.StreamReplDesc;
    public override string Category => AppConstants.ScriptCategory.Containers;
    public override string IconName => "switch";
    public override string[] FileExtensions => AppConstants.VideoContainers.ToArray();
    public override string[] RequiredDependencies => new[] { "ffmpeg", "mkvtoolnix" };
    public override bool UseCustomWidget => true;

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

    public override async Task<List<string>> ExecuteSingleAsync(
        string filePath,
        Dictionary<string, object> settings,
        string? outputPath,
        Action<int, int, string, double?> progressCallback,
        int fileIndex,
        int totalCount)
    {
        ResetCancellation();
        var results = new List<string>();

        LogService.Instance.Info($"Начало подмены дорожек для файла '{Path.GetFileName(filePath)}'", "StreamReplacementScript");

        // 1. Считываем назначения замен из настроек
        var rawReplacements = GetSettingValue<Dictionary<string, object>?>(settings, "replacements", null);
        if (rawReplacements == null || rawReplacements.Count == 0)
        {
            string err = "❌ Ошибка: не назначено ни одной замены для подмены дорожек.";
            LogService.Instance.Error(err, "StreamReplacementScript");
            progressCallback(fileIndex, totalCount, "Ошибка: нет замен", 0.0);
            results.Add(err);
            return results;
        }

        // Парсим замены в типизированный словарь
        var replacements = new Dictionary<int, ReplacementInfo>();
        foreach (var kvp in rawReplacements)
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

        if (replacements.Count == 0)
        {
            string err = "❌ Ошибка: не удалось разобрать назначения замен.";
            LogService.Instance.Error(err, "StreamReplacementScript");
            progressCallback(fileIndex, totalCount, "Ошибка: нет замен", 0.0);
            results.Add(err);
            return results;
        }

        // 2. Зондируем структуру исходного файла
        MediaStructure? structure;
        try
        {
            structure = await MediaProbeService.Instance.ProbeAsync(filePath);
        }
        catch (Exception ex)
        {
            string probeErr = $"❌ Ошибка анализа метаданных файла: {ex.Message}";
            LogService.Instance.Exception(ex, $"Исключение при анализе метаданных для '{filePath}': {ex.Message}", "StreamReplacementScript");
            progressCallback(fileIndex, totalCount, "Ошибка ffprobe", 0.0);
            results.Add(probeErr);
            return results;
        }

        if (structure == null)
        {
            string err = $"❌ ОШИБКА анализа: {Path.GetFileName(filePath)}";
            LogService.Instance.Error(err, "StreamReplacementScript");
            progressCallback(fileIndex, totalCount, "Ошибка ffprobe", 0.0);
            results.Add(err);
            return results;
        }

        // 3. Вычисляем безопасный выходной путь
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        bool isMp4 = ext == ".mp4";
        string targetDir = string.IsNullOrEmpty(outputPath)
            ? Path.GetDirectoryName(filePath) ?? AppContext.BaseDirectory
            : outputPath;

        string stem = Path.GetFileNameWithoutExtension(filePath);
        string targetFile = Path.Combine(targetDir, $"{stem}{ext}");
        string finalOutputFile = GetSafeOutputPath(filePath, targetFile);

        bool overwrite = SettingsManager.Instance.GetSetting("General", "OverwriteExisting", false);
        if (File.Exists(finalOutputFile) && !overwrite)
        {
            string skipExist = $"⏭ ПРОПУСК (файл существует): {Path.GetFileName(finalOutputFile)}";
            LogService.Instance.Info(skipExist, "StreamReplacementScript");
            progressCallback(fileIndex, totalCount, $"Пропущен (существует): {Path.GetFileName(finalOutputFile)}", 100.0);
            results.Add(skipExist);
            return results;
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

        bool success = false;
        try
        {
            if (isMp4)
            {
                LogService.Instance.Info($"Запуск FFmpeg для подмены дорожек в MP4 '{Path.GetFileName(filePath)}'", "StreamReplacementScript");
                
                var ffmpegArgs = PrepareMp4Args(structure.Tracks, replacements);
                
                success = await FFmpegRunner.Instance.RunAsync(
                    inputPath: filePath,
                    outputPath: finalOutputFile,
                    extraArgs: ffmpegArgs,
                    overwrite: overwrite,
                    totalDuration: structure.Duration,
                    onProgress: progress =>
                    {
                        progressCallback(fileIndex, totalCount, $"Сборка MP4 | {progress.Percent:F1}% | Скорость: {(progress.Speed.HasValue ? $"{progress.Speed.Value:F1}x" : "н/д")}", progress.Percent);
                    },
                    cancellationToken: cts.Token
                );
            }
            else
            {
                LogService.Instance.Info($"Запуск mkvmerge для подмены дорожек в MKV '{Path.GetFileName(filePath)}'", "StreamReplacementScript");
                
                var mkvInputs = PrepareMkvInputs(filePath, structure.Tracks, replacements, out var extraArgs);

                success = await MkvmergeRunner.Instance.RunAsync(
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
            string runErr = $"❌ Критическая ошибка при сборке для '{stem}': {ex.Message}";
            LogService.Instance.Exception(ex, $"Исключение в процессе сборки для '{filePath}': {ex.Message}", "StreamReplacementScript");
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
            LogService.Instance.Info(cancelMsg, "StreamReplacementScript");
            results.Add(cancelMsg);
            return results;
        }

        if (success)
        {
            progressCallback(fileIndex, totalCount, "Завершено!", 100.0);
            string successMsg = $"✅ ОБРАБОТАНО: {Path.GetFileName(finalOutputFile)}";
            LogService.Instance.Info(successMsg, "StreamReplacementScript");
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
            string failMsg = $"❌ ОШИБКА сборки файла: {Path.GetFileName(filePath)}";
            LogService.Instance.Error(failMsg, "StreamReplacementScript");
            results.Add(failMsg);
        }

        return results;
    }

    /// <summary>
    /// Формирует аргументы FFmpeg для подмены дорожек в MP4.
    /// </summary>
    private List<string> PrepareMp4Args(List<MediaTrack> streams, Dictionary<int, ReplacementInfo> replacements)
    {
        var extraArgs = new List<string>();
        var extraInputs = new List<string>();
        int inputIdx = 1;
        int outIdx = 0;

        foreach (var stream in streams)
        {
            int sid = stream.TrackId;
            if (replacements.TryGetValue(sid, out var rep))
            {
                LogService.Instance.Info($"MP4: Замена оригинального потока #{sid} на '{Path.GetFileName(rep.Path)}' (ID {rep.SrcId})", "StreamReplacementScript");
                extraInputs.Add(rep.Path);
                extraArgs.Add("-map");
                extraArgs.Add($"{inputIdx}:{rep.SrcId}");
                
                // Переносим метаданные языка и заголовка
                AddFfmpegMetadata(extraArgs, outIdx, stream);
                inputIdx++;
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
        out List<string> extraArgs)
    {
        var inputs = new List<MkvInputSource>();

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
                LogService.Instance.Info($"MKV: Замена оригинального трека #{originalTrackId} на '{Path.GetFileName(rep.Path)}' (ID {rep.SrcId})", "StreamReplacementScript");
                
                var replacementArgs = BuildReplacementArgs(originalTrack, rep.SrcId);
                inputs.Add(new MkvInputSource(rep.Path, replacementArgs));

                trackMap[originalTrackId] = (currentInputIdx, rep.SrcId);
                currentInputIdx++;
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
