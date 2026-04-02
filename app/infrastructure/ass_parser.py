# -*- coding: utf-8 -*-
"""Парсер файлов субтитров формата ASS/SSA."""

import logging
import re
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

from app.core.singleton import SingletonMeta

logger = logging.getLogger(__name__)

# Регулярное выражение для удаления ASS-тегов форматирования.
# Удаляет override-блоки вида {\tag}, а также команды рисования.
_TAG_PATTERN = re.compile(r"\{[^}]*\}")

# Паттерн для строки Dialogue в секции [Events]
_DIALOGUE_PREFIX = "Dialogue:"


@dataclass(frozen=True)
class AssDialogue:
    """Одна строка диалога из ASS-файла.

    Attributes:
        start: Время начала в формате ASS (H:MM:SS.CC).
        end: Время окончания в формате ASS (H:MM:SS.CC).
        style: Имя стиля субтитра.
        actor: Имя актёра (поле Name/Actor).
        text: Текст реплики (с тегами или без).
    """

    start: str
    end: str
    style: str
    actor: str
    effect: str
    text: str


@dataclass
class AssData:
    """Результат парсинга ASS-файла.

    Attributes:
        dialogues: Список всех строк диалогов.
    """

    dialogues: list[AssDialogue] = field(default_factory=list)


