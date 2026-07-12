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
    IMediaProbeService mediaProbeService)
    : AbstractScript(logService, settingsManager, pathManager)
{
    private readonly IMkvmergeRunner _mkvmergeRunner = mkvmergeRunner ?? throw new System.ArgumentNullException(nameof(mkvmergeRunner));
    private readonly IMediaProbeService _mediaProbeService = mediaProbeService ?? throw new System.ArgumentNullException(nameof(mediaProbeService));

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
    public override string IconName => "add";

    /// <summary>
    /// Поддерживаемые расширения медиафайлов для добавления в очередь.
    /// Включает видео-контейнеры, аудио-потоки и файлы субтитров.
    /// </summary>
    public override string[] FileExtensions => [..AppConstants.VideoContainers, ..AppConstants.AudioContainers, ..AppConstants.AudioStreams, ..AppConstants.SubtitleExtensions];

    /// <summary>
    /// Обязательные бинарные зависимости скрипта.
    /// </summary>
    public override string[] RequiredDependencies => ["mkvtoolnix"];

    /// <summary>
    /// Декларативная схема настроек скрипта.
    /// </summary>
    public override List<SettingField> SettingsSchema =>
    [
        new SettingField(
            "subs_title",
            "Заголовок субтитров",
            SettingType.Text,
            "[Надписи]",
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
        string subsTitle = GetSettingValue(settings, "subs_title", "[Надписи]");
        bool cleanTracks = GetSettingValue(settings, "clean_tracks", true);
        bool positionBeforeBuiltin = GetSettingValue(settings, "position_before_builtin", false);

        // 3. Сканируем папку на наличие сопутствующих аудио- и субтитровых файлов с тем же именем (stem)
        string? audioPath = null;
        string? subsPath = null;

        // 3a. Сканируем папку видеофайла на наличие сопутствующих файлов с тем же stem
        if (!string.IsNullOrEmpty(directory))
        {
            try
            {
                var siblingFiles = Directory.GetFiles(directory, $"{stem}.*");
                foreach (var sibling in siblingFiles)
                {
                    string siblingExt = Path.GetExtension(sibling).ToLowerInvariant();
                    if (siblingExt == ext)
                    {
                        continue; // Пропускаем сам видеофайл
                    }

                    if (audioPath == null && (AppConstants.AudioContainers.Contains(siblingExt) || AppConstants.AudioStreams.Contains(siblingExt)))
                    {
                        audioPath = sibling;
                        _logService.Info($"Найден сопутствующий аудиофайл: '{Path.GetFileName(sibling)}'", "MkvAssemblyScript");
                    }
                    else if (subsPath == null && AppConstants.SubtitleExtensions.Contains(siblingExt))
                    {
                        subsPath = sibling;
                        _logService.Info($"Найден сопутствующий файл субтитров: '{Path.GetFileName(sibling)}'", "MkvAssemblyScript");
                    }
                }
            }
            catch (System.Exception ex)
            {
                string scanErr = $"❌ Ошибка сканирования папки на наличие сопутствующих файлов: {ex.Message}";
                _logService.Exception(ex, scanErr, "MkvAssemblyScript");
                results.Add(scanErr);
                return results;
            }
        }

        // 3b. Дополнительно проверяем очередь файлов на наличие сопутствующих файлов из других директорий
        if (audioPath == null || subsPath == null)
        {
            foreach (var queueItem in FilesQueue)
            {
                if (queueItem.FilePath == filePath)
                {
                    continue; // Пропускаем сам видеофайл
                }

                string qExt = Path.GetExtension(queueItem.FilePath).ToLowerInvariant();
                string qStem = Path.GetFileNameWithoutExtension(queueItem.FilePath);

                if (qStem.Equals(stem, System.StringComparison.OrdinalIgnoreCase))
                {
                    if (audioPath == null && (AppConstants.AudioContainers.Contains(qExt) || AppConstants.AudioStreams.Contains(qExt)))
                    {
                        audioPath = queueItem.FilePath;
                        _logService.Info($"Найден сопутствующий аудиофайл в очереди: '{Path.GetFileName(queueItem.FilePath)}'", "MkvAssemblyScript");
                    }
                    else if (subsPath == null && AppConstants.SubtitleExtensions.Contains(qExt))
                    {
                        subsPath = queueItem.FilePath;
                        _logService.Info($"Найден сопутствующий файл субтитров в очереди: '{Path.GetFileName(queueItem.FilePath)}'", "MkvAssemblyScript");
                    }
                }
            }
        }

        // 4. Формирование путей назначения
        string targetDir = string.IsNullOrEmpty(outputPath)
            ? directory
            : outputPath;

        string targetFile = Path.Combine(targetDir, $"{stem}.mkv");
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

        // 6. Формирование аргументов входных файлов для mkvmerge
        List<MkvInputSource> mkvInputs = [];

        // Настройка видео-источника
        List<string> videoArgs = [];
        if (cleanTracks)
        {
            if (audioPath != null)
            {
                videoArgs.Add("--no-audio");
            }
            if (subsPath != null)
            {
                videoArgs.Add("--no-subtitles");
            }
            videoArgs.Add("--no-global-tags");
            videoArgs.Add("--no-track-tags");
        }
        mkvInputs.Add(new MkvInputSource(filePath, videoArgs));

        // Настройка внешнего аудио-источника (если найден)
        if (audioPath != null)
        {
            mkvInputs.Add(new MkvInputSource(audioPath,
            [
                "--audio-tracks", "0",
                "--language", "0:rus",
                "--default-track", "0:yes",
                "--forced-display-flag", "0:yes"
            ]));
        }

        // Настройка внешнего источника субтитров (если найден)
        if (subsPath != null)
        {
            mkvInputs.Add(new MkvInputSource(subsPath,
            [
                "--subtitle-tracks", "0",
                "--language", "0:rus",
                "--track-name", $"\"0:{subsTitle}\"",
                "--default-track", "0:yes",
                "--forced-display-flag", "0:yes"
            ]));
        }

        // Вызов кастомного порядка дорожек, если требуется
        List<string>? extraArgs = null;
        if (!cleanTracks && (audioPath != null || subsPath != null))
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
                        if (audioPath != null)
                        {
                            orderParts.Add($"{nextInputIdx}:0");
                            nextInputIdx++;
                        }
                        if (subsPath != null)
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
                        if (audioPath != null)
                        {
                            orderParts.Add($"{nextInputIdx}:0");
                            nextInputIdx++;
                        }
                        if (subsPath != null)
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

    public override string GetOutputExtension(string inputPath)
    {
        return ".mkv";
    }
}
