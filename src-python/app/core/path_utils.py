# -*- coding: utf-8 -*-
"""Модуль для работы с путями приложения."""

import sys
import shutil
import os
import logging
from pathlib import Path

logger = logging.getLogger(__name__)


def get_app_data_dir() -> Path:
    """Определить базовую директорию для записи данных приложения.

    Если текущая директория доступна для записи (Portable-режим),
    возвращает её. В противном случае возвращает подпапку в %LOCALAPPDATA%.

    Returns:
        Объект Path к доступной для записи директории.
    """
    local_dir = _get_base_dir()

    # Пытаемся проверить права доступа через создание временного файла
    test_file = local_dir / ".write_test"
    try:
        with open(test_file, "w") as f:
            f.write("test")
        test_file.unlink()
        return local_dir
    except (PermissionError, OSError):
        # Если записи нет - используем LOCALAPPDATA
        appdata = os.getenv("LOCALAPPDATA")
        if appdata:
            fallback = Path(appdata) / "KTools"
        else:
            # Если совсем беда - используем домашнюю папку
            fallback = Path.home() / ".ktools"

        return fallback


def get_log_dir() -> Path:
    """Получить путь к директории логов.

    Сначала пытается использовать папку 'logs' в корне приложения (Portable),
    затем переключается на AppData.

    Returns:
        Объект Path к директории логов.
    """
    app_data = get_app_data_dir()
    log_dir = app_data / "logs"
    return log_dir


def ensure_dir(path: Path) -> bool:
    """Безопасно создать директорию и проверить доступ.

    Args:
        path: Путь к директории.

    Returns:
        True, если директория существует и доступна для записи.
    """
    try:
        path.mkdir(parents=True, exist_ok=True)

        # Проверка на запись через временный файл
        test_file = path / ".write_test"
        with open(test_file, "w") as f:
            f.write("test")
        test_file.unlink()
        return True
    except Exception:
        logger.debug(
            "Не удалось создать или проверить директорию: %s",
            path,
        )
        return False


def get_binary_path(binary_name: str) -> str:
    """Найти путь к исполняемому файлу."""
    if sys.platform == "win32" and not binary_name.lower().endswith(".exe"):
        binary_name += ".exe"

    name_map = {"ffmpeg.exe": "kt-ffmpeg.exe", "ffprobe.exe": "kt-ffprobe.exe"}
    binary_name = name_map.get(binary_name.lower(), binary_name)

    base_dir = _get_base_dir()
    search_locations = _build_search_locations(base_dir, binary_name)

    for loc in search_locations:
        if loc.exists():
            return str(loc.absolute())

    local_path = base_dir / binary_name
    if local_path.exists():
        return str(local_path.absolute())

    path_from_shutil = shutil.which(binary_name)
    if path_from_shutil:
        return path_from_shutil

    return binary_name


def _get_base_dir() -> Path:
    """Определить базовую директорию приложения."""
    if getattr(sys, "frozen", False):
        return Path(sys.executable).parent.resolve()
    return Path(__file__).parent.parent.parent.resolve()


def _build_search_locations(base_dir: Path, binary_name: str) -> list[Path]:
    """Построить список путей для поиска бинарника."""
    base_name = binary_name.replace(".exe", "").lower()
    subfolder_map = {
        "mkvmerge": "mkvtoolnix",
        "ffprobe": "ffmpeg",
        "ffmpeg": "ffmpeg",
        "kt-ffmpeg": "ffmpeg",
        "kt-ffprobe": "ffmpeg",
        "deew": "DEE",
        "dee": "DEE",
        "qaac64": "ffmpeg",
    }
    subfolder = subfolder_map.get(base_name, base_name)

    return [
        base_dir / "bin" / subfolder / binary_name,
        base_dir / "bin" / binary_name,
        base_dir / "venv" / "Scripts" / binary_name,
        base_dir / ".venv" / "Scripts" / binary_name,
    ]
