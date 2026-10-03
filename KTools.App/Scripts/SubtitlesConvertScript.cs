// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Infrastructure;
using KTools_App.Models;
using KTools_App.Services.Contracts;

using ExecutionContext = KTools_App.Models.ExecutionContext;

namespace KTools_App.Scripts;

/// <summary>
/// Скрипт профессиональной конвертации и очистки субтитров (ASS/SRT -> VTT/SRT).
/// Поддерживает гибкую чистку тегов форматирования и удаление CAPS-реплик.
/// </summary>
public sealed class SubtitlesConvertScript : AbstractScript
{
    private readonly IFFmpegRunner _ffmpegRunner;
    private readonly IAssParser _assParser;

    public SubtitlesConvertScript(ILogService logService, ISettingsManager settingsManager, IPathManager pathManager, IFFmpegRunner ffmpegRunner, IAssParser assParser)
        : base(logService, settingsManager, pathManager)
    {
        _ffmpegRunner = ffmpegRunner ?? throw new ArgumentNullException(nameof(ffmpegRunner));
        _assParser = assParser ?? throw new ArgumentNullException(nameof(assParser));
    }

    /// <summary>
    /// Локализованное название скрипта.
    /// </summary>
    public override string Name => AppConstants.ScriptMetadata.AssToVttName;

    /// <summary>
    /// Описание назначения скрипта для UI.
    /// </summary>
    public override string Description => AppConstants.ScriptMetadata.AssToVttDesc;

    /// <summary>
    /// Категория обработки.
    /// </summary>
    public override string Category => AppConstants.ScriptCategory.Subtitles;

    /// <summary>
    /// Имя системной Fluent-иконки.
    /// </summary>
    public override string IconName => AppConstants.ScriptIcons.SubtitlesConvert;

    /// <summary>
    /// Поддерживаемые входящие форматы файлов субтитров.
    /// </summary>
    public override string[] FileExtensions => AppConstants.SubtitleExtensions.ToArray();

    /// <summary>
    /// Зависимости скрипта: утилита FFmpeg.
    /// </summary>
    public override string[] RequiredDependencies => new[] { "ffmpeg" };

    /// <summary>
    /// Декларативная схема параметров настроек скрипта.
    /// </summary>
    public override List<SettingField> SettingsSchema => new()
    {
        new SettingField(
            "target_format",
            "Целевой формат",
            SettingType.Combo,
            "WebVTT",
            "Экспорт",
            options: new List<string> { "WebVTT", "SRT", "ASS" }),

        new SettingField(
            "strip_formatting",
            "Удалять теги форматирования",
            SettingType.Checkbox,
            true,
            "Очистка",
            comment: "Полная очистка всех тегов форматирования"),

        new SettingField(
            "keep_styles",
            "Сохранять оформление стилей",
            SettingType.Checkbox,
            false,
            "Очистка",
            comment: "Сохранять курсив и жирность из стилей ASS"),

        new SettingField(
            "strip_caps",
            "Удалять текст в верхнем регистре (КАПС)",
            SettingType.Checkbox,
            false,
            "Очистка",
            comment: "Автоматически вырезать реплики CAPS LOCK"),

        new SettingField(
            "text_patterns",
            "Паттерны регулярных выражений",
            SettingType.KeywordList,
            new List<Dictionary<string, object>>(),
            "Очистка",
            comment: "Последовательное удаление текста/строк по регулярным выражениям"),

        new SettingField(
            "delete_original",
            "Удалить исходный файл",
            SettingType.Checkbox,
            false,
            "Общие")
    };

    /// <summary>
    /// Текущее состояние фильтрации для предпросмотра и выполнения.
    /// </summary>
    public KTools_App.Models.SubtitleFilterState FilterState { get; } = new();

    /// <summary>
    /// Асинхронно запускает процесс конвертации одного файла субтитров.
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

        // Извлекаем пользовательские настройки и синхронизируем с FilterState
        string targetFormat = GetSettingValue(settings, "target_format", "WebVTT");

        // Синхронизируем FilterState с текущими настройками перед выполнением
        FilterState.StripFormatting = GetSettingValue(settings, "strip_formatting", FilterState.StripFormatting);
        FilterState.StripCaps = GetSettingValue(settings, "strip_caps", FilterState.StripCaps);

        var rawPatterns = GetSettingValue<List<Dictionary<string, object>>?>(settings, "text_patterns", null);
        FilterState.TextPatterns.Clear();
        if (rawPatterns != null)
        {
            FilterState.TextPatterns.AddRange(rawPatterns);
        }

