# -*- coding: utf-8 -*-
"""Централизованные константы медиа-форматов."""

# Видео контейнеры и элементарные потоки
VIDEO_EXTENSIONS = frozenset([
    ".mkv", ".mp4", ".avi", ".mov", ".webm", 
    ".hevc", ".h264", ".h265", ".264", ".265", 
    ".vc1", ".m2v", ".avc", ".ivf"
])

# Аудио форматы (lossless и lossy)
AUDIO_EXTENSIONS = frozenset([
    ".mp3", ".flac", ".wav", ".m4a", ".ogg", 
    ".wma", ".aiff", ".alac", ".ape", ".opus", 
    ".ac3", ".eac3", ".dts", ".wv", ".aac", 
    ".thd", ".truehd", ".mlp", ".dtshd", ".pcm", 
    ".mp2", ".m2a"
])

# Форматы субтитров
SUBTITLE_EXTENSIONS = frozenset([
    ".srt", ".ass", ".ssa", ".sub", ".vtt", 
    ".idx", ".sup"
])

# Маппинг распространенных 3-буквенных ISO 639-2 кодов на 2-буквенные ISO 639-1
ISO_LANG_MAP: dict[str, str] = {
    "rus": "ru", "eng": "en", "jpn": "ja", "spa": "es", "fra": "fr", "fre": "fr",
    "deu": "de", "ger": "de", "ita": "it", "por": "pt", "zho": "zh", "chi": "zh",
    "ara": "ar", "kor": "ko", "pol": "pl", "ukr": "uk", "hin": "hi", "tur": "tr",
    "heb": "he", "vie": "vi", "tha": "th", "nld": "nl", "dut": "nl", "swe": "sv",
    "dan": "da", "fin": "fi", "nob": "no", "nor": "no", "ces": "cs", "cze": "cs",
    "hun": "hu", "ron": "ro", "rum": "ro", "ell": "el", "gre": "el", "ind": "id",
    "msa": "ms", "may": "ms", "bul": "bg", "srp": "sr"
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

