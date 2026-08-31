using KTools_App.Services.Contracts;
// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using KTools_App.Core;
using KTools_App.Infrastructure;
using KTools_App.Encoders;

namespace KTools_App.Scripts;

/// <summary>
/// Скрипт комплексного кодирования видео с поддержкой аппаратного ускорения NVENC (GPU) или x265 (CPU),
/// автоматического извлечения шрифтов, фильтрации и вшивания субтитров (burn-in).
/// Все комментарии и сообщения логов выполнены исключительно на русском языке с исчерпывающей полнотой.
/// </summary>
public sealed class VideoEncodingScript : AbstractScript
{
    private string? _finalOutputFileForCleanup;
    private readonly IFFmpegRunner _ffmpegRunner;
    private readonly IMediaProbeService _mediaProbeService;
    private readonly VideoEncoderRegistry _encoderRegistry;

    public VideoEncodingScript(
        ILogService logService, 
        ISettingsManager settingsManager, IPathManager pathManager,
        IFFmpegRunner ffmpegRunner,
        IMediaProbeService mediaProbeService,
        VideoEncoderRegistry encoderRegistry)
        : base(logService, settingsManager, pathManager)
    {
        _ffmpegRunner = ffmpegRunner ?? throw new ArgumentNullException(nameof(ffmpegRunner));
        _mediaProbeService = mediaProbeService ?? throw new ArgumentNullException(nameof(mediaProbeService));
        _encoderRegistry = encoderRegistry ?? throw new ArgumentNullException(nameof(encoderRegistry));
    }

    public override string Name => AppConstants.ScriptMetadata.VideoProcessorName;
    public override string Description => AppConstants.ScriptMetadata.VideoProcessorDesc;
    public override string Category => AppConstants.ScriptCategory.Video;
    public override string IconName => AppConstants.ScriptIcons.VideoEncoding;
    public override string[] FileExtensions => AppConstants.VideoContainers.ToArray();
    public override string[] RequiredDependencies => new[] { "ffmpeg" };

    public override List<SettingField> GetSettingsSchema(Dictionary<string, object>? currentSettings = null)
    {
        return GetSettingsSchemaInternal(currentSettings);
    }

    public override List<SettingField> SettingsSchema => GetSettingsSchemaInternal(null);

