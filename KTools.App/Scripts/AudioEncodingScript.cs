using KTools_App.Services.Contracts;
// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using KTools_App.Core;
using KTools_App.Infrastructure;

namespace KTools_App.Scripts;

/// <summary>
/// Скрипт профессионального кодирования аудиофайлов с поддержкой
/// множества форматов (QAAC, AAC, FLAC, WAV, MP3 и др.).
/// Полностью перенесен из оригинального audio_converter.py.
/// Все комментарии и логирование выполнены исключительно на русском языке.
/// </summary>
public sealed class AudioEncodingScript : AbstractScript
{
    private readonly IFFmpegRunner _ffmpegRunner;
    private readonly QaacRunner _qaacRunner;

    public AudioEncodingScript(ILogService logService, ISettingsManager settingsManager, IPathManager pathManager, IFFmpegRunner ffmpegRunner, QaacRunner qaacRunner)
        : base(logService, settingsManager, pathManager)
    {
        _ffmpegRunner = ffmpegRunner ?? throw new ArgumentNullException(nameof(ffmpegRunner));
        _qaacRunner = qaacRunner ?? throw new ArgumentNullException(nameof(qaacRunner));
    }

    // Карта соответствия форматов, их расширений и кодеков FFmpeg
    private static readonly Dictionary<string, (string ext, string codec)> AudioFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        { "QAAC", (".m4a", "qaac") },
        { "AAC", (".aac", "aac") },
        { "FLAC", (".flac", "flac") },
        { "WAV", (".wav", "pcm_s16le") },
        { "AC3", (".ac3", "ac3") },
        { "EAC3", (".eac3", "eac3") },
        { "MP3", (".mp3", "libmp3lame") },
        { "OPUS", (".opus", "libopus") },
        { "OGG", (".ogg", "libvorbis") },
        { "DTS", (".dts", "dca") },
        { "WavPack", (".wv", "wavpack") },
        { "ALAC", (".m4a", "alac") },
        { "WMA", (".wma", "wmav2") },
        { "AIFF", (".aiff", "pcm_s16be") },
        { "ADPCM", (".wav", "adpcm_ima_wav") }
    };

    // Карта кодеков для различных разрядностей формата WAV
    private static readonly Dictionary<string, string> WavBitDepths = new(StringComparer.OrdinalIgnoreCase)
    {
        { "16-bit", "pcm_s16le" },
        { "24-bit", "pcm_s24le" },
        { "32-bit", "pcm_s32le" },
        { "32-bit Float", "pcm_f32le" }
    };

    // Множество форматов, сжимаемых с потерями (lossy)
    private static readonly HashSet<string> LossyFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "MP3", "AAC", "QAAC", "OGG", "AC3", "EAC3", "DTS", "WMA", "OPUS", "ADPCM"
    };

    // Множество форматов сжатия без потерь (lossless)
    private static readonly HashSet<string> LosslessCompressed = new(StringComparer.OrdinalIgnoreCase)
    {
        "FLAC", "WavPack"
    };

    /// <summary>
    /// Локализованное название скрипта.
    /// </summary>
    public override string Name => AppConstants.ScriptMetadata.AudioConverterName;

    /// <summary>
    /// Описание назначения скрипта для UI.
    /// </summary>
    public override string Description => AppConstants.ScriptMetadata.AudioConverterDesc;

    /// <summary>
    /// Категория медиаобработки.
    /// </summary>
    public override string Category => AppConstants.ScriptCategory.Audio;

    /// <summary>
    /// Имя системной Fluent-иконки.
    /// </summary>
    public override string IconName => AppConstants.ScriptIcons.AudioEncoding;

    /// <summary>
    /// Допустимые расширения файлов (аудио-контейнеры, потоки и видеофайлы).
    /// </summary>
    public override string[] FileExtensions => AppConstants.AudioContainers
        .Concat(AppConstants.AudioStreams)
        .Concat(AppConstants.VideoContainers)
        .ToArray();

    /// <summary>
    /// Внешние бинарные зависимости (FFmpeg).
    /// </summary>
    public override string[] RequiredDependencies => new[] { "ffmpeg" };

    /// <summary>
    /// Скрипт поддерживает параллельную обработку файлов в очереди.
    /// </summary>
    public override bool SupportsParallel => true;

    /// <summary>
    /// Декларативная схема параметров настроек скрипта.
    /// </summary>
    public override List<SettingField> SettingsSchema => new()
    {
        // 1. Группа "Экспорт"
        new SettingField(
            "target_format",
            "Целевой формат",
            SettingType.Combo,
            "QAAC",
            "Экспорт",
            options: AudioFormats.Keys.ToList()),

        new SettingField(
            "use_m4a_container",
            "Упаковать в контейнер (m4a)",
            SettingType.Checkbox,
            true,
            "Экспорт",
            comment: "Рекомендуется для корректного отображения длительности в плеерах",
            visibleIfKey: "target_format",
            visibleIfValues: new List<string> { "QAAC", "AAC", "ALAC" },
            requiresWarning: true,
            warningTitle: "ВНИМАНИЕ! АЛЯРМ! НЕ ТРОЖЬ!!!111",
            warningText: "Если вы отключите эту опцию, то плееры, проводник Windows или Telegram могут показывать неправильную длительность аудио (чаще всего - очень большую длительность вплоть до десятков часов).\n\nЭто лишь ошибка отображения — сам звук будет в полном порядке, а внутри файла ничего не сломано. Рекомендуется оставить упаковку включенной для вашего удобства и душевного спокойствия."),

        // 2. Группа "Параметры кодирования"
        new SettingField(
            "qaac_quality",
            "Качество QAAC (0-127)",
            SettingType.Combo,
            "127",
            "Экспорт:Параметры кодирования",
            options: new List<string> { "0", "16", "32", "48", "64", "80", "96", "112", "127" },
            visibleIfKey: "target_format",
            visibleIfValues: new List<string> { "QAAC" }),

        new SettingField(
            "bitrate",
            "Битрейт (кбит/с)",
            SettingType.Combo,
            "320k",
            "Экспорт:Параметры кодирования",
            options: new List<string> { "64k", "96k", "128k", "160k", "192k", "224k", "256k", "320k", "448k", "640k" },
            visibleIfKey: "target_format",
            visibleIfValues: new List<string> { "MP3", "AAC", "OGG", "AC3", "EAC3", "DTS", "WMA", "OPUS", "ADPCM" }),

        new SettingField(
            "compression",
            "Уровень сжатия FLAC (0-12)",
            SettingType.Combo,
            "5",
            "Экспорт:Параметры кодирования",
            options: Enumerable.Range(0, 13).Select(i => i.ToString()).ToList(),
            visibleIfKey: "target_format",
            visibleIfValues: new List<string> { "FLAC", "WavPack" }),

        new SettingField(
            "wav_bit_depth",
            "Битность WAV",
            SettingType.Combo,
            "24-bit",
            "Экспорт:Параметры кодирования",
            options: WavBitDepths.Keys.ToList(),
            visibleIfKey: "target_format",
            visibleIfValues: new List<string> { "WAV" }),

        // 3. Группа "Общие"
        new SettingField(
            "delete_original",
            "Удалить исходный файл",
            SettingType.Checkbox,
            false,
            "Общие")
    };

    /// <summary>
    /// Выполняет конвертацию одного аудио- или видеофайла.
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

        // 1. Извлекаем настройки пользователя
        string targetFormat = GetSettingValue(settings, "target_format", "QAAC");
        bool useM4a = GetSettingValue(settings, "use_m4a_container", true);
        bool deleteOriginal = GetSettingValue(settings, "delete_original", false);

        string originalName = Path.GetFileName(filePath);
        string inputExt = Path.GetExtension(filePath).ToLowerInvariant();

        _logService.Info(
            $"Начало кодирования аудио для '{originalName}'. " +
            $"Целевой формат: {targetFormat}",
            "AudioEncodingScript");

        // 2. Определяем расширение и кодек
        var (targetExt, codec) = ResolveExtension(targetFormat, useM4a);

        // 3. Проверяем, не совпадает ли расширение исходного файла с целевым
        if (inputExt.Equals(targetExt, StringComparison.OrdinalIgnoreCase) &&
            !LossyFormats.Contains(targetFormat))
        {
            string skipMsg = $"⏭ ПРОПУСК (уже в формате {targetFormat}): {originalName}";
            _logService.Info(skipMsg, "AudioEncodingScript");
            progressCallback(
                fileIndex,
                totalCount,
                $"Пропуск (уже {targetFormat}): {originalName}",
                100.0);
            results.Add(skipMsg);
            return results;
        }

        // 4. Формируем безопасный выходной путь
        string targetDir = string.IsNullOrEmpty(outputPath)
            ? Path.GetDirectoryName(filePath) ?? AppContext.BaseDirectory
            : outputPath;

        string baseOutputName = Path.GetFileNameWithoutExtension(filePath) + targetExt;
        string targetOutputFilePath = Path.Combine(targetDir, baseOutputName);
        string outputFilePath = GetSafeOutputPath(filePath, targetOutputFilePath, settings);
        string outputFileName = Path.GetFileName(outputFilePath);

        // 5. Проверяем флаг перезаписи существующего файла
        bool overwrite = _settingsManager.GetSetting(
            "General", "OverwriteExisting", false);

        if (File.Exists(outputFilePath) && !overwrite)
        {
            string skipMsg = $"⏭ ПРОПУСК (существует): {outputFileName}";
            _logService.Info(skipMsg, "AudioEncodingScript");
            progressCallback(
                fileIndex,
                totalCount,
                $"Пропуск (существует): {outputFileName}",
                100.0);
            results.Add(skipMsg);
            return results;
        }

        // 6. Считываем длительность медиафайла для расчета прогресса выполнения
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
                            System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture,
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
            _logService.Exception(
                ex,
                $"Не удалось прочесть метаданные длительности для '{originalName}': {ex.Message}",
                "AudioEncodingScript");
        }

        _logService.DebugLog(
            $"Длительность медиафайла '{originalName}': {duration:F2} сек.",
            "AudioEncodingScript");

        // 7. Подготавливаем процесс кодирования
        var cts = new CancellationTokenSource();
        bool success = false;

        if (targetFormat.Equals("QAAC", StringComparison.OrdinalIgnoreCase))
        {
            // Случай А: Кодирование через QAAC (True VBR конвейер FFmpeg | QAAC64)
            string tvbr = GetSettingValue(settings, "qaac_quality", "127");
            bool adts = !useM4a;

            progressCallback(fileIndex, totalCount, "Запуск QAAC...", 0.0);
            _logService.Info(
                $"Запуск кодирования QAAC для '{originalName}' -> '{outputFileName}'",
                "AudioEncodingScript");

            var qaacTask = _qaacRunner.RunAsync(
                inputPath: filePath,
                outputPath: outputFilePath,
                tvbr: tvbr,
                adts: adts,
                totalDuration: duration,
                onProgress: progressInfo =>
                {
                    string speedStr = progressInfo.Speed > 0 ? $"{progressInfo.Speed:F1}x" : "н/д";
                    string msg = $"Кодирование QAAC | {progressInfo.Percent:F1}% | Скорость: {speedStr}";
                    progressCallback(fileIndex, totalCount, msg, progressInfo.Percent);
                },
                cancellationToken: cts.Token);

            while (!qaacTask.IsCompleted)
            {
                if (IsCancelled)
                {
                    _logService.Warn(
                        $"Отмена кодирования QAAC пользователем для '{originalName}'",
                        "AudioEncodingScript");
                    cts.Cancel();
                    break;
                }
                await Task.Delay(200);
            }

            try
            {
                success = await qaacTask;
            }
            catch (Exception ex)
            {
                _logService.Exception(
                    ex,
                    $"Ошибка кодирования QAAC для '{originalName}': {ex.Message}",
                    "AudioEncodingScript");
            }
        }
        else
        {
            // Случай Б: Кодирование через FFmpeg для всех остальных форматов
            string currentCodec = codec;
            if (targetFormat.Equals("WAV", StringComparison.OrdinalIgnoreCase))
            {
                string wavDepth = GetSettingValue(settings, "wav_bit_depth", "24-bit");
                if (WavBitDepths.TryGetValue(wavDepth, out var wavCodec))
                {
                    currentCodec = wavCodec;
                }
            }

            var extraArgs = new List<string> { "-c:a", currentCodec, "-map_metadata", "-1" };
            if (LosslessCompressed.Contains(targetFormat))
            {
                string compression = GetSettingValue(settings, "compression", "5");
                extraArgs.Add("-compression_level");
                extraArgs.Add(compression);
            }
            else if (LossyFormats.Contains(targetFormat))
            {
                string bitrate = GetSettingValue(settings, "bitrate", "320k");
                extraArgs.Add("-b:a");
                extraArgs.Add(bitrate);
            }

            if (targetFormat.Equals("DTS", StringComparison.OrdinalIgnoreCase))
            {
                extraArgs.Add("-strict");
                extraArgs.Add("-2");
            }

            if (outputFilePath.EndsWith(".alac", StringComparison.OrdinalIgnoreCase))
            {
                extraArgs.Insert(0, "-f");
                extraArgs.Insert(1, "caf");
            }

            progressCallback(fileIndex, totalCount, "Запуск FFmpeg...", 0.0);
            _logService.Info(
                $"Запуск FFmpeg для кодирования '{originalName}' -> '{outputFileName}'",
                "AudioEncodingScript");

            var ffmpegTask = _ffmpegRunner.RunAsync(
                inputPath: filePath,
                outputPath: outputFilePath,
                extraArgs: extraArgs,
                overwrite: overwrite,
                totalDuration: duration,
                onProgress: progressInfo =>
                {
                    string speedStr = progressInfo.Speed > 0 ? $"{progressInfo.Speed:F1}x" : "н/д";
                    string msg = $"Кодирование | {progressInfo.Percent:F1}% | Скорость: {speedStr}";
                    progressCallback(fileIndex, totalCount, msg, progressInfo.Percent, progressInfo.Fps, progressInfo.Bitrate);
                },
                cancellationToken: cts.Token);

            while (!ffmpegTask.IsCompleted)
            {
                if (IsCancelled)
                {
                    _logService.Warn(
                        $"Отмена кодирования FFmpeg пользователем для '{originalName}'",
                        "AudioEncodingScript");
                    cts.Cancel();
                    break;
                }
                await Task.Delay(200);
            }

            try
            {
                success = await ffmpegTask;
            }
            catch (Exception ex)
            {
                _logService.Exception(
                    ex,
                    $"Ошибка кодирования FFmpeg для '{originalName}': {ex.Message}",
                    "AudioEncodingScript");
            }
        }

        // 8. Обрабатываем результаты
        if (success)
        {
            _logService.Info(
                $"Кодирование аудио завершено успешно. Выходной файл: '{outputFileName}'",
                "AudioEncodingScript");
            progressCallback(fileIndex, totalCount, "Успешно завершено!", 100.0);
            results.Add($"✅ Кодирован: {outputFileName}");

            if (deleteOriginal)
            {
                DeleteSource(filePath, results);
            }
        }
        else
        {
            CleanupFailedOutputFile(outputFilePath);
            if (IsCancelled)
            {
                progressCallback(fileIndex, totalCount, "Отменено пользователем", 0.0);
                results.Add($"⚠ Отменено: {outputFileName}");
            }
            else
            {
                _logService.Error(
                    $"Сбой при кодировании файла '{originalName}'",
                    "AudioEncodingScript");
                progressCallback(fileIndex, totalCount, "Ошибка обработки!", 0.0);
                results.Add($"❌ ОШИБКА: {originalName}");
            }
        }

        return results;
    }

    /// <summary>
    /// Определяет расширение и кодек на основе настроек упаковки.
    /// </summary>
    private (string ext, string codec) ResolveExtension(string targetFormat, bool useM4a)
    {
        if (!AudioFormats.TryGetValue(targetFormat, out var info))
        {
            info = AudioFormats["MP3"];
        }

        string targetExt = info.ext;
        string codec = info.codec;

        if (useM4a && (targetFormat == "AAC" || targetFormat == "QAAC" || targetFormat == "ALAC"))
        {
            targetExt = ".m4a";
        }
        else if (targetFormat == "AAC" || targetFormat == "QAAC")
        {
            targetExt = ".aac";
        }
        else if (targetFormat == "ALAC")
        {
            targetExt = ".alac";
        }

        return (targetExt, codec);
    }

    public override string GetOutputExtension(string inputPath)
    {
        string settingsGroup = _settingsManager.GetSafeGroupName(Name);
        string targetFormat = _settingsManager.GetSetting(settingsGroup, "target_format", "QAAC");
        bool useM4a = _settingsManager.GetSetting(settingsGroup, "use_m4a_container", true);

        var (targetExt, _) = ResolveExtension(targetFormat, useM4a);
        return targetExt;
    }
}
