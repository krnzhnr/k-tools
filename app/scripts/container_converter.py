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
        logger.info(
            "Скрипт конвертации контейнера создан"
        )

    @property
    def name(self) -> str:
        """Отображаемое имя скрипта."""
        return "Конвертация контейнера"

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
        return [".mp4", ".mkv", ".mov", ".avi"]

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

    def execute(
        self,
        files: list[Path],
        settings: dict[str, Any],
        progress_callback: ProgressCallback | None = None,
    ) -> list[str]:
        """Конвертировать контейнер списка файлов.

        Args:
            files: Список файлов для обработки.
            settings: Настройки скрипта (target_format).
            progress_callback: Callback прогресса.

        Returns:
            Список строк-результатов.
        """
        target_key = settings.get("target_format", "MP4")
        target_ext = FORMAT_MAP.get(target_key, ".mp4")
        delete_original = settings.get(
            "delete_original", False
        )
        logger.info(
            "Настройки конвертации: целевой контейнер=%s, удалять исходник=%s",
            target_key, "ДА" if delete_original else "НЕТ"
        )
        results: list[str] = []
        total = len(files)

        logger.info(
            "Запуск конвертации контейнера для %d файл(ов) в формат %s",
            total,
            target_key,
        )

        for idx, file_path in enumerate(files):
            logger.info("Обработка файла [%d/%d]: '%s' (вход)", idx + 1, total, file_path.name)
            if file_path.suffix.lower() == target_ext:
                msg = (
                    f"⏭ Пропущен (уже {target_key}): "
                    f"{file_path.name}"
                )
                results.append(msg)
                logger.info("Файл '%s' уже имеет расширение %s, пропуск", file_path.name, target_ext)
            else:
                output_path = file_path.with_suffix(target_ext)
                logger.info("Формирование выходного пути: '%s'", output_path.name)

                if output_path.exists() and not SettingsManager().overwrite_existing:
                    msg = f"⏭ Пропущен (файл существует): {output_path.name}"
                    logger.info("Пропуск: выходной файл '%s' уже существует", output_path.name)
                    results.append(msg)
                    if progress_callback:
                        progress_callback(idx + 1, total, msg)
                    continue

                logger.debug("Старт FFmpeg для смены контейнера (copy)")
                success = self._ffmpeg.run(
                    input_path=file_path,
                    output_path=output_path,
                    extra_args=["-c", "copy"],
                )

                if success:
                    msg = f"✅ Конвертировано: {output_path.name}"
                    logger.info("Успешная смена контейнера: '%s'", output_path.name)
                    if delete_original:
                        logger.info("Удаление оригинала: '%s'", file_path.name)
                        self._delete_source(file_path, results)
                else:
                    msg = f"❌ Ошибка: {file_path.name}"
                    logger.error("Ошибка при смене контейнера для файла: '%s'", file_path.name)

                results.append(msg)

            if progress_callback:
                progress_callback(
                    idx + 1,
                    total,
                    results[-1],
                )

        logger.info(
            "Конвертация завершена: %d/%d",
            len([r for r in results if r.startswith("✅")]),
            total,
        )

        return results