        bool stripFormatting = FilterState.StripFormatting;
        bool keepStyles = GetSettingValue(settings, "keep_styles", false);
        bool stripCaps = FilterState.StripCaps;
        bool deleteOriginal = GetSettingValue(settings, "delete_original", false);

        string originalName = Path.GetFileName(filePath);
        string inputExt = Path.GetExtension(filePath).ToLowerInvariant();

        // Сопоставляем выходное расширение
        string targetExt = targetFormat.ToUpperInvariant() switch
        {
            "SRT" => ".srt",
            "ASS" => ".ass",
            _ => ".vtt"
        };

        _logService.Write(
            "script.subtitles.started",
            LogLevel.Debug,
            LogStatus.Running,
            $"Начата конвертация субтитров '{originalName}' в формат {LogRedactor.CompactSafeToken(targetFormat)}",
            source: Name,
            properties: LogProps
                .Create("InputName", LogProps.FileName(filePath))
                .With("Extension", LogRedactor.CompactSafeToken(targetFormat)));

        // Вычисляем директорию вывода
        string targetDir = string.IsNullOrEmpty(outputPath)
            ? Path.GetDirectoryName(filePath) ?? AppContext.BaseDirectory
            : outputPath;

        string baseOutputName = Path.GetFileNameWithoutExtension(filePath) + targetExt;
        string targetOutputFilePath = Path.Combine(targetDir, baseOutputName);
        string outputFilePath = GetSafeOutputPath(filePath, targetOutputFilePath, settings);
        string outputFileName = Path.GetFileName(outputFilePath);

        // Проверяем перезапись существующего файла
        bool overwrite = _settingsManager.GetSetting(
            "General", "OverwriteExisting", false);

        if (File.Exists(outputFilePath) && !overwrite)
        {
            string skipMsg = $"⏭ ПРОПУСК (существует): {outputFileName}";
            _logService.Write("script.subtitles.skipped", LogLevel.Info, LogStatus.Skipped, skipMsg, source: Name, properties: LogProps.Create("InputName", LogProps.FileName(filePath)));
            progressCallback(
                fileIndex,
                totalCount,
                $"Пропуск (существует): {outputFileName}",
                100.0);
            results.Add(skipMsg);
            return ExecutionResult.Skipped(
                context,
                results,
                errorCode: "output-exists",
                outputFile: outputFilePath,
                outputExists: true);
        }

        // Проверяем «быстрый путь» (конвертация без изменения реплик)
        if (keepStyles && !stripFormatting && !stripCaps)
        {
            progressCallback(fileIndex, totalCount, "Запуск FFmpeg напрямую...", 0.0);
            _logService.Write(
                "script.subtitles.remux_started",
                LogLevel.Debug,
                LogStatus.Running,
                $"Запущен прямой ремуксинг субтитров через FFmpeg: '{originalName}' -> '{LogProps.FileName(outputFilePath)}'",
                source: Name,
                properties: LogProps
                    .Create("Tool", "ffmpeg")
                    .With("OutputName", LogProps.FileName(outputFilePath)));

            ProcessResult fastResult = await _ffmpegRunner.RunAsync(
                inputPath: filePath,
                outputPath: outputFilePath,
                overwrite: overwrite,
                cancellationToken: CancellationToken);

            if (fastResult.IsSuccess)
            {
                progressCallback(fileIndex, totalCount, "Завершено!", 100.0);
                results.Add($"✅ УСПЕХ: {outputFileName}");
                if (deleteOriginal && !IsCancelled)
                {
                    await DeleteSourceAsync(filePath, results);
                }
            }
            else
            {
                await CleanupFailedOutputFileAsync(outputFilePath);
                progressCallback(fileIndex, totalCount, "Ошибка!", 0.0);
                results.Add($"❌ Ошибка FFmpeg: {outputFileName}");
            }
            if (fastResult.IsSuccess && File.Exists(outputFilePath))
            {
                if (deleteOriginal && File.Exists(filePath))
                {
                    return ExecutionResult.PartiallySucceeded(
                        context,
                        results,
                        errorCode: "source-cleanup-failed",
                        outputFile: outputFilePath,
                        outputExists: true,
                        cleanupState: CleanupState.Failed);
                }
                return ExecutionResult.Succeeded(
                    context,
                    results,
                    outputFile: outputFilePath,
                    outputExists: true,
                    cleanupState: deleteOriginal ? CleanupState.Completed : CleanupState.NotRequired);
            }
            return ExecutionResult.Failed(
                context,
                results,
                errorCode: "output-missing",
                outputFile: outputFilePath,
                outputExists: File.Exists(outputFilePath),
                cleanupState: CleanupState.Completed);
        }