    private List<SettingField> GetSettingsSchemaInternal(Dictionary<string, object>? currentSettings)
    {
        var availableEncoders = _encoderRegistry.GetAvailableEncoders();
        string defaultEncoder = availableEncoders.FirstOrDefault()?.StableId ?? "x265";
        var encoderOptions = availableEncoders.Select(e => e.DisplayName).ToList();
        var encoderValues = availableEncoders.Select(e => e.StableId).ToList();

        var fields = new List<SettingField>
        {
            // --- Вкладка Видео: Кодирование ---
            new SettingField(
                "encoder",
                "Энкодер",
                SettingType.Combo,
                defaultEncoder,
                "Видео:Кодирование",
                options: encoderOptions,
                column: 0,
                colSpan: 1
            ),
            new SettingField(
                "output_container",
                "Контейнер файла",
                SettingType.Combo,
                ".mkv",
                "Видео:Кодирование",
                options: new List<string> { ".mkv", ".mp4" },
                column: 1,
                colSpan: 1
            ),
            new SettingField(
                "force_10bit",
                "Принудительно 10-бит (Main10)",
                SettingType.Checkbox,
                false,
                "Видео:Кодирование",
                column: 0,
                colSpan: 2,
                disableConditions: new List<SettingDisableCondition>
                {
                    new("nvenc_codec", "AVC / H.264")
                }
            ),

            // --- Вкладка Видео: Битрейт ---
            new SettingField(
                "lossless",
                "Режим Lossless",
                SettingType.Checkbox,
                false,
                "Видео:Битрейт",
                column: 0,
                colSpan: 1
            )
        };

        // Динамически внедряем настройки от каждого энкодера с передачей текущего контекста
        foreach (var encoder in availableEncoders)
        {
            var encoderFields = encoder.GetEncoderSettings(currentSettings);
            foreach (var settingField in encoderFields)
            {
                // Добавляем условие видимости, чтобы настройки показывались только когда выбран этот энкодер
                if (settingField.VisibilityConditions == null)
                {
                    settingField.VisibilityConditions = new List<SettingVisibilityCondition>();
                }
                
                settingField.VisibilityConditions.Add(new SettingVisibilityCondition("encoder", new List<string> { encoder.StableId, encoder.DisplayName }));
                fields.Add(settingField);
            }
        }

        // --- Вкладка Видео: Фильтры ---
        fields.Add(new SettingField(
            "autocrop_enabled",
            "Автоматическая обрезка черных полос",
            SettingType.Expander,
            false,
            "Видео:Фильтры",
            comment: "Автоматически определяет и удаляет черные полосы (Letterbox / Pillarbox) по краям видеокадра.",
            headerIconGlyph: "\uE7B5",
            childFields: new List<SettingField>
            {
                new SettingField(
                    "autocrop_limit",
                    "Порог черного (limit)",
                    SettingType.Float,
                    0.094,
                    "Видео:Фильтры",
                    comment: "Порог яркости (от 0.0 до 1.0), ниже которого пиксель считается черным (по умолчанию 0.094 ≈ 24/255).",
                    column: 0,
                    colSpan: 1,
                    minimum: 0.0,
                    maximum: 1.0
                ),
                new SettingField(
                    "autocrop_round",
                    "Кратность сторон (round)",
                    SettingType.Int,
                    16,
                    "Видео:Фильтры",
                    comment: "Значение, которому должны быть кратны ширина и высота после обрезки (обычно 16 или 2).",
                    column: 1,
                    colSpan: 1,
                    minimum: 2,
                    maximum: 64
                ),
                new SettingField(
                    "autocrop_mode",
                    "Режим детекции (mode)",
                    SettingType.Combo,
                    "black",
                    "Видео:Фильтры",
                    options: new List<string> { "black", "mvedges" },
                    comment: "Режим работы детектора: 'black' — поиск черных пикселей, 'mvedges' — анализ краев и векторов движения.",
                    column: 0,
                    colSpan: 1
                ),
                new SettingField(
                    "autocrop_probe_frames",
                    "Кадров для анализа",
                    SettingType.Int,
                    25,
                    "Видео:Фильтры",
                    comment: "Количество последовательных кадров видеоряда для предварительного зондирования обрезки.",
                    column: 1,
                    colSpan: 1,
                    minimum: 5,
                    maximum: 300
                ),
                new SettingField(
                    "autocrop_skip_frames",
                    "Пропуск кадров детектора (skip)",
                    SettingType.Int,
                    2,
                    "Видео:Фильтры",
                    comment: "Количество начальных кадров зондирования, пропускаемых детектором FFmpeg (по умолчанию 2).",
                    column: 0,
                    colSpan: 1,
                    minimum: 0,
                    maximum: 1000
                ),
                new SettingField(
                    "autocrop_reset_frames",
                    "Сброс детектора (reset)",
                    SettingType.Int,
                    0,
                    "Видео:Фильтры",
                    comment: "Интервал кадров для сброса/пересчета области обрезки (0 — без сброса).",
                    column: 1,
                    colSpan: 1,
                    minimum: 0,
                    maximum: 1000
                ),
                new SettingField(
                    "autocrop_probe_points",
                    "Количество точек анализа",
                    SettingType.Int,
                    3,
                    "Видео:Фильтры",
                    comment: "Количество равномерно распределенных по длительности видео контрольных точек для надежного поиска черных полос.",
                    column: 0,
                    colSpan: 1,
                    minimum: 1,
                    maximum: 10
                ),
                new SettingField(
                    "autocrop_tolerance",
                    "Порог микрообрезки (px)",
                    SettingType.Int,
                    16,
                    "Видео:Фильтры",
                    comment: "Порог допуска (в пикселях). Если с края отрезается меньше указанного значения, размер стороны сохраняется исходным для защиты от ложных срезов.",
                    column: 1,
                    colSpan: 1,
                    minimum: 0,
                    maximum: 128
                )
            }
        ));

        fields.AddRange(new List<SettingField>
        {
            // --- Вкладка: Аудио ---
            new SettingField(
                "audio_codec",
                "Кодек аудио",
                SettingType.Combo,
                "copy",
                "Аудио",
                options: new List<string> { "copy", "aac", "ac3", "flac" },
                column: 0,
                colSpan: 1
            ),
            new SettingField(
                "audio_bitrate",
                "Битрейт аудио",
                SettingType.Combo,
                "320k",
                "Аудио",
                options: new List<string> { "128k", "192k", "256k", "320k", "448k", "640k" },
                visibleIfKey: "audio_codec",
                visibleIfValues: new List<string> { "aac", "ac3" },
                column: 1,
                colSpan: 1
            ),
            new SettingField(
                "audio_channels",
                "Каналы",
                SettingType.Combo,
                "Original",
                "Аудио",
                options: new List<string> { "Original", "1", "2", "6" },
                column: 0,
                colSpan: 2,
                visibilityConditions: new List<SettingVisibilityCondition>
                {
                    new("audio_codec", "copy", negate: true)
                }
            ),
            new SettingField(
                "audio_lang_priority",
                "Приоритет языка аудио",
                SettingType.KeywordList,
                new List<Dictionary<string, object>>
                {
                    new() { { "word", "rus" }, { "active", true } },
                    new() { { "word", "jpn" }, { "active", false } },
                    new() { { "word", "eng" }, { "active", false } }
                },
                "Аудио",
                column: 0,
                colSpan: 2
            ),

            // --- Вкладка: Субтитры ---
            new SettingField(
                "burn_in_subtitles",
                "Вшивать найденные надписи в видеоряд (Burn-in)",
                SettingType.Checkbox,
                true,
                "Субтитры",
                column: 0,
                colSpan: 2
            ),
            new SettingField(
                "sub_keywords",
                "Поиск надписей",
                SettingType.KeywordList,
                new List<Dictionary<string, object>>
                {
                    new() { { "word", "Надписи" }, { "active", true } }
                },
                "Субтитры",
                column: 0,
                colSpan: 2,
                visibilityConditions: new List<SettingVisibilityCondition>
                {
                    new("burn_in_subtitles", "True")
                }
            ),
            new SettingField(
                "strip_keywords",
                "Удалять теги оформления субтитров",
                SettingType.KeywordList,
                new List<Dictionary<string, object>>
                {
                    new() { { "word", @"{\fad(500,500)\b1\an3\fnTahoma\fs50\shad3\bord1.3\4c&H000000&\4a&H00&}" }, { "active", false } },
                    new() { { "word", @"{\fad(500,500)\b1\an3\fnTahoma\fs16.667\shad1\bord0.433\4c&H000000&\4a&H00&}" }, { "active", false } },
                    new() { { "word", @"{\fad(500,500)\b1\an3\fnTahoma\fs100\shad6\bord2.6\4c&H000000&\4a&H00&}" }, { "active", false } }
                },
                "Субтитры",
                column: 0,
                colSpan: 2,
                visibilityConditions: new List<SettingVisibilityCondition>
                {
                    new("burn_in_subtitles", "True")
                }
            ),

            // --- Вкладка: Общие ---
            new SettingField(
                "overwrite_source",
                "Заменить исходный файл после обработки",
                SettingType.Checkbox,
                false,
                "Общие",
                column: 0,
                colSpan: 2
            )
        });
        
        return fields;
    }

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

