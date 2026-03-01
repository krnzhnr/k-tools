# -*- coding: utf-8 -*-
"""Централизованные константы медиа-форматов."""

# --- Видео ---
VIDEO_CONTAINERS = frozenset([".mkv", ".mp4", ".mov", ".webm", ".avi"])
VIDEO_STREAMS = frozenset(
    [
        ".hevc",
        ".h264",
        ".h265",
        ".264",
        ".265",
        ".vc1",
        ".m2v",
        ".avc",
        ".ivf",
    ]
)
VIDEO_EXTENSIONS = VIDEO_CONTAINERS | VIDEO_STREAMS

# --- Аудио ---
AUDIO_CONTAINERS = frozenset([".mka", ".m4a"])
AUDIO_STREAMS = frozenset(
    [
        ".mp3",
        ".flac",
        ".wav",
        ".ogg",
        ".wma",
        ".aiff",
        ".alac",
        ".ape",
        ".opus",
        ".ac3",
        ".eac3",
        ".dts",
        ".wv",
        ".aac",
        ".thd",
        ".truehd",
        ".mlp",
        ".dtshd",
        ".pcm",
        ".mp2",
        ".m2a",
    ]
)
AUDIO_EXTENSIONS = AUDIO_CONTAINERS | AUDIO_STREAMS

# --- Агрегаты ---
# Полноценные контейнеры (для демуксинга, очистки метаданных и т.д.)
MEDIA_CONTAINERS = VIDEO_CONTAINERS | AUDIO_CONTAINERS

# Форматы субтитров
SUBTITLE_EXTENSIONS = frozenset(
    [".srt", ".ass", ".ssa", ".sub", ".vtt", ".idx", ".sup"]
)

# Маппинг распространенных 3-буквенных ISO 639-2 кодов на 2-буквенные ISO 639-1
ISO_LANG_MAP: dict[str, str] = {
    "rus": "ru",
    "eng": "en",
    "jpn": "ja",
    "spa": "es",
    "fra": "fr",
    "fre": "fr",
    "deu": "de",
    "ger": "de",
    "ita": "it",
    "por": "pt",
    "zho": "zh",
    "chi": "zh",
    "ara": "ar",
    "kor": "ko",
    "pol": "pl",
    "ukr": "uk",
    "hin": "hi",
    "tur": "tr",
    "heb": "he",
    "vie": "vi",
    "tha": "th",
    "nld": "nl",
    "dut": "nl",
    "swe": "sv",
    "dan": "da",
    "fin": "fi",
    "nob": "no",
    "nor": "no",
    "ces": "cs",
    "cze": "cs",
    "hun": "hu",
    "ron": "ro",
    "rum": "ro",
    "ell": "el",
    "gre": "el",
    "ind": "id",
    "msa": "ms",
    "may": "ms",
    "bul": "bg",
    "srp": "sr",
}


# Расширения для извлекаемых форматов на основе поля codec из mkvmerge
RAW_EXTENSIONS: dict[str, str] = {
    "AC-3": ".ac3",
    "E-AC-3": ".eac3",
    "DTS": ".dts",
    "DTS-HD Master Audio": ".dts",
    "AAC": ".aac",
    "Opus": ".opus",
    "FLAC": ".flac",
    "Vorbis": ".ogg",
    "MP3": ".mp3",
    "TrueHD": ".thd",
    "PCM": ".wav",
    "MPEG Audio": ".mp3",
    "SubRip/SRT": ".srt",
    "SubStationAlpha": ".ass",
    "HDMV PGS": ".sup",
    "VobSub": ".idx",
    "AVC/H.264/MPEG-4p10": ".h264",
    "H.264": ".h264",
    "HEVC/H.265/MPEG-H": ".h265",
    "H.265": ".h265",
    "MPEG-1/2 Video": ".m2v",
    "MPEG-2": ".m2v",
    "VC-1": ".vc1",
    "VP8": ".ivf",
    "VP9": ".ivf",
    "AV1": ".ivf",
    "Timed Text": ".ass",
    "WebVTT": ".ass",
}

