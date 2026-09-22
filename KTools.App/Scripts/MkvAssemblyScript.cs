using KTools_App.Services.Contracts;
// -*- coding: utf-8 -*-
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using KTools_App.Core;
using KTools_App.Infrastructure;

namespace KTools_App.Scripts;

/// <summary>
/// Скрипт сборки медиа-контейнера MKV из отдельных компонентов (видео, аудио, субтитры) на основе утилиты mkvmerge.
/// Сопоставляет входные файлы по базовому имени (stem) и объединяет их в единый файл.
/// Все комментарии, логирование и XML-документация выполнены исключительно на русском языке.
/// </summary>
public sealed class MkvAssemblyScript(
    ILogService logService,
    ISettingsManager settingsManager,
    IPathManager pathManager,
    IMkvmergeRunner mkvmergeRunner,
    IMediaProbeService mediaProbeService,
    IFFmpegRunner ffmpegRunner)
    : AbstractScript(logService, settingsManager, pathManager)
{
    private readonly IMkvmergeRunner _mkvmergeRunner = mkvmergeRunner ?? throw new System.ArgumentNullException(nameof(mkvmergeRunner));
    private readonly IMediaProbeService _mediaProbeService = mediaProbeService ?? throw new System.ArgumentNullException(nameof(mediaProbeService));
    private readonly IFFmpegRunner _ffmpegRunner = ffmpegRunner ?? throw new System.ArgumentNullException(nameof(ffmpegRunner));

    /// <summary>
    /// Русское название скрипта.
    /// </summary>
    public override string Name => AppConstants.ScriptMetadata.MuxerName;

    /// <summary>
    /// Русское описание возможностей скрипта для вывода в UI.
    /// </summary>
    public override string Description => AppConstants.ScriptMetadata.MuxerDesc;

    /// <summary>
    /// Категория медиаобработки.
    /// </summary>
    public override string Category => AppConstants.ScriptCategory.Containers;

    /// <summary>
    /// Название системной Fluent-иконки.
    /// </summary>
    public override string IconName => AppConstants.ScriptIcons.MkvAssembly;

    /// <summary>
    /// Поддерживаемые расширения медиафайлов для добавления в очередь.
    /// Включает видео-контейнеры, аудио-потоки и файлы субтитров.
    /// </summary>
    public override string[] FileExtensions => [..AppConstants.VideoContainers, ..AppConstants.AudioContainers, ..AppConstants.AudioStreams, ..AppConstants.SubtitleExtensions];

    /// <summary>
    /// Обязательные бинарные зависимости скрипта.
    /// </summary>
    public override string[] RequiredDependencies => ["mkvtoolnix", "ffmpeg"];

    /// <summary>
    /// Декларативная схема настроек скрипта.
    /// </summary>
    public override List<SettingField> SettingsSchema =>
    [
        new SettingField(
            "output_container",
            "Формат контейнера",
            SettingType.Combo,
            "MKV",
            "Сборка",
            options: ["MKV", "MP4"]
        ),
        new SettingField(
            "subs_full_title",
            "Заголовок полных субтитров",
            SettingType.Text,
            "Субтитры",
            "Субтитры"
        ),
        new SettingField(
            "subs_signs_title",
            "Заголовок надписей",
            SettingType.Text,
            "Надписи",
            "Субтитры"
        ),
        new SettingField(
            "clean_tracks",
            "Удалить лишние дорожки из источника",
            SettingType.Checkbox,
            true,
            "Сборка"
        ),
        new SettingField(
            "position_before_builtin",
            "Поместить новые дорожки перед встроенными",
            SettingType.Checkbox,
            false,
            "Сборка",
            visibleIfKey: "clean_tracks",
            visibleIfValues: ["False"]
        )
    ];

    /// <summary>
    /// Возвращает только видео-контейнеры из очереди файлов.
    /// Аудио и субтитры обрабатываются как сопутствующие файлы вместе с видео.
    /// </summary>
    public override List<FileQueueItem> GetProcessableFiles(List<FileQueueItem> allFiles)
    {
        return [.. allFiles
            .Where(f => AppConstants.VideoContainers.Contains(
                Path.GetExtension(f.FilePath).ToLowerInvariant()))];
    }

    /// <summary>
    /// Асинхронное выполнение сборки MKV для одного файла.
    /// Если переданный файл не является видеофайлом (например, аудио или субтитры), он пропускается,
    /// так как его обработка происходит совместно с соответствующим видеофайлом.
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
        List<string> results = [];

        string ext = Path.GetExtension(filePath).ToLowerInvariant();

        // 1. Проверяем, является ли текущий файл видео-контейнером.
        // Если это сопутствующий аудиофайл или файл субтитров, мы его пропускаем.
        if (!AppConstants.VideoContainers.Contains(ext))
        {
            string skipMsg = $"[Сборка MKV] Пропуск сопутствующего файла (обрабатывается вместе с видео): '{Path.GetFileName(filePath)}'";
            _logService.Info(skipMsg, "MkvAssemblyScript");
            progressCallback(fileIndex, totalCount, $"Пропуск (сопутствующий файл): {Path.GetFileName(filePath)}", 100.0);
            results.Add($"⏭ ПРОПУСК (сопутствующий файл): {Path.GetFileName(filePath)}");
            return results;
        }

        string stem = Path.GetFileNameWithoutExtension(filePath);
        string directory = Path.GetDirectoryName(filePath) ?? string.Empty;

        _logService.Info($"Начало сборки MKV-контейнера для видеофайла '{Path.GetFileName(filePath)}'", "MkvAssemblyScript");

        // 2. Извлекаем пользовательские настройки
        string subsFullTitle = GetSettingValue(settings, "subs_full_title", "Субтитры");
        string subsSignsTitle = GetSettingValue(settings, "subs_signs_title", "Надписи");
        // Разовая миграция со старых дефолтов ("Полные" / "[Надписи]"): если пользователь
        // значение не менял, подменяем новым и сохраняем, иначе старые дефолты из файла
        // настроек перекрывали бы новые.
        subsFullTitle = MigrateSubsTitle("subs_full_title", subsFullTitle, "Полные", "Субтитры");
        subsSignsTitle = MigrateSubsTitle("subs_signs_title", subsSignsTitle, "[Надписи]", "Надписи");
        bool cleanTracks = GetSettingValue(settings, "clean_tracks", true);
        bool positionBeforeBuiltin = GetSettingValue(settings, "position_before_builtin", false);

        // 3. Поиск сопутствующих аудио- и субтитровых файлов строго в очереди файлов пользователя (FilesQueue).
        // Имя файла — главный источник правды: точное совпадение либо префикс через разделитель
        // (например, "video.dub.mka", "video.en.srt", "video.signs.ass"). К одному видео привязывается N дорожек.
        // Ручная привязка через дроп-зону (MuxPinnedStem) имеет приоритет над автосопоставлением.
        // Роль субтитров (полные/надписи/прочие) определяется суффиксом через MuxTrackTyper.
        // Основная аудиодорожка — явно помеченная (зона "RU Аудио (Осн.)") или первая по имени.
        List<(string Path, bool ExplicitMain)> audioCandidates = [];
        List<(string Path, MuxSubsRole Role)> subsTracks = [];

        foreach (var queueItem in FilesQueue)
        {
            if (queueItem.FilePath == filePath)
            {
                continue; // Пропускаем сам видеофайл
            }

            string qExt = Path.GetExtension(queueItem.FilePath).ToLowerInvariant();
            string qStem = Path.GetFileNameWithoutExtension(queueItem.FilePath);

            if (!MuxGroupMatcher.BelongsToVideo(stem, qStem, queueItem.MuxPinnedStem))
            {
                continue;
            }

            if (AppConstants.AudioContainers.Contains(qExt) || AppConstants.AudioStreams.Contains(qExt))
            {
                if (!audioCandidates.Any(c => string.Equals(c.Path, queueItem.FilePath, StringComparison.OrdinalIgnoreCase)))
                {
                    audioCandidates.Add((queueItem.FilePath, queueItem.MuxAudioMain));
                }
            }
            else if (AppConstants.SubtitleExtensions.Contains(qExt))
            {
                if (!subsTracks.Any(t => string.Equals(t.Path, queueItem.FilePath, StringComparison.OrdinalIgnoreCase)))
                {
                    subsTracks.Add((queueItem.FilePath, MuxTrackTyper.ResolveSubsRole(stem, qStem, queueItem.MuxRoleOverride)));
                }
            }
        }

        // Основная дорожка идёт первой; остальные сортируются по имени.
        audioCandidates.Sort((left, right) =>
        {
            int mainCmp = (left.ExplicitMain ? 0 : 1).CompareTo(right.ExplicitMain ? 0 : 1);
            return mainCmp != 0 ? mainCmp : StringComparer.OrdinalIgnoreCase.Compare(left.Path, right.Path);
        });
        List<string> audioPaths = [.. audioCandidates.Select(c => c.Path)];
        subsTracks.Sort((left, right) =>
        {
            int roleCmp = MuxTrackTyper.GetRoleOrder(left.Role).CompareTo(MuxTrackTyper.GetRoleOrder(right.Role));
            return roleCmp != 0 ? roleCmp : StringComparer.OrdinalIgnoreCase.Compare(left.Path, right.Path);
        });
        List<string> subsPaths = [.. subsTracks.Select(t => t.Path)];

        foreach (string found in audioPaths)
        {
            _logService.Info($"Найден сопутствующий аудиофайл в очереди: '{Path.GetFileName(found)}'", "MkvAssemblyScript");
        }
        foreach (var (subsPath, role) in subsTracks)
        {
            _logService.Info($"Найден сопутствующий файл субтитров в очереди ({MuxTrackTyper.GetRoleLabel(role)}): '{Path.GetFileName(subsPath)}'", "MkvAssemblyScript");
        }

        _logService.Info($"Режим сборки: cleanTracks={cleanTracks} (False — встроенные дорожки сохраняются), внешних аудио: {audioPaths.Count}, субтитров: {subsPaths.Count}", "MkvAssemblyScript");

        // 4. Формирование путей назначения
        string containerChoice = GetSettingValue(settings, "output_container", "MKV");
        bool isMp4 = containerChoice.Equals("MP4", StringComparison.OrdinalIgnoreCase);
        string targetExt = isMp4 ? ".mp4" : ".mkv";

        string targetDir = string.IsNullOrEmpty(outputPath)
            ? directory
            : outputPath;

        string targetFile = Path.Combine(targetDir, $"{stem}{targetExt}");
        string finalOutputFile = GetSafeOutputPath(filePath, targetFile, settings);

        // 5. Проверка существования выходного файла при отключенной перезаписи
        bool overwrite = _settingsManager.GetSetting("General", "OverwriteExisting", false);
        if (File.Exists(finalOutputFile) && !overwrite)
        {
            string skipExist = $"⏭ ПРОПУСК (файл существует): {Path.GetFileName(finalOutputFile)}";
            _logService.Info(skipExist, "MkvAssemblyScript");
            progressCallback(fileIndex, totalCount, $"Пропуск (существует): {Path.GetFileName(finalOutputFile)}", 100.0);
            results.Add(skipExist);
            return results;
        }

        // 5a. Обработка сборки контейнера MP4 (несколько внешних дорожек)
        if (isMp4)
        {
            List<string> mp4AudioPaths = [];
            foreach (string candidate in audioPaths)
            {
                string aExt = Path.GetExtension(candidate).ToLowerInvariant();
                if (aExt == ".flac" || aExt == ".thd" || aExt == ".truehd" || aExt == ".dts" || aExt == ".dtshd")
                {
                    string warnAudio = $"⚠ [Сборка MP4] Внешний аудиофайл '{Path.GetFileName(candidate)}' имеет формат {aExt.TrimStart('.').ToUpperInvariant()}, который не поддерживается контейнером MP4, и будет пропущен.";
                    _logService.Info(warnAudio, "MkvAssemblyScript");
                    results.Add(warnAudio);
                    continue;
                }
                mp4AudioPaths.Add(candidate);
            }

            List<string> mp4SubsPaths = [];
            foreach (string candidate in subsPaths)
            {
                string sExt = Path.GetExtension(candidate).ToLowerInvariant();
                if (sExt == ".ass" || sExt == ".ssa")
                {
                    string warnSub = $"⚠ [Сборка MP4] Субтитры формата ASS/SSA ({Path.GetFileName(candidate)}) не поддерживаются контейнером MP4 и будут пропущены.";
                    _logService.Info(warnSub, "MkvAssemblyScript");
                    results.Add(warnSub);
                    continue;
                }
                mp4SubsPaths.Add(candidate);
            }

            progressCallback(fileIndex, totalCount, $"Сборка MP4: {stem}...", 0.0);

            using var ctsMp4 = new CancellationTokenSource();
            var cancelTaskMp4 = Task.Run(async () =>
            {
                while (!IsCancelled && !ctsMp4.IsCancellationRequested)
                {
                    await Task.Delay(100);
                }
                if (IsCancelled)
                {
                    ctsMp4.Cancel();
                }
            });

            bool mp4Success = false;
            try
            {
                // Внешние входы перечисляются после входа 0 (видео) в порядке: аудио, затем субтитры.
                List<string> mp4InputArgs = [];
                foreach (string externalAudio in mp4AudioPaths)
                {
                    mp4InputArgs.AddRange(["-i", $"\"{externalAudio}\""]);
                }
                foreach (string externalSubs in mp4SubsPaths)
                {
                    mp4InputArgs.AddRange(["-i", $"\"{externalSubs}\""]);
                }

                List<string> extraArgsMp4 = [.. mp4InputArgs, "-c", "copy", "-movflags", "+faststart"];
                extraArgsMp4.Add("-map");
                extraArgsMp4.Add("0:v");

                if (mp4AudioPaths.Count > 0)
                {
                    for (int i = 0; i < mp4AudioPaths.Count; i++)
                    {
                        extraArgsMp4.Add("-map");
                        extraArgsMp4.Add($"{1 + i}:a");
                    }
                }
                else if (!cleanTracks)
                {
                    extraArgsMp4.Add("-map");
                    extraArgsMp4.Add("0:a?");
                }

                if (mp4SubsPaths.Count > 0)
                {
                    int subsInputIdx = 1 + mp4AudioPaths.Count;
                    for (int i = 0; i < mp4SubsPaths.Count; i++)
                    {
                        extraArgsMp4.Add("-map");
                        extraArgsMp4.Add($"{subsInputIdx + i}:s");
                    }
                    extraArgsMp4.Add("-c:s");
                    extraArgsMp4.Add("mov_text");
                }
                else if (!cleanTracks)
                {
                    extraArgsMp4.Add("-sn");
                }

                mp4Success = await _ffmpegRunner.RunAsync(
                    inputPath: filePath,
                    outputPath: finalOutputFile,
                    extraArgs: extraArgsMp4,
                    overwrite: overwrite,
                    onProgress: progress =>
                    {
                        progressCallback(fileIndex, totalCount, $"Сборка MP4 | {progress.Percent:F1}%", progress.Percent);
                    },
                    cancellationToken: ctsMp4.Token
                );
            }
            catch (System.Exception ex)
            {
                string runErr = $"❌ Ошибка при сборке MP4 для '{stem}': {ex.Message}";
                _logService.Exception(ex, runErr, "MkvAssemblyScript");
                results.Add(runErr);
            }
            finally
            {
                ctsMp4.Cancel();
                await cancelTaskMp4;
            }

            if (IsCancelled)
            {
                CleanupIfCancelled(finalOutputFile);
                string cancelMsg = $"⚠ Сборка отменена пользователем: {Path.GetFileName(finalOutputFile)}";
                _logService.Info(cancelMsg, "MkvAssemblyScript");
                results.Add(cancelMsg);
                return results;
            }

            if (mp4Success)
            {
                progressCallback(fileIndex, totalCount, "Сборка завершена!", 100.0);
                string successMsg = $"✅ Собран контейнер MP4: {Path.GetFileName(finalOutputFile)}";
                _logService.Info(successMsg, "MkvAssemblyScript");
                results.Add(successMsg);
            }
            else
            {
                CleanupFailedOutputFile(finalOutputFile);
                string failMsg = $"❌ Ошибка сборки MP4-файла: {Path.GetFileName(finalOutputFile)}";
                _logService.Error(failMsg, "MkvAssemblyScript");
                results.Add(failMsg);
            }

            return results;
        }

        // 6. Формирование аргументов входных файлов для mkvmerge
        List<MkvInputSource> mkvInputs = [];

        // Настройка видео-источника
        List<string> videoArgs = [];
        if (cleanTracks)
        {
            if (audioPaths.Count > 0)
            {
                videoArgs.Add("--no-audio");
            }
            if (subsPaths.Count > 0)
            {
                videoArgs.Add("--no-subtitles");
            }
            videoArgs.Add("--no-global-tags");
            videoArgs.Add("--no-track-tags");
        }
        mkvInputs.Add(new MkvInputSource(filePath, videoArgs));

        // Настройка внешних аудио-источников (если найдены).
        // Первая дорожка — основная (зона "RU Аудио (Осн.)" или первая по имени):
        // флаги default/forced. Язык всех внешних дорожек — русский.
        // Дополнительные различаются заголовком: сначала заголовок из метаданных
        // файла (MediaInfo Title), при его отсутствии — из различающейся части имени файла.
        for (int i = 0; i < audioPaths.Count; i++)
        {
            bool isMain = i == 0;
            string audioStem = Path.GetFileNameWithoutExtension(audioPaths[i]);
            string embeddedTitle = await GetEmbeddedAudioTitleAsync(audioPaths[i]);
            string audioTitle = !string.IsNullOrWhiteSpace(embeddedTitle)
                ? embeddedTitle
                : MuxTrackTyper.InferTrackTitle(stem, audioStem);
            var audioArgs = new List<string>
            {
                "--audio-tracks", "0",
                "--language", "0:rus",
                "--default-track", isMain ? "0:yes" : "0:no",
                "--forced-display-flag", isMain ? "0:yes" : "0:no"
            };
            if (!string.IsNullOrWhiteSpace(audioTitle))
            {
                audioArgs.AddRange(["--track-name", $"\"0:{audioTitle}\""]);
            }
            mkvInputs.Add(new MkvInputSource(audioPaths[i], audioArgs));
        }

        // Настройка внешних источников субтитров (если найдены).
        // Порядок: надписи, полные, прочие. Флаги default/forced получают надписи
        // (первые из них), остальные дорожки добавляются без флагов.
        // Надписи получают отдельный заголовок из настроек.
        bool signsDefaultAssigned = false;
        for (int i = 0; i < subsTracks.Count; i++)
        {
            bool isSignsDefault = subsTracks[i].Role == MuxSubsRole.Signs && !signsDefaultAssigned;
            if (subsTracks[i].Role == MuxSubsRole.Signs)
            {
                signsDefaultAssigned = true;
            }
            string trackTitle = subsTracks[i].Role == MuxSubsRole.Signs ? subsSignsTitle : subsFullTitle;
            var subsArgs = new List<string>
            {
                "--subtitle-tracks", "0",
                "--language", "0:rus",
                "--default-track", isSignsDefault ? "0:yes" : "0:no",
                "--forced-display-flag", isSignsDefault ? "0:yes" : "0:no"
            };
            if (!string.IsNullOrWhiteSpace(trackTitle))
            {
                subsArgs.AddRange(["--track-name", $"\"0:{trackTitle}\""]);
            }
            mkvInputs.Add(new MkvInputSource(subsTracks[i].Path, subsArgs));
        }

        // Вызов кастомного порядка дорожек, если требуется
        List<string>? extraArgs = null;
        if (!cleanTracks && (audioPaths.Count > 0 || subsPaths.Count > 0))
        {
            try
            {
                var mediaStructure = await _mediaProbeService.ProbeAsync(filePath);
                if (mediaStructure != null)
                {
                    List<string> orderParts = [];
                    var videoTrack = mediaStructure.Tracks.FirstOrDefault(t => t.TrackType.Equals("video", System.StringComparison.OrdinalIgnoreCase));
                    if (videoTrack != null)
                    {
                        orderParts.Add($"0:{videoTrack.TrackId}");
                    }

                    int nextInputIdx = 1;

                    // Если новые дорожки позиционируются ПЕРЕД встроенными
                    if (positionBeforeBuiltin)
                    {
                        for (int audioIdx = 0; audioIdx < audioPaths.Count; audioIdx++)
                        {
                            orderParts.Add($"{nextInputIdx}:0");
                            nextInputIdx++;
                        }
                        for (int subsIdx = 0; subsIdx < subsPaths.Count; subsIdx++)
                        {
                            orderParts.Add($"{nextInputIdx}:0");
                            nextInputIdx++;
                        }
                    }

                    // Добавляем все остальные встроенные дорожки
                    foreach (var track in mediaStructure.Tracks)
                    {
                        if (videoTrack != null && track.TrackId == videoTrack.TrackId)
                        {
                            continue; // Видео уже добавлено первым
                        }
                        orderParts.Add($"0:{track.TrackId}");
                    }

                    // Если новые дорожки позиционируются ПОСЛЕ встроенных (по умолчанию)
                    if (!positionBeforeBuiltin)
                    {
                        for (int audioIdx = 0; audioIdx < audioPaths.Count; audioIdx++)
                        {
                            orderParts.Add($"{nextInputIdx}:0");
                            nextInputIdx++;
                        }
                        for (int subsIdx = 0; subsIdx < subsPaths.Count; subsIdx++)
                        {
                            orderParts.Add($"{nextInputIdx}:0");
                            nextInputIdx++;
                        }
                    }

                    extraArgs =
                    [
                        "--track-order",
                        string.Join(",", orderParts)
                    ];
                    _logService.Info($"Сформирован кастомный порядок дорожек (--track-order): {string.Join(",", orderParts)}", "MkvAssemblyScript");
                }
            }
            catch (System.Exception ex)
            {
                _logService.Exception(ex, $"Не удалось построить порядок дорожек: {ex.Message}. Будет использован порядок по умолчанию.", "MkvAssemblyScript");
            }
        }

        // 7. Запуск процесса сборки через MkvmergeRunner с мониторингом отмены
        progressCallback(fileIndex, totalCount, $"Сборка MKV: {stem}...", 0.0);

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
            success = await _mkvmergeRunner.RunAsync(
                finalOutputFile,
                mkvInputs,
                title: stem,
                extraArgs: extraArgs,
                onProgress: progress =>
                {
                    progressCallback(fileIndex, totalCount, $"Сборка MKV | {progress:F1}%", progress);
                },
                cancellationToken: cts.Token
            );
        }
        catch (System.Exception ex)
        {
            string runErr = $"❌ Критическая ошибка при сборке MKV для '{stem}': {ex.Message}";
            _logService.Exception(ex, runErr, "MkvAssemblyScript");
            results.Add(runErr);
        }
        finally
        {
            cts.Cancel();
            await cancelMonitorTask;
        }

        // 8. Обработка завершения и отмены операции
        if (IsCancelled)
        {
            CleanupIfCancelled(finalOutputFile);
            string cancelMsg = $"⚠ Сборка отменена пользователем: {Path.GetFileName(finalOutputFile)}";
            _logService.Info(cancelMsg, "MkvAssemblyScript");
            results.Add(cancelMsg);
            return results;
        }

        try
        {
            if (success)
            {
                progressCallback(fileIndex, totalCount, "Сборка завершена!", 100.0);
                string successMsg = $"✅ Собран контейнер MKV: {Path.GetFileName(finalOutputFile)}";
                _logService.Info(successMsg, "MkvAssemblyScript");
                results.Add(successMsg);
            }
            else
            {
                CleanupFailedOutputFile(finalOutputFile);
                string failMsg = $"❌ Ошибка сборки MKV-файла: {Path.GetFileName(finalOutputFile)}";
                _logService.Error(failMsg, "MkvAssemblyScript");
                results.Add(failMsg);
            }
        }
        catch (System.Exception ex)
        {
            CleanupFailedOutputFile(finalOutputFile);
            string errorMsg = $"❌ Ошибка выполнения скрипта для {Path.GetFileName(filePath)}: {ex.Message}";
            results.Add(errorMsg);
            _logService.Exception(ex, $"Ошибка при выполнении сборки MKV для '{stem}': {ex.Message}", "MkvAssemblyScript");
        }

        return results;
    }

    /// <summary>
    /// Разово мигрирует заголовок субтитров со старого значения по умолчанию на новое,
    /// если пользователь его не менял. Возвращает действующее значение.
    /// </summary>
    private string MigrateSubsTitle(string key, string current, string oldDefault, string newDefault)
    {
        if (!current.Equals(oldDefault, StringComparison.Ordinal))
        {
            return current;
        }

        try
        {
            string groupName = _settingsManager.GetSafeGroupName(Name);
            _settingsManager.SetSetting(groupName, key, newDefault);
        }
        catch (System.Exception ex)
        {
            _logService.Exception(ex, $"Не удалось мигрировать настройку '{key}', использовано значение по умолчанию", "MkvAssemblyScript");
        }

        return newDefault;
    }

    /// <summary>
    /// Извлекает заголовок первой аудиодорожки из метаданных файла (поле Title/Name).
    /// При отсутствии метаданных или ошибке пробы возвращает пустую строку —
    /// вызывающий код использует заголовок из имени файла.
    /// Кавычки вычищаются, так как заголовок подставляется в кавычках в аргументы mkvmerge.
    /// </summary>
    private async Task<string> GetEmbeddedAudioTitleAsync(string audioPath)
    {
        try
        {
            var structure = await _mediaProbeService.ProbeAsync(audioPath);
            var track = structure?.Tracks.FirstOrDefault(t => t.TrackType.Equals("audio", System.StringComparison.OrdinalIgnoreCase));
            string title = track?.Name?.Trim() ?? string.Empty;
            return title.Replace("\"", "'", StringComparison.Ordinal);
        }
        catch (System.Exception ex)
        {
            _logService.Exception(ex, $"Не удалось прочитать заголовок дорожки из '{Path.GetFileName(audioPath)}', будет использован заголовок из имени файла", "MkvAssemblyScript");
            return string.Empty;
        }
    }

    public override string GetOutputExtension(string inputPath)
    {
        return ".mkv";
    }
}