        _logService.Info($"Начало кодирования видео для файла '{Path.GetFileName(filePath)}'", "VideoEncodingScript");

        // 1. Анализируем структуру исходного файла
        MediaStructure? structure;
        try
        {
            structure = await _mediaProbeService.ProbeAsync(filePath);
        }
        catch (Exception ex)
        {
            string probeErr = $"❌ Ошибка анализа метаданных файла: {ex.Message}";
            _logService.Exception(ex, $"Исключение при зондировании '{filePath}': {ex.Message}", "VideoEncodingScript");
            progressCallback(fileIndex, totalCount, "Ошибка ffprobe", 0.0);
            results.Add(probeErr);
            return results;
        }

        if (structure == null)
        {
            string err = $"❌ ОШИБКА анализа: {Path.GetFileName(filePath)}";
            _logService.Error(err, "VideoEncodingScript");
            progressCallback(fileIndex, totalCount, "Ошибка ffprobe", 0.0);
            results.Add(err);
            return results;
        }

        // 2. Создаем временную директорию для извлечения ресурсов (шрифты, субтитры)
        string tempDir = Path.Combine(Path.GetTempPath(), "vproc_" + Path.GetRandomFileName().Substring(0, 8));
        try
        {
            Directory.CreateDirectory(tempDir);
        }
        catch (Exception ex)
        {
            string dirErr = $"❌ Ошибка создания временной папки: {ex.Message}";
            _logService.Exception(ex, dirErr, "VideoEncodingScript");
            results.Add(dirErr);
            return results;
        }

        string? tempSubFile = null;
        string tempFontsDir = Path.Combine(tempDir, "fonts");
        Directory.CreateDirectory(tempFontsDir);

