# -*- coding: utf-8 -*-
"""Скрипт изменения скорости аудио (FPS Converter) через eac3to."""

import logging
from pathlib import Path
from typing import Any

from app.core.abstract_script import AbstractScript, SettingField, SettingType
from app.core.output_resolver import OutputResolver
from app.infrastructure.eac3to_runner import Eac3toRunner

logger = logging.getLogger(__name__)


class AudioSpeedChangerScript(AbstractScript):
    """Скрипт для изменения скорости аудио (FPS)."""

    def __init__(self):
        """Инициализация скрипта."""
        self._runner = Eac3toRunner()
        self._resolver = OutputResolver()

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
        output_path: str | None = None,
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

        logger.info(
            "Настройки изменения скорости: режим='%s', формат вывода=%s, удалять исходник=%s",
            mode, output_format, "ДА" if delete_source else "НЕТ"
        )

        completed = 0
        results = []

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

        logger.info("Запуск процесса изменения скорости для %d файлов в режиме %s", total, mode)

        for i, file_path in enumerate(files):
            try:
                logger.info("Обработка файла [%d/%d]: '%s' (вход)", i + 1, total, file_path.name)
                if not file_path.exists():
                    msg = f"❌ Файл не найден: {file_path.name}"
                    results.append(msg)
                    logger.error("Ошибка: файл не найден по пути '%s'", file_path)
                    continue

                if progress_callback:
                    progress_callback(i, total, f"Обработка: {file_path.name}")

                # Формируем выходной путь через резолвер
                target_dir = self._resolver.resolve(
                    file_path, output_path
                )
                
                # Используем выбранный формат и суффикс
                output_file_path = target_dir / f"{file_path.stem}{suffix}{output_ext}"
                logger.debug("Назначен путь вывода: '%s'", output_file_path.name)

                # Проверка на существование файла
                from app.core.settings_manager import SettingsManager
                if output_file_path.exists() and not SettingsManager().overwrite_existing:
                    msg = f"⏭ Пропущен (файл существует): {output_file_path.name}"
                    logger.info("Пропуск: файл '%s' уже существует", output_file_path.name)
                    results.append(msg)
                    completed += 1
                    continue

                # Формируем аргументы для конкретного файла
                current_args = [str(file_path), str(output_file_path)] + eac3to_args

                logger.debug("Вызов раннера eac3to")
                success = self._runner.run(current_args, cwd=file_path.parent)

                if success:
                    completed += 1
                    msg = f"✅ Успешно: {file_path.name} -> {output_file_path.name}"
                    logger.info("Успешно завершено преобразование для: '%s'", output_file_path.name)
                    results.append(msg)
                    
                    if delete_source:
                        try:
                            logger.info("Удаление исходного файла по запросу: '%s'", file_path.name)
                            file_path.unlink()
                            results.append(f"🗑️ Исходник удален: {file_path.name}")
                        except Exception as e:
                            logger.error("Не удалось удалить исходник '%s': %s", file_path.name, e)
                            results.append(f"⚠️ Ошибка удаления: {file_path.name}")

                else:
                    msg = f"❌ Ошибка eac3to: {file_path.name}"
                    logger.error("Ошибка eac3to при обработке файла: '%s'", file_path.name)
                    results.append(msg)

            except Exception as e:
                logger.exception("Критическая ошибка при обработке файла '%s': %s", file_path.name, e)
                results.append(f"❌ Ошибка: {file_path.name} ({e})")

        logger.info("Изменение скорости завершено. Итог: %d успешно из %d", completed, total)

        return results
