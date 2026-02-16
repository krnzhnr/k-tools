# -*- coding: utf-8 -*-
"""Менеджер настроек приложения."""

import logging
from pathlib import Path
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
        return self._settings.value("general/overwrite_existing", False, type=bool)

    @overwrite_existing.setter
    def overwrite_existing(self, value: bool) -> None:
        """Установить значение перезаписи файлов."""
        self._settings.setValue("general/overwrite_existing", value)
        self._settings.sync()
        logger.info("Настройка 'overwrite_existing' изменена на: %s", value)

    def sync(self) -> None:
        """Принудительная синхронизация с диском."""
        self._settings.sync()
