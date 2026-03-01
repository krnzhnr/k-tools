# -*- coding: utf-8 -*-
"""Скрипт конвертации контейнера видеофайлов."""

from app.core.constants import VIDEO_CONTAINERS, ScriptCategory, ScriptMetadata
import logging
from pathlib import Path
from typing import Any

from app.core.abstract_script import (
    AbstractScript,
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


class ContainerConverterScript(AbstractScript):
    """Конвертация контейнера видео без перекодирования.

    Аналог 'mkv to mp4.bat' и 'mp4 to mkv.bat' —
    меняет контейнер с копированием потоков (-c copy).
    """

    def __init__(self) -> None:
        """Инициализация скрипта конвертации контейнера."""
        self._ffmpeg = FFmpegRunner()
        self._resolver = OutputResolver()
        logger.info("Скрипт конвертации контейнера создан")

    @property
    def category(self) -> str:
        """Категория скрипта."""
        return ScriptCategory.VIDEO

    @property
    def name(self) -> str:
        """Отображаемое имя скрипта."""
        return ScriptMetadata.CONTAINER_CONV_NAME

    @property
    def description(self) -> str:
        """Описание скрипта."""
        return ScriptMetadata.CONTAINER_CONV_DESC

    @property
    def icon_name(self) -> str:
        """Имя иконки FluentIcon."""
        return "SYNC"

    @property
    def file_extensions(self) -> list[str]:
        """Допустимые расширения файлов."""
        return list(VIDEO_CONTAINERS)

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

        if file_path.suffix.lower() == target_ext:
            logger.info(
                "Файл '%s' уже имеет расширение %s, пропуск",
                file_path.name,
                target_ext,
            )
            return [f"⏭ ПРОПУСК (уже {target_key}): {file_path.name}"]

        target_dir = self._resolver.resolve(file_path, output_path)
        output_file_path = self._get_safe_output_path(
            file_path, target_dir / file_path.with_suffix(target_ext).name
        )
        overwrite = SettingsManager().overwrite_existing

        if output_file_path.exists() and not overwrite:
            logger.info(
                "Пропуск: выходной файл '%s' уже существует",
                output_file_path.name,
            )
            return [f"⏭ ПРОПУСК (файл существует): {output_file_path.name}"]

        return self._run_conversion(
            file_path,
            output_file_path,
            settings.get("delete_original", False),
            overwrite,
        )

    def _run_conversion(
        self,
        file_path: Path,
        output_file_path: Path,
        delete_original: bool,
        overwrite: bool,
    ) -> list[str]:
        """Вызов FFmpeg для смены контейнера."""
        logger.debug("Старт FFmpeg для смены контейнера (copy)")
        success = self._ffmpeg.run(
            input_path=file_path,
            output_path=output_file_path,
            extra_args=["-c", "copy"],
            overwrite=overwrite,
        )

        results: list[str] = []
        if success:
            msg = f"✅ Конвертировано: {output_file_path.name}"
            logger.info(
                "Успешная смена контейнера: '%s'", output_file_path.name
            )
            if delete_original:
                self._delete_source(file_path, results)
        else:
            if self.is_cancelled:
                self._cleanup_if_cancelled(output_file_path)
                msg = f"⚠ Отменено: {output_file_path.name}"
                logger.info(
                    "Отмена смены контейнера: '%s'", output_file_path.name
                )
            else:
                msg = f"❌ ОШИБКА: {file_path.name}"
                logger.error(
                    "Ошибка при смене контейнера для файла: '%s'",
                    file_path.name,
                )

        results.insert(0, msg)
        return results
