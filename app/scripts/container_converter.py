# -*- coding: utf-8 -*-
"""Скрипт конвертации контейнера видеофайлов."""

import logging
from pathlib import Path
from typing import Any

from app.core.abstract_script import (
    AbstractScript,
    ProgressCallback,
    SettingField,
    SettingType,
)
from app.core.settings_manager import SettingsManager
from app.core.output_resolver import OutputResolver
from app.infrastructure.ffmpeg_runner import FFmpegRunner

logger = logging.getLogger(__name__)

# Маппинг отображаемого формата к расширению файла.
FORMAT_MAP: dict[str, str] = {
    "MP4": ".mp4",
    "MKV": ".mkv",
}


from app.core.constants import VIDEO_EXTENSIONS

class ContainerConverterScript(AbstractScript):
    """Конвертация контейнера видео без перекодирования.

    Аналог 'mkv to mp4.bat' и 'mp4 to mkv.bat' —
    меняет контейнер с копированием потоков (-c copy).
    """

    def __init__(self) -> None:
        """Инициализация скрипта конвертации контейнера."""
        self._ffmpeg = FFmpegRunner()
        self._resolver = OutputResolver()
        logger.info(
            "Скрипт конвертации контейнера создан"
        )

    @property
    def category(self) -> str:
        """Категория скрипта."""
        return "Видео"

    @property
    def name(self) -> str:
        """Отображаемое имя скрипта."""
        return "Ремуксинг"

    @property
    def description(self) -> str:
        """Описание скрипта."""
        return (
            "Перемещает видео/аудио потоки в другой "
            "контейнер без перекодирования"
        )

    @property
    def icon_name(self) -> str:
        """Имя иконки FluentIcon."""
        return "SYNC"

    @property
    def file_extensions(self) -> list[str]:
        """Допустимые расширения файлов."""
        return list(VIDEO_EXTENSIONS)

    @property
    def settings_schema(self) -> list[SettingField]:
        """Схема настроек скрипта."""
        return [
            SettingField(
                key="target_format",
                label="Целевой формат",
                setting_type=SettingType.COMBO,
                default="MP4",
                options=list(FORMAT_MAP.keys()),
            ),
            SettingField(
                key="delete_original",
                label="Удалить исходный файл",
                setting_type=SettingType.CHECKBOX,
                default=False,
            ),
        ]

    def execute_single(
        self,
        file_path: Path,
        settings: dict[str, Any],
        output_path: str | None = None,
    ) -> list[str]:
        """Конвертировать контейнер одного файла."""
        target_key = settings.get("target_format", "MP4")
        target_ext = FORMAT_MAP.get(target_key, ".mp4")
        delete_original = settings.get("delete_original", False)
        overwrite = SettingsManager().overwrite_existing
        
        results: list[str] = []

        if file_path.suffix.lower() == target_ext:
            msg = f"⏭ Пропущен (уже {target_key}): {file_path.name}"
            logger.info("Файл '%s' уже имеет расширение %s, пропуск", file_path.name, target_ext)
            return [msg]

        target_dir = self._resolver.resolve(file_path, output_path)
        output_file_path = target_dir / file_path.with_suffix(target_ext).name
        
        # Безопасное получение пути
        output_file_path = self._get_safe_output_path(file_path, output_file_path)

        if output_file_path.exists() and not overwrite:
            msg = f"⏭ Пропущен (файл существует): {output_file_path.name}"
            logger.info("Пропуск: выходной файл '%s' уже существует", output_file_path.name)
            return [msg]

        logger.debug("Старт FFmpeg для смены контейнера (copy)")
        success = self._ffmpeg.run(
            input_path=file_path,
            output_path=output_file_path,
            extra_args=["-c", "copy"],
            overwrite=overwrite,
        )

        if success:
            msg = f"✅ Конвертировано: {output_file_path.name}"
            logger.info("Успешная смена контейнера: '%s'", output_file_path.name)
            if delete_original:
                self._delete_source(file_path, results)
        else:
            msg = f"❌ Ошибка: {file_path.name}"
            logger.error("Ошибка при смене контейнера для файла: '%s'", file_path.name)

        results.insert(0, msg)
        return results
