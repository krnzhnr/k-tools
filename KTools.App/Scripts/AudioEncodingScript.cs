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
/// Скрипт профессионального кодирования аудиофайлов с поддержкой
/// множества форматов (QAAC, AAC, FLAC, WAV, MP3 и др.).
/// Полностью перенесен из оригинального audio_converter.py.
/// Все комментарии и логирование выполнены исключительно на русском языке.
/// </summary>
public sealed class AudioEncodingScript : AbstractScript
{
    private readonly IFFmpegRunner _ffmpegRunner;
    private readonly QaacRunner _qaacRunner;
    private readonly IMediaProbeService _mediaProbeService;

    public AudioEncodingScript(
        ILogService logService,
        ISettingsManager settingsManager,
        IPathManager pathManager,
        IFFmpegRunner ffmpegRunner,
        QaacRunner qaacRunner,
        IMediaProbeService mediaProbeService)
        : base(logService, settingsManager, pathManager)
    {
        _ffmpegRunner = ffmpegRunner ?? throw new ArgumentNullException(nameof(ffmpegRunner));
        _qaacRunner = qaacRunner ?? throw new ArgumentNullException(nameof(qaacRunner));
        _mediaProbeService = mediaProbeService ?? throw new ArgumentNullException(nameof(mediaProbeService));
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
            "qaac_mode",
            "Режим кодирования QAAC",
            SettingType.Combo,
            "True VBR (-V)",
            "Экспорт:Параметры кодирования",
            options: new List<string>
            {
                "True VBR (-V)",
                "Constrained VBR (-v)",
                "ABR (-a)",
                "CBR (-c)",
                "HE AAC (--he)"
            },
            visibleIfKey: "target_format",
            visibleIfValues: new List<string> { "QAAC" }),

        new SettingField(
            "qaac_quality",
            "Качество True VBR [0-127] (-V)",
            SettingType.Combo,
            "127",
            "Экспорт:Параметры кодирования",
            options: new List<string> { "0", "16", "32", "48", "64", "80", "90", "96", "112", "127" },
            comment: "Для LC профиля по умолчанию используется -V90",
            visibilityConditions: new List<SettingVisibilityCondition>
            {
                new("target_format", "QAAC"),
                new("qaac_mode", "True VBR (-V)")
            }),

        new SettingField(
            "qaac_bitrate",
            "Битрейт AAC [кбит/с]",
            SettingType.Combo,
            "192k",
            "Экспорт:Параметры кодирования",
            options: new List<string> { "0 (Авто/Максимальный)", "64k", "96k", "128k", "160k", "192k", "224k", "256k", "320k" },
            comment: "Для режимов -a, -v, -c значение \"0\" означает наивысший доступный битрейт, который выбирается автоматически",
            visibilityConditions: new List<SettingVisibilityCondition>
            {
                new("target_format", "QAAC"),
                new("qaac_mode", new List<string> { "Constrained VBR (-v)", "ABR (-a)", "CBR (-c)", "HE AAC (--he)" })
            }),

        new SettingField(
            "qaac_advanced_options",
            "Дополнительные параметры",
            SettingType.Expander,
            null,
            "Экспорт:Параметры кодирования",
            comment: "Тонкая настройка задержки и лимитера при кодировании QAAC",
            headerIconGlyph: "\uE713",
            visibleIfKey: "target_format",
            visibleIfValues: new List<string> { "QAAC" },
            hasToggleSwitch: false,
            childFields: new List<SettingField>
            {
                new SettingField(
                    "qaac_no_delay",
                    "Компенсировать задержку энкодера (--no-delay)",
                    SettingType.Checkbox,
                    false,
                    "Экспорт:Параметры кодирования",
                    comment: "Компенсирует задержку кодировщика путем добавления 960 отсчетов тишины в начало и последующей обрезки 3 кадров AAC. В основном предназначено для решения проблем синхронизации аудио и видео."),

                new SettingField(
                    "qaac_limiter",
                    "Применить смарт-лимитер (--limiter)",
                    SettingType.Checkbox,
                    false,
                    "Экспорт:Параметры кодирования",
                    comment: "Применяет интеллектуальный лимитер, который мягко ограничивает участки, где пиковый уровень превышает (или близок к) 0 dBFS.")
            }),

