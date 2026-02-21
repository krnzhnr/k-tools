# -*- coding: utf-8 -*-
"""Менеджер настроек приложения."""

import logging
from pathlib import Path
from typing import Any
from PyQt6.QtCore import QSettings


logger = logging.getLogger(__name__)


class SettingsManager:
    """Менеджер настроек на базе QSettings.

    Использует формат INI для портативности.
    Файл настроек сохраняется в корневой папке приложения.
    """

    _instance = None

    def __new__(cls):
        if cls._instance is None:
            cls._instance = super(SettingsManager, cls).__new__(cls)
            cls._instance._initialized = False
        return cls._instance

    def __init__(self) -> None:
        if self._initialized:
            return

        # Путь к файлу настроек в корневом каталоге
        settings_path = Path("settings.ini").absolute()
        self._settings = QSettings(str(settings_path), QSettings.Format.IniFormat)
        
        logger.info("Загружены настройки из: %s", settings_path)
        self._initialized = True

    @property
    def overwrite_existing(self) -> bool:
        """Нужно ли перезаписывать существующие файлы."""
        self._settings.beginGroup("General")
        val = self._settings.value("overwrite_existing", False, type=bool)
        self._settings.endGroup()
        return val

    @overwrite_existing.setter
    def overwrite_existing(self, value: bool) -> None:
        """Установить значение перезаписи файлов."""
        self._settings.beginGroup("General")
        self._settings.setValue("overwrite_existing", value)
        self._settings.endGroup()
        self._settings.sync()
        logger.info("Настройка 'overwrite_existing' изменена на: %s", value)

    @property
    def default_output_subfolder(self) -> str:
        """Имя подпапки для результатов по умолчанию."""
        self._settings.beginGroup("General")
        val = self._settings.value("default_output_subfolder", "KTools_Result", type=str)
        self._settings.endGroup()
        return val

    @default_output_subfolder.setter
    def default_output_subfolder(self, value: str) -> None:
        """Установить имя подпапки для результатов."""
        self._settings.beginGroup("General")
        self._settings.setValue("default_output_subfolder", value)
        self._settings.endGroup()
        self._settings.sync()
        logger.info("Настройка 'default_output_subfolder' изменена на: %s", value)

    @property
    def use_auto_subfolder(self) -> bool:
        """Нужно ли создавать автоматическую подпапку."""
        self._settings.beginGroup("General")
        val = self._settings.value("use_auto_subfolder", False, type=bool)
        self._settings.endGroup()
        return val

    @use_auto_subfolder.setter
    def use_auto_subfolder(self, value: bool) -> None:
        """Установить использование автоматической подпапки."""
        self._settings.beginGroup("General")
        self._settings.setValue("use_auto_subfolder", value)
        self._settings.endGroup()
        self._settings.sync()
        logger.info("Настройка 'use_auto_subfolder' изменена на: %s", value)

    @property
    def theme(self) -> str:
        """Тема приложения (Dark/Light)."""
        self._settings.beginGroup("General")
        val = self._settings.value("theme", "Dark", type=str)
        self._settings.endGroup()
        return val

    @theme.setter
    def theme(self, value: str) -> None:
        """Установить тему приложения (Dark/Light)."""
        self._settings.beginGroup("General")
        self._settings.setValue("theme", value)
        self._settings.endGroup()
        self._settings.sync()
        logger.info("Настройка 'theme' изменена на: %s", value)

    @property
    def max_parallel_tasks(self) -> int:
        """Максимальное количество параллельных задач."""
        import os
        default = max(1, (os.cpu_count() or 2) // 2)
        self._settings.beginGroup("General")
        val = self._settings.value("max_parallel_tasks", default, type=int)
        self._settings.endGroup()
        return val

    @max_parallel_tasks.setter
    def max_parallel_tasks(self, value: int) -> None:
        """Установить максимальное количество параллельных задач."""
        self._settings.beginGroup("General")
        self._settings.setValue("max_parallel_tasks", value)
        self._settings.endGroup()
        self._settings.sync()
        logger.info("Настройка 'max_parallel_tasks' изменена на: %d", value)

    def _get_safe_script_name(self, script_name: str) -> str:
        """Нормализовать имя скрипта для использования в качестве имени секции (группы).
        
        Заменяет слэши и другие спецсимволы, которые QSettings 
        может интерпретировать как разделители подгрупп в INI.
        """
        # Заменяем / и \ на нижнее подчеркивание
        safe_name = script_name.replace("/", "_").replace("\\", "_")
        return f"Script_{safe_name}"

    def get_script_setting(self, script_name: str, key: str, default: Any) -> Any:
        """Получить настройку для конкретного скрипта.
        
        Args:
            script_name: Имя скрипта (секция).
            key: Ключ настройки.
            default: Значение по умолчанию.
        """
        group = self._get_safe_script_name(script_name)
        self._settings.beginGroup(group)
        val = self._settings.value(key, default)
        self._settings.endGroup()
        return val

    def set_script_setting(self, script_name: str, key: str, value: Any) -> None:
        """Сохранить настройку для конкретного скрипта.
        
        Args:
            script_name: Имя скрипта (секция).
            key: Ключ настройки.
            value: Значение для сохранения.
        """
        group = self._get_safe_script_name(script_name)
        self._settings.beginGroup(group)
        self._settings.setValue(key, value)
        self._settings.endGroup()
        self._settings.sync()

    def sync(self) -> None:
        """Принудительная синхронизация с диском."""
        self._settings.sync()

    def reset_all_settings(self) -> None:
        """Сбросить все настройки приложения к значениям по умолчанию.
        
        Удаляет все записи из файла настроек.
        """
        self._settings.clear()
        self._settings.sync()
        logger.warning("Все настройки приложения были сброшены к значениям по умолчанию")
