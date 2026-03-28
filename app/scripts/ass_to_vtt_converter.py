# -*- coding: utf-8 -*-
"""Скрипт конвертации субтитров ASS/SSA в WebVTT."""

import logging
from pathlib import Path
from typing import Any

from app.core.abstract_script import (
    AbstractScript,
    SettingField,
    SettingType,
)
from app.core.constants import ScriptCategory, ScriptMetadata
from app.core.output_resolver import OutputResolver
from app.core.settings_manager import SettingsManager
from app.core.temp_file_manager import TempFileManager
from app.infrastructure.ass_parser import AssParser, AssDialogue
from app.infrastructure.ffmpeg_runner import FFmpegRunner

logger = logging.getLogger(__name__)


class AssToVttScript(AbstractScript):
    """Конвертация субтитров ASS/SSA в формат WebVTT.

    Поддерживает фильтрацию строк по актёрам (исключение
    выбранных) и удаление тегов форматирования ASS.
    """

    def __init__(self) -> None:
        """Инициализация конвертера ASS → VTT."""
        self._parser = AssParser()
        self._resolver = OutputResolver()
        self._ffmpeg = FFmpegRunner()
        logger.info("Скрипт конвертации ASS → VTT создан")

    @property
    def name(self) -> str:
        """Отображаемое имя скрипта."""
        return ScriptMetadata.ASS_TO_VTT_NAME

    @property
    def category(self) -> str:
        """Категория скрипта."""
        return ScriptCategory.SUBTITLES

    @property
    def description(self) -> str:
        """Описание скрипта."""
        return ScriptMetadata.ASS_TO_VTT_DESC

    @property
    def icon_name(self) -> str:
        """Имя иконки FluentIcon."""
        return "FONT"

    @property
    def file_extensions(self) -> list[str]:
        """Допустимые расширения входных файлов."""
        return [".ass", ".ssa"]

    @property
    def use_custom_widget(self) -> bool:
        """Использовать кастомный виджет с фильтрацией актёров."""
        return True

    @property
    def supports_parallel(self) -> bool:
        """Конвертация поддерживает параллелизм."""
        return True

    @property
    def settings_schema(self) -> list[SettingField]:
        """Схема настроек скрипта."""
        return [
            SettingField(
                key="strip_formatting",
                label="Удалять теги форматирования",
                setting_type=SettingType.CHECKBOX,
                default=False,
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
        file: Path,
        settings: dict[str, Any],
        output_path: str | None = None,
    ) -> list[str]:
        """Конвертировать один ASS-файл в VTT.

        Args:
            file: Путь к входному ASS-файлу.
            settings: Настройки скрипта.
            output_path: Опциональный путь сохранения.

        Returns:
            Список строк-результатов.
        """
        results: list[str] = []

        # Парсинг файла
        try:
            data = self._parser.parse(file)
        except Exception:
            logger.exception(
                "Ошибка парсинга ASS-файла '%s'",
                file.name,
            )
            results.append(f"❌ Ошибка парсинга: {file.name}")
            return results

        if not data.dialogues:
            msg = f"⏭ ПРОПУСК (нет строк диалогов): {file.name}"
            logger.info("[%s] %s", self.name, msg)
            results.append(msg)
            return results

        # Фильтрация по актёрам и стилям
        excluded: set[str] = set(settings.get("excluded_actors", []))
        excluded_styles = settings.get("excluded_styles", [])
        manual_excl = settings.get("manual_exclusions", {})
        strip_fmt = settings.get("strip_formatting", False)

        # Конвертируем индексы исключений для текущего файла
        # в set для быстрого поиска
        file_excl = set(manual_excl.get(str(file), []))

        dialogues = [
            d for i, d in enumerate(data.dialogues)
            if d.actor not in excluded
            and d.style not in excluded_styles
            and i not in file_excl
        ]
        # Сортировка по времени начала
        # (строковое сравнение работает для H:MM:SS.CC)
        dialogues.sort(key=lambda x: x.start)

        # Если фильтров нет и удаление тегов отключено в UI —
        # используем ffmpeg для «умной» очистки от мусора
        # (Оставляем старую логику прямой конвертации как оптимизацию,
        # если строк нет для фильтрации)
        if not excluded and not excluded_styles and not strip_fmt:
            return self._execute_ffmpeg_conversion(file, output_path)

        if not dialogues:
            msg = (
                f"⏭ ПРОПУСК (все строки исключены "
                f"фильтром): {file.name}"
            )
            logger.info("[%s] %s", self.name, msg)
            results.append(msg)
            return results

        # -------------------------------------------------
        # Многоступенчатая обработка:
        # 1. Формируем промежуточный ASS с учетом фильтров/чистки
        # 2. Пропускаем через FFmpeg для финального VTT
        # -------------------------------------------------

        # Определяем выходной путь
        target_dir = self._resolver.resolve(file, output_path)
        output_file = self._get_safe_output_path(
            file,
            target_dir / file.with_suffix(".vtt").name,
        )
        overwrite = SettingsManager().overwrite_existing

        if output_file.exists() and not overwrite:
            msg = f"⏭ ПРОПУСК (существует): {output_file.name}"
            results.append(msg)
            return results

        temp_ass = TempFileManager().create_temp_file(suffix=".ass")
        try:
            with open(temp_ass, "w", encoding="utf-8") as f:
                f.write(self._parser.get_minimal_header())
                for d in dialogues:
                    # Применяем ручную чистку тегов, если заказано
                    text = d.text
                    if strip_fmt:
                        text = self._parser.strip_tags(text)

                    # Создаем временный объект для сборки строки
                    tmp_d = AssDialogue(
                        start=d.start,
                        end=d.end,
                        style=d.style,
                        actor=d.actor,
                        text=text,
                    )
                    f.write(self._parser.to_ass_line(tmp_d) + "\n")

            # Финальная конвертация через FFmpeg
            success = self._ffmpeg.run(
                input_path=temp_ass,
                output_path=output_file,
                overwrite=overwrite,
            )

            if success:
                logger.info(
                    "[%s] Конвертация завершена: %s → %s",
                    self.name,
                    file.name,
                    output_file.name,
                )
                results.append(f"✅ УСПЕХ: {output_file.name}")
                if settings.get("delete_original", False):
                    self._delete_source(file, results)
            else:
                results.append(f"❌ Ошибка FFmpeg: {output_file.name}")

        except Exception as exc:
            logger.exception("Ошибка при многоступенчатой конвертации")
            results.append(f"❌ Критическая ошибка: {exc}")
        finally:
            TempFileManager().delete_path(temp_ass)

        return results

    def _execute_ffmpeg_conversion(
        self,
        file: Path,
        output_path: str | None = None,
    ) -> list[str]:
        """Конвертировать ASS в VTT через FFmpeg.

        Используется как fallback для «чистой» конвертации без фильтров.

        Args:
            file: Путь к входному файлу.
            output_path: Путь сохранения.

        Returns:
            Список строк с результатами.
        """
        results: list[str] = []
        target_dir = self._resolver.resolve(file, output_path)
        output_file = self._get_safe_output_path(
            file,
            target_dir / file.with_suffix(".vtt").name,
        )
        overwrite = SettingsManager().overwrite_existing

        if output_file.exists() and not overwrite:
            msg = f"⏭ ПРОПУСК (существует): {output_file.name}"
            logger.info("[%s] %s", self.name, msg)
            results.append(msg)
            return results

        success = self._ffmpeg.run(
            input_path=file,
            output_path=output_file,
            overwrite=overwrite,
        )

        if success:
            logger.info(
                "[%s] Конвертация FFmpeg завершена: %s",
                self.name,
                output_file.name,
            )
            results.append(f"✅ УСПЕХ (FFmpeg): {output_file.name}")
        else:
            results.append(f"❌ Ошибка FFmpeg: {output_file.name}")

        return results
