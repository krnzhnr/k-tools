# -*- coding: utf-8 -*-
"""Скрипт очистки метаданных медиафайлов."""

import logging
from pathlib import Path
from typing import Any

from app.core.abstract_script import (
    AbstractScript,
    ProgressCallback,
    SettingField,
    SettingType,
)
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
        logger.info("Скрипт очистки метаданных создан")

    @property
    def name(self) -> str:
        """Отображаемое имя скрипта."""
        return "Очистка метаданных"

    @property
    def description(self) -> str:
        """Описание скрипта."""
        return (
            "Удаляет все метаданные из видеофайлов, "
            "сохраняя оригинальное качество"
        )

    @property
    def icon_name(self) -> str:
        """Имя иконки FluentIcon."""
        return "BROOM"

    @property
    def file_extensions(self) -> list[str]:
        """Допустимые расширения файлов."""
        return [".mp4", ".mkv", ".mov", ".avi"]

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

    def execute(
        self,
        files: list[Path],
        settings: dict[str, Any],
        progress_callback: ProgressCallback | None = None,
    ) -> list[str]:
        """Очистить метаданные из списка файлов.

        Args:
            files: Список файлов для обработки.
            settings: Настройки скрипта (suffix).
            progress_callback: Callback прогресса.

        Returns:
            Список строк-результатов.
        """
        suffix = settings.get("suffix", "_cl")
        delete_original = settings.get(
            "delete_original", False
        )
        results: list[str] = []
        total = len(files)

        logger.info(
            "Начало очистки метаданных: %d файл(ов)",
            total,
        )

        for idx, file_path in enumerate(files):
            output_name = (
                f"{file_path.stem}{suffix}{file_path.suffix}"
            )
            output_path = file_path.parent / output_name

            if output_path.exists():
                msg = f"⏭ Пропущен (уже существует): {output_name}"
                results.append(msg)
                logger.info(msg)
            else:
                success = self._ffmpeg.run(
                    input_path=file_path,
                    output_path=output_path,
                    extra_args=[
                        "-map_metadata", "-1",
                        "-c:v", "copy",
                        "-c:a", "copy",
                    ],
                )

                if success:
                    msg = f"✅ Очищены метаданные: {output_name}"
                    if delete_original:
                        self._delete_source(
                            file_path, results
                        )
                else:
                    msg = f"❌ Ошибка обработки: {file_path.name}"

                results.append(msg)

            if progress_callback:
                progress_callback(
                    idx + 1,
                    total,
                    results[-1],
                )

        logger.info(
            "Очистка метаданных завершена: %d/%d",
            len([r for r in results if r.startswith("✅")]),
            total,
        )

        return results
