# -*- coding: utf-8 -*-
"""Скрипт очистки метаданных медиафайлов."""

from app.core.constants import MEDIA_CONTAINERS, ScriptCategory, ScriptMetadata
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


class MetadataCleanerScript(AbstractScript):
    """Очистка метаданных видеофайлов через FFmpeg.

    Аналог cleaner.bat — удаляет все метаданные,
    копируя аудио и видео потоки без перекодирования.
    """

    def __init__(self) -> None:
        """Инициализация скрипта очистки метаданных."""
        self._ffmpeg = FFmpegRunner()
        self._resolver = OutputResolver()
        logger.info("Скрипт очистки метаданных создан")

    @property
    def category(self) -> str:
        """Категория скрипта."""
        return ScriptCategory.VIDEO

    @property
    def name(self) -> str:
        """Отображаемое имя скрипта."""
        return ScriptMetadata.METADATA_CLEAN_NAME

    @property
    def description(self) -> str:
        """Описание скрипта."""
        return ScriptMetadata.METADATA_CLEAN_DESC

    @property
    def icon_name(self) -> str:
        """Имя иконки FluentIcon."""
        return "BROOM"

    @property
    def file_extensions(self) -> list[str]:
        """Допустимые расширения файлов."""
        return list(MEDIA_CONTAINERS)

    @property
    def settings_schema(self) -> list[SettingField]:
        """Схема настроек скрипта."""
        return [
            SettingField(
                key="suffix",
                label="Суффикс выходного файла",
                setting_type=SettingType.TEXT,
                default="_cl",
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
        """Очистить метаданные одного файла."""
        output_name = f"{file_path.stem}{settings.get('suffix', '_cl')}{file_path.suffix}"  # noqa: E501
        target_dir = self._resolver.resolve(file_path, output_path)
        output_file_path = self._get_safe_output_path(
            file_path, target_dir / output_name
        )
        overwrite = SettingsManager().overwrite_existing

        if output_file_path.exists() and not overwrite:
            logger.info("Пропуск '%s': файл существует", file_path.name)
            return [f"⏭ ПРОПУСК (файл существует): {output_file_path.name}"]

        return self._run_cleaning(
            file_path,
            output_file_path,
            settings.get("delete_original", False),
            overwrite,
        )

    def _run_cleaning(
        self,
        file_path: Path,
        output_file_path: Path,
        delete_original: bool,
        overwrite: bool,
    ) -> list[str]:
        """Запуск FFmpeg для очистки метаданных."""
        logger.debug("Вызов FFmpeg для удаления метаданных")
        success = self._ffmpeg.run(
            input_path=file_path,
            output_path=output_file_path,
            extra_args=["-map_metadata", "-1", "-c:v", "copy", "-c:a", "copy"],
            overwrite=overwrite,
        )

        results: list[str] = []
        if success:
            msg = f"✅ Очищены метаданные: {output_file_path.name}"
            logger.info(
                "Успешно очищены метаданные для файла: '%s'",
                output_file_path.name,
            )
            if delete_original:
                self._delete_source(file_path, results)
        else:
            if self.is_cancelled:
                self._cleanup_if_cancelled(output_file_path)
                msg = f"⚠ Отменено: {output_file_path.name}"
                logger.info(
                    "Отмена очистки метаданных: '%s'", output_file_path.name
                )
            else:
                msg = f"❌ ОШИБКА обработки: {file_path.name}"
                logger.error(
                    "Не удалось очистить метаданные для файла: '%s'",
                    file_path.name,
                )

        results.insert(0, msg)
        return results
