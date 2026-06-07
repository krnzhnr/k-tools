// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using KTools_App.Core;
using KTools_App.Infrastructure;

namespace KTools_App.Scripts;

/// <summary>
/// Скрипт массового извлечения выбранных дорожек и встроенных шрифтов из медиафайлов.
/// Реализует One-Pass извлечение через FFmpeg для высокой скорости работы.
/// Все комментарии и логирование выполнены исключительно на русском языке.
/// </summary>
public sealed class TrackExtractorScript : AbstractScript
{
    private static readonly Regex SanitizeRegex = new(@"[<>:""/\\|?*]", RegexOptions.Compiled);

    public override string Name => AppConstants.ScriptMetadata.TrackExtrName;
    public override string Description => AppConstants.ScriptMetadata.TrackExtrDesc;
    public override string Category => AppConstants.ScriptCategory.Containers;
    public override string IconName => "download";
    public override string[] FileExtensions => AppConstants.VideoContainers.ToArray();
    public override string[] RequiredDependencies => new[] { "mkvtoolnix", "ffmpeg" };
    public override bool UseCustomWidget => true;

    public override List<SettingField> SettingsSchema => new()
    {
        new SettingField(
            "name_format",
            "Шаблон имени файла",
            SettingType.Combo,
            "{original}_{lang}_{id}",
            "Именование",
            options: new List<string>
            {
                "{original}_{lang}_{id}",
                "{original}_{id}_{lang}",
                "{original}_{lang}"
            }
        ),
        new SettingField(
            "create_subfolders",
            "Создавать отдельную подпапку для каждого файла",
            SettingType.Checkbox,
            false,
            "Папки"
        )
    };

    /// <summary>
    /// Асинхронно обрабатывает один файл: собирает аргументы FFmpeg, выполняет One-Pass извлечение дорожек
    /// и последовательно извлекает встроенные шрифты.
    /// </summary>
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

        LogService.Instance.Info($"Начало извлечения дорожек для файла: {Path.GetFileName(filePath)}", "TrackExtractorScript");

        // 1. Извлекаем выбранные пользователем дорожки и вложения (шрифты)
        var tracksPerFile = GetSettingValue<Dictionary<string, List<int>>?>(settings, "selected_tracks_per_file", null);
        var attachmentsPerFile = GetSettingValue<Dictionary<string, List<int>>?>(settings, "selected_attachments_per_file", null);

        List<int>? selectedTrackIds = null;
        tracksPerFile?.TryGetValue(filePath, out selectedTrackIds);

        List<int>? selectedAttachmentIds = null;
        attachmentsPerFile?.TryGetValue(filePath, out selectedAttachmentIds);

        bool hasTracks = selectedTrackIds != null && selectedTrackIds.Count > 0;
        bool hasAttachments = selectedAttachmentIds != null && selectedAttachmentIds.Count > 0;

        if (!hasTracks && !hasAttachments)
        {
            string skipMsg = $"⏭ Пропущен (нет выбранных дорожек или шрифтов): {Path.GetFileName(filePath)}";
            LogService.Instance.Info(skipMsg, "TrackExtractorScript");
            progressCallback(fileIndex, totalCount, "Пропущен (нет выбора)", 100.0);
            results.Add(skipMsg);
            return results;
        }

        // 2. Получаем структуру метаданных
        var structure = await MediaProbeService.Instance.ProbeAsync(filePath);
        if (structure == null)
        {
            string err = $"❌ Ошибка анализа метаданных файла: {Path.GetFileName(filePath)}";
            LogService.Instance.Error(err, "TrackExtractorScript");
            progressCallback(fileIndex, totalCount, "Ошибка анализа", 0.0);
            results.Add(err);
            return results;
        }

        // 3. Вычисляем выходную директорию
        string baseDir = string.IsNullOrEmpty(outputPath)
            ? Path.GetDirectoryName(filePath) ?? AppContext.BaseDirectory
            : outputPath;

