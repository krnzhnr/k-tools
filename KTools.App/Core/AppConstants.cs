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
    /// Расширения для извлекаемых форматов на основе поля codec из mkvmerge.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> RawExtensions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "AC-3", ".ac3" },
        { "ac3", ".ac3" },
        { "E-AC-3", ".eac3" },
        { "eac3", ".eac3" },
        { "E-AC-3+", ".ec3" },
        { "ec3", ".ec3" },
        { "DTS", ".dts" },
        { "dts-hd", ".dts" },
        { "dtshd", ".dts" },
        { "DTS-HD Master Audio", ".dts" },
        { "AAC", ".aac" },
        { "Opus", ".opus" },
        { "FLAC", ".flac" },
        { "Vorbis", ".ogg" },
        { "MP3", ".mp3" },
        { "mp2", ".mp3" },
        { "TrueHD", ".thd" },
        { "thd", ".thd" },
        { "PCM", ".wav" },
        { "pcm_bluray", ".wav" },
        { "pcm_s24le", ".wav" },
        { "pcm_s16le", ".wav" },
        { "pcm_s32le", ".wav" },
        { "pcm_f32le", ".wav" },
        { "MPEG Audio", ".mp3" },
        { "SubRip/SRT", ".srt" },
        { "subrip", ".srt" },
        { "SubStationAlpha", ".ass" },
        { "ass", ".ass" },
        { "ssa", ".ass" },
        { "HDMV PGS", ".sup" },
        { "hdmv_pgs_subtitle", ".sup" },
        { "pgs", ".sup" },
        { "sup", ".sup" },
        { "VobSub", ".idx" },
        { "dvd_subtitle", ".sub" },
        { "AVC/H.264/MPEG-4p10", ".h264" },
        { "H.264", ".h264" },
        { "avc", ".h264" },
        { "HEVC/H.265/MPEG-H", ".h265" },
        { "H.265", ".h265" },
        { "hevc", ".h265" },
        { "MPEG-1/2 Video", ".m2v" },
        { "MPEG-2", ".m2v" },
        { "mpeg2video", ".m2v" },
        { "VC-1", ".vc1" },
        { "vc1", ".vc1" },
        { "VP8", ".ivf" },
        { "VP9", ".ivf" },
        { "AV1", ".ivf" },
        { "Timed Text", ".ass" },
        { "WebVTT", ".ass" }
    };

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