        try
        {
            // 3. Извлечение шрифтов (вложений)
            var fontAttachments = structure.GetFontAttachments();
            int fontCount = 0;
            if (fontAttachments.Count > 0)
            {
                string inputExt = Path.GetExtension(filePath).ToLowerInvariant();
                bool isMkv = inputExt.Equals(".mkv", StringComparison.OrdinalIgnoreCase) || inputExt.Equals(".mka", StringComparison.OrdinalIgnoreCase);

                foreach (var font in fontAttachments)
                {
                    if (IsCancelled) break;

                    int ffmpegAttachmentIndex = isMkv
                        ? structure.Tracks.Count + structure.Attachments.IndexOf(font)
                        : font.AttachmentId;

                    string outFontPath = Path.Combine(tempFontsDir, font.FileName);
                    bool fSuccess = await _ffmpegRunner.ExtractAttachmentAsync(filePath, ffmpegAttachmentIndex, outFontPath);
                    if (fSuccess)
                    {
                        fontCount++;
                    }
                }
                _logService.Info($"Извлечено встроенных шрифтов во временную папку: {fontCount}", "VideoEncodingScript");
            }

            // 4. Поиск и извлечение субтитров для вшивания (burn-in)
            bool burnInSubtitles = GetSettingValue(settings, "burn_in_subtitles", true);
            if (burnInSubtitles)
            {
                var subKeywords = GetSettingValue<List<Dictionary<string, object>>?>(settings, "sub_keywords", null);
                var activeSubKeywords = subKeywords?
                    .Where(d => d.TryGetValue("active", out var act) && SafeGetBool(act))
                    .Select(d => d.TryGetValue("word", out var w) ? SafeGetString(w)?.ToLowerInvariant() : null)
                    .Where(w => w != null)
                    .ToList() ?? new List<string?>();

                var subTracks = structure.GetSubtitleTracks();
                MediaTrack? targetSubTrack = null;

                // Сначала ищем по ключевым словам в названии трека
                if (activeSubKeywords.Count > 0)
                {
                    foreach (var word in activeSubKeywords)
                    {
                        targetSubTrack = subTracks.FirstOrDefault(t => t.Name.ToLowerInvariant().Contains(word!));
                        if (targetSubTrack != null) break;
                    }
                }

                // Если не найдено - ищем default/forced
                if (targetSubTrack == null)
                {
                    targetSubTrack = subTracks.FirstOrDefault(t => t.IsDefault || t.IsForced);
                }

                // Если все еще не найдено - берем первый трек субтитров
                if (targetSubTrack == null)
                {
                    targetSubTrack = subTracks.FirstOrDefault();
                }

                if (targetSubTrack != null)
                {
                    int relSubIdx = subTracks.ToList().IndexOf(targetSubTrack);
                    tempSubFile = Path.Combine(tempDir, $"subs_{DateTime.Now.Ticks}.ass");

                    _logService.Info($"Извлечение субтитров #{targetSubTrack.TrackId} (относительный индекс {relSubIdx}) во временный файл", "VideoEncodingScript");
                    bool extSubSuccess = await _ffmpegRunner.ExtractSubtitleAsync(filePath, relSubIdx, tempSubFile, relative: true);

                    if (extSubSuccess && File.Exists(tempSubFile))
                    {
                        // Очистка субтитров от нежелательных тегов оформления
                        var stripKeywords = GetSettingValue<List<Dictionary<string, object>>?>(settings, "strip_keywords", null);
                        var activeStrip = stripKeywords?
                            .Where(d => d.TryGetValue("active", out var act) && SafeGetBool(act))
                            .Select(d => d.TryGetValue("word", out var w) ? SafeGetString(w) : null)
                            .Where(w => w != null)
                            .ToList() ?? new List<string?>();

                        if (activeStrip.Count > 0)
                        {
                            var lines = File.ReadAllLines(tempSubFile, System.Text.Encoding.UTF8);
                            var cleanLines = new List<string>();
                            int removedCount = 0;

                            foreach (var line in lines)
                            {
                                bool shouldStrip = activeStrip.Any(word => line.Contains(word!));
                                if (shouldStrip)
                                {
                                    removedCount++;
                                    continue;
                                }
                                cleanLines.Add(line);
                            }

                            if (removedCount > 0)
                            {
                                File.WriteAllLines(tempSubFile, cleanLines, new System.Text.UTF8Encoding(false));
                                _logService.Info($"Очистка субтитров: удалено {removedCount} строк оформления", "VideoEncodingScript");
                            }
                        }
                    }
                    else
                    {
                        tempSubFile = null;
                        _logService.Warn("Не удалось извлечь субтитры для вшивания, кодирование продолжится без них", "VideoEncodingScript");
                    }
                }
            }

            // 5. Поиск аудиодорожки на основе языковых приоритетов
            var audioLangPriority = GetSettingValue<List<Dictionary<string, object>>?>(settings, "audio_lang_priority", null);
            var activeLangs = audioLangPriority?
                .Where(d => d.TryGetValue("active", out var act) && SafeGetBool(act))
                .Select(d => d.TryGetValue("word", out var w) ? SafeGetString(w)?.ToLowerInvariant().Trim() : null)
                .Where(w => w != null)
                .ToList() ?? new List<string?>();

            var audioTracks = structure.GetAudioTracks();
            MediaTrack? bestAudio = null;

            if (activeLangs.Count > 0)
            {
                foreach (var lang in activeLangs)
                {
                    bestAudio = audioTracks.FirstOrDefault(t => 
                    {
                        string normTrack = AppConstants.NormalizeLanguage(t.Language);
                        string normLang = AppConstants.NormalizeLanguage(lang!);
                        return normTrack.Equals(normLang, StringComparison.OrdinalIgnoreCase) ||
                               normTrack.Contains(normLang, StringComparison.OrdinalIgnoreCase) ||
                               normLang.Contains(normTrack, StringComparison.OrdinalIgnoreCase);
                    });

                    if (bestAudio != null) break;
                }
            }

            if (bestAudio == null)
            {
                bestAudio = audioTracks.FirstOrDefault(t => t.IsDefault || t.IsForced);
            }

            if (bestAudio == null)
            {
                bestAudio = audioTracks.FirstOrDefault();
            }

            int relAudioIdx = bestAudio != null ? audioTracks.ToList().IndexOf(bestAudio) : 0;
            if (bestAudio != null)
            {
                _logService.Info($"Выбран аудиопоток #{bestAudio.TrackId} (относительный индекс {relAudioIdx}, язык '{bestAudio.Language}')", "VideoEncodingScript");
            }

            // 6. Вычисляем выходные пути
            string targetDir = string.IsNullOrEmpty(outputPath)
                ? Path.GetDirectoryName(filePath) ?? AppContext.BaseDirectory
                : outputPath;

            string containerExt = GetSettingValue(settings, "output_container", ".mkv");
            if (!containerExt.StartsWith(".")) containerExt = "." + containerExt;

            string stem = Path.GetFileNameWithoutExtension(filePath);
            string targetFile = Path.Combine(targetDir, $"{stem}{containerExt}");
            string finalOutputFile = GetSafeOutputPath(filePath, targetFile, settings);

            // Объявляем finalOutputFile на уровне выше, чтобы она была доступна в catch блоке
            _finalOutputFileForCleanup = finalOutputFile;

            bool overwrite = _settingsManager.GetSetting("General", "OverwriteExisting", false);
            if (File.Exists(finalOutputFile) && !overwrite)
            {
                string skipExist = $"⏭ ПРОПУСК (файл существует): {Path.GetFileName(finalOutputFile)}";
                _logService.Info(skipExist, "VideoEncodingScript");
                progressCallback(fileIndex, totalCount, $"Пропущен (существует): {Path.GetFileName(finalOutputFile)}", 100.0);
                results.Add(skipExist);
                return results;
            }

            // 7. Сборка параметров FFmpeg
            var ffmpegArgs = new List<string>();

            // А) Видеопараметры
            string encoderId = GetSettingValue(settings, "encoder", "x265");
            bool force10Bit = GetSettingValue(settings, "force_10bit", false);
            bool lossless = GetSettingValue(settings, "lossless", false);

            var encoderInstance = _encoderRegistry.GetEncoderById(encoderId) 
                ?? _encoderRegistry.GetAvailableEncoders().FirstOrDefault(e => e.DisplayName == encoderId);

            if (encoderInstance == null)
            {
                throw new InvalidOperationException($"Энкодер '{encoderId}' не найден или не поддерживается оборудованием.");
            }

            var context = new KTools_App.Encoders.EncoderSharedContext(
                IsLossless: lossless,
                Force10Bit: force10Bit,
                ContainerExtension: containerExt
            );

            var encoderArgs = encoderInstance.BuildEncoderArguments(settings, context);
            ffmpegArgs.AddRange(encoderArgs);

            // Б) Аудиопараметры
            string audioCodec = GetSettingValue(settings, "audio_codec", "copy");
            ffmpegArgs.AddRange(new[] { "-c:a", audioCodec });

            if (audioCodec != "copy")
            {
                ffmpegArgs.AddRange(new[] { "-b:a", GetSettingValue(settings, "audio_bitrate", "320k") });
                string ac = GetSettingValue(settings, "audio_channels", "Original");
                if (ac == "2")
                {
                    // Даунмикс в стерео с коэффициентами HandBrake (обход rematrix_maxval)
                    ffmpegArgs.AddRange(new[] { "-af", AppConstants.FFmpegAudio.StereoDownmixPanFilter });
                }
                else if (ac != "Original")
                {
                    ffmpegArgs.AddRange(new[] { "-ac", ac });
                }
            }

            // В) Видеофильтры (Hardsub приоритет 0, AutoCrop приоритет 10)
            var videoFilters = new List<string>();

            if (burnInSubtitles && tempSubFile != null && File.Exists(tempSubFile))
            {
                string escapedSubPath = EscapeFilterPath(tempSubFile);
                string escapedFontsDir = EscapeFilterPath(tempFontsDir);
                videoFilters.Add($"subtitles=filename='{escapedSubPath}':fontsdir='{escapedFontsDir}'");
            }

            bool autoCropEnabled = GetSettingValue(settings, "autocrop_enabled", false);
            if (autoCropEnabled)
            {
                double cropLimit = GetSettingValue(settings, "autocrop_limit", 0.094);
                int cropRound = GetSettingValue(settings, "autocrop_round", 16);
                string cropMode = GetSettingValue(settings, "autocrop_mode", "black");
                int probeFrames = GetSettingValue(settings, "autocrop_probe_frames", 25);
                int skipFrames = GetSettingValue(settings, "autocrop_skip_frames", 2);
                int resetFrames = GetSettingValue(settings, "autocrop_reset_frames", 0);
                int probePoints = GetSettingValue(settings, "autocrop_probe_points", 3);
                int tolerance = GetSettingValue(settings, "autocrop_tolerance", 16);

                var videoTrack = structure.GetVideoTracks().FirstOrDefault();
                int srcW = 0, srcH = 0;
                string sourceRes = videoTrack?.Resolution ?? string.Empty;
                if (!string.IsNullOrEmpty(sourceRes))
                {
                    var resParts = sourceRes.Split('x');
                    if (resParts.Length == 2)
                    {
                        int.TryParse(resParts[0], out srcW);
                        int.TryParse(resParts[1], out srcH);
                    }
                }

                double duration = structure.Duration;
                var probeOffsets = new List<double>();

                if (duration <= 0 || duration < 5.0 || probePoints <= 1)
                {
                    double singleOffset = duration > 0 ? duration / 2.0 : 0.0;
                    probeOffsets.Add(singleOffset);
                }
                else
                {
                    for (int i = 1; i <= probePoints; i++)
                    {
                        probeOffsets.Add(duration * i / (probePoints + 1));
                    }
                }

                _logService.Info($"Запуск многоточечного детектора черных полос для '{Path.GetFileName(filePath)}' (точек: {probeOffsets.Count}, limit={cropLimit:F4}, round={cropRound}, tolerance={tolerance}px, mode={cropMode})", "VideoEncodingScript");

                int maxCropW = 0;
                int maxCropH = 0;
                int successfulProbes = 0;
                bool fullScreenDetectedInPoint = false;
                double fullScreenTimestamp = 0;

                for (int pIdx = 0; pIdx < probeOffsets.Count; pIdx++)
                {
                    double seekSec = probeOffsets[pIdx];
                    progressCallback(fileIndex, totalCount, $"Анализ черных полос (точка {pIdx + 1}/{probeOffsets.Count})...", 0.0);

                    string? pointCrop = await _ffmpegRunner.DetectCropAsync(
                        filePath,
                        skipSeconds: seekSec,
                        probeFrames: probeFrames,
                        limit: cropLimit,
                        round: cropRound,
                        skip: skipFrames,
                        reset: resetFrames,
                        mode: cropMode,
                        cancellationToken: CancellationToken
                    );

                    if (!string.IsNullOrWhiteSpace(pointCrop))
                    {
                        var parts = pointCrop.Split(':');
                        if (parts.Length >= 2 &&
                            int.TryParse(parts[0], out int pW) &&
                            int.TryParse(parts[1], out int pH))
                        {
                            successfulProbes++;
                            _logService.Info($"Контрольная точка {pIdx + 1}/{probeOffsets.Count} ({seekSec:F1}s): определен кадр {pW}x{pH} (crop={pointCrop})", "VideoEncodingScript");

                            if (srcW > 0 && srcH > 0 && pW >= srcW && pH >= srcH)
                            {
                                fullScreenDetectedInPoint = true;
                                fullScreenTimestamp = seekSec;
                            }

                            if (pW > maxCropW) maxCropW = pW;
                            if (pH > maxCropH) maxCropH = pH;
                        }
                    }
                    else
                    {
                        _logService.Warn($"Контрольная точка {pIdx + 1}/{probeOffsets.Count} ({seekSec:F1}s): детектор не обнаружил область кадрирования (возможно, темная сцена)", "VideoEncodingScript");
                    }
                }

                if (successfulProbes > 0 && maxCropW > 0 && maxCropH > 0)
                {
                    if (fullScreenDetectedInPoint)
                    {
                        _logService.Info($"В контрольной точке {fullScreenTimestamp:F1}s обнаружен полнокадровый фрагмент (IMAX/Open Matte). Кадрирование отменено во избежание обрезки полезного видеоряда.", "VideoEncodingScript");
                    }
                    else
                    {
                        // 1. Выравнивание кратности cropRound вверх
                        if (cropRound > 1)
                        {
                            maxCropW = ((maxCropW + cropRound - 1) / cropRound) * cropRound;
                            maxCropH = ((maxCropH + cropRound - 1) / cropRound) * cropRound;
                        }

                        // 2. Гарантия четности для кодеков YUV420p
                        if (maxCropW % 2 != 0) maxCropW++;
                        if (maxCropH % 2 != 0) maxCropH++;

                        if (srcW > 0 && maxCropW > srcW) maxCropW = srcW;
                        if (srcH > 0 && maxCropH > srcH) maxCropH = srcH;

                        // 3. Фильтр допуска (Tolerance): отмена микрообрезки
                        int diffW = srcW - maxCropW;
                        if (srcW > 0 && diffW > 0 && diffW <= tolerance)
                        {
                            _logService.Info($"Разница по ширине ({diffW}px) меньше или равна порогу допуска ({tolerance}px). Ширина сброшена в исходные {srcW}px.", "VideoEncodingScript");
                            maxCropW = srcW;
                        }

                        int diffH = srcH - maxCropH;
                        if (srcH > 0 && diffH > 0 && diffH <= tolerance)
                        {
                            _logService.Info($"Разница по высоте ({diffH}px) меньше или равна порогу допуска ({tolerance}px). Высота сброшена в исходные {srcH}px.", "VideoEncodingScript");
                            maxCropH = srcH;
                        }

                        bool isResolutionChanged = true;
                        if (srcW > 0 && srcH > 0)
                        {
                            isResolutionChanged = maxCropW != srcW || maxCropH != srcH;
                        }

                        if (isResolutionChanged)
                        {
                            // 4. Симметричное центрирование координат
                            int cropX = srcW > maxCropW ? (srcW - maxCropW) / 2 : 0;
                            int cropY = srcH > maxCropH ? (srcH - maxCropH) / 2 : 0;
                            cropX = (cropX / 2) * 2;
                            cropY = (cropY / 2) * 2;

                            string finalCrop = $"{maxCropW}:{maxCropH}:{cropX}:{cropY}";
                            _logService.Info($"Итоговые параметры кадрирования: crop={finalCrop}", "VideoEncodingScript");
                            videoFilters.Add($"crop={finalCrop}");

                            string cropBadgeText = !string.IsNullOrEmpty(sourceRes)
                                ? $"{sourceRes} ➔ {maxCropW}x{maxCropH}"
                                : $"{maxCropW}x{maxCropH}";

                            var queueItem = FilesQueue.FirstOrDefault(f =>
                                f.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));
                            if (queueItem != null)
                            {
                                queueItem.CropBadgeText = cropBadgeText;
                                _logService.Info($"Установлен бейджик кадрирования для '{Path.GetFileName(filePath)}': {cropBadgeText}", "VideoEncodingScript");
                            }
                        }
                        else
                        {
                            _logService.Info("Черные полосы не обнаружены или не требуют обрезки (разрешение сохранено)", "VideoEncodingScript");
                        }
                    }
                }
                else
                {
                    _logService.Warn("Детектор черных полос не смог определить параметры кадрирования ни в одной контрольной точке", "VideoEncodingScript");
                }
            }

