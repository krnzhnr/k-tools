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
        return "Видео"

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
        output_path: str | None = None,
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
        logger.info(
            "Получены настройки скрипта: суффикс='%s', удалять исходник=%s",
            suffix, "ДА" if delete_original else "НЕТ"
        )
        results: list[str] = []
        total = len(files)

        logger.info(
            "Запуск процесса очистки метаданных для %d файл(ов)",
            total,
        )

        for idx, file_path in enumerate(files):
            if progress_callback:
                progress_callback(
                    idx, total, f"Очистка: {file_path.name}"
                )
            output_name = (
                f"{file_path.stem}{suffix}{file_path.suffix}"
            )
            target_dir = self._resolver.resolve(
                file_path, output_path
            )
            output_file_path = target_dir / output_name
            logger.info("Обработка файла [%d/%d]: '%s' -> '%s'", idx + 1, total, file_path.name, output_name)

            if output_file_path.exists() and not SettingsManager().overwrite_existing:
                msg = f"⏭ Пропущен (файл существует): {output_name}"
                logger.info("Пропуск файла '%s': выходной файл уже существует и перезапись отключена", file_path.name)
                results.append(msg)
                if progress_callback:
                    progress_callback(idx + 1, total, msg)
                continue
            else:
                logger.debug("Вызов FFmpeg для удаления метаданных")
                success = self._ffmpeg.run(
                    input_path=file_path,
                    output_path=output_file_path,
                    extra_args=[
                        "-map_metadata", "-1",
                        "-c:v", "copy",
                        "-c:a", "copy",
                    ],
                )

                if success:
                    msg = f"✅ Очищены метаданные: {output_name}"
                    logger.info("Успешно очищены метаданные для файла: '%s'", output_name)
                    if delete_original:
                        logger.info("Запрошено удаление исходного файла: '%s'", file_path.name)
                        self._delete_source(
                            file_path, results
                        )
                else:
                    msg = f"❌ Ошибка обработки: {file_path.name}"
                    logger.error("Не удалось очистить метаданные для файла: '%s'", file_path.name)

                results.append(msg)

            if progress_callback:
                progress_callback(
                    idx + 1,
                    total,
                    results[-1],
                )

        success_count = len([r for r in results if r.startswith("✅")])
        logger.info(
            "Весь процесс очистки метаданных завершен. Итог: %d успешно, %d всего",
            success_count,
            total,
        )

        return results
