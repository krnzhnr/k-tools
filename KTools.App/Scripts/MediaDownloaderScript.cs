// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using KTools_App.Core;
using KTools_App.Services.Contracts;

namespace KTools_App.Scripts;

/// <summary>
/// Скрипт загрузки медиаконтента из сети через утилиту yt-dlp.
/// Поддерживает индивидуальный выбор качества видео и аудио, скачивание субтитров и отображение прогресса.
/// </summary>
public sealed class MediaDownloaderScript : AbstractScript
{
    private static readonly Regex ProgressRegex = new(@"\[download\]\s+(\d+(?:\.\d+)?)%\s+of", RegexOptions.Compiled);

    public override string Name => "Загрузка медиа";
    public override string Description => "Загрузка видео- и аудиофайлов из сети по URL-адресам через yt-dlp";
    public override string Category => "Сеть";
    public override string IconName => AppConstants.ScriptIcons.MediaDownloader;
    public override string[] FileExtensions => new[] { ".url", ".html" }; // Виртуальные расширения

    public override string FirstTabHeader => "Загрузка";
    public override bool ShowUrlInputBar => true;

    public override string[] RequiredDependencies => new[] { "yt-dlp", "ffmpeg" };

    public override List<SettingField> SettingsSchema => new()
    {
        new SettingField("VideoContainer", "Контейнер видео", SettingType.Combo, "Авто", "Формат сохранения",
            comment: "Целевой видеоконтейнер при объединении видео и аудиодорожек",
            options: new List<string> { "Авто", "MP4", "MKV" }),
        new SettingField("AudioFormat", "Формат аудио", SettingType.Combo, "Исходный", "Формат сохранения",
            comment: "Формат сохранения аудиофайлов при выборе только звуковой дорожки",
            options: new List<string> { "Исходный", "MP3 (320k)", "M4A (AAC)", "FLAC", "Opus" }),
        new SettingField("ForceOverwrite", "Перезаписывать существующие файлы", SettingType.Checkbox, false, "Формат сохранения",
            comment: "Принудительно скачивать и перезаписывать медиафайлы при повторном запуске"),
        new SettingField("DownloadSubtitles", "Скачивать субтитры", SettingType.Checkbox, false, "Субтитры"),
        new SettingField("EmbedSubtitles", "Встраивать субтитры в видео", SettingType.Checkbox, true, "Субтитры",
            visibleIfKey: "DownloadSubtitles",
            visibleIfValues: new List<string> { "True" }),
        new SettingField("CleanSubtitles", "Очищать и форматировать субтитры (WebVTT)", SettingType.Checkbox, true, "Субтитры",
            visibleIfKey: "DownloadSubtitles",
            visibleIfValues: new List<string> { "True" }),
        new SettingField("AdditionalArgs", "Дополнительные аргументы yt-dlp", SettingType.Text, string.Empty, "Дополнительно")
    };

    public MediaDownloaderScript(ILogService logService, ISettingsManager settingsManager, IPathManager pathManager)
        : base(logService, settingsManager, pathManager)
    {
    }

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

        if (string.IsNullOrWhiteSpace(filePath) || 
            !Uri.TryCreate(filePath, UriKind.Absolute, out var validatedUri) || 
            (validatedUri.Scheme != Uri.UriSchemeHttp && validatedUri.Scheme != Uri.UriSchemeHttps))
        {
            _logService.Warn($"Отклонена невалидная ссылка для скачивания: '{filePath}'", "MediaDownloaderScript");
            results.Add($"❌ Ошибка: Невалидный URL адрес '{filePath}' (поддерживаются только http:// и https://)");
            return results;
        }

        // Настройки скрипта
        string videoContainer = GetSettingValue(settings, "VideoContainer", "Авто");
        string audioFormatSetting = GetSettingValue(settings, "AudioFormat", "Исходный");
        bool forceOverwrite = GetSettingValue(settings, "ForceOverwrite", false) || _settingsManager.OverwriteExisting;
        bool downloadSubs = GetSettingValue(settings, "DownloadSubtitles", false);
        bool embedSubs = GetSettingValue(settings, "EmbedSubtitles", true);
        bool cleanSubs = GetSettingValue(settings, "CleanSubtitles", true);
        string additionalArgs = GetSettingValue(settings, "AdditionalArgs", string.Empty);

        // Получаем информацию о качестве и субтитрах для данной ссылки
        var queueItem = FilesQueue.FirstOrDefault(f => f.FilePath == filePath);
        string formatArgValue = queueItem?.SelectedFormat?.FormatArg ?? "bv*+ba/b";
        bool isAudioOnly = queueItem?.SelectedFormat?.IsAudioOnly ?? (formatArgValue == "ba" || formatArgValue == "ba/b");
        string subtitleCode = queueItem?.SelectedSubtitle?.Code ?? "none";
        string displayName = queueItem?.DisplayName ?? filePath;