        bool createSubfolders = GetSettingValue(settings, "create_subfolders", false);
        if (createSubfolders)
        {
            string subfolder = Path.GetFileNameWithoutExtension(filePath);
            baseDir = Path.Combine(baseDir, subfolder);
            try
            {
                if (!Directory.Exists(baseDir))
                {
                    Directory.CreateDirectory(baseDir);
                    LogService.Instance.Info($"Создана целевая подпапка: {baseDir}", "TrackExtractorScript");
                }
            }
            catch (Exception ex)
            {
                string err = $"❌ Ошибка создания папки '{baseDir}': {ex.Message}";
                LogService.Instance.Exception(ex, err, "TrackExtractorScript");
                results.Add(err);
                return results;
            }
        }

        string nameFormat = GetSettingValue(settings, "name_format", "{original}_{lang}_{id}");
        bool overwrite = SettingsManager.Instance.GetSetting("General", "OverwriteExisting", false);

        var ffmpegArgs = new List<string>();
        var filesToExtract = new List<string>();
        var extractResults = new List<string>();

        // 4. Определение языковых дубликатов для формирования уникальных суффиксов
        var activeTracks = structure.Tracks.Where(t => selectedTrackIds != null && selectedTrackIds.Contains(t.TrackId)).ToList();
        var langCounts = activeTracks
            .Where(t => !string.IsNullOrEmpty(t.Language) && t.Language != "und")
            .GroupBy(t => t.Language)
            .ToDictionary(g => g.Key, g => g.Count());

        var duplicateLangs = langCounts.Where(kv => kv.Value > 1).Select(kv => kv.Key).ToHashSet();

