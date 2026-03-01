# -*- coding: utf-8 -*-
"""Менеджер настроек приложения."""

import logging
from pathlib import Path
from typing import Any
from PyQt6.QtCore import QSettings

from app.core.singleton import SingletonMeta

logger = logging.getLogger(__name__)


class SettingsManager(metaclass=SingletonMeta):
    """Менеджер настроек на базе QSettings.

    Использует формат INI для портативности.
    Файл настроек сохраняется в корневой папке приложения.
    """

    def __init__(self) -> None:
        import threading

        self._lock = threading.RLock()

        settings_path = self._determine_settings_path()

        try:
            self._settings = QSettings(
                str(settings_path), QSettings.Format.IniFormat
            )
            logger.info("Загружены настройки из: %s", settings_path)
        except Exception as init_err:
            logger.error(
                "Критическая ошибка инициализации QSettings (%s): %s",
                settings_path,
                init_err,
            )
            self._settings = QSettings("KTools", "KTools")

    def _determine_settings_path(self) -> Path:
        """Определить путь для сохранения файла настроек."""
        import os
        import tempfile

        settings_path = Path("settings.ini").absolute()

        try:
            test_path = (
                settings_path
                if settings_path.exists()
                else settings_path.parent
            )
            if not os.access(test_path, os.W_OK):
                fallback_dir = (
                    Path(
                        os.getenv(
                            "LOCALAPPDATA", Path.home() / "AppData" / "Local"
                        )
                    )
                    / "KTools"
                )
                fallback_dir.mkdir(parents=True, exist_ok=True)
                return fallback_dir / "settings.ini"
            return settings_path
        except Exception:
            try:
                return (
                    Path(tempfile.gettempdir())
                    / "ktools_settings_fallback.ini"
                )
            except Exception:
                return Path("memory_settings_fallback.ini")

    @property
    def overwrite_existing(self) -> bool:
        """Нужно ли перезаписывать существующие файлы."""
        with self._lock:
            return self._settings.value(
                "General/overwrite_existing", False, type=bool
            )

    @overwrite_existing.setter
    def overwrite_existing(self, value: bool) -> None:
        """Установить значение перезаписи файлов."""
        with self._lock:
            self._settings.setValue("General/overwrite_existing", value)
            self._settings.sync()
        logger.info("Настройка 'overwrite_existing' изменена на: %s", value)

    @property
    def default_output_subfolder(self) -> str:
        """Имя подпапки для результатов по умолчанию."""
        with self._lock:
            return self._settings.value(
                "General/default_output_subfolder", "KTools_Result", type=str
            )

    @default_output_subfolder.setter
    def default_output_subfolder(self, value: str) -> None:
        """Установить имя подпапки для результатов."""
        with self._lock:
            self._settings.setValue("General/default_output_subfolder", value)
            self._settings.sync()
        logger.info(
            "Настройка 'default_output_subfolder' изменена на: %s", value
        )

    @property
    def use_auto_subfolder(self) -> bool:
        """Нужно ли создавать автоматическую подпапку."""
        with self._lock:
            return self._settings.value(
                "General/use_auto_subfolder", False, type=bool
            )

    @use_auto_subfolder.setter
    def use_auto_subfolder(self, value: bool) -> None:
        """Установить использование автоматической подпапки."""
        with self._lock:
            self._settings.setValue("General/use_auto_subfolder", value)
            self._settings.sync()
        logger.info("Настройка 'use_auto_subfolder' изменена на: %s", value)

    @property
    def theme(self) -> str:
        """Тема приложения (Dark/Light)."""
        with self._lock:
            return self._settings.value("General/theme", "Dark", type=str)

    @theme.setter
    def theme(self, value: str) -> None:
        """Установить тему приложения (Dark/Light)."""
        with self._lock:
            self._settings.setValue("General/theme", value)
            self._settings.sync()
        logger.info("Настройка 'theme' изменена на: %s", value)

    @property
    def max_parallel_tasks(self) -> int:
        """Максимальное количество параллельных задач."""
        import os

        default = max(1, (os.cpu_count() or 2) // 2)
        with self._lock:
            return self._settings.value(
                "General/max_parallel_tasks", default, type=int
            )

    @max_parallel_tasks.setter
    def max_parallel_tasks(self, value: int) -> None:
        """Установить максимальное количество параллельных задач."""
        with self._lock:
            self._settings.setValue("General/max_parallel_tasks", value)
            self._settings.sync()
        logger.info("Настройка 'max_parallel_tasks' изменена на: %d", value)

    def _get_safe_script_name(self, script_name: str) -> str:
        """Нормализовать имя скрипта для использования в качестве имени секции (группы).  # noqa: E501

        Заменяет слэши и другие спецсимволы, которые QSettings
        может интерпретировать как разделители подгрупп в INI.
        """
        # Заменяем / и \ на нижнее подчеркивание
        safe_name = script_name.replace("/", "_").replace("\\", "_")
        return f"Script_{safe_name}"

    def get_script_setting(
        self, script_name: str, key: str, default: Any
    ) -> Any:
        """Получить настройку для конкретного скрипта.

        Args:
            script_name: Имя скрипта (секция).
            key: Ключ настройки.
            default: Значение по умолчанию.
        """
        group = self._get_safe_script_name(script_name)
        with self._lock:
            return self._settings.value(f"{group}/{key}", default)

    def set_script_setting(
        self, script_name: str, key: str, value: Any
    ) -> None:
        """Сохранить настройку для конкретного скрипта.

        Args:
            script_name: Имя скрипта (секция).
            key: Ключ настройки.
            value: Значение для сохранения.
        """
        group = self._get_safe_script_name(script_name)
        with self._lock:
            self._settings.setValue(f"{group}/{key}", value)
            self._settings.sync()

    def sync(self) -> None:
        """Принудительная синхронизация с диском."""
        with self._lock:
            self._settings.sync()

    def reset_all_settings(self) -> None:
        """Сбросить все настройки приложения к значениям по умолчанию.

        Удаляет все записи из файла настроек.
        """
        with self._lock:
            self._settings.clear()
            self._settings.sync()
        logger.warning(
            "Все настройки приложения были сброшены к значениям по умолчанию"
        )
