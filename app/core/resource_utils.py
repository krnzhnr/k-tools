# -*- coding: utf-8 -*-
"""Утилиты для работы с ресурсами приложения."""

import os
import sys
from pathlib import Path


def get_resource_path(relative_path: str) -> str:
    """Получить абсолютный путь к ресурсу.

    Учитывает специфику работы PyInstaller (sys._MEIPASS).
    Если файл находится в папке assets, он будет найден как в корне
    (при сборке), так и в самой папке assets (при разработке).

    Args:
        relative_path: Относительный путь к файлу (например, 'app_icon.ico').

    Returns:
        Абсолютный путь к файлу (в виде строки для совместимости с API).
    """
    # 1. Проверяем режим PyInstaller
    base_path = Path(getattr(sys, "_MEIPASS", os.path.abspath(".")))
    rel_p = Path(relative_path)

    # 2. Формируем прямой путь (для сборки, где всё в куче)
    path = base_path / rel_p
    if path.exists():
        return str(path.resolve())

    # 3. Формируем путь через assets (для режима разработки)
    path_with_assets = base_path / "assets" / rel_p
    if path_with_assets.exists():
        return str(path_with_assets.resolve())

    # Fallback: возвращаем разрешенный путь (даже если файла нет)
    return str(path.resolve())
