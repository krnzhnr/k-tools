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
/// Скрипт для фильтрации и управления внутренними потоками медиафайлов (видео, аудио, субтитры).
/// Позволяет сохранять только выбранные или удалять нежелательные дорожки из контейнеров MKV и MP4.
/// Все комментарии, логирование событий и XML-документация выполнены исключительно на русском языке.
/// </summary>
public sealed class StreamManagementScript : AbstractScript
{
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
    public override string IconName => "list";

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

    /// <summary>
    /// Асинхронно выполняет фильтрацию дорожек для отдельного файла.
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

        LogService.Instance.Info($"Начало управления потоками для файла '{Path.GetFileName(filePath)}'", "StreamManagementScript");

        // 1. Считываем выбранные пользователем дорожки для текущего файла
        var tracksPerFile = GetSettingValue<Dictionary<string, List<int>>?>(settings, "selected_tracks_per_file", null);
        List<int>? selectedTrackIds = null;
        tracksPerFile?.TryGetValue(filePath, out selectedTrackIds);

        if (selectedTrackIds == null || selectedTrackIds.Count == 0)
        {
            string skipMsg = $"踩 ПРОПУСК (нет выбранных дорожек): {Path.GetFileName(filePath)}";
            LogService.Instance.Info(skipMsg, "StreamManagementScript");
            progressCallback(fileIndex, totalCount, $"Пропуск (нет выбора): {Path.GetFileName(filePath)}", 100.0);
            results.Add(skipMsg);
            return results;
        }

        // 2. Выполняем зондирование структуры файла
        MediaStructure? structure;
        try
        {
            structure = await MediaProbeService.Instance.ProbeAsync(filePath);
        }
        catch (Exception ex)
        {
            string probeErr = $"❌ Ошибка анализа метаданных файла: {ex.Message}";
            LogService.Instance.Exception(ex, $"Исключение при анализе метаданных для '{filePath}': {ex.Message}", "StreamManagementScript");
            progressCallback(fileIndex, totalCount, "Ошибка ffprobe", 0.0);
            results.Add(probeErr);
            return results;
        }

        if (structure == null)
        {
            string err = $"❌ ОШИБКА анализа: {Path.GetFileName(filePath)}";
            LogService.Instance.Error(err, "StreamManagementScript");
            progressCallback(fileIndex, totalCount, "Ошибка ffprobe", 0.0);
            results.Add(err);
            return results;
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
        LogService.Instance.Info($"Определено к сохранению {keptTracks.Count} из {allTrackIds.Count} дорожек.", "StreamManagementScript");

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
        string finalOutputFile = GetSafeOutputPath(filePath, targetFile);

        // Проверка флага перезаписи существующего файла
        bool overwrite = SettingsManager.Instance.GetSetting("General", "OverwriteExisting", false);
        if (File.Exists(finalOutputFile) && !overwrite)
        {
            string skipExist = $"⏭ ПРОПУСК (файл существует): {Path.GetFileName(finalOutputFile)}";
            LogService.Instance.Info(skipExist, "StreamManagementScript");
            progressCallback(fileIndex, totalCount, $"Пропуск (существует): {Path.GetFileName(finalOutputFile)}", 100.0);
            results.Add(skipExist);
            return results;
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

        bool success = false;
        try
        {
            if (useFfmpeg)
            {
                LogService.Instance.Info($"Запуск FFmpeg для фильтрации дорожек '{Path.GetFileName(filePath)}' в '{Path.GetFileName(finalOutputFile)}'", "StreamManagementScript");
                success = await FFmpegRunner.Instance.RunAsync(
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
                LogService.Instance.Info($"Запуск mkvmerge для фильтрации дорожек '{Path.GetFileName(filePath)}' в '{Path.GetFileName(finalOutputFile)}'", "StreamManagementScript");
                var mkvmergeArgs = BuildTrackArgs(structure.Tracks, keepIds);

                var mkvInputs = new List<MkvInputSource>
                {
                    new MkvInputSource(filePath, mkvmergeArgs)
                };

                progressCallback(fileIndex, totalCount, "Фильтрация (mkvmerge)...", 30.0);

                success = await MkvmergeRunner.Instance.RunAsync(
                    outputPath: finalOutputFile,
                    inputs: mkvInputs,
                    cancellationToken: cts.Token
                );
            }
        }
        catch (Exception ex)
        {
            string runErr = $"❌ Критическая ошибка при обработке потоков для '{stem}': {ex.Message}";
            LogService.Instance.Exception(ex, $"Исключение в процессе фильтрации для '{filePath}': {ex.Message}", "StreamManagementScript");
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
            LogService.Instance.Info(cancelMsg, "StreamManagementScript");
            results.Add(cancelMsg);
            return results;
        }

        try
        {
            if (success)
            {
                progressCallback(fileIndex, totalCount, "Завершено!", 100.0);
                string successMsg = $"✅ ОБРАБОТАНО: {Path.GetFileName(finalOutputFile)}";
                LogService.Instance.Info(successMsg, "StreamManagementScript");
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
                string failMsg = $"❌ ОШИБКА обработки файла: {Path.GetFileName(filePath)}";
                LogService.Instance.Error(failMsg, "StreamManagementScript");
                results.Add(failMsg);
            }
        }
        catch (Exception ex)
        {
            CleanupFailedOutputFile(finalOutputFile);
            string errorMsg = $"❌ Ошибка выполнения скрипта для {Path.GetFileName(filePath)}: {ex.Message}";
            results.Add(errorMsg);
            LogService.Instance.Exception(ex, $"Ошибка при выполнении фильтрации потоков для '{stem}': {ex.Message}", "StreamManagementScript");
        }

        return results;
    }

    /// <summary>
    /// Безопасно сопоставляет имя кодека с расширением сырого потока из констант.
    /// </summary>
    private static string GetRawExtension(string codec, string defaultExt)
    {
        if (AppConstants.RawExtensions.TryGetValue(codec, out var ext))
        {
            return ext;
        }
        return defaultExt;
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
