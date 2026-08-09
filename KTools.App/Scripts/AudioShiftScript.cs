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
    private readonly IEac3toRunner _eac3toRunner;

    public AudioShiftScript(
        ILogService logService,
        ISettingsManager settingsManager,
        IPathManager pathManager,
        IFFmpegRunner ffmpegRunner,
        IEac3toRunner eac3toRunner)
        : base(logService, settingsManager, pathManager)
    {
        _ffmpegRunner = ffmpegRunner ?? throw new ArgumentNullException(nameof(ffmpegRunner));
        _eac3toRunner = eac3toRunner ?? throw new ArgumentNullException(nameof(eac3toRunner));
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
    public override string[] RequiredDependencies => new[] { "ffmpeg", "eac3to" };

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
            "Формат и режим вывода",
            SettingType.Combo,
            "eac3to Bitstream (Без перекодирования)",
            "Настройки экспорта",
            options: new List<string> { "eac3to Bitstream (Без перекодирования)", "FLAC (FFmpeg Lossless)", "WAV (FFmpeg PCM)" })
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
        string format = GetSettingValue(settings, "OutputFormat", "eac3to Bitstream (Без перекодирования)");

        _logService.Info($"Параметры обработки: сдвиг {shiftMs} мс, направление: {direction}, режим: {format}", "AudioShiftScript");

        // 1. Определение пути к выходному файлу
        string targetDir = string.IsNullOrEmpty(outputPath)
            ? Path.GetDirectoryName(filePath) ?? AppContext.BaseDirectory
            : outputPath;

        bool isPassthrough = format.StartsWith("eac3to", StringComparison.OrdinalIgnoreCase);
        string inputExt = Path.GetExtension(filePath).TrimStart('.');
        string ext = isPassthrough
            ? inputExt
            : (format.Contains("FLAC", StringComparison.OrdinalIgnoreCase) ? "flac" : "wav");

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

        int signedShiftMs = direction == "Назад" ? -Math.Abs(shiftMs) : Math.Abs(shiftMs);

        // 2. Обработка через eac3to Bitstream (без перекодирования)
        if (isPassthrough)
        {
            progressCallback(fileIndex, totalCount, "Запуск eac3to прямоточного сдвига (Bitstream)...", 10.0);

            string shiftArg = signedShiftMs >= 0 ? $"+{signedShiftMs}ms" : $"{signedShiftMs}ms";
            var eac3toArgs = new List<string>
            {
                $"\"{filePath}\"",
                $"\"{outputFilePath}\"",
                shiftArg,
                "-silence",
                "-progressnumbers",
                "-log=nul"
            };

            using var ctsEac3 = new CancellationTokenSource();
            var eac3Task = _eac3toRunner.RunAsync(
                args: eac3toArgs,
                onProgress: pct =>
                {
                    string text = $"Сдвиг eac3to... {pct:F1}%";
                    progressCallback(fileIndex, totalCount, text, pct);
                },
                cancellationToken: ctsEac3.Token);

            while (!eac3Task.IsCompleted)
            {
                if (IsCancelled)
                {
                    ctsEac3.Cancel();
                    break;
                }
                await Task.Delay(200);
            }

            bool eac3Success = false;
            try
            {
                eac3Success = await eac3Task;
            }
            catch (Exception ex)
            {
                _logService.Exception(ex, $"Ошибка обработки файла '{originalName}' через eac3to: {ex.Message}", "AudioShiftScript");
            }

            if (IsCancelled || !eac3Success || !File.Exists(outputFilePath))
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
                    _logService.Error($"Не удалось выполнить прямоточный сдвиг аудио для '{filePath}'. Проверьте логи eac3to.", "AudioShiftScript");
                }
                progressCallback(fileIndex, totalCount, "Ошибка или отмена", 100.0);
                return results;
            }

            _logService.Info($"Прямоточный сдвиг аудио через eac3to успешно выполнен: '{outputFilePath}'", "AudioShiftScript");
            progressCallback(fileIndex, totalCount, "Завершено", 100.0);
            results.Add($"✔ Сдвиг аудио (Bitstream) выполнен успешно: {outputName}");
            return results;
        }

        // 3. Получение длительности для FFmpeg Lossless
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

        // 4. Формирование аргументов FFmpeg
        var extraArgs = new List<string>();

        if (direction == "Вперед")
        {
            extraArgs.Add("-af");
            extraArgs.Add($"adelay={shiftMs}:all=1");
        }
        else
        {
            double shiftSec = shiftMs / 1000.0;
            string shiftSecStr = shiftSec.ToString("F3", CultureInfo.InvariantCulture);
            extraArgs.Add("-af");
            extraArgs.Add($"atrim=start={shiftSecStr},asetpts=PTS-STARTPTS");
        }

        if (ext == "flac")
        {
            extraArgs.Add("-c:a");
            extraArgs.Add("flac");
        }
        else
        {
            extraArgs.Add("-c:a");
            extraArgs.Add("pcm_s16le");
        }

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