            if (videoFilters.Count > 0)
            {
                ffmpegArgs.Add("-vf");
                ffmpegArgs.Add(string.Join(",", videoFilters));
            }

            // Г) Маппинг и общие флаги
            ffmpegArgs.AddRange(new[] {
                "-map", "0:v:0",
                "-map", $"0:a:{relAudioIdx}?"
            });

            var containerTag = encoderInstance.GetContainerTag(settings, context);
            if (!string.IsNullOrEmpty(containerTag))
            {
                ffmpegArgs.AddRange(new[] { "-tag:v", containerTag });
            }

            if (containerExt.Equals(".mp4", StringComparison.OrdinalIgnoreCase))
            {
                ffmpegArgs.AddRange(new[] { "-movflags", "+faststart" });
            }

            ffmpegArgs.AddRange(new[] {
                "-map_metadata", "-1"
            });

            // Д) Аппаратное декодирование на входе
            var inputArgs = await encoderInstance.BuildInputArgumentsAsync(structure, CancellationToken);

            // 8. Запуск процесса кодирования
            _logService.Info($"Запуск FFmpeg для кодирования видео '{Path.GetFileName(filePath)}' в '{Path.GetFileName(finalOutputFile)}'", "VideoEncodingScript");
            progressCallback(fileIndex, totalCount, "Кодирование видео...", 0.0);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);

            bool success = false;
            try
            {
                success = await _ffmpegRunner.RunAsync(
                    inputPath: filePath,
                    outputPath: finalOutputFile,
                    extraArgs: ffmpegArgs,
                    inputArgs: inputArgs.Count > 0 ? inputArgs : null,
                    overwrite: overwrite,
                    totalDuration: structure.Duration,
                    onProgress: progress =>
                    {
                        string msg = $"Кодирование | {progress.Percent:F1}%";
                        if (progress.Fps.HasValue) msg += $" | FPS: {Convert.ToInt32(progress.Fps.Value)}";
                        if (!string.IsNullOrEmpty(progress.Bitrate)) msg += $" | {progress.Bitrate}";
                        if (progress.Speed.HasValue) msg += $" | Скорость: {progress.Speed.Value:F1}x";
                        if (!string.IsNullOrEmpty(progress.Eta)) msg += $" | ETA: {progress.Eta}";

                        progressCallback(fileIndex, totalCount, msg, progress.Percent, progress.Fps, progress.Bitrate);
                    },
                    cancellationToken: cts.Token
                );
            }
            finally
            {
            }

            // 9. Обработка результатов
            if (IsCancelled)
            {
                CleanupFailedOutputFile(finalOutputFile);
                string cancelMsg = $"⚠ Обработка отменена пользователем: {Path.GetFileName(finalOutputFile)}";
                _logService.Info(cancelMsg, "VideoEncodingScript");
                results.Add(cancelMsg);
                return results;
            }

            if (success)
            {
                progressCallback(fileIndex, totalCount, "Завершено!", 100.0);
                string successMsg = $"✅ ОБРАБОТАНО: {Path.GetFileName(finalOutputFile)}";
                _logService.Info(successMsg, "VideoEncodingScript");
                results.Add(successMsg);

                bool overwriteSource = GetSettingValue(settings, "overwrite_source", false);
                if (overwriteSource && string.IsNullOrEmpty(outputPath))
                {
                    ReplaceSourceWithResult(filePath, finalOutputFile, results);
                }
            }
            else
            {
                CleanupFailedOutputFile(finalOutputFile);
                string failMsg = $"❌ ОШИБКА кодирования файла: {Path.GetFileName(filePath)}";
                _logService.Error(failMsg, "VideoEncodingScript");
                results.Add(failMsg);
            }
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrEmpty(_finalOutputFileForCleanup))
            {
                CleanupFailedOutputFile(_finalOutputFileForCleanup);
            }
            string runErr = $"❌ Критическая ошибка при кодировании видео для '{Path.GetFileName(filePath)}': {ex.Message}";
            _logService.Exception(ex, $"Исключение в процессе кодирования '{filePath}': {ex.Message}", "VideoEncodingScript");
            results.Add(runErr);
        }
        finally
        {
            // Очищаем временную папку
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch (Exception ex)
            {
                _logService.Warn($"Не удалось удалить временную папку '{tempDir}': {ex.Message}", "VideoEncodingScript");
            }
        }

        return results;
    }

    private static string EscapeFilterPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        
        // 1. Приводим к POSIX-разделителям
        string clean = path.Replace("\\", "/");
        
        // 2. Экранирование спецсимволов для фильтров FFmpeg (порядок важен)
        clean = clean.Replace(":", "\\:");
        clean = clean.Replace("'", "'\\''");
        clean = clean.Replace("[", "\\[");
        clean = clean.Replace("]", "\\]");
        clean = clean.Replace(",", "\\,");
        clean = clean.Replace(";", "\\;");
        clean = clean.Replace("`", "\\`");
        
        return clean;
    }

    /// <summary>
    /// Вычисляет список доступных в системе CUVID декодеров.
    /// </summary>
    private async Task<HashSet<string>> GetAvailableCuvidDecodersAsync(CancellationToken cancellationToken = default)
    {
        var decoders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string ffmpegPath = _pathManager.GetBinaryPath("ffmpeg");
        if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
        {
            _logService.Warn("kt-ffmpeg не найден, определение CUVID-декодеров пропущено", "VideoEncodingScript");
            return decoders;
        }

        try
        {
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = "-decoders",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                }
            };
            process.Start();
            ActiveProcessTracker.Register(process);

            try
            {
                using var reader = process.StandardOutput;
                string? line;
                while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
                {
                    if (line.Contains("_cuvid", StringComparison.OrdinalIgnoreCase))
                    {
                        // Извлекаем название декодера (обычно второе слово в строке)
                        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        foreach (var part in parts)
                        {
                            if (part.Contains("_cuvid", StringComparison.OrdinalIgnoreCase))
                            {
                                decoders.Add(part.Trim());
                            }
                        }
                    }
                }
                await process.WaitForExitAsync(cancellationToken);
            }
            finally
            {
                ActiveProcessTracker.Unregister(process);
            }
        }
        catch (Exception ex)
        {
            _logService.Warn($"Не удалось определить доступные CUVID-декодеры: {ex.Message}", "VideoEncodingScript");
        }
        return decoders;
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
            if (elem.ValueKind == System.Text.Json.JsonValueKind.Number)
            {
                return elem.TryGetInt32(out var val) && val != 0;
            }
        }
        try
        {
            return Convert.ToBoolean(obj);
        }
        catch
        {
            return false;
        }
    }

    private static string? SafeGetString(object? obj)
    {
        if (obj == null) return null;
        if (obj is string s) return s;
        if (obj is System.Text.Json.JsonElement elem)
        {
            return elem.ValueKind == System.Text.Json.JsonValueKind.String ? elem.GetString() : elem.ToString();
        }
        return obj.ToString();
    }

    public override string GetOutputExtension(string inputPath)
    {
        return ".mp4";
    }
}
