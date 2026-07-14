// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KTools_App.Core;
using KTools_App.Services.Contracts;

namespace KTools_App.Scripts;

/// <summary>
/// Скрипт для сдвига аудиопотока (задержки или опережения) с сохранением в Lossless-форматы WAV/FLAC.
/// Все комментарии, логи и документация выполнены на русском языке в соответствии с регламентом.
/// </summary>
public sealed class AudioShiftScript : AbstractScript
{
    private readonly IFFmpegRunner _ffmpegRunner;

    public AudioShiftScript(ILogService logService, ISettingsManager settingsManager, IPathManager pathManager, IFFmpegRunner ffmpegRunner)
        : base(logService, settingsManager, pathManager)
    {
        _ffmpegRunner = ffmpegRunner ?? throw new ArgumentNullException(nameof(ffmpegRunner));
    }

    /// <summary>
    /// Русское название скрипта.
    /// </summary>
    public override string Name => AppConstants.ScriptMetadata.AudioShiftName;

    /// <summary>
    /// Русское описание назначения скрипта для интерфейса.
    /// </summary>
    public override string Description => AppConstants.ScriptMetadata.AudioShiftDesc;

    /// <summary>
    /// Категория медиаобработки.
    /// </summary>
    public override string Category => AppConstants.ScriptCategory.Audio;

    /// <summary>
    /// Имя Fluent-иконки для отображения в меню.
    /// </summary>
    public override string IconName => AppConstants.ScriptIcons.AudioShift;

    /// <summary>
    /// Список поддерживаемых расширений файлов.
    /// </summary>
    public override string[] FileExtensions => AppConstants.AudioStreams
        .Concat(AppConstants.AudioContainers)
        .ToArray();

    /// <summary>
    /// Список внешних зависимостей.
    /// </summary>
    public override string[] RequiredDependencies => new[] { "ffmpeg" };

    /// <summary>
    /// Поддерживает ли скрипт параллельную обработку файлов.
    /// </summary>
    public override bool SupportsParallel => true;

    /// <summary>
    /// Схема настроек параметров скрипта для генерации UI.
    /// </summary>
    public override List<SettingField> SettingsSchema => new()
    {
        new SettingField(
            "ShiftMs",
            "Величина сдвига (мс)",
            SettingType.Int,
            1000,
            "Настройки сдвига"),

        new SettingField(
            "ShiftDirection",
            "Направление сдвига",
            SettingType.Combo,
            "Вперед",
            "Настройки сдвига",
            options: new List<string> { "Вперед", "Назад" }),

        new SettingField(
            "OutputFormat",
            "Формат вывода (Lossless)",
            SettingType.Combo,
            "FLAC",
            "Настройки экспорта",
            options: new List<string> { "FLAC", "WAV" })
    };

