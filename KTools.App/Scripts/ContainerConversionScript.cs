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
/// Скрипт быстрой смены медиаконтейнера видеофайлов без перекодирования.
/// Использует копирование потоков FFmpeg (-c copy) для мгновенной конвертации.
/// Полностью локализован на русский язык с избыточным комментированием шагов.
/// </summary>
public sealed class ContainerConversionScript : AbstractScript
{
    private static readonly Dictionary<string, string> FormatMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { "MP4", ".mp4" },
        { "MKV", ".mkv" },
        { "MOV", ".mov" },
        { "WEBM", ".webm" },
        { "AVI", ".avi" },
        { "TS", ".ts" }
    };

    /// <summary>
    /// Локализованное название скрипта.
    /// </summary>
    public override string Name => AppConstants.ScriptMetadata.ContainerConvName;

    /// <summary>
    /// Описание назначения и ограничений скрипта для UI.
    /// </summary>
    public override string Description => AppConstants.ScriptMetadata.ContainerConvDesc;

    /// <summary>
    /// Категория медиаобработки.
    /// </summary>
    public override string Category => AppConstants.ScriptCategory.Video;

    /// <summary>
    /// Системное имя Fluent-иконки для меню.
    /// </summary>
    public override string IconName => "forward";

    /// <summary>
    /// Список допустимых входящих расширений (видеофайлы и GIF).
    /// </summary>
    public override string[] FileExtensions => AppConstants.VideoContainers.Concat(new[] { ".gif" }).ToArray();

    /// <summary>
    /// Внешняя бинарная зависимость: утилита FFmpeg.
    /// </summary>
    public override string[] RequiredDependencies => new[] { "ffmpeg" };

    /// <summary>
    /// Декларативная схема настроек параметров конвертации.
    /// </summary>
    public override List<SettingField> SettingsSchema => new()
    {
        new SettingField("target_format", "Целевой формат", SettingType.Combo, "MP4", "Общие",
            options: FormatMap.Keys.ToList()),
        new SettingField("delete_original", "Удалить исходный файл", SettingType.Checkbox, false, "Общие")
    };

    /// <summary>
    /// Асинхронно запускает процесс ремуксинга одного медиафайла.
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

        // Извлекаем настройки пользователя
        string targetKey = GetSettingValue(settings, "target_format", "MP4");
        bool deleteOriginal = GetSettingValue(settings, "delete_original", false);

        if (!FormatMap.TryGetValue(targetKey, out string? targetExt))
        {
            targetExt = ".mp4";
        }

        string originalName = Path.GetFileName(filePath);
        string inputExt = Path.GetExtension(filePath).ToLowerInvariant();

        LogService.Instance.Info($"Запущена конвертация контейнера для файла: '{originalName}'. Целевой формат: {targetKey}", "ContainerConversionScript");

        // 1. Проверяем, совпадает ли исходный формат с целевым
        if (inputExt.Equals(targetExt, StringComparison.OrdinalIgnoreCase))
        {
            LogService.Instance.Info($"Файл '{originalName}' уже находится в формате {targetKey}. Конвертация пропущена.", "ContainerConversionScript");
            progressCallback(fileIndex, totalCount, $"Пропуск (уже {targetKey}): {originalName}", 100.0);
            results.Add($"Ref: {filePath}");
            results.Add($"⏭ ПРОПУСК (уже {targetKey}): {originalName}");
            return results;
        }

        // 2. Получаем метаданные структуры файла через ffprobe
        progressCallback(fileIndex, totalCount, "Анализ структуры медиафайла...", 0.0);
        LogService.Instance.DebugLog($"Запрос метаданных ffprobe для: '{originalName}'", "ContainerConversionScript");
        var info = await FFmpegRunner.Instance.GetVideoInfoAsync(filePath);

        // 3. Выполняем детальную проверку совместимости видео/аудио кодеков с новым контейнером
        var (compatible, reason) = CheckCompatibility(filePath, targetExt, info);
        if (!compatible)
        {
            string msg = $"⚠ ПРОПУСК (требуется перекодирование): {originalName}. {reason} Для перекодирования используйте инструмент «{AppConstants.ScriptMetadata.VideoProcessorName}».";
            LogService.Instance.Warn($"Файл '{originalName}' несовместим с контейнером {targetKey}: {reason}", "ContainerConversionScript");
            progressCallback(fileIndex, totalCount, "Пропуск: требуется перекодирование", 100.0);
            results.Add(msg);
            return results;
        }

        // 4. Формируем безопасный путь для вывода
        string targetDir = string.IsNullOrEmpty(outputPath)
            ? Path.GetDirectoryName(filePath) ?? AppContext.BaseDirectory
            : outputPath;

        string baseOutputName = Path.GetFileNameWithoutExtension(filePath) + targetExt;
        string targetOutputFilePath = Path.Combine(targetDir, baseOutputName);
        string outputFilePath = GetSafeOutputPath(filePath, targetOutputFilePath);
        string outputFileName = Path.GetFileName(outputFilePath);

        // 5. Проверяем существование файла и флаг перезаписи
        bool overwrite = SettingsManager.Instance.GetSetting("General", "OverwriteExisting", false);
        if (File.Exists(outputFilePath) && !overwrite)
        {
            LogService.Instance.Info($"Выходной файл '{outputFileName}' уже существует. Конвертация пропущена.", "ContainerConversionScript");
            progressCallback(fileIndex, totalCount, $"Пропуск (существует): {outputFileName}", 100.0);
            results.Add($"⏭ ПРОПУСК (файл существует): {outputFileName}");
            return results;
        }

        // 6. Считываем длительность медиафайла для расчета прогресса выполнения
        double duration = 0.0;
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
        LogService.Instance.DebugLog($"Длительность медиафайла '{originalName}': {duration:F2} сек.", "ContainerConversionScript");

        // 7. Запускаем FFmpeg с копированием видео и аудио потоков
        progressCallback(fileIndex, totalCount, "Запуск FFmpeg...", 0.0);
        LogService.Instance.DebugLog($"Инициализация процесса FFmpeg для ремуксинга '{originalName}' -> '{outputFileName}'", "ContainerConversionScript");

        var extraArgs = new List<string> { "-c", "copy" };
        var cts = new CancellationTokenSource();

        var ffmpegTask = FFmpegRunner.Instance.RunAsync(
            inputPath: filePath,
            outputPath: outputFilePath,
            extraArgs: extraArgs,
            overwrite: overwrite,
            totalDuration: duration,
            onProgress: progressInfo =>
            {
                string speedStr = progressInfo.Speed > 0 ? $"{progressInfo.Speed:F1}x" : "н/д";
                string msg = $"Конвертация | {progressInfo.Percent:F1}% | Скорость: {speedStr}";
                progressCallback(fileIndex, totalCount, msg, progressInfo.Percent, progressInfo.Fps, progressInfo.Bitrate);
            },
            cancellationToken: cts.Token
        );

        // Мониторинг отмены со стороны пользователя
        while (!ffmpegTask.IsCompleted)
        {
            if (IsCancelled)
            {
                LogService.Instance.Warn($"Пользователь инициировал отмену конвертации для '{originalName}'", "ContainerConversionScript");
                cts.Cancel();
                break;
            }
            await Task.Delay(200);
        }

        bool success = false;
        try
        {
            success = await ffmpegTask;
        }
        catch (Exception ex)
        {
            LogService.Instance.Exception(ex, $"Сбой при запуске/работе FFmpeg для файла '{originalName}': {ex.Message}", "ContainerConversionScript");
        }

        // 8. Обрабатываем итог выполнения
        if (success)
        {
            LogService.Instance.Info($"Конвертация контейнера успешно завершена. Выходной файл: '{outputFileName}'", "ContainerConversionScript");
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
            if (IsCancelled)
            {
                progressCallback(fileIndex, totalCount, "Отменено пользователем", 0.0);
                results.Add($"⚠ Отменено: {outputFileName}");
            }
            else
            {
                LogService.Instance.Error($"Не удалось выполнить смену контейнера для файла '{originalName}'", "ContainerConversionScript");
                progressCallback(fileIndex, totalCount, "Ошибка обработки!", 0.0);
                results.Add($"❌ ОШИБКА: {originalName}");
            }
        }

        return results;
    }

    /// <summary>
    /// Проверяет совместимость видеокодека и аудиокодека с целевым расширением контейнера.
    /// Предотвращает попытки ремуксинга несовместимых потоков без их перекодирования.
    /// </summary>
    private (bool isCompatible, string reason) CheckCompatibility(string filePath, string targetExt, JsonDocument? info)
    {
        if (info == null)
        {
            LogService.Instance.Warn($"Отсутствуют данные анализа структуры (ffprobe null) для '{Path.GetFileName(filePath)}'. Совместимость принята по умолчанию.", "ContainerConversionScript");
            return (true, "");
        }

        string inputExt = Path.GetExtension(filePath).ToLowerInvariant();
        targetExt = targetExt.ToLowerInvariant();

        if (inputExt == ".gif" || targetExt == ".gif")
        {
            return (false, "Формат GIF требует обязательного перекодирования видеопотока.");
        }

        string videoCodec = "";
        string audioCodec = "";
        bool hasAudio = false;

        try
        {
            if (info.RootElement.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    if (stream.TryGetProperty("codec_type", out var typeProp))
                    {
                        string codecType = typeProp.GetString() ?? "";
                        if (codecType == "video")
                        {
                            if (string.IsNullOrEmpty(videoCodec) && stream.TryGetProperty("codec_name", out var codecProp))
                            {
                                videoCodec = codecProp.GetString()?.ToLowerInvariant() ?? "";
                            }
                        }
                        else if (codecType == "audio")
                        {
                            hasAudio = true;
                            if (string.IsNullOrEmpty(audioCodec) && stream.TryGetProperty("codec_name", out var codecProp))
                            {
                                audioCodec = codecProp.GetString()?.ToLowerInvariant() ?? "";
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Exception(ex, $"Не удалось разобрать потоки медиафайла для детекции совместимости: {ex.Message}", "ContainerConversionScript");
            return (true, ""); // При сбоях парсинга полагаемся на FFmpeg
        }

        LogService.Instance.DebugLog($"Анализ совместимости. Видеокодек: '{videoCodec}', Аудиокодек: '{audioCodec}', Целевой контейнер: '{targetExt}'", "ContainerConversionScript");

        // Формат MKV поддерживает абсолютно любые видео и аудио кодеки
        if (targetExt == ".mkv")
        {
            return (true, "");
        }

        // 1. Проверка совместимости видеокодека
        if (!string.IsNullOrEmpty(videoCodec))
        {
            if (targetExt == ".mp4" || targetExt == ".mov" || targetExt == ".ts" || targetExt == ".m2ts")
            {
                string[] allowed = { "h264", "hevc", "mpeg4", "mpeg2video", "av1" };
                if (!allowed.Contains(videoCodec))
                {
                    return (false, $"Видеокодек {videoCodec.ToUpperInvariant()} не поддерживается контейнером {targetExt.ToUpperInvariant()} без перекодирования.");
                }
            }
            else if (targetExt == ".webm")
            {
                string[] allowed = { "vp8", "vp9", "av1" };
                if (!allowed.Contains(videoCodec))
                {
                    return (false, $"Видеокодек {videoCodec.ToUpperInvariant()} не поддерживается контейнером WEBM без перекодирования.");
                }
            }
            else if (targetExt == ".avi")
            {
                string[] allowed = { "mpeg4", "h264", "mjpeg" };
                if (!allowed.Contains(videoCodec))
                {
                    return (false, $"Видеокодек {videoCodec.ToUpperInvariant()} не поддерживается контейнером AVI без перекодирования.");
                }
            }
        }

        // 2. Проверка совместимости аудиокодека
        if (hasAudio && !string.IsNullOrEmpty(audioCodec))
        {
            if (targetExt == ".mp4" || targetExt == ".mov" || targetExt == ".ts" || targetExt == ".m2ts")
            {
                string[] allowed = { "aac", "mp3", "ac3", "eac3", "mp2" };
                if (!allowed.Contains(audioCodec))
                {
                    return (false, $"Аудиокодек {audioCodec.ToUpperInvariant()} не поддерживается контейнером {targetExt.ToUpperInvariant()} без перекодирования.");
                }
            }
            else if (targetExt == ".webm")
            {
                string[] allowed = { "opus", "vorbis" };
                if (!allowed.Contains(audioCodec))
                {
                    return (false, $"Аудиокодек {audioCodec.ToUpperInvariant()} не поддерживается контейнером WEBM без перекодирования.");
                }
            }
            else if (targetExt == ".avi")
            {
                string[] allowed = { "mp3", "ac3", "pcm_s16le" };
                if (!allowed.Contains(audioCodec))
                {
                    return (false, $"Аудиокодек {audioCodec.ToUpperInvariant()} не поддерживается контейнером AVI без перекодирования.");
                }
            }
        }

        return (true, "");
    }

    public override string GetOutputExtension(string inputPath)
    {
        string settingsGroup = SettingsManager.Instance.GetSafeGroupName(Name);
        string targetKey = SettingsManager.Instance.GetSetting(settingsGroup, "target_format", "MP4");
        if (FormatMap.TryGetValue(targetKey, out string? targetExt))
        {
            return targetExt;
        }
        return ".mp4";
    }
}
