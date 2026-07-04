using KTools_App.Services.Contracts;
// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

using KTools_App.Core;
using KTools_App.Infrastructure;

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
    public override string IconName => "font";

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

        // Извлекаем пользовательские настройки и синхронизируем с FilterState
        string targetFormat = GetSettingValue(settings, "target_format", "WebVTT");
        
        // Синхронизируем FilterState с текущими настройками перед выполнением
        FilterState.StripFormatting = GetSettingValue(settings, "strip_formatting", FilterState.StripFormatting);
        FilterState.StripCaps = GetSettingValue(settings, "strip_caps", FilterState.StripCaps);

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

        _logService.Info(
            $"Начало конвертации субтитров для '{originalName}'. " +
            $"Целевой формат: {targetFormat}",
            "SubtitlesConvertScript");

        // Вычисляем директорию вывода
        string targetDir = string.IsNullOrEmpty(outputPath)
            ? Path.GetDirectoryName(filePath) ?? AppContext.BaseDirectory
            : outputPath;

        string baseOutputName = Path.GetFileNameWithoutExtension(filePath) + targetExt;
        string targetOutputFilePath = Path.Combine(targetDir, baseOutputName);
        string outputFilePath = GetSafeOutputPath(filePath, targetOutputFilePath);
        string outputFileName = Path.GetFileName(outputFilePath);

        // Проверяем перезапись существующего файла
        bool overwrite = _settingsManager.GetSetting(
            "General", "OverwriteExisting", false);
            
        if (File.Exists(outputFilePath) && !overwrite)
        {
            string skipMsg = $"⏭ ПРОПУСК (существует): {outputFileName}";
            _logService.Info(skipMsg, "SubtitlesConvertScript");
            progressCallback(
                fileIndex,
                totalCount,
                $"Пропуск (существует): {outputFileName}",
                100.0);
            results.Add(skipMsg);
            return results;
        }

        // Проверяем «быстрый путь» (конвертация без изменения реплик)
        if (keepStyles && !stripFormatting && !stripCaps)
        {
            progressCallback(fileIndex, totalCount, "Запуск FFmpeg напрямую...", 0.0);
            _logService.Info(
                $"Запущен прямой ремуксинг субтитров FFmpeg: " +
                $"'{originalName}' -> '{outputFileName}'",
                "SubtitlesConvertScript");

            bool fastSuccess = await _ffmpegRunner.RunAsync(
                inputPath: filePath,
                outputPath: outputFilePath,
                overwrite: overwrite);

            if (fastSuccess)
            {
                progressCallback(fileIndex, totalCount, "Завершено!", 100.0);
                results.Add($"✅ УСПЕХ: {outputFileName}");
                if (deleteOriginal)
                {
                    DeleteSource(filePath, results);
                }
            }
            else
            {
                CleanupFailedOutputFile(outputFilePath);
                progressCallback(fileIndex, totalCount, "Ошибка!", 0.0);
                results.Add($"❌ Ошибка FFmpeg: {outputFileName}");
            }
            return results;
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
            _logService.Exception(
                ex,
                $"Ошибка парсинга субтитров '{originalName}': {ex.Message}",
                "SubtitlesConvertScript");
            results.Add($"❌ Ошибка парсинга: {originalName}");
            progressCallback(fileIndex, totalCount, "Ошибка парсинга!", 0.0);
            return results;
        }

        if (assData.Dialogues.Count == 0)
        {
            string emptyMsg = $"⏭ ПРОПУСК (нет строк диалогов): {originalName}";
            _logService.Info(emptyMsg, "SubtitlesConvertScript");
            progressCallback(
                fileIndex,
                totalCount,
                $"Пропуск (нет строк): {originalName}",
                100.0);
            results.Add(emptyMsg);
            return results;
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
            _logService.Exception(
                ex,
                $"Не удалось создать временную директорию: {ex.Message}",
                "SubtitlesConvertScript");
            results.Add($"❌ Сбой файловой системы: {originalName}");
            progressCallback(fileIndex, totalCount, "Ошибка создания папки!", 0.0);
            return results;
        }

        string tempAssPath = Path.Combine(tempDir, "temp.ass");
        
        try
        {
            using (var writer = new StreamWriter(
                tempAssPath,
                false,
                Encoding.UTF8))
            {
                writer.Write(_assParser.GetMinimalHeader());
                
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
                                     (isEmptyAfterFilters && !isManuallyIncluded) || 
                                     (isFiltered && !isManuallyIncluded);

                    if (isDeleted)
                    {
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
                return results;
            }

            // Транскодирование отфильтрованного временного ASS в выходной формат
            progressCallback(fileIndex, totalCount, "Финальное сохранение...", 50.0);
            _logService.Info(
                $"Запуск FFmpeg для конвертации временного ASS: " +
                $"'{originalName}' -> '{outputFileName}'",
                "SubtitlesConvertScript");

            bool success = await _ffmpegRunner.RunAsync(
                inputPath: tempAssPath,
                outputPath: outputFilePath,
                overwrite: overwrite);

            if (success)
            {
                progressCallback(fileIndex, totalCount, "Успешно завершено!", 100.0);
                results.Add($"✅ Конвертирован: {outputFileName}");
                
                if (deleteOriginal)
                {
                    DeleteSource(filePath, results);
                }
            }
            else
            {
                CleanupFailedOutputFile(outputFilePath);
                progressCallback(fileIndex, totalCount, "Ошибка FFmpeg!", 0.0);
                results.Add($"❌ Ошибка FFmpeg: {outputFileName}");
            }
        }
        catch (Exception ex)
        {
            CleanupFailedOutputFile(outputFilePath);
            _logService.Exception(
                ex,
                $"Критическая ошибка обработки субтитров для '{originalName}': " +
                $"{ex.Message}",
                "SubtitlesConvertScript");
            results.Add($"❌ Критическая ошибка: {originalName}");
            progressCallback(fileIndex, totalCount, "Критическая ошибка!", 0.0);
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
                _logService.DebugLog(
                    $"Не удалось удалить временную директорию субтитров: " +
                    $"{ex.Message}",
                    "SubtitlesConvertScript");
            }
        }

        return results;
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
}
