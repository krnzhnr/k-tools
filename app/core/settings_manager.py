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
            self._migrate_old_sections()
        except Exception as init_err:
            logger.error(
                "Критическая ошибка инициализации QSettings (%s): %s",
                settings_path,
                init_err,
            )
            self._settings = QSettings("KTools", "KTools")

    def _determine_settings_path(self) -> Path:
        """Определить путь для сохранения файла настроек."""
        import tempfile
        from app.core.path_utils import get_app_data_dir, ensure_dir

        app_data_dir = get_app_data_dir()

        if ensure_dir(app_data_dir):
            return app_data_dir / "settings.ini"

        # Крайний случай - системный temp
        try:
            temp_path = (
                Path(tempfile.gettempdir())
                / "ktools_settings_fallback.ini"
            )
            return temp_path
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

    @property
    def clear_list_on_add(self) -> bool:
        """Очищать ли список перед добавлением новых файлов."""
        with self._lock:
            return self._settings.value(
                "General/clear_list_on_add", False, type=bool
            )

    @clear_list_on_add.setter
    def clear_list_on_add(self, value: bool) -> None:
        """Установить режим очистки списка при добавлении."""
        with self._lock:
            self._settings.setValue("General/clear_list_on_add", value)
            self._settings.sync()
        logger.info("Настройка 'clear_list_on_add' изменена на: %s", value)

    @property
    def show_logs_tab(self) -> bool:
        """Показывать ли вкладку логов."""
        with self._lock:
            return self._settings.value(
                "General/show_logs_tab", False, type=bool
            )

    @show_logs_tab.setter
    def show_logs_tab(self, value: bool) -> None:
        """Установить отображение вкладки логов."""
        with self._lock:
            self._settings.setValue("General/show_logs_tab", value)
            self._settings.sync()
        logger.info("Настройка 'show_logs_tab' изменена на: %s", value)

    @property
    def auto_check_updates(self) -> bool:
        """Проверять ли обновления автоматически при запуске."""
        with self._lock:
            return self._settings.value(
                "Updates/auto_check_updates", True, type=bool
            )

    @auto_check_updates.setter
    def auto_check_updates(self, value: bool) -> None:
        """Установить режим автоматической проверки обновлений."""
        with self._lock:
            self._settings.setValue("Updates/auto_check_updates", value)
            self._settings.sync()
        logger.info("Настройка 'auto_check_updates' изменена на: %s", value)

    @property
    def include_pre_releases(self) -> bool:
        """Включать ли предварительные версии (пре-релизы)."""
        with self._lock:
            return self._settings.value(
                "Updates/include_pre_releases", False, type=bool
            )

    @include_pre_releases.setter
    def include_pre_releases(self, value: bool) -> None:
        """Установить режим включения пре-релизов."""
        with self._lock:
            self._settings.setValue("Updates/include_pre_releases", value)
            self._settings.sync()
        logger.info("Настройка 'include_pre_releases' изменена на: %s", value)

    @property
    def last_check_time(self) -> str:
        """Время последней проверки обновлений."""
        with self._lock:
            return self._settings.value(
                "Updates/last_check_time", "", type=str
            )

    @last_check_time.setter
    def last_check_time(self, value: str) -> None:
        """Установить время последней проверки обновлений."""
        with self._lock:
            self._settings.setValue("Updates/last_check_time", value)
            self._settings.sync()
        logger.info("Настройка 'last_check_time' изменена на: '%s'", value)

    def initialize_all_defaults(self, registry: Any) -> None:
        """Инициализировать отсутствующие настройки значениями по умолчанию.

        Args:
            registry: Реестр скриптов (ScriptRegistry).
        """
        import os
        import json

        # 1. Общие настройки
        gen_defaults = {
            "General/theme": "Dark",
            "General/overwrite_existing": False,
            "General/default_output_subfolder": "KTools_Result",
            "General/use_auto_subfolder": False,
            "General/max_parallel_tasks": max(1, (os.cpu_count() or 2) // 2),
            "General/clear_list_on_add": False,
            "General/show_logs_tab": False,
            "Updates/auto_check_updates": True,
            "Updates/include_pre_releases": False,
            "Updates/last_check_time": "",
        }

        with self._lock:
            # Сначала общие
            for key, val in gen_defaults.items():
                if not self._settings.contains(key):
                    self._settings.setValue(key, val)

            # Теперь настройки скриптов
            for script in registry.scripts:
                group = self._get_safe_script_name(script.name)
                for field in script.settings_schema:
                    # Исключаем статические заголовки/заглушки из файла
                    from app.core.abstract_script import SettingType

                    if field.setting_type == SettingType.SUBTITLE:
                        continue

                    full_key = f"{group}/{field.key}"
                    if not self._settings.contains(full_key):
                        val = field.default
                        if isinstance(val, (list, dict)):
                            val = json.dumps(val, ensure_ascii=False)
                        self._settings.setValue(full_key, val)

            self._settings.sync()
        logger.info(
            "Завершена инициализация настроек по умолчанию в settings.ini"
        )

    def _get_safe_script_name(self, script_name: str) -> str:
        """Нормализовать имя скрипта для использования
        в качестве имени секции (группы).

        Заменяет слэши и другие спецсимволы, которые QSettings
        может интерпретировать как разделители подгрупп в INI.
        """
        # Словарь перевода русских названий в читаемые ASCII-имена
        translation_map = {
            "ASS/SRT → VTT": "ASS_SRT_to_VTT",
            "Транскодирование аудио": "Audio_Transcoding",
            "Даунмикс в Stereo": "Audio_Downmix_to_Stereo",
            "Изменение скорости аудио": "Audio_Speed_Change",
            "Декомпозиция каналов": "Channel_Decomposition",
            "Ремуксинг": "Remuxing",
            "Очистка метаданных": "Metadata_Cleanup",
            "Муксинг": "Muxing",
            "Управление потоками": "Stream_Management",
            "Замена потоков": "Stream_Replacement",
            "Демуксинг": "Demuxing",
            "Видео-процессор": "Video_Processor",
        }

        # Получаем безопасное английское имя
        safe_name = translation_map.get(script_name, script_name)
        safe_name = (
            safe_name.replace("/", "_")
            .replace("\\", "_")
            .replace(" ", "_")
        )
        return f"Script_{safe_name}"

    def _migrate_old_sections(self) -> None:
        """Перенести настройки из нечитаемых секций в новые ASCII."""
        old_to_new = {
            "Script_ASS/SRT → VTT": "Script_ASS_SRT_to_VTT",
            "Script_Транскодирование аудио": "Script_Audio_Transcoding",
            "Script_Даунмикс в Stereo": "Script_Audio_Downmix_to_Stereo",
            "Script_Изменение скорости аудио": "Script_Audio_Speed_Change",
            "Script_Декомпозиция каналов": "Script_Channel_Decomposition",
            "Script_Ремуксинг": "Script_Remuxing",
            "Script_Очистка метаданных": "Script_Metadata_Cleanup",
            "Script_Муксинг": "Script_Muxing",
            "Script_Управление потоками": "Script_Stream_Management",
            "Script_Замена потоков": "Script_Stream_Replacement",
            "Script_Демуксинг": "Script_Demuxing",
            "Script_Видео-процессор": "Script_Video_Processor",
        }

        with self._lock:
            all_keys = self._settings.allKeys()

            # 1. Сначала очищаем поврежденные ключи, мусор и плейсхолдеры
            for k in list(all_keys):
                if (
                    "SRT%20%U" in k
                    or "%U04" in k
                    or "\\" in k
                    or k.startswith("Script_ASS/")
                    or "sub_filters_placeholder" in k
                ):
                    self._settings.remove(k)

            # Перечитываем ключи после первичной очистки
            all_keys = self._settings.allKeys()

            # 2. Выполняем плоскую миграцию по абсолютным путям
            for old_group, new_group in old_to_new.items():
                prefix = f"{old_group}/"
                matching_keys = [
                    k for k in all_keys if k.startswith(prefix)
                ]

                if matching_keys:
                    logger.info(
                        "Миграция настроек из '%s' в '%s'...",
                        old_group,
                        new_group,
                    )
                    for full_key in matching_keys:
                        rel_key = full_key[len(prefix):]
                        val = self._settings.value(full_key)

                        # Записываем в новую группу
                        self._settings.setValue(
                            f"{new_group}/{rel_key}", val
                        )

                        # Удаляем старый ключ
                        self._settings.remove(full_key)

            self._settings.sync()

    def get_script_setting(
        self,
        script_name: str,
        key: str,
        default: Any,
        type_hint: Any = None,
    ) -> Any:
        """Получить настройку для конкретного скрипта.

        Args:
            script_name: Имя скрипта (секция).
            key: Ключ настройки.
            default: Значение по умолчанию.
            type_hint: Ожидаемый тип данных (int, bool, str и т.д.).
        """
        import json

        group = self._get_safe_script_name(script_name)
        with self._lock:
            val = self._settings.value(f"{group}/{key}", None)
            if val is not None:
                # Проверяем, является ли сохраненное значение JSON-строкой
                if (
                    isinstance(val, str)
                    and (val.startswith("[") or val.startswith("{"))
                ):
                    try:
                        return json.loads(val)
                    except Exception:
                        pass

                if type_hint is not None:
                    try:
                        if type_hint is bool:
                            return str(val).lower() in ("true", "1")
                        return type_hint(val)
                    except Exception:
                        pass
                return val
            return default

    def set_script_setting(
        self, script_name: str, key: str, value: Any
    ) -> None:
        """Сохранить настройку для конкретного скрипта.

        Args:
            script_name: Имя скрипта (секция).
            key: Ключ настройки.
            value: Значение для сохранения.
        """
        import json

        group = self._get_safe_script_name(script_name)
        with self._lock:
            # Сериализуем сложные структуры в JSON для сохранения читаемости
            if isinstance(value, (list, dict)):
                serialized_val = json.dumps(value, ensure_ascii=False)
                self._settings.setValue(f"{group}/{key}", serialized_val)
            else:
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
