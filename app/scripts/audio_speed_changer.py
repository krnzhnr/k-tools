# -*- coding: utf-8 -*-
"""Скрипт изменения скорости аудио (FPS Converter) через eac3to."""

import logging
from pathlib import Path
from typing import Any

from app.core.abstract_script import AbstractScript, SettingField, SettingType
from app.infrastructure.eac3to_runner import Eac3toRunner

logger = logging.getLogger(__name__)


class AudioSpeedChangerScript(AbstractScript):
    """Скрипт для изменения скорости аудио (FPS)."""

    def __init__(self):
        """Инициализация скрипта."""
        self._runner = Eac3toRunner()

    @property
    def name(self) -> str:
        return "Изменение скорости аудио (eac3to)"

    @property
    def description(self) -> str:
        return "Изменяет скорость/тон аудио (PAL ↔ NTSC, Кино) с помощью eac3to."

    @property
    def icon_name(self) -> str:
        return "SPEED_HIGH"

    @property
    def file_extensions(self) -> list[str]:
        # eac3to поддерживает множество форматов, ограничимся основными
        return [".ac3", ".eac3", ".dts", ".wav", ".flac", ".aac", ".thd", ".mpa", ".mp3"]

    @property
    def settings_schema(self) -> list[SettingField]:
        return [
            SettingField(
                key="mode",
                label="Режим преобразования",
                setting_type=SettingType.COMBO,
                options=[
                    "Slowdown (25.000 → 23.976)",
                    "Speedup (23.976 → 25.000)",
                    "Custom (24.000 → 23.976)",
                    "Custom (25.000 → 24.000)",
                    # "Ручной ввод (Custom FPS)" # TODO: реализовать позже если нужно
                ],
                default="Slowdown (25.000 -> 23.976)",
            ),
            # Поля для ручного ввода пока скрыты/не реализованы в первой версии, 
            # так как eac3to имеет готовые флаги для большинства задач.
            # Если потребуется, добавим visible_if для режима "Ручной ввод".
            
            SettingField(
                key="output_format",
                label="Формат вывода",
                setting_type=SettingType.COMBO,
                options=["FLAC", "WAV"],
                default="FLAC",
            ),
            SettingField(
                key="delete_source",
                label="Удалить исходный файл",
                setting_type=SettingType.CHECKBOX,
                default=False,
            ),
        ]

    def execute(
        self,
        files: list[Path],
        settings: dict[str, Any],
        progress_callback=None,
    ) -> list[str]:
        """Выполнить изменение скорости."""
        total = len(files)
        completed = 0
        results = []

        mode = settings.get("mode")
        output_format = settings.get("output_format", "FLAC")
        # Приводим к нижнему регистру и добавляем точку (.flac, .wav)
        output_ext = f".{output_format.lower()}"
        
        delete_source = settings.get("delete_source", False)

        # Определение аргументов eac3to на основе режима
        eac3to_args = []
        suffix = ""

        if mode == "Slowdown (25.000 → 23.976)":
            eac3to_args.append("-slowdown")
            suffix = "_slowdown"
        elif mode == "Speedup (23.976 → 25.000)":
            eac3to_args.append("-speedup")
            suffix = "_speedup"
        elif mode == "Custom (24.000 → 23.976)":
            eac3to_args.append("-24.000")
            eac3to_args.append("-slowdown") 
            suffix = "_24_to_23"
        elif mode == "Custom (25.000 → 24.000)":
            eac3to_args.append("-25.000")
            eac3to_args.append("-changeTo24.000")
            suffix = "_25_to_24"

        logger.info(f"Начало обработки {total} файлов. Режим: {mode}")

        for i, file_path in enumerate(files):
            try:
                if not file_path.exists():
                    msg = f"❌ Файл не найден: {file_path.name}"
                    results.append(msg)
                    logger.error(msg)
                    continue

                if progress_callback:
                    progress_callback(completed, total, f"Обработка: {file_path.name}")

                # Формируем выходной путь
                output_dir = file_path.parent / "Slowed"
                output_dir.mkdir(exist_ok=True)
                
                # Используем выбранный формат
                output_path = output_dir / f"{file_path.stem}{suffix}{output_ext}"

                # Формируем аргументы для конкретного файла
                # eac3to "input" "output" options
                current_args = [str(file_path), str(output_path)] + eac3to_args

                success = self._runner.run(current_args, cwd=file_path.parent)

                if success:
                    completed += 1
                    msg = f"✅ Успешно: {file_path.name} -> {output_path.name}"
                    results.append(msg)
                    
                    if delete_source:
                        try:
                            file_path.unlink()
                            logger.info(f"Исходный файл удален: {file_path}")
                            results.append(f"🗑️ Исходник удален: {file_path.name}")
                        except Exception as e:
                            logger.error(f"Не удалось удалить исходник: {e}")
                            results.append(f"⚠️ Ошибка удаления: {file_path.name}")

                else:
                    msg = f"❌ Ошибка eac3to: {file_path.name}"
                    results.append(msg)

            except Exception as e:
                logger.exception(f"Критическая ошибка при обработке {file_path.name}: {e}")
                results.append(f"❌ Ошибка: {file_path.name} ({e})")

        return results
