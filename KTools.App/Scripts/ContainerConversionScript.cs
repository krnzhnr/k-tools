// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
/// Скрипт быстрой смены медиаконтейнера видеофайлов без перекодирования.
/// Использует копирование потоков FFmpeg (-c copy) для мгновенной конвертации.
/// Полностью локализован на русский язык с избыточным комментированием шагов.
/// </summary>
public sealed class ContainerConversionScript : AbstractScript
{
    private readonly IFFmpegRunner _ffmpegRunner;

    public ContainerConversionScript(ILogService logService, ISettingsManager settingsManager, IPathManager pathManager, IFFmpegRunner ffmpegRunner)
        : base(logService, settingsManager, pathManager)
    {
        _ffmpegRunner = ffmpegRunner ?? throw new ArgumentNullException(nameof(ffmpegRunner));
    }

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
    public override string IconName => AppConstants.ScriptIcons.ContainerConversion;

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

        // Извлекаем настройки пользователя
        string targetKey = GetSettingValue(settings, "target_format", "MP4");
        bool deleteOriginal = GetSettingValue(settings, "delete_original", false);

        if (!FormatMap.TryGetValue(targetKey, out string? targetExt))
        {
            targetExt = ".mp4";
        }

        string originalName = Path.GetFileName(filePath);
        string inputExt = Path.GetExtension(filePath).ToLowerInvariant();

        _logService.Write("script.container_conversion.started", LogLevel.Debug, LogStatus.Running, $"Начата конвертация контейнера '{originalName}' в формат {LogRedactor.CompactSafeToken(targetKey)}", source: Name, properties: LogProps.Create("InputName", LogProps.FileName(filePath)).With("Container", LogRedactor.CompactSafeToken(targetKey)));
        // 1. Проверяем, совпадает ли исходный формат с целевым
        if (inputExt.Equals(targetExt, StringComparison.OrdinalIgnoreCase))
        {
            _logService.Write("script.container_conversion.already_target", LogLevel.Info, LogStatus.Skipped, $"Файл '{originalName}' уже в формате {LogRedactor.CompactSafeToken(targetKey)}, конвертация не требуется", source: Name, properties: LogProps.Create("Container", LogRedactor.CompactSafeToken(targetKey)));
            progressCallback(fileIndex, totalCount, $"Пропуск (уже {targetKey}): {originalName}", 100.0);
            results.Add($"Ref: {filePath}");
            results.Add($"⏭ ПРОПУСК (уже {targetKey}): {originalName}");
            return ExecutionResult.Skipped(
                context,
                results,
                errorCode: "already-target-container",
                outputFile: filePath,
                outputExists: File.Exists(filePath));
        }

        // 2. Получаем метаданные структуры файла через ffprobe
        progressCallback(fileIndex, totalCount, "Анализ структуры медиафайла...", 0.0);
        _logService.Write("media.probe.requested", LogLevel.Debug, LogStatus.Running, $"Запрошены метаданные файла '{originalName}'", source: Name, properties: LogProps.Create("Tool", "ffprobe").With("InputName", LogProps.FileName(filePath)));
        var info = await _ffmpegRunner.GetVideoInfoAsync(filePath);

        // 3. Выполняем детальную проверку совместимости видео/аудио кодеков с новым контейнером
        var (compatible, reason) = CheckCompatibility(filePath, targetExt, info);
        if (!compatible)
        {
            string msg = $"⚠ ПРОПУСК (требуется перекодирование): {originalName}. {reason} Для перекодирования используйте инструмент «{AppConstants.ScriptMetadata.VideoProcessorName}».";
            _logService.Write("script.container_conversion.incompatible", LogLevel.Warning, LogStatus.Skipped, $"Файл '{originalName}' несовместим с контейнером {LogRedactor.CompactSafeToken(targetKey)}: {reason}", source: Name, properties: LogProps.Create("Container", LogRedactor.CompactSafeToken(targetKey)).With("Reason", LogRedactor.CompactSafeToken(reason)));
            progressCallback(fileIndex, totalCount, "Пропуск: требуется перекодирование", 100.0);
            results.Add(msg);
            return ExecutionResult.Skipped(
                context,
                results,
                errorCode: "incompatible-container",
                outputFile: filePath,
                outputExists: File.Exists(filePath));
        }

        // 4. Формируем безопасный путь для вывода
        string targetDir = string.IsNullOrEmpty(outputPath)
            ? Path.GetDirectoryName(filePath) ?? AppContext.BaseDirectory
            : outputPath;