        // Парсинг файла субтитров
        progressCallback(fileIndex, totalCount, "Анализ структуры субтитров...", 0.0);
        AssData assData;
        try
        {
            assData = _assParser.Parse(filePath);
        }
        catch (Exception ex)
        {
            _logService.Write(
                "script.subtitles.parse_failed",
                LogLevel.Error,
                LogStatus.Failed,
                $"Субтитры '{originalName}' не разобраны",
                ex,
                Name,
                properties: LogProps
                    .Create("ErrorCode", "SUBTITLE_PARSE_FAILED")
                    .With("InputName", LogProps.FileName(filePath))
                    .With("Retryable", false));
            results.Add($"❌ Ошибка парсинга: {originalName}");
            progressCallback(fileIndex, totalCount, "Ошибка парсинга!", 0.0);
            return ExecutionResult.FromException(
                context,
                ex,
                results,
                errorCode: "parse-failed",
                cleanupState: CleanupState.NotStarted);
        }

        if (assData.Dialogues.Count == 0)
        {
            string emptyMsg = $"⏭ ПРОПУСК (нет строк диалогов): {originalName}";
            _logService.Write("script.subtitles.empty", LogLevel.Info, LogStatus.Skipped, emptyMsg, source: Name, properties: LogProps.Create("InputName", LogProps.FileName(filePath)));
            progressCallback(
                fileIndex,
                totalCount,
                $"Пропуск (нет строк): {originalName}",
                100.0);
            results.Add(emptyMsg);
            return ExecutionResult.Skipped(
                context,
                results,
                errorCode: "no-dialogues",
                outputFile: outputFilePath,
                outputExists: File.Exists(outputFilePath));
        }

        // Подготовка временного файла .ass с отфильтрованными репликами
        string tempDir = Path.Combine(
            _pathManager.GetSettingsDirectory(),
            "temp_subs_" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(tempDir);
        }
        catch (Exception ex)
        {
            _logService.Write(
                "script.subtitles.temp_dir_failed",
                LogLevel.Error,
                LogStatus.Failed,
                "Временный каталог для обработки субтитров не создан",
                ex,
                Name,
                properties: LogProps
                    .Create("ErrorCode", "TEMP_DIR_FAILED")
                    .With("CleanupState", "NotStarted"));
            results.Add($"❌ Сбой файловой системы: {originalName}");
            progressCallback(fileIndex, totalCount, "Ошибка создания папки!", 0.0);
            return ExecutionResult.FromException(
                context,
                ex,
                results,
                errorCode: "temporary-directory-failed",
                cleanupState: CleanupState.NotStarted);
        }

        string tempAssPath = Path.Combine(tempDir, "temp.ass");
        int deletedLinesCount = 0;
        int modifiedLinesCount = 0;
        Exception? terminalException = null;

