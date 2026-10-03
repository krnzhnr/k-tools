// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
/// Скрипт массового извлечения выбранных дорожек и встроенных шрифтов из медиафайлов.
/// Реализует One-Pass извлечение через FFmpeg для высокой скорости работы.
/// Все комментарии и логирование выполнены исключительно на русском языке.
/// </summary>
public sealed class TrackExtractorScript : AbstractScript
{
    private const string SourceName = nameof(TrackExtractorScript);

    private readonly IMediaProbeService _mediaProbeService;
    private readonly IFFmpegRunner _ffmpegRunner;

    public TrackExtractorScript(ILogService logService, ISettingsManager settingsManager, IPathManager pathManager, IMediaProbeService mediaProbeService, IFFmpegRunner ffmpegRunner)
        : base(logService, settingsManager, pathManager)
    {
        _mediaProbeService = mediaProbeService ?? throw new ArgumentNullException(nameof(mediaProbeService));
        _ffmpegRunner = ffmpegRunner ?? throw new ArgumentNullException(nameof(ffmpegRunner));
    }

    private static readonly Regex SanitizeRegex = new(@"[<>:""/\\|?*]", RegexOptions.Compiled);

    public override string Name => AppConstants.ScriptMetadata.TrackExtrName;
    public override string Description => AppConstants.ScriptMetadata.TrackExtrDesc;
    public override string Category => AppConstants.ScriptCategory.Containers;
    public override string IconName => AppConstants.ScriptIcons.TrackExtractor;
    public override string[] FileExtensions => AppConstants.AllContainers.ToArray();
    public override string[] RequiredDependencies => new[] { "mkvtoolnix", "ffmpeg" };
    public override bool UseCustomWidget => true;

