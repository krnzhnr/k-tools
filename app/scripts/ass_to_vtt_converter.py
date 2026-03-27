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
from app.infrastructure.ass_parser import AssParser

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
                default=True,
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
        excluded: set[str] = set(
            settings.get("excluded_actors", [])
        )
        excluded_styles: set[str] = set(
            settings.get("excluded_styles", [])
        )
        strip_fmt = settings.get("strip_formatting", True)

        dialogues = [
            d for d in data.dialogues
            if d.actor not in excluded
            and d.style not in excluded_styles
        ]

        if not dialogues:
            msg = (
                f"⏭ ПРОПУСК (все строки исключены "
                f"фильтром): {file.name}"
            )
            logger.info("[%s] %s", self.name, msg)
            results.append(msg)
            return results

        # Генерация VTT-контента
        vtt_content = self._generate_vtt(dialogues, strip_fmt)

        # Определяем выходной путь
        target_dir = self._resolver.resolve(file, output_path)
        output_file = self._get_safe_output_path(
            file,
            target_dir / file.with_suffix(".vtt").name,
        )
        overwrite = SettingsManager().overwrite_existing

        if output_file.exists() and not overwrite:
            msg = (
                f"⏭ ПРОПУСК (файл уже существует): "
                f"{output_file.name}"
            )
            logger.info("[%s] %s", self.name, msg)
            results.append(msg)
            return results

        # Запись VTT-файла
        try:
            output_file.write_text(
                vtt_content, encoding="utf-8",
            )
            logger.info(
                "[%s] Файл создан: %s (%d строк)",
                self.name,
                output_file.name,
                len(dialogues),
            )
            results.append(f"✅ УСПЕХ: {output_file.name}")

            if settings.get("delete_original", False):
                self._delete_source(file, results)
        except OSError:
            logger.exception(
                "Ошибка записи VTT-файла '%s'",
                output_file.name,
            )
            results.append(
                f"❌ Ошибка записи: {output_file.name}"
            )

        return results

    def _generate_vtt(
        self,
        dialogues: list,
        strip_formatting: bool,
    ) -> str:
        """Сгенерировать контент WebVTT из списка диалогов.

        Args:
            dialogues: Отфильтрованные строки диалогов.
            strip_formatting: Удалять ли теги форматирования.

        Returns:
            Строка с содержимым VTT-файла.
        """
        lines: list[str] = ["WEBVTT", ""]

        index = 0
        for dialogue in dialogues:
            start = self._parser.ass_time_to_vtt(dialogue.start)
            end = self._parser.ass_time_to_vtt(dialogue.end)

            text = dialogue.text
            if strip_formatting:
                text = self._parser.strip_tags(text)

            # Пропускаем строки с пустым текстом после очистки
            if not text.strip():
                continue

            index += 1
            lines.append(str(index))
            lines.append(f"{start} --> {end}")
            lines.append(text)
            lines.append("")

        return "\n".join(lines)