        new SettingField(
            "bitrate",
            "Битрейт (кбит/с)",
            SettingType.Combo,
            "320k",
            "Экспорт:Параметры кодирования",
            options: new List<string> { "64k", "96k", "128k", "160k", "192k", "224k", "256k", "320k", "448k", "640k" },
            comment: "Для формата OGG (Vorbis) битрейт автоматически адаптируется под число каналов (до 224k для моно, до 500k для стерео)",
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

        // 1. Извлекаем настройки пользователя
        string targetFormat = GetSettingValue(settings, "target_format", "QAAC");
        bool useM4a = GetSettingValue(settings, "use_m4a_container", true);
        bool deleteOriginal = GetSettingValue(settings, "delete_original", false);

        string originalName = Path.GetFileName(filePath);
        string inputExt = Path.GetExtension(filePath).ToLowerInvariant();

        _logService.Write("script.audio_encoding.started", LogLevel.Debug, LogStatus.Running, $"Начато кодирование аудио '{originalName}' в формат {LogRedactor.CompactSafeToken(targetFormat)}", source: Name, properties: LogProps.Create("InputName", LogProps.FileName(filePath)).With("Extension", LogRedactor.CompactSafeToken(targetFormat)));

        // 2. Определяем расширение и кодек
        var (targetExt, codec) = ResolveExtension(targetFormat, useM4a);

        // 3. Проверяем, не совпадает ли расширение исходного файла с целевым
        if (inputExt.Equals(targetExt, StringComparison.OrdinalIgnoreCase) &&
            !LossyFormats.Contains(targetFormat))
        {
            string skipMsg = $"⏭ ПРОПУСК (уже в формате {targetFormat}): {originalName}";
            _logService.Write("script.audio_encoding.skipped", LogLevel.Info, LogStatus.Skipped, skipMsg, source: Name, properties: LogProps.Create("InputName", LogProps.FileName(filePath)));
            progressCallback(
                fileIndex,
                totalCount,
                $"Пропуск (уже {targetFormat}): {originalName}",
                100.0);
            results.Add(skipMsg);
            return ExecutionResult.Skipped(
                context,
                results,
                errorCode: "already-target-format",
                outputFile: filePath,
                outputExists: File.Exists(filePath));
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
            _logService.Write("script.audio_encoding.skipped", LogLevel.Info, LogStatus.Skipped, skipMsg, source: Name, properties: LogProps.Create("InputName", LogProps.FileName(filePath)));
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

        // 6. Считываем длительность медиафайла и количество каналов для расчета прогресса и валидации кодеков
        double duration = 0.0;
        int audioChannels = 0;
        try
        {
            var structure = await _mediaProbeService.ProbeAsync(filePath);
            if (structure != null)
            {
                duration = structure.Duration;
                var audioTrack = structure.Tracks.FirstOrDefault(t => t.TrackType == "audio");
                if (audioTrack != null && audioTrack.Channels > 0)
                {
                    audioChannels = audioTrack.Channels;
                }
            }

            // Фоллбэк: если MediaProbeService не вернул каналы или длительность, считываем через ffprobe
            if (audioChannels <= 0 || duration <= 0)
            {
                var info = await _ffmpegRunner.GetVideoInfoAsync(filePath);
                if (info != null)
                {
                    if (duration <= 0 && info.RootElement.TryGetProperty("format", out var fmtProp) && fmtProp.TryGetProperty("duration", out var fmtDur))
                    {
                        if (fmtDur.ValueKind == JsonValueKind.String && double.TryParse(fmtDur.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double fd))
                        {
                            duration = fd;
                        }
                        else if (fmtDur.ValueKind == JsonValueKind.Number)
                        {
                            duration = fmtDur.GetDouble();
                        }
                    }

                    if (info.RootElement.TryGetProperty("streams", out var streamsProp) && streamsProp.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var s in streamsProp.EnumerateArray())
                        {
                            if (s.TryGetProperty("codec_type", out var ct) && ct.GetString() == "audio")
                            {
                                if (audioChannels <= 0 && s.TryGetProperty("channels", out var chProp) && chProp.TryGetInt32(out int ch) && ch > 0)
                                {
                                    audioChannels = ch;
                                }
                                if (duration <= 0 && s.TryGetProperty("duration", out var stDur))
                                {
                                    if (stDur.ValueKind == JsonValueKind.String && double.TryParse(stDur.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double sd))
                                    {
                                        duration = sd;
                                    }
                                    else if (stDur.ValueKind == JsonValueKind.Number)
                                    {
                                        duration = stDur.GetDouble();
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // По умолчанию принимаем стерео (2 канала), если не удалось определить
            if (audioChannels <= 0)
            {
                audioChannels = 2;
            }
        }
        catch (Exception ex)
        {
            _logService.Write("script.audio_encoding.probe_failed", LogLevel.Warning, LogStatus.Skipped, $"Метаданные для '{originalName}' не прочитаны, кодирование продолжится с начальными параметрами", ex, Name, properties: LogProps.Create("ErrorCode", "PROBE_FAILED").With("InputName", LogProps.FileName(filePath)));
        }

        _logService.Write("media.metadata.detected", LogLevel.Debug, LogStatus.Succeeded, $"Медиафайл '{originalName}': длительность {duration:F2} с, аудиоканалов: {audioChannels}", source: Name, properties: LogProps.Create("InputName", LogProps.FileName(filePath)).With("DurationMs", duration * 1000d).With("AudioChannels", audioChannels));

        // 7. Подготавливаем процесс кодирования
        var cts = new CancellationTokenSource();
        ProcessResult? success = null;

        if (targetFormat.Equals("QAAC", StringComparison.OrdinalIgnoreCase))
        {
            // Случай А: Кодирование через QAAC (True VBR конвейер FFmpeg | QAAC64)
            string qaacMode = GetSettingValue(settings, "qaac_mode", "True VBR (-V)");
            string qaacQuality = GetSettingValue(settings, "qaac_quality", "127");
            string qaacBitrate = GetSettingValue(settings, "qaac_bitrate", "192k");
            bool noDelay = GetSettingValue(settings, "qaac_no_delay", false);
            bool limiter = GetSettingValue(settings, "qaac_limiter", false);
            bool adts = !useM4a;

            string selectedVal = qaacMode.StartsWith("True VBR", StringComparison.OrdinalIgnoreCase)
                ? qaacQuality
                : qaacBitrate;

            progressCallback(fileIndex, totalCount, "Запуск QAAC...", 0.0);
            _logService.Write("script.audio_encoding.qaac_started", LogLevel.Debug, LogStatus.Running, $"Запущено кодирование QAAC '{originalName}' -> '{LogProps.FileName(outputFilePath)}'", source: Name, properties: LogProps.Create("Tool", "qaac").With("InputName", LogProps.FileName(filePath)).With("OutputName", LogProps.FileName(outputFilePath)));

            var qaacTask = _qaacRunner.RunAsync(
                inputPath: filePath,
                outputPath: outputFilePath,
                tvbr: qaacQuality,
                adts: adts,
                totalDuration: duration,
                onProgress: progressInfo =>
                {
                    string speedStr = progressInfo.Speed > 0 ? $"{progressInfo.Speed:F1}x" : "н/д";
                    string msg = $"Кодирование QAAC | {progressInfo.Percent:F1}% | Скорость: {speedStr}";
                    progressCallback(fileIndex, totalCount, msg, progressInfo.Percent);
                },
                cancellationToken: cts.Token,
                mode: qaacMode,
                qualityOrBitrate: selectedVal,
                noDelay: noDelay,
                limiter: limiter);

            while (!qaacTask.IsCompleted)
            {
                if (IsCancelled)
                {
                    _logService.Write("script.audio_encoding.qaac_cancelled", LogLevel.Info, LogStatus.Cancelled, $"Кодирование QAAC для '{originalName}' отменено", source: Name, properties: LogProps.Create("Tool", "qaac").With("Reason", "UserRequested").With("CleanupState", "NotStarted"));
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
                _logService.Write("script.audio_encoding.qaac_failed", LogLevel.Error, LogStatus.Failed, $"Кодирование QAAC для '{originalName}' не выполнено", ex, Name, properties: LogProps.Create("ErrorCode", "QAAC_ENCODING_FAILED").With("Tool", "qaac").With("InputName", LogProps.FileName(filePath)).With("Retryable", true));
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

                // Для формата OGG (энкодер libvorbis) проверяем лимиты битрейта по каналам:
                // libvorbis падает с ошибкой -22 (Invalid argument), если для моно указан битрейт > 224k или для стерео > 500k.
                if (targetFormat.Equals("OGG", StringComparison.OrdinalIgnoreCase))
                {
                    int numericBitrate = ParseBitrateKbps(bitrate);
                    if (audioChannels == 1 && numericBitrate > 224)
                    {
                        string adjustedBitrate = "224k";
                        string note = $"ℹ️ Для моно-аудио в формате OGG (Vorbis) битрейт скорректирован с {bitrate} до максимально допустимого {adjustedBitrate}";
                        _logService.Write("script.audio_encoding.note", LogLevel.Debug, LogStatus.Changed, note, source: Name, properties: LogProps.Create("InputName", LogProps.FileName(filePath)));
                        results.Add(note);
                        bitrate = adjustedBitrate;
                    }
                    else if (audioChannels == 2 && numericBitrate > 500)
                    {
                        string adjustedBitrate = "448k";
                        string note = $"ℹ️ Для стерео-аудио в формате OGG (Vorbis) битрейт скорректирован с {bitrate} до максимально допустимого {adjustedBitrate}";
                        _logService.Write("script.audio_encoding.note", LogLevel.Debug, LogStatus.Changed, note, source: Name, properties: LogProps.Create("InputName", LogProps.FileName(filePath)));
                        results.Add(note);
                        bitrate = adjustedBitrate;
                    }
                }

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
            _logService.Write("script.audio_encoding.ffmpeg_started", LogLevel.Debug, LogStatus.Running, $"Запущен FFmpeg для кодирования '{originalName}' -> '{LogProps.FileName(outputFilePath)}'", source: Name, properties: LogProps.Create("Tool", "ffmpeg").With("InputName", LogProps.FileName(filePath)).With("OutputName", LogProps.FileName(outputFilePath)));

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
                    _logService.Write("script.audio_encoding.ffmpeg_cancelled", LogLevel.Info, LogStatus.Cancelled, $"Кодирование FFmpeg для '{originalName}' отменено", source: Name, properties: LogProps.Create("Tool", "ffmpeg").With("Reason", "UserRequested").With("CleanupState", "NotStarted"));
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
                _logService.Write("script.audio_encoding.ffmpeg_failed", LogLevel.Error, LogStatus.Failed, $"Кодирование FFmpeg для '{originalName}' не выполнено", ex, Name, properties: LogProps.Create("ErrorCode", "FFMPEG_ENCODING_FAILED").With("Tool", "ffmpeg").With("InputName", LogProps.FileName(filePath)).With("Retryable", true));
            }
        }

        // 8. Обрабатываем результаты
        if (success?.IsSuccess == true)
        {
            _logService.Write("script.audio_encoding.completed", LogLevel.Info, LogStatus.Succeeded, $"Кодирование аудио завершено, выходной файл: '{LogProps.FileName(outputFilePath)}'", source: Name, properties: LogProps.Create("OutputName", LogProps.FileName(outputFilePath)).With("Verified", true));
            progressCallback(fileIndex, totalCount, "Успешно завершено!", 100.0);
            results.Add($"✅ Кодирован: {outputFileName}");

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
                _logService.Write("script.audio_encoding.failed", LogLevel.Error, LogStatus.Failed, $"Кодирование аудио для '{originalName}' не выполнено", source: Name, properties: LogProps.Create("ErrorCode", "AUDIO_ENCODING_FAILED").With("InputName", LogProps.FileName(filePath)).With("Retryable", true));
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

    /// <summary>
    /// Извлекает числовое значение битрейта в кбит/с из строки вида "320k" или "192".
    /// </summary>
    private static int ParseBitrateKbps(string bitrate)
    {
        if (string.IsNullOrWhiteSpace(bitrate)) return 320;
        string clean = bitrate.Trim().TrimEnd('k', 'K', 'b', 'B', 's', 'S', '/');
        return int.TryParse(clean, out int val) ? val : 320;
    }
}