        try
        {
            using (var writer = new StreamWriter(
                tempAssPath,
                false,
                Encoding.UTF8))
            {
                if (targetFormat.ToUpperInvariant() == "ASS" && !string.IsNullOrEmpty(assData.Header))
                {
                    writer.Write(assData.Header);
                    if (!assData.Header.EndsWith("\n"))
                    {
                        writer.WriteLine();
                    }
                }
                else
                {
                    writer.Write(_assParser.GetMinimalHeader());
                }

                int dialogueIndex = 0;
                foreach (var d in assData.Dialogues)
                {
                    if (IsCancelled)
                    {
                        break;
                    }

                    // Пропуск изначально пустых строк
                    if (string.IsNullOrWhiteSpace(
                        _assParser.StripTags(d.Text)))
                    {
                        dialogueIndex++;
                        continue;
                    }

                    // Проверяем фильтрацию по актеру, стилю или эффекту
                    bool isFiltered =
                        (!string.IsNullOrEmpty(d.Actor) && FilterState.ExcludedActors.Contains(d.Actor)) ||
                        (!string.IsNullOrEmpty(d.Style) && FilterState.ExcludedStyles.Contains(d.Style)) ||
                        (!string.IsNullOrEmpty(d.Effect) && FilterState.ExcludedEffects.Contains(d.Effect));

                    // Проверяем ручные переопределения
                    bool isManuallyIncluded = FilterState.ManualInclusions.TryGetValue(filePath, out var incSet) && incSet.Contains(dialogueIndex);
                    bool isManuallyExcluded = FilterState.ManualExclusions.TryGetValue(filePath, out var excSet) && excSet.Contains(dialogueIndex);

                    string text = d.Text;

                    // Применяем последовательную regex-фильтрацию
                    bool isDeletedByRegex = false;
                    bool isModifiedByRegex = false;
                    foreach (var patternDict in FilterState.TextPatterns)
                    {
                        if (patternDict.TryGetValue("active", out var act) && SafeGetBool(act) &&
                            patternDict.TryGetValue("word", out var p) && p?.ToString() is string pattern && !string.IsNullOrEmpty(pattern))
                        {
                            bool onlyPart = patternDict.TryGetValue("only_part", out var op) && SafeGetBool(op);
                            try
                            {
                                var regex = new System.Text.RegularExpressions.Regex(pattern);
                                if (regex.IsMatch(text))
                                {
                                    if (onlyPart)
                                    {
                                        string newText = regex.Replace(text, string.Empty);
                                        if (newText != text)
                                        {
                                            text = newText;
                                            isModifiedByRegex = true;
                                        }
                                    }
                                    else
                                    {
                                        isDeletedByRegex = true;
                                        break;
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                _logService.Write("subtitle.pattern.invalid", LogLevel.Warning, LogStatus.Skipped, $"Регулярное выражение конвертера недопустимо, правило пропущено: {pattern}", ex, Name, properties: LogProps.Create("ErrorCode", "INVALID_REGEX").With("Key", LogRedactor.CompactSafeToken(pattern)));
                            }
                        }
                    }

                    if (isModifiedByRegex && !isDeletedByRegex)
                    {
                        modifiedLinesCount++;
                    }

                    // Применяем чистку CAPS LOCK
                    if (stripCaps)
                    {
                        text = _assParser.StripCaps(text);
                    }

                    // Применяем удаление тегов форматирования
                    if (stripFormatting)
                    {
                        text = _assParser.StripTags(text);
                    }

                    bool isEmptyAfterFilters = string.IsNullOrWhiteSpace(_assParser.StripTags(text));

                    // Если строка включена вручную и в результате очистки фильтрами она стала пустой,
                    // возвращаем оригинальный текст, чтобы она корректно попала в финальные субтитры.
                    if (isManuallyIncluded && isEmptyAfterFilters)
                    {
                        text = d.Text;
                        isEmptyAfterFilters = false;
                    }

                    // Строка удаляется, если:
                    // 1. Она изначально пустая (всегда удаляется, обработано выше)
                    // 2. Она явно исключена пользователем вручную
                    // 3. Она пустая после фильтров и не была вручную включена
                    // 4. Она попала под фильтр и не была вручную включена
                    bool isDeleted = isManuallyExcluded ||
                                     (isDeletedByRegex && !isManuallyIncluded) ||
                                     (isEmptyAfterFilters && !isManuallyIncluded) ||
                                     (isFiltered && !isManuallyIncluded);

                    if (isDeleted)
                    {
                        if (isDeletedByRegex && !isManuallyIncluded)
                        {
                            deletedLinesCount++;
                        }
                        dialogueIndex++;
                        continue;
                    }

                    var tempDialogue = new AssDialogue(
                        start: d.Start,
                        end: d.End,
                        style: d.Style,
                        actor: d.Actor,
                        effect: d.Effect,
                        text: text);

                    writer.WriteLine(_assParser.ToAssLine(tempDialogue));
                    dialogueIndex++;
                }
            }

            if (IsCancelled)
            {
                CleanupIfCancelled(outputFilePath);
                progressCallback(
                    fileIndex,
                    totalCount,
                    "Отменено пользователем",
                    0.0);
                results.Add($"⚠ Отменено: {outputFileName}");
                return ExecutionResult.Cancelled(
                    context,
                    results,
                    errorCode: "cancelled",
                    outputFile: outputFilePath,
                    outputExists: File.Exists(outputFilePath),
                    cleanupState: CleanupState.Completed);
            }


            // Транскодирование отфильтрованного временного ASS в выходной формат
            progressCallback(fileIndex, totalCount, "Финальное сохранение...", 50.0);
            _logService.Write(
                "script.subtitles.ass_convert_started",
                LogLevel.Debug,
                LogStatus.Running,
                $"Запущен FFmpeg для конвертации временного ASS: '{originalName}' -> '{LogProps.FileName(outputFilePath)}'",
                source: Name,
                properties: LogProps
                    .Create("Tool", "ffmpeg")
                    .With("OutputName", LogProps.FileName(outputFilePath)));

            ProcessResult result = await _ffmpegRunner.RunAsync(
                inputPath: tempAssPath,
                outputPath: outputFilePath,
                overwrite: overwrite,
                cancellationToken: CancellationToken);

            if (result.IsSuccess)
            {
                progressCallback(fileIndex, totalCount, "Успешно завершено!", 100.0);
                results.Add($"✅ Конвертирован: {outputFileName}");

                if (deletedLinesCount > 0 || modifiedLinesCount > 0)
                {
                    _logService.Write("subtitle.cleanup.applied", LogLevel.Debug, LogStatus.Succeeded, $"Очистка субтитров по регулярным выражениям для '{originalName}': удалено строк {deletedLinesCount}, изменено строк {modifiedLinesCount}", source: Name, properties: LogProps.Create("InputName", LogProps.FileName(filePath)).With("Count", deletedLinesCount).With("Total", modifiedLinesCount));
                }

                if (deleteOriginal)
                {
                    await DeleteSourceAsync(filePath, results);
                }
                if (!File.Exists(outputFilePath))
                {
                    return ExecutionResult.Failed(
                        context,
                        results,
                        errorCode: "output-missing",
                        outputFile: outputFilePath,
                        outputExists: false,
                        cleanupState: CleanupState.Completed);
                }
                return deleteOriginal && File.Exists(filePath)
                    ? ExecutionResult.PartiallySucceeded(
                        context,
                        results,
                        errorCode: "source-cleanup-failed",
                        outputFile: outputFilePath,
                        outputExists: true,
                        cleanupState: CleanupState.Failed)
                    : ExecutionResult.Succeeded(
                        context,
                        results,
                        outputFile: outputFilePath,
                        outputExists: true,
                        cleanupState: deleteOriginal ? CleanupState.Completed : CleanupState.NotRequired);
            }
            else
            {
                await CleanupFailedOutputFileAsync(outputFilePath);
                progressCallback(fileIndex, totalCount, "Ошибка FFmpeg!", 0.0);
                results.Add($"❌ Ошибка FFmpeg: {outputFileName}");
                return ExecutionResult.Failed(
                    context,
                    results,
                    errorCode: "output-missing",
                    outputFile: outputFilePath,
                    outputExists: File.Exists(outputFilePath),
                    cleanupState: CleanupState.Completed);
            }
        }
        catch (Exception ex)
        {
            await CleanupFailedOutputFileAsync(outputFilePath);
            _logService.Write(
                "script.subtitles.failed",
                LogLevel.Error,
                LogStatus.Failed,
                $"Обработка субтитров для '{originalName}' не выполнена",
                ex,
                Name,
                properties: LogProps
                    .Create("ErrorCode", "SUBTITLE_CONVERT_FAILED")
                    .With("InputName", LogProps.FileName(filePath))
                    .With("Retryable", true));
            results.Add($"❌ Критическая ошибка: {originalName}");
            progressCallback(fileIndex, totalCount, "Критическая ошибка!", 0.0);
            terminalException = ex;
        }
        finally
        {
            // Удаляем временные файлы
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
            catch (Exception ex)
            {
                _logService.Write(
                    "script.subtitles.temp_cleanup_failed",
                    LogLevel.Warning,
                    LogStatus.Failed,
                    "Временный каталог обработки субтитров не удалён",
                    ex,
                    Name,
                    properties: LogProps
                        .Create("ErrorCode", "TEMP_DIR_CLEANUP_FAILED")
                        .With("CleanupState", "Failed"));
            }
        }

        if (terminalException is not null)
        {
            return ExecutionResult.FromException(
                context,
                terminalException,
                results,
                errorCode: "subtitle-conversion-exception",
                outputFile: outputFilePath,
                outputExists: File.Exists(outputFilePath),
                cleanupState: CleanupState.Completed);
        }

        return ExecutionResult.Failed(
            context,
            results,
            errorCode: "conversion-ended",
            outputFile: outputFilePath,
            outputExists: File.Exists(outputFilePath),
            cleanupState: CleanupState.Completed);
    }

    public override string GetOutputExtension(string inputPath)
    {
        string settingsGroup = _settingsManager.GetSafeGroupName(Name);
        string targetFormat = _settingsManager.GetSetting(settingsGroup, "target_format", "WebVTT");
        return targetFormat.ToUpperInvariant() switch
        {
            "SRT" => ".srt",
            "ASS" => ".ass",
            _ => ".vtt"
        };
    }

    private static bool SafeGetBool(object? obj)
    {
        if (obj == null) return false;
        if (obj is bool b) return b;
        if (obj is System.Text.Json.JsonElement elem)
        {
            if (elem.ValueKind == System.Text.Json.JsonValueKind.True) return true;
            if (elem.ValueKind == System.Text.Json.JsonValueKind.False) return false;
            if (elem.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return bool.TryParse(elem.GetString(), out var parsed) && parsed;
            }
        }
        return false;
    }
}
