# -*- coding: utf-8 -*-
"""Модуль для работы с путями приложения."""

import os
import sys
import shutil
from pathlib import Path


def get_binary_path(binary_name: str) -> str:
    """Найти путь к исполняемому файлу.

    Сначала ищет в папке 'bin/' относительно исполняемого файла
    (или корня проекта при разработке), затем в системном PATH.

    Args:
        binary_name: Имя бинарника (например, 'ffmpeg').

    Returns:
        Полный путь к бинарнику или просто имя, если не найдено.
    """
    # Если на Windows и нет расширения .exe — добавляем
    if sys.platform == "win32" and not binary_name.lower().endswith(".exe"):
        binary_name += ".exe"

    # 1. Определяем базовую директорию
    # В замороженном (PyInstaller) состоянии sys.executable — это путь к exe
    if getattr(sys, "frozen", False):
        base_dir = Path(sys.executable).parent
    else:
        # В режиме разработки — корень проекта (над app/)
        base_dir = Path(__file__).parent.parent.parent

    # 2. Определение потенциальных путей внутри bin/
    # Сначала ищем в подпапке с именем инструмента (напр. bin/ffmpeg/ffmpeg.exe)
    # Для mkvmerge ищем в папке mkvtoolnix
    subfolder_name = "mkvtoolnix" if "mkvmerge" in binary_name.lower() else binary_name.replace(".exe", "").lower()
    
    search_locations = [
        base_dir / "bin" / subfolder_name / binary_name,  # bin/ffmpeg/ffmpeg.exe
        base_dir / "bin" / binary_name,                    # bin/ffmpeg.exe (legacy)
    ]

    for loc in search_locations:
        if loc.exists():
            return str(loc.absolute())

    # 3. Пробуем найти рядом с исполняемым файлом (в корне)
    local_path = base_dir / binary_name
    if local_path.exists():
        return str(local_path.absolute())

    # 4. Fallback: поиск в системном PATH
    path_from_shutil = shutil.which(binary_name)
    if path_from_shutil:
        return path_from_shutil

    # 5. Последняя надежда: просто возвращаем имя
    return binary_name
