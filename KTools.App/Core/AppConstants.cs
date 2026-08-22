// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;

namespace KTools_App.Core;

/// <summary>
/// Централизованный класс глобальных констант приложения.
/// Содержит все неизменяемые параметры, расширения файлов, маппинг языков
/// и метаданные скриптов, дублируя логику оригинального constants.py.
/// Все комментарии и описание методов выполнены на русском языке в соответствии с регламентом.
/// </summary>
public static class AppConstants
{
    /// <summary>
    /// Глобальный размер шрифтовых иконок на домашней странице.
    /// </summary>
    public const double HomeIconSize = 30.0;

    /// <summary>
    /// Глобальный размер подложки (фона) иконок на домашней странице.
    /// </summary>
    public static double HomeIconBgSize => HomeIconSize + 18.0;

    /// <summary>
    /// Множество расширений видео-контейнеров с точкой в нижнем регистре.
    /// </summary>
    public static readonly IReadOnlySet<string> VideoContainers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".mov", ".webm", ".avi", ".m2ts", ".ts", ".m4s"
    };

    /// <summary>
    /// Множество расширений сырых видео-потоков с точкой в нижнем регистре.
    /// </summary>
    public static readonly IReadOnlySet<string> VideoStreams = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".hevc", ".h264", ".h265", ".264", ".265", ".vc1", ".m2v", ".avc", ".ivf"
    };

    /// <summary>
    /// Множество расширений аудио-контейнеров с точкой в нижнем регистре.
    /// </summary>
    public static readonly IReadOnlySet<string> AudioContainers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mka", ".m4a"
    };

    /// <summary>
    /// Объединенный список всех поддерживаемых медиа-контейнеров (видео и аудио) с точкой в нижнем регистре.
    /// </summary>
    public static readonly IReadOnlySet<string> AllContainers = new HashSet<string>(
        VideoContainers.Concat(AudioContainers),
        StringComparer.OrdinalIgnoreCase
    );

    /// <summary>
    /// Множество расширений аудио-потоков с точкой в нижнем регистре.
    /// </summary>
    public static readonly IReadOnlySet<string> AudioStreams = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".wav", ".ogg", ".wma", ".aiff", ".alac", ".ape", ".opus",
        ".ac3", ".eac3", ".ec3", ".dts", ".wv", ".aac", ".thd", ".truehd", ".mlp",
        ".dtshd", ".pcm", ".mp2", ".m2a"
    };

    /// <summary>
    /// Объединенный список всех поддерживаемых медиафайлов (видео и аудио контейнеров и сырых потоков) с точкой в нижнем регистре.
    /// </summary>
    public static readonly IReadOnlySet<string> AllMediaExtensions = new HashSet<string>(
        VideoContainers.Concat(VideoStreams).Concat(AudioContainers).Concat(AudioStreams),
        StringComparer.OrdinalIgnoreCase
    );

    /// <summary>
    /// Множество расширений файлов субтитров с точкой в нижнем регистре.
    /// </summary>
    public static readonly IReadOnlySet<string> SubtitleExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".ass", ".ssa", ".sub", ".vtt", ".idx", ".sup"
    };

    /// <summary>
    /// Строковые константы оригинальных категорий для группировки скриптов в интерфейсе.
    /// </summary>
    public static class ScriptCategory
    {
        /// <summary>
        /// Категория для скриптов обработки звука.
        /// </summary>
        public const string Audio = "Аудио";

        /// <summary>
        /// Категория для скриптов обработки видео.
        /// </summary>
        public const string Video = "Видео";

        /// <summary>
        /// Категория для скриптов работы с контейнерами.
        /// </summary>
        public const string Containers = "Контейнеры";

        /// <summary>
        /// Категория для скриптов работы с файлами субтитров.
        /// </summary>
        public const string Subtitles = "Субтитры";

        /// <summary>
        /// Категория для сетевых скриптов (загрузка медиа).
        /// </summary>
        public const string Network = "Сеть";

        /// <summary>
        /// Категория для утилит и вспомогательных инструментов.
        /// </summary>
        public const string Tools = "Инструменты";
    }

    /// <summary>
    /// Константы имен иконок для всех скриптов (Unicode-глифы Segoe MDL2 Assets / Segoe Fluent Icons).
    /// </summary>
    public static class ScriptIcons
    {
        public const string VideoEncoding = "\uE116";
        public const string ContainerConversion = "\uE895";
        public const string MetadataCleanup = "\uEA99";
        public const string AudioEncoding = "\uE189";
        public const string AudioDownmix = "\uEA3C";
        public const string AudioSpeed = "\uEC49";
        public const string AudioChannels = "\uE8C6";
        public const string AudioShift = "\uE121";
        public const string MkvAssembly = "\uE7B8";
        public const string StreamManagement = "\uE762";
        public const string StreamReplacement = "\uE8AB";
        public const string TrackExtractor = "\uE7AC";
        public const string SubtitlesConvert = "\uE8D2";
        public const string SubtitlesShift = "\uE121";
        public const string AudioTransplant = "\uE8AB";
        public const string Calculator = "\uE1D0";
        public const string MediaDownloader = "\uE128";
    }

    /// <summary>
    /// Константы цветов для категорий скриптов.
    /// </summary>
    public static class CategoryColors
    {
        public static readonly Color VideoBg = ColorHelper.FromArgb(0x33, 0x1B, 0x9D, 0xE3);
        public static readonly Color VideoFg = ColorHelper.FromArgb(0xFF, 0x1B, 0x9D, 0xE3);

        public static readonly Color AudioBg = ColorHelper.FromArgb(0x33, 0x28, 0xCA, 0xC6);
        public static readonly Color AudioFg = ColorHelper.FromArgb(0xFF, 0x28, 0xCA, 0xC6);

        public static readonly Color ContainersBg = ColorHelper.FromArgb(0x33, 0xEB, 0x6E, 0x4D);
        public static readonly Color ContainersFg = ColorHelper.FromArgb(0xFF, 0xEB, 0x6E, 0x4D);

        public static readonly Color SubtitlesBg = ColorHelper.FromArgb(0x33, 0xA8, 0x78, 0xE8);
        public static readonly Color SubtitlesFg = ColorHelper.FromArgb(0xFF, 0xA8, 0x78, 0xE8);

        public static readonly Color NetworkBg = ColorHelper.FromArgb(0x33, 0xE8, 0x4D, 0x6E);
        public static readonly Color NetworkFg = ColorHelper.FromArgb(0xFF, 0xE8, 0x4D, 0x6E);

        public static readonly Color ToolsBg = ColorHelper.FromArgb(0x33, 0xF0, 0xA3, 0x30);
        public static readonly Color ToolsFg = ColorHelper.FromArgb(0xFF, 0xF0, 0xA3, 0x30);

        public static readonly Color DefaultBg = ColorHelper.FromArgb(0x33, 0xFF, 0xFF, 0xFF);
        public static readonly Color DefaultFg = ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
    }

    /// <summary>
    /// Текстовые метаданные (названия и описания) всех 12 скриптов приложения.
    /// </summary>
    public static class ScriptMetadata
    {
        // --- Аудио скрипты ---
        public const string AudioConverterName = "Кодирование аудио";
        public const string AudioConverterDesc = "Перекодирование аудио в QAAC, AAC, FLAC, WAV, E-AC3, AC3 и др. с настройкой качества";

        public const string AudioDownmixName = "Даунмикс в Stereo";
        public const string AudioDownmixDesc = "Даунмикс 5.1/7.1 в Stereo 2.0 (DDP/DD) через Dolby Encoding Engine";

        public const string AudioSpeedName = "Изменение скорости аудио";
        public const string AudioSpeedDesc = "Изменение скорости/тона аудио (PAL ↔ NTSC) с помощью eac3to.";

        public const string AudioSplitName = "Разделение каналов";
        public const string AudioSplitDesc = "Разделение многоканального аудио на моно-WAV файлы с опциональной склейкой в стереопары";

        // --- Видео скрипты ---
        public const string ContainerConvName = "Конвертация контейнера";
        public const string ContainerConvDesc = "Перемещение видео/аудио потоков в другой контейнер без перекодирования";

        public const string MetadataCleanName = "Очистка метаданных";
        public const string MetadataCleanDesc = "Удаление метаданных из видеофайлов с сохранением оригинального качества";

        public const string VideoProcessorName = "Кодирование видео";
        public const string VideoProcessorDesc = "Кодирование видео: изменение формата, вшивание субтитров, фильтрация тегов и настройка звука";

        // --- Контейнерные скрипты ---
        public const string MuxerName = "Сборка MKV";
        public const string MuxerDesc = "Сборка контейнера MKV из отдельных потоков видео, аудио и субтитров с сопоставлением по имени";

        public const string StreamMgrName = "Управление потоками";
        public const string StreamMgrDesc = "Удаление или сохранение выбранных дорожек (видео, аудио, субтитры) в MKV и MP4 файлах.";

        public const string StreamReplName = "Замена потоков";
        public const string StreamReplDesc = "Заменяет дорожки в MKV/MP4 на внешние файлы (видео, аудио, субтитры).";

        public const string TrackExtrName = "Разборка контейнера";
        public const string TrackExtrDesc = "Массовое извлечение потоков из контейнера с авто-именованием.";

        // --- Скрипты субтитров ---
        public const string AssToVttName = "Конвертация субтитров";
        public const string AssToVttDesc = "Конвертация субтитров ASS/SSA/SRT в WebVTT с фильтрацией по актёрам и очисткой тегов.";

        public const string AudioShiftName = "Сдвиг аудио";
        public const string AudioShiftDesc = "Изменение временного сдвига (задержки) аудиопотока с сохранением в Lossless-форматы FLAC/WAV.";

        public const string AudioTransplantName = "Пересадка аудио";
        public const string AudioTransplantDesc = "Пересадка аудиодорожки в видеоконтейнер с визуальной синхронизацией по нативной осциллограмме (Win2D).";

        public const string SubtitleShiftName = "Сдвиг субтитров";
        public const string SubtitleShiftDesc = "Изменение тайминга субтитров (ASS, SRT, VTT, SSA) на заданное количество миллисекунд.";

        public const string BitrateViewerName = "Анализ битрейта видео и аудио";
        public const string BitrateViewerDesc = "Анализ распределения битрейта медиапотоков с интерактивной Win2D GPU-визуализацией.";
    }

    /// <summary>
    /// Маппинг распространенных трехбуквенных языковых кодов ISO 639-2 на двухбуквенные коды ISO 639-1.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> IsoLangMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "rus", "ru" }, { "eng", "en" }, { "jpn", "ja" }, { "spa", "es" }, { "fra", "fr" },
        { "fre", "fr" }, { "deu", "de" }, { "ger", "de" }, { "ita", "it" }, { "por", "pt" },
        { "zho", "zh" }, { "chi", "zh" }, { "ara", "ar" }, { "kor", "ko" }, { "pol", "pl" },
        { "ukr", "uk" }, { "hin", "hi" }, { "tur", "tr" }, { "heb", "he" }, { "vie", "vi" },
        { "tha", "th" }, { "nld", "nl" }, { "dut", "nl" }, { "swe", "sv" }, { "dan", "da" },
        { "fin", "fi" }, { "nob", "no" }, { "nor", "no" }, { "ces", "cs" }, { "cze", "cs" },
        { "hun", "hu" }, { "ron", "ro" }, { "rum", "ro" }, { "ell", "el" }, { "gre", "el" },
        { "ind", "id" }, { "msa", "ms" }, { "may", "ms" }, { "bul", "bg" }, { "srp", "sr" }
    };

    /// <summary>
    /// Проверка языкового кода на корректность.
    /// Возвращает true, если переданный код является валидным 3-буквенным или 2-буквенным кодом, присутствующим в ISO_LANG_MAP.
    /// </summary>
    public static bool IsValidLanguageCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        string cleanCode = code.Trim().ToLowerInvariant();
        return IsoLangMap.ContainsKey(cleanCode) || IsoLangMap.Values.Contains(cleanCode);
    }

    /// <summary>
    /// Нормализация языкового кода.
    /// Приводит IETF-теги (например, 'es-419') к базовому языку ('es'),
    /// а также конвертирует 3-буквенные ISO 639-2 коды в 2-буквенные.
    /// </summary>
    public static string NormalizeLanguage(string lang)
    {
        if (string.IsNullOrWhiteSpace(lang) || lang.Equals("und", StringComparison.OrdinalIgnoreCase))
            return "und";

        string baseLang = lang.Split('-')[0].Trim().ToLowerInvariant();
        return IsoLangMap.TryGetValue(baseLang, out string? value) ? value : baseLang;
    }

    /// <summary>
    /// Расширения для извлекаемых форматов на основе поля codec из mkvmerge и ffprobe.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> RawExtensions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        // Аудио кодеки
        { "AC-3", ".ac3" },
        { "ac3", ".ac3" },
        { "a52", ".ac3" },
        { "E-AC-3", ".eac3" },
        { "eac3", ".eac3" },
        { "E-AC-3+", ".ec3" },
        { "ec3", ".ec3" },
        { "E-AC-3 Atmos", ".eac3" },
        { "E-AC-3 JOC", ".eac3" },
        { "Dolby Digital Plus", ".eac3" },
        { "Dolby Digital", ".ac3" },
        { "DTS", ".dts" },
        { "dts-hd", ".dts" },
        { "dtshd", ".dts" },
        { "dca", ".dts" },
        { "DTS-HD Master Audio", ".dts" },
        { "DTS-HD High Resolution Audio", ".dts" },
        { "DTS Express", ".dts" },
        { "DTS:X", ".dts" },
        { "DTS-ES", ".dts" },
        { "AAC", ".aac" },
        { "aac_latm", ".aac" },
        { "Opus", ".opus" },
        { "FLAC", ".flac" },
        { "Vorbis", ".ogg" },
        { "MP3", ".mp3" },
        { "mp2", ".mp3" },
        { "mp1", ".mp3" },
        { "MPEG Audio", ".mp3" },
        { "MPEG Audio Layer 3", ".mp3" },
        { "TrueHD", ".thd" },
        { "thd", ".thd" },
        { "TrueHD Atmos", ".thd" },
        { "TrueHD / Dolby Atmos", ".thd" },
        { "mlp", ".thd" },
        { "PCM", ".wav" },
        { "pcm_bluray", ".wav" },
        { "pcm_s24le", ".wav" },
        { "pcm_s16le", ".wav" },
        { "pcm_s32le", ".wav" },
        { "pcm_f32le", ".wav" },
        { "pcm_u8", ".wav" },
        { "wavpack", ".wv" },
        { "wv", ".wv" },
        { "ape", ".ape" },
        { "monkeys_audio", ".ape" },
        { "alac", ".m4a" },

        // Субтитры
        { "SubRip/SRT", ".srt" },
        { "subrip", ".srt" },
        { "srt", ".srt" },
        { "SubStationAlpha", ".ass" },
        { "ass", ".ass" },
        { "ssa", ".ass" },
        { "HDMV PGS", ".sup" },
        { "hdmv_pgs_subtitle", ".sup" },
        { "pgs", ".sup" },
        { "sup", ".sup" },
        { "VobSub", ".idx" },
        { "dvd_subtitle", ".sub" },
        { "Timed Text", ".ass" },
        { "WebVTT", ".vtt" },
        { "vtt", ".vtt" },

        // Видео кодеки (H.264 / AVC)
        { "AVC/H.264/MPEG-4p10", ".h264" },
        { "MPEG-4p10/AVC/h.264", ".h264" },
        { "H.264", ".h264" },
        { "h264", ".h264" },
        { "264", ".h264" },
        { "avc", ".h264" },
        { "avc1", ".h264" },
        { "V_MPEG4/ISO/AVC", ".h264" },

        // Видео кодеки (H.265 / HEVC)
        { "HEVC/H.265/MPEG-H", ".h265" },
        { "MPEGH/HEVC", ".h265" },
        { "H.265", ".h265" },
        { "h265", ".h265" },
        { "265", ".h265" },
        { "hevc", ".h265" },
        { "hev1", ".h265" },
        { "hvc1", ".h265" },
        { "V_MPEGH/ISO/HEVC", ".h265" },

        // Прочие видео кодеки
        { "MPEG-1/2 Video", ".m2v" },
        { "MPEG-1 Video", ".m1v" },
        { "MPEG-2 Video", ".m2v" },
        { "MPEG-2", ".m2v" },
        { "mpeg2video", ".m2v" },
        { "mpeg1video", ".m1v" },
        { "V_MPEG1", ".m1v" },
        { "V_MPEG2", ".m2v" },
        { "VC-1", ".vc1" },
        { "vc1", ".vc1" },
        { "wvc1", ".vc1" },
        { "V_MS/VFW/FOURCC", ".vc1" },
        { "VP8", ".ivf" },
        { "VP9", ".ivf" },
        { "AV1", ".ivf" },
        { "av01", ".ivf" },
        { "mjpeg", ".mjpeg" },
        { "mjpg", ".mjpeg" }
    };

    /// <summary>
    /// Интеллектуально определяет расширение файла сырого потока на основе имени кодека и типа дорожки.
    /// Выполняет точный поиск, нормализацию и анализ ключевых подстрок.
    /// </summary>
    /// <param name="codec">Название или идентификатор кодека из метаданных.</param>
    /// <param name="trackType">Тип дорожки (video, audio, subtitles).</param>
    /// <returns>Расширение файла с точкой (например, ".h264", ".hevc", ".dts", ".srt" или fallback-контейнер).</returns>
    public static string ResolveRawExtension(string? codec, string? trackType = null)
    {
        if (!string.IsNullOrWhiteSpace(codec))
        {
            string cleanCodec = codec.Trim();

            // 1. Прямой поиск в словаре
            if (RawExtensions.TryGetValue(cleanCodec, out string? directExt))
            {
                return directExt;
            }

            // 2. Нормализованный поиск без точек и спецсимволов
            string normalized = cleanCodec.ToLowerInvariant().Replace(".", "").Replace("-", "").Replace("_", "").Replace("/", " ");

            // Видео эвристики
            if (normalized.Contains("h264") || normalized.Contains("avc") || normalized.Contains("mpeg4p10") || normalized.Contains("264"))
                return ".h264";
            if (normalized.Contains("hevc") || normalized.Contains("h265") || normalized.Contains("hvc1") || normalized.Contains("hev1") || normalized.Contains("265"))
                return ".h265";
            if (normalized.Contains("mpeg2") || normalized.Contains("m2v"))
                return ".m2v";
            if (normalized.Contains("mpeg1") || normalized.Contains("m1v"))
                return ".m1v";
            if (normalized.Contains("vc1") || normalized.Contains("wvc1"))
                return ".vc1";
            if (normalized.Contains("av1") || normalized.Contains("av01") || normalized.Contains("vp9") || normalized.Contains("vp8"))
                return ".ivf";
            if (normalized.Contains("mjpeg") || normalized.Contains("mjpg"))
                return ".mjpeg";

            // Аудио эвристики
            if (normalized.Contains("truehd") || normalized.Contains("thd") || normalized.Contains("mlp"))
                return ".thd";
            if (normalized.Contains("dts"))
                return ".dts";
            if (normalized.Contains("eac3") || normalized.Contains("ec3") || normalized.Contains("ddp") || normalized.Contains("plus"))
                return ".eac3";
            if (normalized.Contains("ac3") || normalized.Contains("a52"))
                return ".ac3";
            if (normalized.Contains("flac"))
                return ".flac";
            if (normalized.Contains("alac"))
                return ".m4a";
            if (normalized.Contains("opus"))
                return ".opus";
            if (normalized.Contains("vorbis"))
                return ".ogg";
            if (normalized.Contains("aac"))
                return ".aac";
            if (normalized.Contains("mp3") || normalized.Contains("mpegaudio") || normalized.Contains("layer3"))
                return ".mp3";
            if (normalized.Contains("pcm") || normalized.Contains("wav"))
                return ".wav";

            // Субтитры эвристики
            if (normalized.Contains("subrip") || normalized.Contains("srt"))
                return ".srt";
            if (normalized.Contains("ass") || normalized.Contains("ssa") || normalized.Contains("substationalpha"))
                return ".ass";
            if (normalized.Contains("pgs") || normalized.Contains("sup") || normalized.Contains("hdmv"))
                return ".sup";
            if (normalized.Contains("webvtt") || normalized.Contains("vtt"))
                return ".vtt";
            if (normalized.Contains("vobsub"))
                return ".idx";
        }

        // Fallback на основе типа дорожки
        if (!string.IsNullOrWhiteSpace(trackType))
        {
            if (trackType.Equals("video", StringComparison.OrdinalIgnoreCase))
                return ".mkv";
            if (trackType.Equals("audio", StringComparison.OrdinalIgnoreCase))
                return ".mka";
            if (trackType.Equals("subtitles", StringComparison.OrdinalIgnoreCase) || trackType.Equals("subtitle", StringComparison.OrdinalIgnoreCase))
                return ".mks";
        }

        return ".bin";
    }

    /// <summary>
    /// Кодеки субтитров, требующие конвертации (не поддерживают прямое копирование).
    /// Ключ — имя кодека из mkvmerge, значение — целевой кодек FFmpeg.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> SubtitleConvertCodecs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "Timed Text", "ass" },
        { "WebVTT", "ass" }
    };

    /// <summary>
    /// Цветовые hex-коды для графического отображения статусов файлов в UI.
    /// </summary>
    public static class StatusColors
    {
        /// <summary>
        /// Синий цвет для файлов в ожидании обработки.
        /// </summary>
        public const string Pending = "#1B9DE3";

        /// <summary>
        /// Бирюзово-зеленый цвет для успешно завершенных файлов.
        /// </summary>
        public const string Success = "#28CAC6";

        /// <summary>
        /// Красно-оранжевый цвет для файлов с ошибками обработки.
        /// </summary>
        public const string Error = "#EB6E4D";

        /// <summary>
        /// Желтый цвет для предупреждений.
        /// </summary>
        public const string Warning = "#F5A623";
    }

    /// <summary>
    /// Константы для обработки аудио через FFmpeg (даунмикс, ресемплинг).
    /// </summary>
    public static class FFmpegAudio
    {
        /// <summary>
        /// Фильтр pan для даунмикса многоканального аудио в стерео с коэффициентами HandBrake.
        /// Центральный и окружающие каналы подмешиваются с коэффициентом 0.7071 (-3 дБ), LFE отбрасывается.
        /// Использование фильтра pan обходит автоматическую нормализацию матрицы (rematrix_maxval) в libswresample,
        /// сохраняя оригинальную громкость фронтальных каналов.
        /// </summary>
        public const string StereoDownmixPanFilter =
            "pan=stereo|FL=FL+0.7071*FC+0.7071*BL+0.7071*SL|FR=FR+0.7071*FC+0.7071*BR+0.7071*SR";
    }
}