        string baseOutputName = Path.GetFileNameWithoutExtension(filePath) + targetExt;
        string targetOutputFilePath = Path.Combine(targetDir, baseOutputName);
        string outputFilePath = GetSafeOutputPath(filePath, targetOutputFilePath, settings);
        string outputFileName = Path.GetFileName(outputFilePath);

        // 5. Проверяем существование файла и флаг перезаписи
        bool overwrite = _settingsManager.GetSetting("General", "OverwriteExisting", false);
        if (File.Exists(outputFilePath) && !overwrite)
        {
            _logService.Write("script.container_conversion.output_exists", LogLevel.Info, LogStatus.Skipped, $"Выходной файл '{LogProps.FileName(outputFilePath)}' уже существует, конвертация пропущена", source: Name, properties: LogProps.Create("OutputName", LogProps.FileName(outputFilePath)).With("ArtifactExists", true));
            progressCallback(fileIndex, totalCount, $"Пропуск (существует): {outputFileName}", 100.0);
            results.Add($"⏭ ПРОПУСК (файл существует): {outputFileName}");
            return ExecutionResult.Skipped(
                context,
                results,
                errorCode: "output-exists",
                outputFile: outputFilePath,
                outputExists: true);
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
        _logService.Write("media.duration.detected", LogLevel.Debug, LogStatus.Succeeded, $"Длительность медиафайла определена: {duration:F2} с", source: Name, properties: LogProps.Create("InputName", LogProps.FileName(filePath)).With("DurationMs", duration * 1000d));
        // 7. Запускаем FFmpeg с копированием видео и аудио потоков
        progressCallback(fileIndex, totalCount, "Запуск FFmpeg...", 0.0);
        _logService.Write("script.container_conversion.ffmpeg_started", LogLevel.Debug, LogStatus.Running, $"Запущен ремуксинг через FFmpeg: '{originalName}' -> '{LogProps.FileName(outputFilePath)}'", source: Name, properties: LogProps.Create("Tool", "ffmpeg").With("InputName", LogProps.FileName(filePath)).With("OutputName", LogProps.FileName(outputFilePath)));

        // Входные аргументы: нормализуем генерацию меток времени (PTS), если в исходном контейнере они отсутствуют или рассинхронизированы
        var inputArgs = new List<string> { "-fflags", "+genpts" };

        // Базовые аргументы ремуксинга: копирование без перекодирования всех подходящих потоков
        var extraArgs = new List<string> { "-map", "0", "-c", "copy" };

        // Исключаем дорожки субтитров при упаковке в контейнеры, не поддерживающие текстовые потоки без конвертации (MP4/MOV не принимают ASS/SSA/PGS в режиме copy)
        if (targetExt is ".mp4" or ".mov")
        {
            // Для совместимости контейнера MP4 отключаем несовместимые субтитры, если они присутствуют
            extraArgs.Add("-c:s");
            extraArgs.Add("mov_text");

            // Нормализуем шкалу времени видеопотока до стандартных 90 кГц (90000 Hz), устраняя погрешности миллисекундных таймштампов MKV (1/1000)
            // и предотвращая ложное определение частоты кадров как переменной (VFR вместо исходного CFR)
            extraArgs.Add("-video_track_timescale");
            extraArgs.Add("90000");

            // Перемещаем заголовочный индекс в начало файла для мгновенного старта воспроизведения и корректного чтения плеерами
            extraArgs.Add("-movflags");
            extraArgs.Add("+faststart");
        }
        else if (targetExt is ".ts" or ".m2ts")
        {
            // Для контейнера MPEG-TS битстрим-фильтры Annex B необходимы при копировании H.264 / HEVC
            extraArgs.Add("-bsf:v");
            extraArgs.Add("dump_extra");
        }

        var cts = new CancellationTokenSource();

        var ffmpegTask = _ffmpegRunner.RunAsync(
            inputPath: filePath,
            outputPath: outputFilePath,
            extraArgs: extraArgs,
            inputArgs: inputArgs,
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
                _logService.Write(
                    "container_conversion.cancelled",
                    LogLevel.Info,
                    LogStatus.Cancelled,
                    $"Пользователь инициировал отмену конвертации для '{originalName}'",
                    null,
                    "ContainerConversionScript",
                    context: context.ToLogContext(),
                    properties: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["ErrorCode"] = "cancelled",
                        ["InputName"] = originalName
                    });
                cts.Cancel();
                break;
            }
            await Task.Delay(200);
        }

        ProcessResult? success = null;
        try
        {
            success = await ffmpegTask;
        }
        catch (Exception ex)
        {
            _logService.Write(
                "container_conversion.ffmpeg_failed",
                LogLevel.Error,
                LogStatus.Failed,
                $"Сбой при запуске или работе FFmpeg для файла '{originalName}'",
                ex,
                "ContainerConversionScript",
                context: context.ToLogContext(),
                properties: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["ErrorCode"] = "ffmpeg-failed",
                    ["InputName"] = originalName
                });
        }

        // 8. Обрабатываем итог выполнения
        if (success?.IsSuccess == true)
        {
            _logService.Write("script.container_conversion.completed", LogLevel.Info, LogStatus.Succeeded, $"Конвертация контейнера завершена, выходной файл: '{LogProps.FileName(outputFilePath)}'", source: Name, properties: LogProps.Create("OutputName", LogProps.FileName(outputFilePath)).With("Verified", true));
            progressCallback(fileIndex, totalCount, "Успешно завершено!", 100.0);
            results.Add($"✅ Конвертирован: {outputFileName}");

            if (deleteOriginal)
            {
                await DeleteSourceAsync(filePath, results);
            }
        }
        else
        {
            await CleanupFailedOutputFileAsync(outputFilePath);
            if (IsCancelled)
            {
                progressCallback(fileIndex, totalCount, "Отменено пользователем", 0.0);
                results.Add($"⚠ Отменено: {outputFileName}");
            }
            else
            {
                _logService.Write("script.container_conversion.failed", LogLevel.Error, LogStatus.Failed, $"Смена контейнера для файла '{originalName}' не выполнена", source: Name, properties: LogProps.Create("ErrorCode", "CONTAINER_CONVERSION_FAILED").With("InputName", LogProps.FileName(filePath)).With("Retryable", true));
                progressCallback(fileIndex, totalCount, "Ошибка обработки!", 0.0);
                results.Add($"❌ ОШИБКА: {originalName}");
            }
        }

        if (IsCancelled)
        {
            return ExecutionResult.Cancelled(
                context,
                results,
                errorCode: "cancelled",
                outputFile: outputFilePath,
                outputExists: File.Exists(outputFilePath),
                cleanupState: CleanupState.Completed);
        }
        if (success?.IsSuccess == true && File.Exists(outputFilePath))
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
            outputExists: File.Exists(outputFilePath));
    }

    /// <summary>
    /// Проверяет совместимость видеокодека и аудиокодека с целевым расширением контейнера.
    /// Предотвращает попытки ремуксинга несовместимых потоков без их перекодирования.
    /// </summary>
    private (bool isCompatible, string reason) CheckCompatibility(string filePath, string targetExt, JsonDocument? info)
    {
        if (info == null)
        {
            _logService.Write("script.container_conversion.probe_missing", LogLevel.Warning, LogStatus.Skipped, $"Данные анализа структуры файла '{LogProps.FileName(filePath)}' отсутствуют, совместимость принята по умолчанию", source: Name, properties: LogProps.Create("ErrorCode", "PROBE_DATA_MISSING").With("InputName", LogProps.FileName(filePath)));
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
            _logService.Write("script.container_conversion.parse_failed", LogLevel.Warning, LogStatus.Skipped, "Список дорожек медиафайла для проверки совместимости не разобран, проверка пропущена", ex, Name, properties: LogProps.Create("ErrorCode", "STREAM_PARSE_FAILED").With("Tool", "ffprobe"));
            return (true, ""); // При сбоях парсинга полагаемся на FFmpeg
        }

        _logService.Write("script.container_conversion.compatibility", LogLevel.Debug, LogStatus.Succeeded, $"Проверка совместимости: видеокодек {LogRedactor.CompactSafeToken(videoCodec)}, аудиокодек {LogRedactor.CompactSafeToken(audioCodec)}, целевой контейнер {LogRedactor.CompactSafeToken(targetExt)}", source: Name, properties: LogProps.Create("Codec", LogRedactor.CompactSafeToken(videoCodec)).With("Container", LogRedactor.CompactSafeToken(targetExt)));
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
        string settingsGroup = _settingsManager.GetSafeGroupName(Name);
        string targetKey = _settingsManager.GetSetting(settingsGroup, "target_format", "MP4");
        if (FormatMap.TryGetValue(targetKey, out string? targetExt))
        {
            return targetExt;
        }
        return ".mp4";
    }
}
