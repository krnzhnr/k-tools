# -*- coding: utf-8 -*-
"""Скрипт для разделения аудио на отдельные потоки (моно-файлы) через eac3to."""

import logging
import os
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
from app.infrastructure.eac3to_runner import Eac3toRunner
from app.infrastructure.ffmpeg_runner import FFmpegRunner

logger = logging.getLogger(__name__)


class AudioSplitterScript(AbstractScript):
    """Разделение аудио на отдельные потоки через eac3to.

    Позволяет разбить многоканальный аудиофайл на отдельные
    моно-файлы WAV и опционально склеить их в стереопары.
    """

    def __init__(self) -> None:
        """Инициализация скрипта разделения аудио."""
        self._runner = Eac3toRunner()
        self._ffmpeg = FFmpegRunner()
        self._resolver = OutputResolver()
        logger.info("Скрипт AudioSplitterScript инициализирован")

    @property
    def category(self) -> str:
        """Категория скрипта."""
        return "Аудио"

    @property
    def name(self) -> str:
        """Отображаемое имя скрипта."""
        return "Разделение аудио (eac3to)"

    @property
    def description(self) -> str:
        """Описание скрипта."""
        return (
            "Разбивает многоканальный аудиофайл на отдельные "
            "моно-потоки (WAV) с опциональной склейкой в стереопары"
        )

    @property
    def icon_name(self) -> str:
        """Имя иконки FluentIcon."""
        return "SHARE"

    @property
    def file_extensions(self) -> list[str]:
        """Допустимые расширения файлов."""
        return [
            ".wav", ".w64", ".rf64", ".pcm", ".mkv", ".mp4", 
            ".m4a", ".ac3", ".dts", ".eac3", ".dtshd", ".thd", 
            ".truehd", ".flac", ".aac"
        ]

    @property
    def settings_schema(self) -> list[SettingField]:
        """Схема настроек скрипта."""
        return [
            SettingField(
                key="merge_stereo",
                label="Склеивать каналы (только для 5.1 / 7.1: L+R → LR, SL+SR → SLSR, BL+BR → BLBR)",
                setting_type=SettingType.CHECKBOX,
                default=True,
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
        """Выполнить разделение и склейку аудиофайлов.

        Args:
            files: Список файлов для обработки.
            settings: Настройки.
            output_path: Опциональный путь сохранения.
            progress_callback: Callback прогресса.

        Returns:
            Список строк-результатов.
        """
        delete_original = settings.get("delete_original", False)
        merge_stereo = settings.get("merge_stereo", True)
        results: list[str] = []
        total = len(files)

        logger.info(
            "Запущено разделение аудио для %d файл(ов), склейка стерео: %s",
            total, "ВКЛ" if merge_stereo else "ВЫКЛ"
        )

        for idx, file_path in enumerate(files):
            if progress_callback:
                progress_callback(
                    idx, total, f"Обработка: {file_path.name}"
                )
            
            logger.info(
                "Обработка файла [%d/%d]: '%s'",
                idx + 1, total, file_path.name
            )

            target_dir = self._resolver.resolve(file_path, output_path)
            # eac3to требует ".wavs" для автоматического именования моно-каналов
            # он создаст файлы вида "{stem}.L.wav", "{stem}.R.wav" и т.д.
            output_pattern = target_dir / (file_path.stem + ".wavs")

            logger.debug("Целевой шаблон eac3to: '%s'", output_pattern)

            # Команда eac3to "input" "output.wavs"
            args = [str(file_path), str(output_pattern)]
            
            # eac3to может жаловаться на существующие файлы, поэтому лучше 
            # дать ему -overwrite если такая опция есть, но runner просто 
            # передает список аргументов.
            
            success = self._runner.run(args)

            if success:
                msg = f"✅ Разделено: {file_path.name}"
                logger.info("Успешное разделение: '%s'", file_path.name)
                
                if merge_stereo:
                    self._perform_stereo_merge(file_path.stem, target_dir, results)

                if delete_original:
                    logger.info("Задание удаления исходника: '%s'", file_path.name)
                    self._delete_source(file_path, results)
            else:
                msg = f"❌ Ошибка eac3to: {file_path.name}"
                logger.error("Ошибка eac3to для файла: '%s'", file_path.name)

            results.append(msg)

            if progress_callback:
                progress_callback(idx + 1, total, results[-1])

        return results

    def _perform_stereo_merge(self, stem: str, target_dir: Path, results: list[str]) -> None:
        """Поиск моно-файлов и их склейка в стереопары.

        Args:
            stem: Базовое имя файла (без канала).
            target_dir: Директория с моно-файлами.
            results: Список результатов для логирования.
        """
        # Сначала проверяем количество созданных моно-файлов.
        # Если их 2 или меньше, то это стерео или моно — склейка не нужна.
        mono_files = list(target_dir.glob(f"{stem}.*.wav"))
        if len(mono_files) <= 2:
            logger.info(
                "Файл '%s' имеет %d канала(ов). Склейка не требуется.",
                stem, len(mono_files)
            )
            return

        logger.info("Начало поиска моно-каналов для склейки стерео в '%s'", target_dir)
        
        # Ожидаемые пары: (Левый, Правый, Имя_Выхода)
        pairs = [
            ("L", "R", "LR"),
            ("SL", "SR", "SLSR"),
            ("BL", "BR", "BLBR"), # На всякий случай для 7.1
        ]

        for left_sfx, right_sfx, out_sfx in pairs:
            # Формируем пути
            left_path = target_dir / f"{stem}.{left_sfx}.wav"
            right_path = target_dir / f"{stem}.{right_sfx}.wav"
            output_path = target_dir / f"{stem}.{out_sfx}.wav"

            if left_path.exists() and right_path.exists():
                logger.info("Найдена пара каналов для склейки: %s + %s", left_sfx, right_sfx)
                
                # Команда для FFmpeg: объединение в стерео
                # FFmpegRunner добавит -i для left_path автоматически
                extra_args = [
                    "-i", str(right_path),
                    "-filter_complex", "join=inputs=2:channel_layout=stereo",
                    "-c:a", "pcm_s24le",
                ]

                # FFmpegRunner.run(input_path, output_path, extra_args)
                merge_success = self._ffmpeg.run(
                    input_path=left_path,
                    output_path=output_path,
                    extra_args=extra_args
                )

                if merge_success:
                    results.append(f"🔗 Склеено стерео: {output_path.name}")
                    logger.info("Успешная склейка стерео '%s'", output_path.name)
                    
                    # Удаление моно-файлов после успешной склейки
                    try:
                        left_path.unlink()
                        right_path.unlink()
                        logger.debug("Моно-файлы %s и %s удалены", left_sfx, right_sfx)
                    except Exception as e:
                        logger.error("Не удалось удалить моно-файлы после склейки: %s", e)
                else:
                    logger.error("Ошибка FFmpeg при склейке пары %s+%s", left_sfx, right_sfx)
                    results.append(f"⚠ Ошибка склейки стерео: {stem}.{out_sfx}.wav")
            else:
                logger.debug("Пара %s+%s не найдена, пропуск", left_sfx, right_sfx)