        // Определяем директорию сохранения
        string targetDir = string.IsNullOrEmpty(outputPath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
            : outputPath;

        if (!Directory.Exists(targetDir))
        {
            try
            {
                Directory.CreateDirectory(targetDir);
            }
            catch (Exception ex)
            {
                results.Add($"❌ Ошибка создания папки сохранения: {ex.Message}");
                return results;
            }
        }

        string ytdlpPath = _pathManager.GetBinaryPath("yt-dlp");
        if (!File.Exists(ytdlpPath))
        {
            results.Add("❌ Ошибка: отсутствует исполняемый файл yt-dlp");
            return results;
        }

        progressCallback(fileIndex, totalCount, "Запуск скачивания...", 0.0);

        // Формируем аргументы
        var args = new List<string>
        {
            $"-f \"{formatArgValue}\"",
            $"--paths \"{targetDir}\"",
            // Переименовываем скачиваемый файл по стандартному шаблону названия
            "--output \"%(title)s.%(ext)s\"",
            // Оптимизация стабильности при блокировках и сбросах сети (ТСПУ / обрывы SSL)
            "--http-chunk-size 10M",
            "--concurrent-fragments 4",
            "--retries 25",
            "--fragment-retries 25"
        };

        if (forceOverwrite)
        {
            args.Add("--force-overwrites");
        }
        else
        {
            args.Add("--no-force-overwrites");
        }

        // Централизованное управление форматом сохранения:
        if (isAudioOnly)
        {
            // Для аудио: извлечение в указанный пользователем формат
            switch (audioFormatSetting)
            {
                case "MP3 (320k)":
                    args.Add("--extract-audio");
                    args.Add("--audio-format mp3");
                    args.Add("--audio-quality 0");
                    break;
                case "M4A (AAC)":
                    args.Add("--extract-audio");
                    args.Add("--audio-format m4a");
                    break;
                case "FLAC":
                    args.Add("--extract-audio");
                    args.Add("--audio-format flac");
                    break;
                case "Opus":
                    args.Add("--extract-audio");
                    args.Add("--audio-format opus");
                    break;
                default:
                    // "Исходный" - оставляем оригинальный аудиопоток без транскодирования
                    break;
            }
        }
        else
        {
            // Для видео: централизованный выбор контейнера
            if (videoContainer.Equals("MP4", StringComparison.OrdinalIgnoreCase))
            {
                args.Add("--merge-output-format mp4");
                args.Add("--remux-video mp4");
            }
            else if (videoContainer.Equals("MKV", StringComparison.OrdinalIgnoreCase))
            {
                args.Add("--merge-output-format mkv");
            }
        }

        // Обработка субтитров
        if (downloadSubs)
        {
            if (subtitleCode == "all")
            {
                args.Add("--write-subs");
                args.Add("--write-auto-subs");
                args.Add("--all-subs");
            }
            else if (subtitleCode != "none")
            {
                args.Add("--write-subs");
                args.Add("--write-auto-subs");
                args.Add($"--sub-langs \"{subtitleCode}\"");
            }

            if (subtitleCode != "none" && embedSubs && !isAudioOnly)
            {
                args.Add("--embed-subs");
                // Если не задан MP4 принудительно, используем универсальный MKV для субтитров
                if (!videoContainer.Equals("MP4", StringComparison.OrdinalIgnoreCase))
                {
                    if (!args.Contains("--merge-output-format mkv"))
                    {
                        args.Add("--merge-output-format mkv");
                    }
                }
            }
        }

        // Путь к ffmpeg для сборки видео+аудио
        string ffmpegPath = _pathManager.GetBinaryPath("ffmpeg");
        if (File.Exists(ffmpegPath))
        {
            args.Add($"--ffmpeg-location \"{ffmpegPath}\"");
        }

        if (!string.IsNullOrWhiteSpace(additionalArgs))
        {
            args.Add(additionalArgs);
        }

        string nodePath = _pathManager.GetBinaryPath("node");
        if (File.Exists(nodePath))
        {
            args.Add($"--js-runtimes \"node:{nodePath}\"");
        }

        // Ссылка
        args.Add($"\"{filePath}\"");

        string argumentsString = string.Join(" ", args);

        var startInfo = new ProcessStartInfo
        {
            FileName = ytdlpPath,
            Arguments = argumentsString,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        try
        {
            using var process = new Process { StartInfo = startInfo };
            process.Start();
            ActiveProcessTracker.Register(process);

            try
            {
                // Читаем stdout для прогресса и логов
                var outputReaderTask = Task.Run(async () =>
                {
                    int lastRaiseTime = Environment.TickCount;
                    while (!process.StandardOutput.EndOfStream)
                    {
                        string? line = await process.StandardOutput.ReadLineAsync();
                        if (line == null) continue;

                        if (IsCancelled)
                        {
                            try { process.Kill(true); } catch (Exception killEx) { _logService.Warn($"Не удалось завершить процесс yt-dlp при отмене: {killEx.Message}", "MediaDownloaderScript"); }
                            break;
                        }

                        // Вывод в лог в реальном времени
                        lock (results)
                        {
                            AppendToLog(line + "\r\n");
                            TrimSavedLogToTail(50000, 40000);
                        }

                        int now = Environment.TickCount;
                        if (Math.Abs(now - lastRaiseTime) > 250)
                        {
                            lastRaiseTime = now;
                            RaiseStateChanged();
                        }

                        // Парсим прогресс
                        var match = ProgressRegex.Match(line);
                        double? percent = null;
                        if (match.Success && double.TryParse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out double p))
                        {
                            percent = p;
                        }

                        progressCallback(fileIndex, totalCount, line, percent);
                    }
                });

                // Читаем stderr для логирования
                var errorReaderTask = Task.Run(async () =>
                {
                    int lastRaiseTime = Environment.TickCount;
                    while (!process.StandardError.EndOfStream)
                    {
                        string? line = await process.StandardError.ReadLineAsync();
                        if (line == null) continue;

                        _logService.Warn($"[yt-dlp stderr] {line}", "MediaDownloaderScript");

                        // Вывод в лог в реальном времени
                        lock (results)
                        {
                            AppendToLog($"[stderr] {line}\r\n");
                            TrimSavedLogToTail(50000, 40000);
                        }

                        int now = Environment.TickCount;
                        if (Math.Abs(now - lastRaiseTime) > 250)
                        {
                            lastRaiseTime = now;
                            RaiseStateChanged();
                        }

                        if (IsCancelled)
                        {
                            try { process.Kill(true); } catch (Exception killEx) { _logService.Warn($"Не удалось завершить процесс yt-dlp при отмене: {killEx.Message}", "MediaDownloaderScript"); }
                            break;
                        }
                    }
                });

                await Task.WhenAll(process.WaitForExitAsync(), outputReaderTask, errorReaderTask);
                RaiseStateChanged();

                if (IsCancelled)
                {
                    results.Add($"⚠ Отменено: {displayName}");
                    return results;
                }

                if (process.ExitCode == 0)
                {
                    if (downloadSubs && cleanSubs)
                    {
                        try
                        {
                            CleanDownloadedVttFiles(targetDir);
                        }
                        catch (Exception ex)
                        {
                            _logService.Error($"Ошибка автоматической очистки VTT: {ex.Message}", "MediaDownloaderScript");
                        }
                    }

                    progressCallback(fileIndex, totalCount, "Загрузка успешно завершена!", 100.0);
                    results.Add($"✅ Скачано: {displayName}");
                }
                else
                {
                    progressCallback(fileIndex, totalCount, "Ошибка", 0.0);
                    results.Add($"❌ Ошибка загрузки (Код: {process.ExitCode}) для {displayName}");
                }
            }
            finally
            {
                ActiveProcessTracker.Unregister(process);
            }
        }
        catch (Exception ex)
        {
            results.Add($"❌ Критическая ошибка при загрузке {displayName}: {ex.Message}");
        }

        return results;
    }