    /// <summary>
    /// Выполнение сдвига аудио для одного файла.
    /// </summary>
    public override async Task<List<string>> ExecuteSingleAsync(
        string filePath,
        Dictionary<string, object> settings,
        string? outputPath,
        ScriptProgressCallback progressCallback,
        int fileIndex,
        int totalCount)
    {
        var results = new List<string>();
        string originalName = Path.GetFileName(filePath);

        _logService.Info($"Начало сдвига аудиопотока для файла: '{originalName}'", "AudioShiftScript");
        progressCallback(fileIndex, totalCount, "Чтение метаданных длительности...", 0.0);

        int shiftMs = GetSettingValue(settings, "ShiftMs", 1000);
        string direction = GetSettingValue(settings, "ShiftDirection", "Вперед");
        string format = GetSettingValue(settings, "OutputFormat", "FLAC");

        _logService.Info($"Параметры обработки: сдвиг {shiftMs} мс, направление: {direction}, формат: {format}", "AudioShiftScript");

        // 1. Получение длительности аудиофайла для отслеживания прогресса
        double duration = 0.0;
        try
        {
            var info = await _ffmpegRunner.GetVideoInfoAsync(filePath);
            if (info != null && info.RootElement.TryGetProperty("format", out var formatProp))
            {
                if (formatProp.TryGetProperty("duration", out var durProp))
                {
                    if (durProp.ValueKind == JsonValueKind.String &&
                        double.TryParse(
                            durProp.GetString(),
                            NumberStyles.Any,
                            CultureInfo.InvariantCulture,
                            out double d))
                    {
                        duration = d;
                    }
                    else if (durProp.ValueKind == JsonValueKind.Number)
                    {
                        duration = durProp.GetDouble();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logService.Exception(ex, $"Не удалось прочесть метаданные длительности для '{originalName}': {ex.Message}", "AudioShiftScript");
        }

        _logService.DebugLog($"Длительность аудиофайла '{originalName}': {duration:F2} сек.", "AudioShiftScript");

        // 2. Определение пути к выходному файлу
        string targetDir = string.IsNullOrEmpty(outputPath)
            ? Path.GetDirectoryName(filePath) ?? AppContext.BaseDirectory
            : outputPath;

        string ext = format.ToLowerInvariant();
        string outputName = $"{Path.GetFileNameWithoutExtension(filePath)}_shifted.{ext}";
        string outputFilePath = Path.Combine(targetDir, outputName);
        outputFilePath = GetSafeOutputPath(filePath, outputFilePath, settings);

        // Проверка флага перезаписи
        bool overwrite = _settingsManager.GetSetting("General", "OverwriteExisting", false);
        if (File.Exists(outputFilePath) && !overwrite)
        {
            string msg = $"Пропуск (существует): {outputName}";
            progressCallback(fileIndex, totalCount, msg, 100.0);
            results.Add($"⏭ ПРОПУСК (файл существует): {outputName}");
            _logService.Info($"Файл результата '{outputFilePath}' уже существует, обработка пропущена.", "AudioShiftScript");
            return results;
        }

        // 3. Формирование аргументов FFmpeg
        var extraArgs = new List<string>();

        if (direction == "Вперед")
        {
            // Задержка аудио: adelay
            extraArgs.Add("-af");
            extraArgs.Add($"adelay={shiftMs}:all=1");
        }
        else
        {
            // Опережение аудио: atrim и сброс PTS
            double shiftSec = shiftMs / 1000.0;
            string shiftSecStr = shiftSec.ToString("F3", CultureInfo.InvariantCulture);
            extraArgs.Add("-af");
            extraArgs.Add($"atrim=start={shiftSecStr},asetpts=PTS-STARTPTS");
        }

        // Задаем кодек в зависимости от lossless формата
        if (format == "FLAC")
        {
            extraArgs.Add("-c:a");
            extraArgs.Add("flac");
        }
        else // WAV
        {
            extraArgs.Add("-c:a");
            extraArgs.Add("pcm_s16le");
        }

        // 4. Асинхронный запуск FFmpeg
        progressCallback(fileIndex, totalCount, "Запуск FFmpeg обработки...", 0.0);
        using var cts = new CancellationTokenSource();

        var runTask = _ffmpegRunner.RunAsync(
            inputPath: filePath,
            outputPath: outputFilePath,
            extraArgs: extraArgs,
            overwrite: true,
            totalDuration: duration,
            onProgress: pct =>
            {
                string text = $"Обработка сдвига... {pct.Percent:F1}%";
                progressCallback(fileIndex, totalCount, text, pct.Percent);
            },
            cancellationToken: cts.Token);

        while (!runTask.IsCompleted)
        {
            if (IsCancelled)
            {
                cts.Cancel();
                break;
            }
            await Task.Delay(200);
        }

        bool success = false;
        try
        {
            success = await runTask;
        }
        catch (Exception ex)
        {
            _logService.Exception(ex, $"Ошибка обработки файла '{originalName}' через FFmpeg: {ex.Message}", "AudioShiftScript");
        }

        if (IsCancelled || !success || !File.Exists(outputFilePath))
        {
            CleanupFailedOutputFile(outputFilePath);
            if (IsCancelled)
            {
                results.Add($"⚠ Отменено: {outputName}");
                _logService.Info($"Обработка файла '{originalName}' отменена пользователем.", "AudioShiftScript");
            }
            else
            {
                results.Add($"❌ Ошибка обработки файла для {originalName}");
                _logService.Error($"Не удалось выполнить сдвиг аудио для '{filePath}'. Проверьте логи FFmpeg.", "AudioShiftScript");
            }
            progressCallback(fileIndex, totalCount, "Ошибка или отмена", 100.0);
            return results;
        }

        _logService.Info($"Сдвиг аудио успешно выполнен, результат: '{outputFilePath}'", "AudioShiftScript");
        progressCallback(fileIndex, totalCount, "Завершено", 100.0);
        results.Add($"✔ Сдвиг аудио выполнен успешно: {outputName}");

        return results;
    }
}
