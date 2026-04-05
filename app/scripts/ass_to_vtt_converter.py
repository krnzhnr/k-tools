# -*- coding: utf-8 -*-
"""Скрипт конвертации субтитров ASS/SSA в WebVTT."""

import logging
from pathlib import Path
from typing import Any

from app.core.abstract_script import (
    AbstractScript,
    SettingField,
    SettingType,
    ProgressCallback,
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
        return [".ass", ".ssa", ".srt"]

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
                comment="Полная очистка всех тегов",
                setting_type=SettingType.CHECKBOX,
                default=True,
            ),
            SettingField(
                key="keep_styles",
                label="Сохранять оформление стилей",
                comment="Переносить курсив/жирный из заголовков "
                "стилей ASS в VTT",
                setting_type=SettingType.CHECKBOX,
                default=False,
            ),
            SettingField(
                key="strip_caps",
                label="Удалять текст в верхнем регистре (КАПС)",
                comment="Автоматическое вырезание надписей, "
                "сделанных капсом (\"SIGN\\NDialogue\" -> \"Dialogue\")",
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
        file_path: Path,
        settings: dict[str, Any],
        output_path: str | None = None,
        progress_callback: ProgressCallback | None = None,
        current: int = 0,
        total: int = 1,
    ) -> list[str]:
        """Конвертировать один ASS-файл в VTT.

        Args:
            file_path: Путь к входному ASS-файлу.
            settings: Настройки скрипта.
            output_path: Опциональный путь сохранения.
            progress_callback: Callback для отслеживания прогресса.

        Returns:
            Список строк-результатов.
        """
        results: list[str] = []

        # Парсинг файла
        try:
            data = self._parser.parse(file_path)
        except Exception:
            logger.exception(
                "Ошибка парсинга ASS-файла '%s'",
                file_path.name,
            )
            results.append(f"❌ Ошибка парсинга: {file_path.name}")
            return results

        if not data.dialogues:
            msg = f"⏭ ПРОПУСК (нет строк диалогов): {file_path.name}"
            logger.info("[%s] %s", self.name, msg)
            results.append(msg)
            return results

        # Фильтрация по актёрам и стилям
        excluded: set[str] = set(settings.get("excluded_actors", []))
        excluded_styles = settings.get("excluded_styles", [])
        excluded_effects = settings.get("excluded_effects", [])
        manual_excl = settings.get("manual_exclusions", {})
        strip_fmt = settings.get("strip_formatting", False)
        strip_caps = settings.get("strip_caps", False)

        # Конвертируем индексы исключений для текущего файла
        # в set для быстрого поиска
        file_excl = set(manual_excl.get(str(file_path), []))

        dialogues = [
            d
            for i, d in enumerate(data.dialogues)
            if d.actor not in excluded
            and d.style not in excluded_styles
            and d.effect not in excluded_effects
            and i not in file_excl
            and self._parser.strip_tags(d.text).strip()
        ]
        # Сортировка по времени начала
        # (строковое сравнение работает для H:MM:SS.CC)
        dialogues.sort(key=lambda x: x.start)

        # Если пользователь хочет сохранить стили и не использует фильтры —
        # используем «быстрый путь» (прямую передачу исходника в FFmpeg).
        # Это позволяет FFmpeg увидеть оригинальную секцию [V4+ Styles].
        keep_styles = settings.get("keep_styles", False)
        if (
            keep_styles
            and not excluded
            and not excluded_styles
            and not excluded_effects
            and not strip_fmt
        ):
            return self._execute_ffmpeg_conversion(file_path, output_path)

        # В остальных случаях используем режим формирования временного файла
        # через minimal_header. Если keep_styles=False (по умолчанию), это
        # нейтрализует стилевое форматирование и дает чистый VTT.

        if not dialogues:
            msg = (
                f"⏭ ПРОПУСК (все строки исключены "
                f"фильтром): {file_path.name}"
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
        target_dir = self._resolver.resolve(file_path, output_path)
        output_file = self._get_safe_output_path(
            file_path,
            target_dir / file_path.with_suffix(".vtt").name,
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
                    # Применяем ручную чистку тегов и капса, если заказано
                    text = d.text
                    if strip_caps:
                        text = self._parser.strip_caps(text)
                    if strip_fmt:
                        text = self._parser.strip_tags(text)

                    # Проверяем, остался ли в строке хоть какой-то текст (без учета тегов).
                    # Если строка пуста (весь капс вырезан), пропускаем её.
                    if not self._parser.strip_tags(text).strip():
                        continue

                    # Создаем временный объект для сборки строки
                    tmp_d = AssDialogue(
                        start=d.start,
                        end=d.end,
                        style=d.style,
                        actor=d.actor,
                        effect=d.effect,
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
                    file_path.name,
                    output_file.name,
                )
                results.append(f"✅ УСПЕХ: {output_file.name}")
                if settings.get("delete_original", False):
                    self._delete_source(file_path, results)
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
        file_path: Path,
        output_path: str | None = None,
    ) -> list[str]:
        """Конвертировать ASS в VTT через FFmpeg напрямую.

        Позволяет сохранить оригинальное оформление стилей (курсив, жирность).

        Args:
            file_path: Путь к входному файлу.
            output_path: Путь сохранения.

        Returns:
            Список строк с результатами.
        """
        results: list[str] = []
        target_dir = self._resolver.resolve(file_path, output_path)
        output_file = self._get_safe_output_path(
            file_path,
            target_dir / file_path.with_suffix(".vtt").name,
        )
        overwrite = SettingsManager().overwrite_existing

        if output_file.exists() and not overwrite:
            msg = f"⏭ ПРОПУСК (существует): {output_file.name}"
            logger.info("[%s] %s", self.name, msg)
            results.append(msg)
            return results

        success = self._ffmpeg.run(
            input_path=file_path,
            output_path=output_file,
            overwrite=overwrite,
        )

        if success:
            logger.info(
                "[%s] Конвертация FFmpeg завершена: %s",
                self.name,
                output_file.name,
            )
            results.append(f"✅ УСПЕХ (Direct): {output_file.name}")
        else:
            results.append(f"❌ Ошибка FFmpeg: {output_file.name}")

        return results
