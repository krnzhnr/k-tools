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

namespace KTools_App.Scripts;

/// <summary>
/// Скрипт комплексного кодирования видео с поддержкой аппаратного ускорения NVENC (GPU) или x265 (CPU),
/// автоматического извлечения шрифтов, фильтрации и вшивания субтитров (burn-in).
/// Все комментарии и сообщения логов выполнены исключительно на русском языке с исчерпывающей полнотой.
/// </summary>
public sealed class VideoEncodingScript : AbstractScript
{
    private static bool _isNvencChecked;
    private static bool _isNvencSupported;
    private static Task<bool>? _nvencCheckTask;
    private static readonly object _nvencLock = new();
    private string? _finalOutputFileForCleanup;
    private readonly IFFmpegRunner _ffmpegRunner;
    private readonly IMediaProbeService _mediaProbeService;

    public VideoEncodingScript(
        ILogService logService, 
        ISettingsManager settingsManager, IPathManager pathManager,
        IFFmpegRunner ffmpegRunner,
        IMediaProbeService mediaProbeService)
        : base(logService, settingsManager, pathManager)
    {
        _ffmpegRunner = ffmpegRunner ?? throw new ArgumentNullException(nameof(ffmpegRunner));
        _mediaProbeService = mediaProbeService ?? throw new ArgumentNullException(nameof(mediaProbeService));

        lock (_nvencLock)
        {
            if (!_isNvencChecked && _nvencCheckTask == null)
            {
                _nvencCheckTask = Task.Run(async () =>
                {
                    try
                    {
                        bool result = await _ffmpegRunner.CheckNvencSupportAsync();
                        _isNvencSupported = result;
                        _isNvencChecked = true;
                        _logService.Info($"Фоновая проверка поддержки NVENC завершена. Результат: {result}", "VideoEncodingScript");
                        return result;
                    }
                    catch (Exception ex)
                    {
                        _logService.Exception(ex, "Ошибка при фоновой проверке поддержки NVENC в VideoEncodingScript", "VideoEncodingScript");
                        _isNvencSupported = false;
                        _isNvencChecked = true;
                        return false;
                    }
                });
            }
        }
    }

    /// <summary>
    /// Проверяет поддержку NVENC в фоновом режиме с возвратом кэшированного результата.
    /// </summary>
    private bool IsNvencSupported
    {
        get
        {
            return _isNvencSupported;
        }
    }

    public override string Name => AppConstants.ScriptMetadata.VideoProcessorName;
    public override string Description => AppConstants.ScriptMetadata.VideoProcessorDesc;
    public override string Category => AppConstants.ScriptCategory.Video;
    public override string IconName => "video";
    public override string[] FileExtensions => AppConstants.VideoContainers.ToArray();
    public override string[] RequiredDependencies => new[] { "ffmpeg" };