    private void CleanDownloadedVttFiles(string directory)
    {
        if (!Directory.Exists(directory)) return;

        var vttFiles = Directory.GetFiles(directory, "*.vtt", SearchOption.TopDirectoryOnly);
        foreach (var file in vttFiles)
        {
            var lastWrite = File.GetLastWriteTime(file);
            if ((DateTime.Now - lastWrite).TotalMinutes > 3) continue;

            CleanVttFile(file);
        }
    }

    private void CleanVttFile(string filePath)
    {
        try
        {
            var lines = File.ReadAllLines(filePath);
            var cleanedLines = new List<string>();

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    cleanedLines.Add(line);
                    continue;
                }

                if (line.Contains("-->"))
                {
                    // Очищаем настройки позиционирования в таймстампе
                    var match = Regex.Match(line, @"\d{2}:\d{2}:\d{2}[.,]\d{3}\s+-->\s+\d{2}:\d{2}:\d{2}[.,]\d{3}");
                    if (match.Success)
                    {
                        cleanedLines.Add(match.Value);
                    }
                    else
                    {
                        cleanedLines.Add(line);
                    }
                }
                else if (line.StartsWith("WEBVTT") || line.StartsWith("NOTE") || line.StartsWith("STYLE"))
                {
                    cleanedLines.Add(line);
                }
                else
                {
                    // Декодируем сущности
                    string cleaned = line;
                    cleaned = cleaned.Replace("&lt;", "<").Replace("&gt;", ">");
                    cleaned = cleaned.Replace("&amp;", "&").Replace("&quot;", "\"").Replace("&apos;", "'").Replace("&#39;", "'");

                    // Удаляем теги форматирования и внутренние таймстампы
                    cleaned = Regex.Replace(cleaned, @"<[^>]+>", "");

                    // Нормализуем пробелы
                    cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();

                    cleanedLines.Add(cleaned);
                }
            }

            File.WriteAllLines(filePath, cleanedLines, System.Text.Encoding.UTF8);
            _logService.Info($"Субтитры успешно отформатированы: {Path.GetFileName(filePath)}", "MediaDownloaderScript");
        }
        catch (Exception ex)
        {
            _logService.Error($"Не удалось отформатировать файл субтитров '{filePath}': {ex.Message}", "MediaDownloaderScript");
        }
    }
}
