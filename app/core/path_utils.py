# -*- coding: utf-8 -*-
"""Модуль для работы с путями приложения."""

import sys
import shutil
from pathlib import Path


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