    public override List<SettingField> SettingsSchema => new()
    {
        new SettingField(
            "name_format",
            "Шаблон имени файла",
            SettingType.Text,
            "{original}",
            "Именование",
            comment: "Шаблон имени для извлеченных файлов. Доступные теги: {original} - имя файла, {lang} - язык, {id} - ID дорожки, {title} - заголовок, {codec} - кодек."
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

        _logService.Write("script.track_extractor.started", LogLevel.Debug, LogStatus.Running, $"Начато извлечение дорожек файла '{LogProps.FileName(filePath)}'", source: Name, properties: LogProps.Create("InputName", LogProps.FileName(filePath)));

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
            _logService.Write("script.track_extractor.skipped", LogLevel.Info, LogStatus.Skipped, skipMsg, source: Name, properties: LogProps.Create("InputName", LogProps.FileName(filePath)));
            progressCallback(fileIndex, totalCount, "Пропущен (нет выбора)", 100.0);
            results.Add(skipMsg);
            return ExecutionResult.Skipped(
                context,
                results,
                errorCode: "no-selection");
        }

        // 2. Получаем структуру метаданных
        var structure = await _mediaProbeService.ProbeAsync(filePath);
        if (structure == null)
        {
            string err = $"❌ Ошибка анализа метаданных файла: {Path.GetFileName(filePath)}";
            _logService.Write("script.track_extractor.failed", LogLevel.Error, LogStatus.Failed, err, source: Name, properties: LogProps.Create("ErrorCode", "TRACK_EXTRACTION_FAILED").With("InputName", LogProps.FileName(filePath)).With("Retryable", true));
            progressCallback(fileIndex, totalCount, "Ошибка анализа", 0.0);
            results.Add(err);
            return ExecutionResult.Failed(
                context,
                results,
                errorCode: "probe-empty",
                retryable: true);
        }

        // 3. Вычисляем выходную директорию
        string baseDir = string.IsNullOrEmpty(outputPath)
            ? Path.GetDirectoryName(filePath) ?? AppContext.BaseDirectory
            : outputPath;

        string inputDir = Path.GetDirectoryName(filePath) ?? "";
        if (_settingsManager.UseAutoSubfolder && (string.IsNullOrEmpty(outputPath) || baseDir.Equals(inputDir, StringComparison.OrdinalIgnoreCase)))
        {
            string subfolderName = _settingsManager.DefaultOutputSubfolder;
            if (string.IsNullOrWhiteSpace(subfolderName))
            {
                subfolderName = "KTools_Result";
            }
            baseDir = Path.Combine(inputDir, subfolderName);
        }

        bool createSubfolders = GetSettingValue(settings, "create_subfolders", false);
        if (createSubfolders)
        {
            string subfolder = Path.GetFileNameWithoutExtension(filePath);
            baseDir = Path.Combine(baseDir, subfolder);
        }

        try
        {
            if (!Directory.Exists(baseDir))
            {
                Directory.CreateDirectory(baseDir);
                _logService.Write("script.track_extractor.output_dir_created", LogLevel.Debug, LogStatus.Succeeded, "Создана папка для результатов извлечения", source: Name, properties: LogProps.Create("FileName", LogProps.FileName(baseDir)));
            }
        }
        catch (Exception ex)
        {
            _logService.Write("script.track_extractor.output_dir_failed", LogLevel.Error, LogStatus.Failed, "Папка для результатов извлечения не создана", ex, Name, properties: LogProps.Create("ErrorCode", "OUTPUT_DIR_FAILED").With("FileName", LogProps.FileName(baseDir)));
            results.Add("Ошибка создания папки");
            return ExecutionResult.FromException(
                context,
                ex,
                results,
                errorCode: "output-directory-failed",
                cleanupState: CleanupState.NotStarted);
        }

        string nameFormat = GetSettingValue(
            settings,
            "name_format",
            "{original}_{lang}_{id}");

        bool overwrite = _settingsManager.GetSetting(
            "General",
            "OverwriteExisting",
            false);

        var filesToExtract = new List<string>();
        var extractResults = new List<string>();
        var ffmpegArgs = new List<string>();
        bool tracksSuccess = true;
        bool fontsSuccess = true;

        // 4. Определение языковых дубликатов для формирования уникальных суффиксов
        var activeTracks = structure.Tracks
            .Where(t =>
                selectedTrackIds != null &&
                selectedTrackIds.Contains(t.TrackId))
            .ToList();

        var langCounts = activeTracks
            .Where(t =>
                !string.IsNullOrEmpty(t.Language) &&
                t.Language != "und")
            .GroupBy(t => t.Language)
            .ToDictionary(g => g.Key, g => g.Count());

        var duplicateLangs = langCounts
            .Where(kv => kv.Value > 1)
            .Select(kv => kv.Key)
            .ToHashSet();

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
                string trackStem = Path.GetFileNameWithoutExtension(outFilename);
                string trackExt = Path.GetExtension(outFilename);
                string renamedStem = ApplyPowerRename(trackStem, fileIndex, settings);
                outFilename = $"{renamedStem}{trackExt}";
                string outPath = Path.Combine(baseDir, outFilename);

                if (File.Exists(outPath) && !overwrite)
                {
                    _logService.Write("script.track_extractor.track_skipped", LogLevel.Debug, LogStatus.Skipped, $"Дорожка #{track.TrackId} пропущена: файл '{LogProps.FileName(outFilename)}' уже существует", source: Name, properties: LogProps.Create("Index", track.TrackId).With("OutputName", LogProps.FileName(outFilename)));
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
                        _logService.Write("script.track_extractor.pcm_unpack", LogLevel.Debug, LogStatus.Running, $"Применяется распаковка Blu-ray PCM в WAV 24 бит для дорожки #{track.TrackId}", source: Name, properties: LogProps.Create("Index", track.TrackId).With("Codec", LogRedactor.CompactSafeToken(track.Codec)).With("Container", "wav"));
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
                        _logService.Write("script.track_extractor.subtitle_convert", LogLevel.Debug, LogStatus.Running, $"Применяется конвертация субтитров {LogRedactor.CompactSafeToken(track.Codec)} -> {LogRedactor.CompactSafeToken(convertCodec)} для дорожки #{track.TrackId}", source: Name, properties: LogProps.Create("Index", track.TrackId).With("Codec", LogRedactor.CompactSafeToken(convertCodec)));
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
        tracksSuccess = true;
        if (ffmpegArgs.Count > 0)
        {
            _logService.Write("script.track_extractor.ffmpeg_started", LogLevel.Debug, LogStatus.Running, $"Запущен FFmpeg для извлечения дорожек одним проходом из '{LogProps.FileName(filePath)}'", source: Name, properties: LogProps.Create("Tool", "ffmpeg").With("InputName", LogProps.FileName(filePath)));

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
                progressCallback(fileIndex, totalCount, $"Извлечение дорожек | {p.Percent:F1}% | Скорость: {speedStr}", p.Percent, p.Fps, p.Bitrate);
            };

            ProcessResult tracksRun = await _ffmpegRunner.RunAsync(
                inputPath: filePath,
                outputPath: null, // Передаем null, так как все выходы со своими флагами уже находятся в ffmpegArgs
                extraArgs: ffmpegArgs,
                overwrite: overwrite,
                totalDuration: duration,
                onProgress: onProgress,
                cancellationToken: cts.Token
            );

            tracksSuccess = tracksRun.IsSuccess;
            if (!tracksSuccess)
            {
                if (IsCancelled)
                {
                    CleanupIfCancelled(filesToExtract.ToArray());
                    string cancelMsg = $"⚠ Извлечение отменено пользователем: {Path.GetFileName(filePath)}";
                    _logService.Write("script.track_extractor.cancelled", LogLevel.Info, LogStatus.Cancelled, cancelMsg, source: Name, properties: LogProps.Create("Reason", "UserRequested").With("CleanupState", "NotStarted"));
                    results.Add(cancelMsg);
                    return IsCancelled
                        ? ExecutionResult.Cancelled(
                            context,
                            results,
                            errorCode: "cancelled",
                            outputFile: filesToExtract.FirstOrDefault(),
                            outputExists: filesToExtract.Any(File.Exists),
                            cleanupState: CleanupState.Completed)
                        : ExecutionResult.Failed(
                            context,
                            results,
                            errorCode: "track-extraction-failed",
                            outputFile: filesToExtract.FirstOrDefault(),
                            outputExists: filesToExtract.Any(File.Exists),
                            retryable: true,
                            cleanupState: CleanupState.Completed);
                }
                else
                {
                    string failMsg = $"❌ Ошибка во время выполнения FFmpeg для {Path.GetFileName(filePath)}";
                    _logService.Write("script.track_extractor.failed", LogLevel.Error, LogStatus.Failed, failMsg, source: Name, properties: LogProps.Create("ErrorCode", "TRACK_EXTRACTION_FAILED").With("InputName", LogProps.FileName(filePath)).With("Retryable", true));
                    results.Add(failMsg);
                    return ExecutionResult.Failed(
                        context,
                        results,
                        errorCode: "track-extraction-failed",
                        outputFile: filesToExtract.FirstOrDefault(),
                        outputExists: filesToExtract.Any(File.Exists),
                        retryable: true,
                        cleanupState: CleanupState.Completed);
                }
            }
        }

        // 7. Последовательное извлечение выбранных шрифтов (вложений)
        fontsSuccess = true;
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
                    _logService.Write("script.track_extractor.font_skipped", LogLevel.Debug, LogStatus.Skipped, $"Шрифт пропущен: '{LogProps.FileName(font.FileName)}' уже существует", source: Name, properties: LogProps.Create("Index", font.AttachmentId).With("OutputName", LogProps.FileName(font.FileName)));
                    extractResults.Add($"Шрифт пропущен: '{LogProps.FileName(font.FileName)}' уже существует");
                    continue;
                }

