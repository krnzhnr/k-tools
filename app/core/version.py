# -*- coding: utf-8 -*-
"""Управление версионированием приложения."""

import sys

# Эта строка обновляется автоматически скриптом build.py
VERSION = "1.3.0"

def get_app_version() -> str:
    """Получить строку версии приложения.
    
    Returns:
        Строка версии (например, '1.0.026') или 'Dev Mode', если запущено из IDE.
    """
    if getattr(sys, "frozen", False):
        return VERSION
    
    return "1.3.0"

def get_version_badge_text() -> str:
    """Получить текст для бейджа версии с префиксом.
    
    Returns:
        Текст для отображения в UI.
    """
    version = get_app_version()
    return f"v{version}"