    public override List<SettingField> SettingsSchema
    {
        get
        {
            string defaultEncoder = IsNvencSupported ? "NVENC (GPU)" : "x265 (CPU)";

            return new List<SettingField>
            {
                // --- Вкладка Видео: Энкодер ---
                new SettingField(
                    "encoder",
                    "Энкодер",
                    SettingType.Combo,
                    defaultEncoder,
                    "Видео:Энкодер",
                    options: new List<string> { "NVENC (GPU)", "x265 (CPU)" },
                    column: 0,
                    colSpan: 1
                ),
                new SettingField(
                    "nvenc_preset",
                    "Пресет NVENC",
                    SettingType.Combo,
                    "p7",
                    "Видео:Энкодер",
                    options: new List<string> { "p1", "p2", "p3", "p4", "p5", "p6", "p7" },
                    visibleIfKey: "encoder",
                    visibleIfValues: new List<string> { "NVENC (GPU)" },
                    column: 1,
                    colSpan: 1
                ),
                new SettingField(
                    "cpu_preset",
                    "Пресет CPU",
                    SettingType.Combo,
                    "medium",
                    "Видео:Энкодер",
                    options: new List<string> { "ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow" },
                    visibleIfKey: "encoder",
                    visibleIfValues: new List<string> { "x265 (CPU)" },
                    column: 1,
                    colSpan: 1
                ),
                new SettingField(
                    "force_10bit",
                    "Принудительно 10-бит (Main10)",
                    SettingType.Checkbox,
                    false,
                    "Видео:Энкодер",
                    column: 0,
                    colSpan: 2
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
                ),
                new SettingField(
                    "auto_bitrate",
                    "Авторасчет битрейта и буфера",
                    SettingType.Checkbox,
                    true,
                    "Видео:Битрейт",
                    column: 1,
                    colSpan: 1,
                    visibilityConditions: new List<SettingVisibilityCondition>
                    {
                        new("encoder", "NVENC (GPU)"),
                        new("nvenc_rc", new List<string> { "cbr", "vbr", "vbr_hq" }),
                        new("lossless", "False")
                    }
                ),
                new SettingField(
                    "nvenc_rc",
                    "Режим битрейта (NVENC)",
                    SettingType.Combo,
                    "vbr_hq",
                    "Видео:Битрейт",
                    options: new List<string> { "cbr", "vbr", "vbr_hq", "constqp" },
                    column: 0,
                    colSpan: 1,
                    visibilityConditions: new List<SettingVisibilityCondition>
                    {
                        new("encoder", "NVENC (GPU)"),
                        new("lossless", "False")
                    }
                ),
                new SettingField(
                    "cpu_rc",
                    "Режим качества (CPU)",
                    SettingType.Combo,
                    "CRF",
                    "Видео:Битрейт",
                    options: new List<string> { "CRF", "Битрейт (ABR)" },
                    column: 0,
                    colSpan: 1,
                    visibilityConditions: new List<SettingVisibilityCondition>
                    {
                        new("encoder", "x265 (CPU)"),
                        new("lossless", "False")
                    }
                ),
                new SettingField(
                    "v_bitrate",
                    "Битрейт видео (кбит/с)",
                    SettingType.Int,
                    4000,
                    "Видео:Битрейт",
                    comment: "Целевой битрейт видеопотока",
                    column: 1,
                    colSpan: 1,
                    visibilityConditions: new List<SettingVisibilityCondition>
                    {
                        new("encoder", "NVENC (GPU)"),
                        new("nvenc_rc", new List<string> { "cbr", "vbr", "vbr_hq" }),
                        new("lossless", "False")
                    }
                ),
                new SettingField(
                    "v_qp",
                    "QP / Quality (NVENC)",
                    SettingType.Int,
                    0,
                    "Видео:Битрейт",
                    comment: "Параметр постоянного качества QP (0-51). 0 - без потерь",
                    column: 1,
                    colSpan: 1,
                    visibilityConditions: new List<SettingVisibilityCondition>
                    {
                        new("encoder", "NVENC (GPU)"),
                        new("nvenc_rc", "constqp"),
                        new("lossless", "False")
                    }
                ),
                new SettingField(
                    "cpu_crf",
                    "CRF (x265 CPU)",
                    SettingType.Int,
                    23,
                    "Видео:Битрейт",
                    comment: "Коэффициент постоянного качества (0-51). Меньше = лучше",
                    column: 1,
                    colSpan: 1,
                    visibilityConditions: new List<SettingVisibilityCondition>
                    {
                        new("encoder", "x265 (CPU)"),
                        new("cpu_rc", "CRF"),
                        new("lossless", "False")
                    }
                ),
                new SettingField(
                    "cpu_v_bitrate",
                    "Битрейт видео (кбит/с)",
                    SettingType.Int,
                    4000,
                    "Видео:Битрейт",
                    comment: "Целевой битрейт видеопотока",
                    column: 1,
                    colSpan: 1,
                    visibilityConditions: new List<SettingVisibilityCondition>
                    {
                        new("encoder", "x265 (CPU)"),
                        new("cpu_rc", "Битрейт (ABR)"),
                        new("lossless", "False")
                    }
                ),
                new SettingField(
                    "min_bitrate",
                    "Минимальный битрейт (кбит/с)",
                    SettingType.Int,
                    4000,
                    "Видео:Битрейт",
                    column: 0,
                    colSpan: 1,
                    visibilityConditions: new List<SettingVisibilityCondition>
                    {
                        new("encoder", "NVENC (GPU)"),
                        new("nvenc_rc", new List<string> { "cbr", "vbr", "vbr_hq" }),
                        new("lossless", "False")
                    }
                ),
                new SettingField(
                    "max_bitrate",
                    "Максимальный битрейт (кбит/с)",
                    SettingType.Int,
                    8000,
                    "Видео:Битрейт",
                    column: 1,
                    colSpan: 1,
                    visibilityConditions: new List<SettingVisibilityCondition>
                    {
                        new("encoder", "NVENC (GPU)"),
                        new("nvenc_rc", new List<string> { "cbr", "vbr", "vbr_hq" }),
                        new("lossless", "False")
                    }
                ),
                new SettingField(
                    "bufsize",
                    "Размер буфера (кбит)",
                    SettingType.Int,
                    16000,
                    "Видео:Битрейт",
                    column: 0,
                    colSpan: 2,
                    visibilityConditions: new List<SettingVisibilityCondition>
                    {
                        new("encoder", "NVENC (GPU)"),
                        new("nvenc_rc", new List<string> { "cbr", "vbr", "vbr_hq" }),
                        new("lossless", "False")
                    }
                ),

                // --- Вкладка Видео: Фильтры ---
                new SettingField(
                    "sub_filters_placeholder",
                    "Фильтры пока не настроены",
                    SettingType.Subtitle,
                    "",
                    "Видео:Фильтры",
                    comment: "Здесь будут доступны видеофильтры: ресайз, обрезка чёрных полос и др."
                ),

                // --- Вкладка Видео: Дополнительно ---
                new SettingField(
                    "nv_lookahead",
                    "Lookahead (NVENC)",
                    SettingType.Combo,
                    "32",
                    "Видео:Расширенные параметры",
                    options: new List<string> { "Выкл", "8", "16", "24", "32" },
                    visibleIfKey: "encoder",
                    visibleIfValues: new List<string> { "NVENC (GPU)" },
                    column: 0,
                    colSpan: 1
                ),
                new SettingField(
                    "nv_aq",
                    "Spatial AQ (NVENC)",
                    SettingType.Checkbox,
                    true,
                    "Видео:Расширенные параметры",
                    visibleIfKey: "encoder",
                    visibleIfValues: new List<string> { "NVENC (GPU)" },
                    column: 1,
                    colSpan: 1
                ),
                new SettingField(
                    "cpu_tune",
                    "Tune (x265 CPU)",
                    SettingType.Combo,
                    "Нет",
                    "Видео:Расширенные параметры",
                    options: new List<string> { "Нет", "grain", "animation", "fastdecode", "zerolatency", "psnr", "ssim" },
                    visibleIfKey: "encoder",
                    visibleIfValues: new List<string> { "x265 (CPU)" },
                    column: 0,
                    colSpan: 1
                ),
                new SettingField(
                    "cpu_aq_mode",
                    "AQ Mode (x265 CPU)",
                    SettingType.Combo,
                    "2",
                    "Видео:Расширенные параметры",
                    options: new List<string> { "0", "1", "2", "3" },
                    visibleIfKey: "encoder",
                    visibleIfValues: new List<string> { "x265 (CPU)" },
                    column: 1,
                    colSpan: 1
                ),
                new SettingField(
                    "cpu_lookahead",
                    "Lookahead (x265 CPU)",
                    SettingType.Combo,
                    "20",
                    "Видео:Расширенные параметры",
                    options: new List<string> { "Выкл", "10", "20", "30", "40" },
                    visibleIfKey: "encoder",
                    visibleIfValues: new List<string> { "x265 (CPU)" },
                    column: 0,
                    colSpan: 2
                ),

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
                    "sub_keywords",
                    "Поиск надписей",
                    SettingType.KeywordList,
                    new List<Dictionary<string, object>>
                    {
                        new() { { "word", "Надписи" }, { "active", true } }
                    },
                    "Субтитры",
                    column: 0,
                    colSpan: 2
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
                    colSpan: 2
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
            };
        }
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

            string stem = Path.GetFileNameWithoutExtension(filePath);
            string targetFile = Path.Combine(targetDir, $"{stem}.mp4");
            string finalOutputFile = GetSafeOutputPath(filePath, targetFile);

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
            string encoder = GetSettingValue(settings, "encoder", "x265 (CPU)");
            bool force10Bit = GetSettingValue(settings, "force_10bit", false);
            string pixFmt = force10Bit ? "yuv420p10le" : "yuv420p";

            if (encoder == "NVENC (GPU)")
            {
                if (pixFmt == "yuv420p10le") pixFmt = "p010le";
                ffmpegArgs.AddRange(new[] { "-c:v", "hevc_nvenc", "-pix_fmt", pixFmt });
                ffmpegArgs.AddRange(new[] { "-preset", GetSettingValue(settings, "nvenc_preset", "p7") });

                if (GetSettingValue(settings, "lossless", false))
                {
                    int vQp = GetSettingValue(settings, "v_qp", 0);
                    ffmpegArgs.AddRange(new[] { "-rc", "constqp", "-qp", vQp.ToString(), "-tune", "lossless" });
                }
                else
                {
                    string rc = GetSettingValue(settings, "nvenc_rc", "vbr_hq");
                    ffmpegArgs.AddRange(new[] { "-rc", rc });

                    if (rc == "constqp")
                    {
                        int vQp = GetSettingValue(settings, "v_qp", 0);
                        int qp = vQp > 0 ? vQp : 23;
                        ffmpegArgs.AddRange(new[] { "-qp", qp.ToString() });
                    }
                    else
                    {
                        int vBr = GetSettingValue(settings, "v_bitrate", 4000);
                        int minBr = vBr;
                        int maxBr = vBr * 2;
                        int bufSize = maxBr * 2;

                        if (!GetSettingValue(settings, "auto_bitrate", true))
                        {
                            minBr = GetSettingValue(settings, "min_bitrate", vBr);
                            maxBr = GetSettingValue(settings, "max_bitrate", vBr * 2);
                            bufSize = GetSettingValue(settings, "bufsize", maxBr * 2);
                        }

                        ffmpegArgs.AddRange(new[] {
                            "-b:v", $"{vBr}k",
                            "-minrate", $"{minBr}k",
                            "-maxrate", $"{maxBr}k",
                            "-bufsize", $"{bufSize}k"
                        });
                    }

                    string nvLookahead = GetSettingValue(settings, "nv_lookahead", "32");
                    if (nvLookahead != "Выкл")
                    {
                        ffmpegArgs.AddRange(new[] { "-rc-lookahead", nvLookahead });
                    }
                    if (GetSettingValue(settings, "nv_aq", true))
                    {
                        ffmpegArgs.AddRange(new[] { "-spatial-aq", "1", "-aq-strength", "15" });
                    }
                }
            }
            else // x265 CPU
            {
                ffmpegArgs.AddRange(new[] { "-c:v", "libx265", "-pix_fmt", pixFmt });
                ffmpegArgs.AddRange(new[] { "-preset", GetSettingValue(settings, "cpu_preset", "medium") });

                var x265Params = new List<string>();

                if (GetSettingValue(settings, "lossless", false))
                {
                    x265Params.Add("lossless=1");
                }
                else
                {
                    string cpuRc = GetSettingValue(settings, "cpu_rc", "CRF");
                    if (cpuRc == "CRF")
                    {
                        int crf = GetSettingValue(settings, "cpu_crf", 23);
                        ffmpegArgs.AddRange(new[] { "-crf", crf.ToString() });
                    }
                    else
                    {
                        int vBr = GetSettingValue(settings, "cpu_v_bitrate", 4000);
                        int maxBr = vBr * 2;
                        int bufSize = maxBr * 2;
                        ffmpegArgs.AddRange(new[] {
                            "-b:v", $"{vBr}k",
                            "-maxrate", $"{maxBr}k",
                            "-bufsize", $"{bufSize}k"
                        });
                    }
                }

                string tune = GetSettingValue(settings, "cpu_tune", "Нет");
                if (tune != "Нет")
                {
                    ffmpegArgs.AddRange(new[] { "-tune", tune });
                }

                x265Params.Add($"aq-mode={GetSettingValue(settings, "cpu_aq_mode", "2")}");

                string cpuLa = GetSettingValue(settings, "cpu_lookahead", "20");
                if (cpuLa != "Выкл")
                {
                    x265Params.Add($"rc-lookahead={cpuLa}");
                }

                if (x265Params.Count > 0)
                {
                    ffmpegArgs.Add("-x265-params");
                    ffmpegArgs.Add(string.Join(":", x265Params));
                }
            }

            // Б) Аудиопараметры
            string audioCodec = GetSettingValue(settings, "audio_codec", "copy");
            ffmpegArgs.AddRange(new[] { "-c:a", audioCodec });

            if (audioCodec != "copy")
            {
                ffmpegArgs.AddRange(new[] { "-b:a", GetSettingValue(settings, "audio_bitrate", "320k") });
                string ac = GetSettingValue(settings, "audio_channels", "Original");
                if (ac != "Original")
                {
                    ffmpegArgs.AddRange(new[] { "-ac", ac });
                }
            }

            // В) Вшивание субтитров (burn-in) через фильтры
            if (tempSubFile != null && File.Exists(tempSubFile))
            {
                string escapedSubPath = EscapeFilterPath(tempSubFile);
                string escapedFontsDir = EscapeFilterPath(tempFontsDir);
                ffmpegArgs.Add("-vf");
                ffmpegArgs.Add($"subtitles=filename='{escapedSubPath}':fontsdir='{escapedFontsDir}'");
            }

            // Г) Маппинг и общие флаги
            ffmpegArgs.AddRange(new[] {
                "-map", "0:v:0",
                "-map", $"0:a:{relAudioIdx}?",
                "-tag:v", "hvc1",
                "-movflags", "+faststart",
                "-map_metadata", "-1"
            });

            // Д) Аппаратное декодирование на входе (CUVID)
            var inputArgs = new List<string>();
            if (encoder == "NVENC (GPU)")
            {
                var vTracks = structure.GetVideoTracks();
                if (vTracks.Count > 0)
                {
                    string vCodec = vTracks[0].Codec.ToLowerInvariant();
                    // Проверяем доступные декодеры CUVID
                    var decoders = GetAvailableCuvidDecoders();
                    string? cuvid = null;

                    var mapping = new Dictionary<string, string>
                    {
                        { "h264", "h264_cuvid" },
                        { "hevc", "hevc_cuvid" },
                        { "vp8", "vp8_cuvid" },
                        { "vp9", "vp9_cuvid" },
                        { "vc1", "vc1_cuvid" },
                        { "mpeg2video", "mpeg2_cuvid" },
                        { "mpeg4", "mpeg4_cuvid" }
                    };

                    if (mapping.TryGetValue(vCodec, out var mappedCuvid) && decoders.Contains(mappedCuvid))
                    {
                        cuvid = mappedCuvid;
                    }

                    if (cuvid != null)
                    {
                        inputArgs.AddRange(new[] { "-hwaccel", "cuda", "-c:v", cuvid });
                    }
                }
            }

            // 8. Запуск процесса кодирования
            _logService.Info($"Запуск FFmpeg для кодирования видео '{Path.GetFileName(filePath)}' в '{Path.GetFileName(finalOutputFile)}'", "VideoEncodingScript");
            progressCallback(fileIndex, totalCount, "Кодирование видео...", 0.0);

            using var cts = new CancellationTokenSource();
            var cancelMonitorTask = Task.Run(async () =>
            {
                while (!IsCancelled && !cts.IsCancellationRequested)
                {
                    await Task.Delay(100);
                }
                if (IsCancelled)
                {
                    cts.Cancel();
                }
            });

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
                cts.Cancel();
                await cancelMonitorTask;
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
    private static HashSet<string> GetAvailableCuvidDecoders()
    {
        var decoders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = "-decoders",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                }
            };
            process.Start();
            using var reader = process.StandardOutput;
            string? line;
            while ((line = reader.ReadLine()) != null)
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
            process.WaitForExit();
        }
        catch
        {
            // Ошибки при получении игнорируем
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