class AssParser(metaclass=SingletonMeta):
    """Парсер файлов субтитров в формате ASS/SSA.

    Извлекает диалоги, актёров и деликатно удаляет теги.
    """

    def __init__(self) -> None:
        """Инициализация парсера ASS."""
        logger.info("Парсер ASS/SSA инициализирован")

    def parse(self, path: Path) -> AssData:
        """Распарсить ASS-файл и извлечь все строки диалогов.

        Args:
            path: Путь к ASS/SSA файлу.

        Returns:
            Объект AssData со списком диалогов.
        """
        data = AssData()
        in_events = False
        format_fields: list[str] = []

        logger.info("Парсинг ASS-файла: %s", path.name)

        try:
            content = path.read_text(encoding="utf-8-sig")
        except UnicodeDecodeError:
            logger.warning(
                "UTF-8 не подошёл для '%s', пробуем cp1251",
                path.name,
            )
            try:
                content = path.read_text(encoding="cp1251")
            except Exception:
                logger.exception(
                    "Не удалось прочитать файл '%s'",
                    path.name,
                )
                return data

        for line in content.splitlines():
            stripped = line.strip()

            # Определение начала секции [Events]
            if stripped.lower() == "[events]":
                in_events = True
                continue

            # Выход из секции [Events] при начале новой секции
            if stripped.startswith("[") and stripped.endswith("]"):
                if in_events:
                    break
                continue

            if not in_events:
                continue

            # Парсинг строки Format
            if stripped.startswith("Format:"):
                raw_fields = stripped[len("Format:"):].split(",")
                format_fields = [f.strip().lower() for f in raw_fields]
                continue

            # Парсинг строки Dialogue
            if stripped.startswith(_DIALOGUE_PREFIX):
                dialogue = self._parse_dialogue_line(
                    stripped,
                    format_fields,
                )
                if dialogue is not None:
                    data.dialogues.append(dialogue)

        logger.info(
            "Из '%s' извлечено %d строк диалогов",
            path.name,
            len(data.dialogues),
        )
        return data

    def _parse_dialogue_line(
        self,
        line: str,
        format_fields: list[str],
    ) -> AssDialogue | None:
        """Распарсить одну строку Dialogue.

        Формат по умолчанию (ASS v4+):
        Dialogue: Layer,Start,End,Style,Name,MarginL,MarginR,
                  MarginV,Effect,Text

        Args:
            line: Строка Dialogue из ASS-файла.
            format_fields: Список полей из строки Format.

        Returns:
            AssDialogue или None при ошибке парсинга.
        """
        # Отрезаем "Dialogue:" и разбиваем по запятым
        # Текст может содержать запятые, поэтому лимитируем split
        after_prefix = line[len(_DIALOGUE_PREFIX):].strip()

        if format_fields:
            num_fields = len(format_fields)
        else:
            # Стандарт ASS v4+: 10 полей
            num_fields = 10

        parts = after_prefix.split(",", num_fields - 1)

        if len(parts) < num_fields:
            logger.debug(
                "Пропуск строки Dialogue: недостаточно полей (%d < %d)",
                len(parts),
                num_fields,
            )
            return None

        # Маппинг полей по Format-строке или стандарту
        if format_fields:
            field_map = {
                name: parts[i].strip() for i, name in enumerate(format_fields)
            }
        else:
            field_map = {
                "start": parts[1].strip(),
                "end": parts[2].strip(),
                "style": parts[3].strip(),
                "name": parts[4].strip(),
                "text": parts[9].strip(),
            }

        return AssDialogue(
            start=field_map.get("start", "0:00:00.00"),
            end=field_map.get("end", "0:00:00.00"),
            style=field_map.get("style", ""),
            actor=(field_map.get("name") or field_map.get("actor") or ""),
            effect=field_map.get("effect", ""),
            text=field_map.get("text", ""),
        )

    def get_actors(self, path: Path) -> set[str]:
        """Извлечь множество уникальных актёров из ASS-файла.

        Args:
            path: Путь к ASS-файлу.

        Returns:
            Множество имён актёров (без пустых строк).
        """
        data = self.parse(path)
        actors = {d.actor for d in data.dialogues if d.actor}
        logger.info(
            "Из '%s' извлечено %d уникальных актёров",
            path.name,
            len(actors),
        )
        return actors

    def get_actors_from_files(
        self,
        paths: list[Path],
    ) -> set[str]:
        """Извлечь уникальных актёров из нескольких ASS-файлов.

        Args:
            paths: Список путей к ASS-файлам.

        Returns:
            Объединённое множество актёров без дублей.
        """
        all_actors: set[str] = set()
        for path in paths:
            try:
                all_actors |= self.get_actors(path)
            except Exception:
                logger.exception(
                    "Ошибка при извлечении актёров из '%s'",
                    path.name,
                )
        logger.info(
            "Всего уникальных актёров из %d файлов: %d",
            len(paths),
            len(all_actors),
        )
        return all_actors

    def get_styles(self, path: Path) -> set[str]:
        """Извлечь множество уникальных стилей из ASS-файла.

        Args:
            path: Путь к ASS-файлу.

        Returns:
            Множество имён стилей (поле Style в Dialogue).
        """
        data = self.parse(path)
        styles = {d.style for d in data.dialogues if d.style}
        logger.info(
            "Из '%s' извлечено %d уникальных стилей",
            path.name,
            len(styles),
        )
        return styles

    def get_styles_from_files(
        self,
        paths: list[Path],
    ) -> set[str]:
        """Извлечь уникальные стили из нескольких ASS-файлов.

        Args:
            paths: Список путей к ASS-файлам.

        Returns:
            Объединённое множество стилей без дублей.
        """
        all_styles: set[str] = set()
        for path in paths:
            try:
                all_styles |= self.get_styles(path)
            except Exception:
                logger.exception(
                    "Ошибка при извлечении стилей из '%s'",
                    path.name,
                )
        logger.info(
            "Всего уникальных стилей из %d файлов: %d",
            len(paths),
            len(all_styles),
        )
        return all_styles

    def get_effects(self, path: Path) -> set[str]:
        """Извлечь множество уникальных эффектов из ASS-файла.

        Args:
            path: Путь к ASS-файлу.

        Returns:
            Множество имён эффектов (поле Effect в Dialogue).
        """
        data = self.parse(path)
        effects = {d.effect for d in data.dialogues if d.effect}
        logger.info(
            "Из '%s' извлечено %d уникальных эффектов",
            path.name,
            len(effects),
        )
        return effects

    def get_effects_from_files(
        self,
        paths: list[Path],
    ) -> set[str]:
        """Извлечь уникальные эффекты из нескольких ASS-файлов.

        Args:
            paths: Список путей к ASS-файлам.

        Returns:
            Объединённое множество эффектов без дублей.
        """
        all_effects: set[str] = set()
        for path in paths:
            try:
                all_effects |= self.get_effects(path)
            except Exception:
                logger.exception(
                    "Ошибка при извлечении эффектов из '%s'",
                    path.name,
                )
        logger.info(
            "Всего уникальных эффектов из %d файлов: %d",
            len(paths),
            len(all_effects),
        )
        return all_effects

    @staticmethod
    def strip_tags(text: str) -> str:
        """Удалить теги форматирования ASS из текста.

        Удаляет override-блоки {\\...}, конвертирует
        \\N и \\n в переносы строк.

        Args:
            text: Исходный текст с тегами.

        Returns:
            Очищенный текст.
        """
        # Удаляем override-блоки
        cleaned = _TAG_PATTERN.sub("", text)
        # Конвертируем жёсткие и мягкие переносы ASS
        cleaned = cleaned.replace("\\N", "\n")
        cleaned = cleaned.replace("\\n", "\n")
        # Конвертируем неразрывные пробелы
        cleaned = cleaned.replace("\\h", " ")
        # Убираем лишние пробелы
        cleaned = cleaned.strip()
        return cleaned

    @staticmethod
    def ass_time_to_vtt(ass_time: str) -> str:
        """Конвертировать таймкод ASS в формат WebVTT.

        ASS:  H:MM:SS.CC  (сотые доли секунды)
        VTT:  HH:MM:SS.mmm  (миллисекунды)

        Args:
            ass_time: Таймкод в формате ASS.

        Returns:
            Таймкод в формате WebVTT.
        """
        try:
            # Математически точная конвертация через секунды
            # (защита от round-ошибок)
            h_str, m_str, s_rest = ass_time.split(":")
            s_str, cs_str = s_rest.split(".")

            # Считаем общее количество секунд (float)
            total_seconds = (
                int(h_str) * 3600
                + int(m_str) * 60
                + int(s_str)
                + int(cs_str) / 100
            )

            # Разложение обратно в VTT формат
            ms = int(round((total_seconds % 1) * 1000))
            total_int = int(total_seconds)
            s = total_int % 60
            m = (total_int // 60) % 60
            h = total_int // 3600

            # Коррекция переполнения при округлении (напр. 59.999 -> 60.000)
            if ms == 1000:
                ms = 0
                s += 1
                if s == 60:
                    s = 0
                    m += 1
                    if m == 60:
                        m = 0
                        h += 1

            return f"{h:02d}:{m:02d}:{s:02d}.{ms:03d}"
        except (ValueError, IndexError, ZeroDivisionError):
            logger.warning(
                "Некорректный таймкод ASS: '%s', " "используется нулевой",
                ass_time,
            )
            return "00:00:00.000"

    @staticmethod
    def to_ass_line(dialogue: Any) -> str:
        """Собрать объект диалога обратно в строку формата ASS.

        Использует стандартный порядок полей v4.00+.

        Args:
            dialogue: Объект диалога (AssDialogue).

        Returns:
            Строка Dialogue:...
        """
        # Текст может содержать переносы строк, заменяем их обратно на \N
        text = dialogue.text.replace("\n", "\\N")
        return (
            f"Dialogue: 0,{dialogue.start},{dialogue.end},"
            f"{dialogue.style},{dialogue.actor},0,0,0,"
            f"{dialogue.effect},{text}"
        )

    @staticmethod
    def get_minimal_header() -> str:
        """Получить минимальный заголовок ASS-файла для FFmpeg.

        Returns:
            Строка с заголовком и структурой [Events].
        """
        return (
            "[Script Info]\n"
            "ScriptType: v4.00+\n"
            "PlayResX: 1920\n"
            "PlayResY: 1080\n\n"
            "[Events]\n"
            "Format: Layer, Start, End, Style, Name, "
            "MarginL, MarginR, MarginV, Effect, Text\n"
        )