        // 5. Формирование аргументов One-Pass извлечения дорожек
        if (hasTracks && selectedTrackIds != null)
        {
            foreach (var trackId in selectedTrackIds)
            {
                var track = structure.Tracks.FirstOrDefault(t => t.TrackId == trackId);
                if (track == null) continue;

                string ext = GetExtensionForTrack(track);
                
                string nameSuffix = "";
                if (!string.IsNullOrEmpty(track.Language) && duplicateLangs.Contains(track.Language) && !string.IsNullOrEmpty(track.Name))
                {
                    nameSuffix = SanitizeName(track.Name);
                }

                string outFilename = FormatFilename(Path.GetFileNameWithoutExtension(filePath), track, ext, nameFormat, nameSuffix);
                string outPath = Path.Combine(baseDir, outFilename);

                if (File.Exists(outPath) && !overwrite)
                {
                    LogService.Instance.Info($"Дорожка #{track.TrackId} пропущена (файл существует): {outFilename}", "TrackExtractorScript");
                    extractResults.Add($"⏭ Пропущена дорожка {track.TrackId}: {outFilename}");
                    continue;
                }

                string codecFlag;
                string codecValue;

                if (track.TrackType.Equals("video", StringComparison.OrdinalIgnoreCase))
                {
                    codecFlag = "-c:v";
                    codecValue = "copy";
                }
                else if (track.TrackType.Equals("audio", StringComparison.OrdinalIgnoreCase))
                {
                    codecFlag = "-c:a";
                    // Особая обработка PCM из M2TS/TS
                    if (track.Codec.Equals("PCM", StringComparison.OrdinalIgnoreCase) && 
                        ext.Equals(".wav", StringComparison.OrdinalIgnoreCase) && 
                        (filePath.EndsWith(".m2ts", StringComparison.OrdinalIgnoreCase) || filePath.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)))
                    {
                        codecValue = "pcm_s24le";
                        LogService.Instance.Info($"Применяется распаковка Blu-ray PCM -> PCM 24-bit WAV для дорожки #{track.TrackId}", "TrackExtractorScript");
                    }
                    else
                    {
                        codecValue = "copy";
                    }
                }
                else // Субтитры
                {
                    codecFlag = "-c:s";
                    if (AppConstants.SubtitleConvertCodecs.TryGetValue(track.Codec, out string? convertCodec))
                    {
                        codecValue = convertCodec;
                        LogService.Instance.Info($"Применяется конвертация субтитров {track.Codec} -> {convertCodec} для дорожки #{track.TrackId}", "TrackExtractorScript");
                    }
                    else
                    {
                        codecValue = "copy";
                    }
                }

                ffmpegArgs.Add("-map");
                ffmpegArgs.Add($"0:{track.TrackId}");
                ffmpegArgs.Add(codecFlag);
                ffmpegArgs.Add(codecValue);
                ffmpegArgs.Add($"\"{outPath}\"");

                filesToExtract.Add(outPath);
                extractResults.Add($"✅ Извлечена дорожка {track.TrackId}: {outFilename}");
            }
        }

        // 6. Запуск FFmpeg для One-Pass извлечения дорожек
        bool tracksSuccess = true;
        if (ffmpegArgs.Count > 0)
        {
            LogService.Instance.Info($"Запуск процесса FFmpeg для One-Pass извлечения дорожек из {Path.GetFileName(filePath)}", "TrackExtractorScript");
            
            double duration = structure.Duration;

            var cts = new CancellationTokenSource();
            
            Action<ProgressInfo> onProgress = p =>
            {
                if (IsCancelled)
                {
                    cts.Cancel();
                    return;
                }
                string speedStr = p.Speed.HasValue ? $"{p.Speed.Value:F1}x" : "н/д";
                progressCallback(fileIndex, totalCount, $"Извлечение дорожек | {p.Percent:F1}% | Скорость: {speedStr}", p.Percent);
            };

            tracksSuccess = await FFmpegRunner.Instance.RunAsync(
                inputPath: filePath,
                outputPath: null, // Передаем null, так как все выходы со своими флагами уже находятся в ffmpegArgs
                extraArgs: ffmpegArgs,
                overwrite: overwrite,
                totalDuration: duration,
                onProgress: onProgress,
                cancellationToken: cts.Token
            );

            if (!tracksSuccess)
            {
                if (IsCancelled)
                {
                    CleanupIfCancelled(filesToExtract.ToArray());
                    string cancelMsg = $"⚠ Извлечение отменено пользователем: {Path.GetFileName(filePath)}";
                    LogService.Instance.Info(cancelMsg, "TrackExtractorScript");
                    results.Add(cancelMsg);
                    return results;
                }
                else
                {
                    string failMsg = $"❌ Ошибка во время выполнения FFmpeg для {Path.GetFileName(filePath)}";
                    LogService.Instance.Error(failMsg, "TrackExtractorScript");
                    results.Add(failMsg);
                    return results;
                }
            }
        }

        // 7. Последовательное извлечение выбранных шрифтов (вложений)
        bool fontsSuccess = true;
        if (hasAttachments && selectedAttachmentIds != null)
        {
            var activeFonts = structure.Attachments.Where(a => selectedAttachmentIds.Contains(a.AttachmentId) && a.IsFont).ToList();
            int fontIndex = 0;
            int totalFonts = activeFonts.Count;

            foreach (var font in activeFonts)
            {
                if (IsCancelled)
                {
                    fontsSuccess = false;
                    break;
                }

                fontIndex++;
                string outFontPath = Path.Combine(baseDir, font.FileName);

                if (File.Exists(outFontPath) && !overwrite)
                {
                    LogService.Instance.Info($"Шрифт пропущен (существует): {font.FileName}", "TrackExtractorScript");
                    extractResults.Add($"静态 Пропущен шрифт: {font.FileName}");
                    continue;
                }

                progressCallback(fileIndex, totalCount, $"Извлечение шрифтов | {fontIndex} из {totalFonts} ({font.FileName})", 100.0 * fontIndex / totalFonts);
                
                int ffmpegAttachmentIndex = structure.Attachments.IndexOf(font);
                LogService.Instance.Info($"Запуск извлечения шрифта #{font.AttachmentId} (индекс FFmpeg: {ffmpegAttachmentIndex}, файл: {font.FileName})", "TrackExtractorScript");
                
                bool fSuccess = await FFmpegRunner.Instance.ExtractAttachmentAsync(filePath, ffmpegAttachmentIndex, outFontPath);
                
                if (fSuccess)
                {
                    extractResults.Add($"✅ Извлечен шрифт: {font.FileName}");
                }
                else
                {
                    LogService.Instance.Error($"Не удалось извлечь шрифт #{font.AttachmentId} ({font.FileName})", "TrackExtractorScript");
                    extractResults.Add($"❌ Ошибка извлечения шрифта: {font.FileName}");
                    
                    if (IsCancelled)
                    {
                        fontsSuccess = false;
                        CleanupIfCancelled(outFontPath);
                        break;
                    }
                }
            }
        }

        if (IsCancelled)
        {
            string cancelMsg = $"⚠ Извлечение отменено пользователем: {Path.GetFileName(filePath)}";
            LogService.Instance.Info(cancelMsg, "TrackExtractorScript");
            results.Add(cancelMsg);
            return results;
        }

        if (tracksSuccess && fontsSuccess)
        {
            LogService.Instance.Info($"Успешно завершено извлечение для файла: {Path.GetFileName(filePath)}", "TrackExtractorScript");
            progressCallback(fileIndex, totalCount, "Успешно завершено!", 100.0);
            results.AddRange(extractResults);
        }
        else
        {
            string failMsg = $"❌ Сбой извлечения потоков из файла: {Path.GetFileName(filePath)}";
            LogService.Instance.Error(failMsg, "TrackExtractorScript");
            progressCallback(fileIndex, totalCount, "Ошибка выполнения", 0.0);
            results.Add(failMsg);
        }

        return results;
    }

    /// <summary>
    /// Возвращает расширение файла для дорожки на основе ее кодека.
    /// </summary>
    private string GetExtensionForTrack(MediaTrack track)
    {
        if (AppConstants.RawExtensions.TryGetValue(track.Codec, out string? rawExt))
        {
            return rawExt;
        }

        if (track.TrackType.Equals("video", StringComparison.OrdinalIgnoreCase))
            return ".mkv";
        if (track.TrackType.Equals("audio", StringComparison.OrdinalIgnoreCase))
            return ".mka";
        if (track.TrackType.Equals("subtitles", StringComparison.OrdinalIgnoreCase))
            return ".mks";

        return ".bin";
    }

    /// <summary>
    /// Очищает имя дорожки от недопустимых символов файловой системы Windows.
    /// </summary>
    private static string SanitizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        string clean = name.Trim('\'', '"', ' ');
        clean = SanitizeRegex.Replace(clean, "");
        clean = clean.Replace(" ", "_");
        return clean;
    }

    /// <summary>
    /// Форматирует имя выходного файла в соответствии с выбранным шаблоном навигации.
    /// </summary>
    private string FormatFilename(
        string originalStem,
        MediaTrack track,
        string ext,
        string nameFormat,
        string nameSuffix)
    {
        string lang = !string.IsNullOrEmpty(track.Language) && track.Language != "und" ? track.Language : "";
        
        if (!string.IsNullOrEmpty(nameSuffix))
        {
            lang = !string.IsNullOrEmpty(lang) ? $"{lang}_{nameSuffix}" : nameSuffix;
        }

        string trackIdStr = $"track{track.TrackId:D2}";
        string name = nameFormat.Replace("{original}", originalStem);
        name = name.Replace("{id}", trackIdStr);

        if (name.Contains("{lang}"))
        {
            if (!string.IsNullOrEmpty(lang))
            {
                name = name.Replace("{lang}", lang);
            }
            else
            {
                name = name.Replace("_{lang}", "")
                           .Replace("{lang}_", "")
                           .Replace("{lang}", "");
            }
        }

        name = name.Replace("__", "_").Trim('_');
        return $"{name}{ext}";
    }

    /// <summary>
    /// Безопасно подчищает частично записанные файлы с диска в случае отмены операции.
    /// </summary>
    private void CleanupIfCancelled(params string[] filePaths)
    {
        foreach (var path in filePaths)
        {
            if (File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                    LogService.Instance.Info($"Удален временный недописанный файл: {Path.GetFileName(path)}", "TrackExtractorScript");
                }
                catch (Exception ex)
                {
                    LogService.Instance.Exception(ex, $"Не удалось подчистить временный файл {path} при отмене", "TrackExtractorScript");
                }
            }
        }
    }
}
