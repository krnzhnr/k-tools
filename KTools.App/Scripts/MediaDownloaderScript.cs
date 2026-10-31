// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Infrastructure;
using KTools_App.Models;
using KTools_App.Services.Contracts;
using ExecutionContext = KTools_App.Models.ExecutionContext;

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
        string hostLabel = SafeHostLabel(filePath);
        ProcessExecutionContext processContext = ProcessExecutionContext.FromOperation(
            context.OperationId,
            "yt-dlp",
            context.ItemId);

        if (string.IsNullOrWhiteSpace(filePath) ||
            !Uri.TryCreate(filePath, UriKind.Absolute, out var validatedUri) ||
            (validatedUri.Scheme != Uri.UriSchemeHttp && validatedUri.Scheme != Uri.UriSchemeHttps))
        {
            WriteOutcome(
                "media_download.rejected",
                LogLevel.Warning,
                LogStatus.Skipped,
                "Отклонена невалидная ссылка для скачивания",
                processContext,
                null,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["ErrorCode"] = "invalid-url",
                    ["Stage"] = "validate"
                });
            results.Add("Недопустимый URL: поддерживаются только http:// и https://");
            return ExecutionResult.Failed(
                context,
                results,
                errorCode: "invalid-url",
                retryable: false);
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

        // Определяем директорию сохранения
        string targetDir = string.IsNullOrEmpty(outputPath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
            : outputPath;
        DateTime downloadStartedUtc = DateTime.UtcNow;
        HashSet<string> filesBefore = Directory.Exists(targetDir)
            ? Directory.GetFiles(targetDir, "*", SearchOption.TopDirectoryOnly)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(targetDir))
        {
            try
            {
                Directory.CreateDirectory(targetDir);
            }
            catch (Exception ex)
            {
                WriteOutcome(
                    "media_download.output_directory_failed",
                    LogLevel.Error,
                    LogStatus.Failed,
                    "Не удалось создать папку сохранения",
                    processContext,
                    ex,
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["ErrorCode"] = "output-directory-failed",
                        ["HostLabel"] = hostLabel,
                        ["Stage"] = "prepare"
                    });
                results.Add("Ошибка создания папки сохранения");
                return ExecutionResult.FromException(
                    context,
                    ex,
                    results,
                    errorCode: "output-directory-failed",
                    cleanupState: CleanupState.NotStarted);
            }
        }

        string ytdlpPath = _pathManager.GetBinaryPath("yt-dlp");
        if (!File.Exists(ytdlpPath))
        {
            WriteOutcome(
                "media_download.dependency_missing",
                LogLevel.Error,
                LogStatus.Failed,
                "Отсутствует исполняемый файл внешней утилиты 'yt-dlp'",
                processContext,
                null,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["ErrorCode"] = "missing-dependency",
                    ["HostLabel"] = hostLabel,
                    ["Stage"] = "prepare"
                });
            results.Add("Отсутствует исполняемый файл yt-dlp");
            return ExecutionResult.Failed(
                context,
                results,
                errorCode: "missing-dependency",
                retryable: false);
        }

        progressCallback(fileIndex, totalCount, "Запуск скачивания...", 0.0);

        // Формируем аргументы. Пользовательские AdditionalArgs, адрес ссылки и её
        // query-часть в журнал не попадают: только количество аргументов и их хеш.
        var args = new List<string>
        {
            $"-f \"{formatArgValue}\"",
            $"--paths \"{targetDir}\"",
            "--output \"%(title)s.%(ext)s\"",
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
                    break;
            }
        }
        else
        {
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

        args.Add($"\"{filePath}\"");

        string argumentsString = string.Join(" ", args);
        var runner = new DirectProcessRunner(_logService);
        var journalLimiter = new ProcessOutputRateLimiter(4, 1000);
        int lastRaiseTick = Environment.TickCount;
        var outputArtifacts = new List<string>();

        void AppendJournal(string prefix, string line)
        {
            if (!journalLimiter.TryAcquire())
            {
                return;
            }

            lock (results)
            {
                AppendToLog(prefix + LogRedactor.Default.RedactDetailToken(line) + "\r\n");
                TrimSavedLogToTail(50000, 40000);
            }
        }

        ProcessResult runResult;
        try
        {
            runResult = await runner.RunAsync(
                ytdlpPath,
                "yt-dlp",
                argumentsString,
                processContext,
                onOutputLine: line =>
                {
                    var match = ProgressRegex.Match(line);
                    double? percent = null;
                    if (match.Success && double.TryParse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out double p))
                    {
                        percent = p;
                    }

                    AppendJournal(string.Empty, line);
                    progressCallback(fileIndex, totalCount, line, percent);

                    if (Environment.TickCount - lastRaiseTick > 250)
                    {
                        lastRaiseTick = Environment.TickCount;
                        RaiseStateChanged();
                    }
                },
                onErrorLine: line => AppendJournal("[stderr] ", line),
                isCancellationRequested: () => IsCancelled,
                cancellationToken: CancellationToken,
                workingDir: targetDir);
        }
        catch (Exception ex)
        {
            WriteOutcome(
                "media_download.critical_failure",
                LogLevel.Error,
                LogStatus.Failed,
                "Критическая ошибка при загрузке",
                processContext,
                ex,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["ErrorCode"] = "download-exception",
                    ["HostLabel"] = hostLabel
                });
            results.Add("Критическая ошибка при загрузке");
            return ExecutionResult.FromException(
                context,
                ex,
                results,
                errorCode: "download-exception",
                cleanupState: CleanupState.Unknown);
        }

        RaiseStateChanged();

        if (Directory.Exists(targetDir))
        {
            outputArtifacts = Directory.GetFiles(targetDir, "*", SearchOption.TopDirectoryOnly)
                .Where(path => !filesBefore.Contains(path) || File.GetLastWriteTimeUtc(path) >= downloadStartedUtc.AddSeconds(-2))
                .Where(IsDownloadedArtifact)
                .ToList();
        }

        if (runResult.IsCancelled)
        {
            results.Add("Загрузка отменена");
            return ExecutionResult.Cancelled(
                context,
                results,
                errorCode: "cancelled",
                exitCode: runResult.ExitCode,
                outputFile: outputArtifacts.FirstOrDefault(),
                outputExists: outputArtifacts.Count > 0,
                cleanupState: CleanupState.Partial);
        }

        bool cleanupFailed = false;
        if (runResult.ExitCode == 0 && downloadSubs && cleanSubs && outputArtifacts.Count > 0)
        {
            try
            {
                CleanDownloadedVttFiles(targetDir);
            }
            catch (Exception ex)
            {
                cleanupFailed = true;
                WriteOutcome(
                    "media_download.subtitle_cleanup_failed",
                    LogLevel.Warning,
                    LogStatus.PartiallySucceeded,
                    "Ошибка автоматической очистки загруженных субтитров",
                    processContext,
                    ex,
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["ErrorCode"] = "subtitle-cleanup-failed",
                        ["HostLabel"] = hostLabel,
                        ["CleanupState"] = "failed"
                    });
            }
        }

        if (runResult.ExitCode == 0 && outputArtifacts.Count == 0)
        {
            results.Add("Загрузка завершена без выходного файла");
            return ExecutionResult.Failed(
                context,
                results,
                errorCode: "artifact-missing",
                exitCode: runResult.ExitCode,
                outputExists: false,
                retryable: true,
                cleanupState: CleanupState.Completed);
        }

        if (!runResult.IsSuccess)
        {
            results.Add($"Ошибка загрузки (код: {runResult.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "н/д"})");
            return ExecutionResult.Failed(
                context,
                results,
                errorCode: runResult.ErrorCode ?? "process-exit",
                exitCode: runResult.ExitCode,
                outputFile: outputArtifacts.FirstOrDefault(),
                outputExists: outputArtifacts.Count > 0,
                retryable: true,
                cleanupState: CleanupState.Completed);
        }

        progressCallback(fileIndex, totalCount, "Загрузка успешно завершена!", 100.0);
        results.Add("Загрузка завершена");
        if (cleanupFailed)
        {
            return ExecutionResult.PartiallySucceeded(
                context,
                results,
                errorCode: "subtitle-cleanup-failed",
                exitCode: runResult.ExitCode,
                outputFile: outputArtifacts[0],
                outputExists: true,
                cleanupState: CleanupState.Partial);
        }

        return ExecutionResult.Succeeded(
            context,
            results,
            outputFile: outputArtifacts[0],
            outputExists: true,
            exitCode: runResult.ExitCode,
            cleanupState: CleanupState.Completed);
    }

    private void WriteOutcome(
        string eventId,
        LogLevel level,
        LogStatus status,
        string message,
        ProcessExecutionContext context,
        Exception? exception,
        IReadOnlyDictionary<string, object?>? properties)
    {
        try
        {
            Dictionary<string, object?> merged = context.ToLogProperties();
            if (properties is not null)
            {
                foreach (KeyValuePair<string, object?> pair in properties)
                {
                    merged[pair.Key] = pair.Value;
                }
            }

            ExceptionInfo? info = null;
            if (exception is not null)
            {
                try
                {
                    info = ExceptionInfo.FromException(exception);
                }
                catch (Exception)
                {
                    info = null;
                }
            }

            _logService.Write(new LogEvent
            {
                EventId = eventId,
                Level = level,
                Status = status,
                Source = "MediaDownloaderScript",
                Message = message,
                OperationId = context.OperationId,
                ItemId = context.ItemId,
                Attempt = context.Attempt,
                Tool = context.Tool,
                Properties = merged,
                Exception = info
            });
        }
        catch (Exception)
        {
        }
    }

    private static string SafeHostLabel(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return "unknown";
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return "unknown";
        }

        return string.IsNullOrEmpty(uri.Host) ? "unknown" : LogRedactor.CompactSafeToken(uri.Host);
    }

    private static bool IsDownloadedArtifact(string path)
    {
        string extension = Path.GetExtension(path);
        return !extension.Equals(".part", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".ytdl", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".download", StringComparison.OrdinalIgnoreCase);
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
            _logService.Write(
                "script.download.subtitles_formatted",
                LogLevel.Debug,
                LogStatus.Succeeded,
                $"Субтитры загружены и приведены к выбранному формату: '{LogProps.FileName(filePath)}'",
                source: Name,
                properties: LogProps
                    .Create("InputName", LogProps.FileName(filePath))
                    .With("Extension", Path.GetExtension(filePath)));
        }
        catch (Exception ex)
        {
            _logService.Write(
                "media_download.subtitle_format_failed",
                LogLevel.Warning,
                LogStatus.PartiallySucceeded,
                $"Файл субтитров '{LogProps.FileName(filePath)}' не приведён к выбранному формату",
                ex,
                "MediaDownloaderScript",
                properties: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["Stage"] = "subtitle_format",
                    ["FileName"] = LogProps.FileName(filePath)
                });
        }
    }
}