                progressCallback(fileIndex, totalCount, $"Извлечение шрифтов | {fontIndex} из {totalFonts} ({font.FileName})", 100.0 * fontIndex / totalFonts);

                string inputExt = Path.GetExtension(filePath)
                    .ToLowerInvariant();
                bool isMkv =
                    inputExt.Equals(
                        ".mkv",
                        StringComparison.OrdinalIgnoreCase) ||
                    inputExt.Equals(
                        ".mka",
                        StringComparison.OrdinalIgnoreCase);
                int ffmpegAttachmentIndex = isMkv
                    ? structure.Tracks.Count +
                      structure.Attachments.IndexOf(font)
                    : font.AttachmentId;

                _logService.Write("font.extract.started", LogLevel.Debug, LogStatus.Running, $"Запуск извлечения шрифта #{font.AttachmentId} (индекс FFmpeg: {ffmpegAttachmentIndex}, файл: {font.FileName})", source: SourceName, properties: LogProps.Create("InputName", font.FileName).With("Count", font.AttachmentId).With("Total", ffmpegAttachmentIndex));

                ProcessResult fResult = await _ffmpegRunner.ExtractAttachmentAsync(
                    filePath,
                    ffmpegAttachmentIndex,
                    outFontPath,
                    cancellationToken: CancellationToken);
                bool fSuccess = fResult.IsSuccess;

