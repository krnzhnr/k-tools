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
        Абсолютный путь к файлу.
    """
    # 1. Проверяем режим PyInstaller
    base_path = getattr(sys, '_MEIPASS', os.path.abspath("."))
    
    # 2. Формируем прямой путь (для сборки, где всё в куче)
    path = os.path.join(base_path, relative_path)
    if os.path.exists(path):
        return path
        
    # 3. Формируем путь через assets (для режима разработки)
    path_with_assets = os.path.join(base_path, "assets", relative_path)
    if os.path.exists(path_with_assets):
        return path_with_assets
        
    # Fallback: возвращаем как есть
    return path