# Кодеки субтитров, требующие конвертации (не поддерживают copy).
# Ключ — имя кодека из mkvmerge, значение — целевой кодек FFmpeg.
SUBTITLE_CONVERT_CODECS: dict[str, str] = {
    "Timed Text": "ass",
    "WebVTT": "ass",
}


class ScriptCategory:
    """Константы имен категорий скриптов."""

    AUDIO = "Аудио"
    VIDEO = "Видео"
    CONTAINERS = "Контейнеры"


class ScriptMetadata:
    """Метаданные всех скриптов приложения."""

    # Аудио
    AUDIO_CONVERTER_NAME = "Транскодирование аудио"
    AUDIO_CONVERTER_DESC = (
        "Перекодирует аудиофайлы в QAAC, AAC, FLAC, WAV, "
        "E-AC3, AC3 и др. с настройкой качества"
    )

    AUDIO_DOWNMIX_NAME = "Даунмикс в Stereo"
    AUDIO_DOWNMIX_DESC = (
        "Даунмикс 5.1/7.1 в Stereo 2.0 (DDP/DD) через Dolby Encoding Engine"
    )

    AUDIO_SPEED_NAME = "Изменение скорости аудио"
    AUDIO_SPEED_DESC = (
        "Изменяет скорость/тон аудио (PAL ↔ NTSC, Кино) с помощью eac3to."
    )

    AUDIO_SPLIT_NAME = "Декомпозиция каналов"
    AUDIO_SPLIT_DESC = (
        "Разбивает многоканальное аудио на моно-WAV с "
        "опциональной склейкой в стереопары"
    )

    # Видео
    CONTAINER_CONV_NAME = "Ремуксинг"
    CONTAINER_CONV_DESC = (
        "Перемещает видео/аудио потоки в другой "
        "контейнер без перекодирования"
    )

    METADATA_CLEAN_NAME = "Очистка метаданных"
    METADATA_CLEAN_DESC = (
        "Удаляет все метаданные из видеофайлов, "
        "сохраняя оригинальное качество"
    )

    # Контейнеры
    MUXER_NAME = "Муксинг"
    MUXER_DESC = (
        "Собирает MKV из видео, аудио и субтитров. "
        "Файлы сопоставляются по имени."
    )

    STREAM_MGR_NAME = "Управление потоками"
    STREAM_MGR_DESC = (
        "Удаление или сохранение выбранных дорожек "
        "(видео, аудио, субтитры) в MKV и MP4 файлах."
    )

    STREAM_REPL_NAME = "Замена потоков"
    STREAM_REPL_DESC = (
        "Заменяет дорожки в MKV/MP4 на внешние файлы "
        "(видео, аудио, субтитры)."
    )

    TRACK_EXTR_NAME = "Демуксинг"
    TRACK_EXTR_DESC = (
        "Массовое извлечение потоков из контейнера с авто-именованием."
    )


# Конфигурация категорий для UI
# Порядок в словаре определяет порядок отображения
CATEGORY_CONFIG = {
    ScriptCategory.VIDEO: {
        "icon": "VIDEO",
        "nav_key": "video",
        "color": ("rgba(27, 157, 227, 0.2)", "#1B9DE3"),  # Голубой
    },
    ScriptCategory.AUDIO: {
        "icon": "MUSIC",
        "nav_key": "audio",
        "color": ("rgba(40, 202, 198, 0.2)", "#28CAC6"),  # Бирюзовый
    },
    ScriptCategory.CONTAINERS: {
        "icon": "SHARE",
        "nav_key": "containers",
        "color": ("rgba(235, 110, 77, 0.2)", "#EB6E4D"),  # Терракотовый
    },
}


def normalize_language(lang: str) -> str:
    """Нормализация языкового кода.

    Приводит IETF-теги (например, 'es-419') к базовому языку ('es'),
    а также конвертирует 3-буквенные ISO 639-2 коды в 2-буквенные.
    """
    if not lang or lang.lower() == "und":
        return "und"

    # Отсекаем региональные суффиксы для IETF тегов (например, pt-br -> pt)
    base_lang = lang.split("-")[0].lower()

    return ISO_LANG_MAP.get(base_lang, base_lang)