                if (fSuccess)
                {
                    extractResults.Add($"✅ Извлечен шрифт: {font.FileName}");
                }
                else
                {
                    _logService.Write("script.track_extractor.font_failed", LogLevel.Error, LogStatus.Failed, $"Шрифт #{font.AttachmentId} не извлечён", source: Name, properties: LogProps.Create("ErrorCode", "ATTACHMENT_EXTRACTION_FAILED").With("Index", font.AttachmentId).With("OutputName", LogProps.FileName(font.FileName)).With("Retryable", true));
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
            _logService.Write("script.track_extractor.cancelled", LogLevel.Info, LogStatus.Cancelled, cancelMsg, source: Name, properties: LogProps.Create("Reason", "UserRequested").With("CleanupState", "NotStarted"));
            results.Add(cancelMsg);
            return ExecutionResult.Cancelled(
                context,
                results,
                errorCode: "cancelled",
                outputFile: filesToExtract.FirstOrDefault(),
                outputExists: filesToExtract.Any(File.Exists),
                cleanupState: CleanupState.Partial);
        }

        try
        {
            if (tracksSuccess && fontsSuccess)
            {
                _logService.Write("script.track_extractor.completed", LogLevel.Info, LogStatus.Succeeded, $"Извлечение дорожек для '{LogProps.FileName(filePath)}' завершено", source: Name, properties: LogProps.Create("InputName", LogProps.FileName(filePath)).With("Verified", true));
                progressCallback(fileIndex, totalCount, "Успешно завершено!", 100.0);
                results.AddRange(extractResults);
            }
            else
            {
                CleanupIfCancelled(filesToExtract.ToArray());
                string failMsg = $"❌ Сбой извлечения потоков из файла: {Path.GetFileName(filePath)}";
                _logService.Write("script.track_extractor.failed", LogLevel.Error, LogStatus.Failed, failMsg, source: Name, properties: LogProps.Create("ErrorCode", "TRACK_EXTRACTION_FAILED").With("InputName", LogProps.FileName(filePath)).With("Retryable", true));
                progressCallback(fileIndex, totalCount, "Ошибка выполнения", 0.0);
                results.Add(failMsg);
            }
        }
        catch (Exception ex)
        {
            CleanupIfCancelled(filesToExtract.ToArray());
            string errorMsg = $"❌ Ошибка выполнения скрипта для {Path.GetFileName(filePath)}";
            results.Add(errorMsg);
            _logService.Write("script.track_extractor.failed", LogLevel.Error, LogStatus.Failed, $"Извлечение дорожек для '{LogProps.FileName(filePath)}' не выполнено", ex, Name, properties: LogProps.Create("ErrorCode", "TRACK_EXTRACTION_FAILED").With("InputName", LogProps.FileName(filePath)).With("Retryable", true));
            return ExecutionResult.FromException(
                context,
                ex,
                results,
                errorCode: "track-extraction-exception",
                outputFile: filesToExtract.FirstOrDefault(),
                outputExists: filesToExtract.Any(File.Exists),
                cleanupState: CleanupState.Completed);
        }

