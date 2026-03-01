# -*- coding: utf-8 -*-
"""Управление версионированием приложения."""

import logging
import re
import sys
from pathlib import Path

logger = logging.getLogger(__name__)

# Эта строка обновляется автоматически скриптом build.py
VERSION = "1.5.0"

_CHANGELOG_VERSION_RE = re.compile(r"^#\s+(\d+\.\d+\.\d+)")


def _read_version_from_changelog() -> str | None:
    """Прочитать версию из первого заголовка CHANGELOG.md.

    Returns:
        Строка версии или None, если не удалось.
    """
    changelog = Path(__file__).resolve().parents[2] / "CHANGELOG.md"
    try:
        with changelog.open(encoding="utf-8") as fh:
            for line in fh:
                match = _CHANGELOG_VERSION_RE.match(line)
                if match:
                    return match.group(1)
    except OSError:
        logger.debug("CHANGELOG.md не найден: %s", changelog)
    return None


def get_app_version() -> str:
    """Получить строку версии приложения.

    Returns:
        Строка версии (например, '1.4.9').
    """
    if getattr(sys, "frozen", False):
        return VERSION

    return _read_version_from_changelog() or VERSION


def get_version_badge_text() -> str:
    """Получить текст для бейджа версии с префиксом.

    Returns:
        Текст для отображения в UI.
    """
    version = get_app_version()
    return f"v{version}"
