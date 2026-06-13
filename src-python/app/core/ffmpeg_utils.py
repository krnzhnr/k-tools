# -*- coding: utf-8 -*-
"""Утилиты для работы с путями и строками в FFmpeg."""

import re
from pathlib import Path


def sanitize_filename_part(text: str, max_length: int = 50) -> str:
    """Очищает строку для использования в качестве части имени файла.

    Удаляет недопустимые символы и символы, ломающие парсинг фильтров FFmpeg:
    ' , ; ` [ ] и стандартные запрещенные символы Windows.

    Args:
        text: Исходная строка (заголовок дорожки и т.д.).
        max_length: Максимальная длина результата.

    Returns:
        Очищенная строка, безопасная для файловой системы и FFmpeg.
    """
    if not text:
        return "untitled"

    # Удаляем или заменяем недопустимые символы
    sanitized = re.sub(r"[\\/:*?\"<>|\[\]\n\r\t',;`]+", "", text)
    sanitized = sanitized.strip(". ")  # Удаляем точки и пробелы с краев

    if len(sanitized) > max_length:
        # Обрезаем и убеждаемся, что не заканчивается на разделитель
        sanitized = sanitized[:max_length].strip("_ .-")

    if not sanitized:
        return "untitled"

    return sanitized


def escape_ffmpeg_path(path: Path | str) -> str:
    """Экранирует путь для использования внутри фильтров FFmpeg.

    Например, для использования в subtitles=filename='PATH'.
    Учитывает правила экранирования POSIX-разделителей и спецсимволов.

    Args:
        path: Путь к файлу (Path или строка).

    Returns:
        Экранированная строка пути.
    """
    if not path:
        return ""

    path_str = str(Path(path).absolute())

    # 1. Приводим к POSIX разделителям (безопаснее в FFmpeg на всех ОС)
    escaped = path_str.replace("\\", "/")

    # 2. Экранирование спецсимволов. Порядок замен важен.
    # Экранируем двоеточие (актуально для Windows C\:)
    escaped = escaped.replace(":", "\\:")

    # Экранируем одинарную кавычку (путь будет обернут в '')
    escaped = escaped.replace("'", "\\'")

    # Экранируем квадратные скобки (используются для меток потоков)
    escaped = escaped.replace("[", "\\[")
    escaped = escaped.replace("]", "\\]")

    # Экранируем запятую (разделитель фильтров)
    escaped = escaped.replace(",", "\\,")

    # Экранируем точку с запятой (разделитель фильтр-графов)
    escaped = escaped.replace(";", "\\;")

    # Экранируем обратную кавычку
    escaped = escaped.replace("`", "\\`")

    return escaped