        string? outputFile = filesToExtract.FirstOrDefault(File.Exists);
        if (outputFile is null && Directory.Exists(baseDir))
        {
            outputFile = Directory.GetFiles(baseDir, "*", SearchOption.TopDirectoryOnly).FirstOrDefault();
        }
        if (tracksSuccess && fontsSuccess && outputFile is not null)
        {
            return ExecutionResult.Succeeded(
                context,
                results,
                outputFile: outputFile,
                outputExists: true,
                cleanupState: CleanupState.Completed);
        }
        if (outputFile is not null)
        {
            return ExecutionResult.PartiallySucceeded(
                context,
                results,
                errorCode: "partial-extraction",
                outputFile: outputFile,
                outputExists: true,
                cleanupState: CleanupState.Partial);
        }
        return ExecutionResult.Failed(
            context,
            results,
            errorCode: "output-missing",
            outputExists: false,
            cleanupState: CleanupState.Completed);
    }

    /// <summary>
    /// Возвращает расширение файла для дорожки на основе ее кодека.
    /// </summary>
    private string GetExtensionForTrack(MediaTrack track)
    {
        return AppConstants.ResolveRawExtension(track.Codec, track.TrackType);
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
    /// Поддерживает плейсхолдеры: {original}, {lang}, {id}, {title}, {codec} и их синонимы.
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
        string trackTitle = SanitizeName(track.Name);
        string trackCodec = SanitizeName(track.Codec).ToLowerInvariant();

        string name = nameFormat;

        // 1. Подстановка имени оригинального файла
        name = ReplacePlaceholder(name, new[] { "{original}", "{original_name}", "{file_name}" }, originalStem);

        // 2. Подстановка ID дорожки
        name = ReplacePlaceholder(name, new[] { "{id}", "{track_id}" }, trackIdStr);

        // 3. Подстановка названия/заголовка дорожки
        name = ReplacePlaceholder(name, new[] { "{title}", "{track_title}", "{name}" }, trackTitle);

        // 4. Подстановка кодека
        name = ReplacePlaceholder(name, new[] { "{codec}", "{track_codec}" }, trackCodec);

        // 5. Подстановка языка
        name = ReplacePlaceholder(name, new[] { "{lang}", "{language}" }, lang);

        // Очистка от дублирующихся или висящих разделителей
        name = CleanSeparators(name);

        if (string.IsNullOrWhiteSpace(name))
        {
            name = $"{originalStem}_{trackIdStr}";
        }

        return $"{name}{ext}";
    }

    /// <summary>
    /// Безопасно заменяет плейсхолдеры на значения, убирая лишние разделители при отсутствии значения.
    /// </summary>
    private static string ReplacePlaceholder(string template, string[] placeholders, string value)
    {
        foreach (var placeholder in placeholders)
        {
            if (template.Contains(placeholder, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(value))
                {
                    template = Regex.Replace(template, Regex.Escape(placeholder), value, RegexOptions.IgnoreCase);
                }
                else
                {
                    template = Regex.Replace(template, "_" + Regex.Escape(placeholder), "", RegexOptions.IgnoreCase);
                    template = Regex.Replace(template, Regex.Escape(placeholder) + "_", "", RegexOptions.IgnoreCase);
                    template = Regex.Replace(template, "-" + Regex.Escape(placeholder), "", RegexOptions.IgnoreCase);
                    template = Regex.Replace(template, Regex.Escape(placeholder) + "-", "", RegexOptions.IgnoreCase);
                    template = Regex.Replace(template, Regex.Escape(placeholder), "", RegexOptions.IgnoreCase);
                }
            }
        }
        return template;
    }

    /// <summary>
    /// Очищает имя от лишних разделителей.
    /// </summary>
    private static string CleanSeparators(string name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;

        // 1. Очистка пустых скобок, оставшихся от незаполненных плейсхолдеров
        name = Regex.Replace(name, @"\[\s*\]", "");
        name = Regex.Replace(name, @"\(\s*\)", "");
        name = Regex.Replace(name, @"\{\s*\}", "");

        // 2. Схлопывание дублирующихся разделителей и пробелов
        name = Regex.Replace(name, @"_+", "_");
        name = Regex.Replace(name, @"-+", "-");
        name = Regex.Replace(name, @"\s+", " ");
        name = Regex.Replace(name, @"_-", "-");
        name = Regex.Replace(name, @"-_", "-");

        return name.Trim('_', '-', ' ');
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
                    _logService.Write("script.track_extractor.temp_removed", LogLevel.Debug, LogStatus.Succeeded, "Временный недописанный файл удалён", source: Name, properties: LogProps.Create("OutputName", LogProps.FileName(path)).With("CleanupState", "Removed"));
                }
                catch (Exception ex)
                {
                    _logService.Write("script.track_extractor.temp_cleanup_failed", LogLevel.Warning, LogStatus.Failed, "Временный файл не удалён при отмене операции", ex, Name, properties: LogProps.Create("ErrorCode", "TEMP_CLEANUP_FAILED").With("OutputName", LogProps.FileName(path)).With("CleanupState", "Failed"));
                }
            }
        }
    }
}
