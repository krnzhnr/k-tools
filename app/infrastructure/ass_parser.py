# -*- coding: utf-8 -*-
"""Парсер файлов субтитров формата ASS/SSA и SRT."""

import logging
import re
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

from app.core.singleton import SingletonMeta

logger = logging.getLogger(__name__)


@dataclass(frozen=True)
class AssDialogue:
    """Одна строка диалога из файла субтитров.

    Attributes:
        start: Время начала в формате ASS (H:MM:SS.CC).
        end: Время окончания в формате ASS (H:MM:SS.CC).
        style: Имя стиля субтитра.
        actor: Имя актёра (поле Name/Actor).
        effect: Поле эффекта реплики.
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
    """Результат парсинга файла субтитров.

    Attributes:
        dialogues: Список всех строк диалогов.
    """

    dialogues: list[AssDialogue] = field(default_factory=list)


class AssParser(metaclass=SingletonMeta):
    """Парсер файлов субтитров в форматах ASS/SSA и SRT.

    Извлекает диалоги, актёров и деликатно удаляет теги форматирования.
    """

    # Регулярное выражение для удаления ASS-тегов форматирования.
    TAG_PATTERN = re.compile(r"\{[^}]*\}")
    # Регулярное выражение для поиска слов в верхнем регистре (от 2-х букв)
    CAPS_PATTERN = re.compile(r"\b[A-ZА-ЯЁ]{2,}\b")
    # Регулярное выражение для удаления HTML-подобных тегов SRT (<i>, <b>, etc.)
    HTML_TAG_PATTERN = re.compile(r"<[^>]*>")

    # Паттерн (префикс) для строки Dialogue в секции [Events]
    DIALOGUE_PREFIX = "Dialogue:"

    def __init__(self) -> None:
        """Инициализация парсера."""
        logger.info("Парсер субтитров инициализирован")

    def parse(self, path: Path) -> AssData:
        """Распарсить файл субтитров (ASS/SSA или SRT).

        Args:
            path: Путь к файлу.

        Returns:
            Объект AssData со списком диалогов.
        """
        ext = path.suffix.lower()
        if ext == ".srt":
            return self.parse_srt(path)
        return self.parse_ass(path)

    def parse_ass(self, path: Path) -> AssData:
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
            if stripped.startswith(self.DIALOGUE_PREFIX):
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

    def parse_srt(self, path: Path) -> AssData:
        """Распарсить SRT-файл и преобразовать в AssDialogue.

        Args:
            path: Путь к SRT-файлу.

        Returns:
            Объект AssData со списком диалогов.
        """
        data = AssData()
        logger.info("Парсинг SRT-файла: %s", path.name)

        try:
            content = path.read_text(encoding="utf-8-sig")
        except UnicodeDecodeError:
            try:
                content = path.read_text(encoding="cp1251")
            except Exception:
                logger.error("Не удалось прочитать SRT '%s'", path.name)
                return data

        # Разделяем на блоки по пустым строкам
        blocks = re.split(r"\n\s*\n", content.strip())
        for block in blocks:
            lines = block.strip().splitlines()
            if len(lines) < 2:
                continue

            # Поиск строки с таймкодами (обычно 2-я строка)
            time_line = ""
            text_lines = []
            for i, line in enumerate(lines):
                if "-->" in line:
                    time_line = line
                    text_lines = lines[i + 1 :]
                    break

            if not time_line:
                continue

            try:
                start_srt, end_srt = [
                    t.strip() for t in time_line.split("-->")
                ]
                start_ass = self.srt_time_to_ass(start_srt)
                end_ass = self.srt_time_to_ass(end_srt)
                # SRT может содержать HTML теги, сохраняем их для strip_tags
                text = "\\N".join(text_lines)

                data.dialogues.append(
                    AssDialogue(
                        start=start_ass,
                        end=end_ass,
                        style="Default",
                        actor="",
                        effect="",
                        text=text,
                    )
                )
            except Exception:
                logger.debug("Ошибка парсинга блока SRT: %s", time_line)
                continue

        logger.info(
            "Из '%s' извлечено %d реплик SRT",
            path.name,
            len(data.dialogues),
        )
        return data

    def _parse_dialogue_line(
        self,
        line: str,
        format_fields: list[str],
    ) -> AssDialogue | None:
        """Распарсить одну строку Dialogue из ASS."""
        after_prefix = line[len(self.DIALOGUE_PREFIX):].strip()

        if format_fields:
            num_fields = len(format_fields)
        else:
            num_fields = 10

        parts = after_prefix.split(",", num_fields - 1)

        if len(parts) < num_fields:
            return None

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
            style=field_map.get("style", "Default"),
            actor=(field_map.get("name") or field_map.get("actor") or ""),
            effect=field_map.get("effect", ""),
            text=field_map.get("text", ""),
        )

    def get_actors(self, path: Path) -> set[str]:
        """Извлечь множество уникальных актёров из файла."""
        data = self.parse(path)
        return {d.actor for d in data.dialogues if d.actor}

    def get_actors_from_files(self, paths: list[Path]) -> set[str]:
        """Извлечь уникальных актёров из нескольких файлов."""
        all_actors: set[str] = set()
        for path in paths:
            try:
                all_actors |= self.get_actors(path)
            except Exception:
                logger.debug("Ошибка извлечения актёров из %s", path.name)
        return all_actors

    def get_styles(self, path: Path) -> set[str]:
        """Извлечь множество уникальных стилей из файла."""
        data = self.parse(path)
        return {d.style for d in data.dialogues if d.style}

    def get_styles_from_files(self, paths: list[Path]) -> set[str]:
        """Извлечь уникальные стили из нескольких файлов."""
        all_styles: set[str] = set()
        for path in paths:
            try:
                all_styles |= self.get_styles(path)
            except Exception:
                logger.debug("Ошибка извлечения стилей из %s", path.name)
        return all_styles

    def get_effects(self, path: Path) -> set[str]:
        """Извлечь множество уникальных эффектов из файла."""
        data = self.parse(path)
        return {d.effect for d in data.dialogues if d.effect}

    def get_effects_from_files(self, paths: list[Path]) -> set[str]:
        """Извлечь уникальные эффекты из нескольких файлов."""
        all_effects: set[str] = set()
        for path in paths:
            try:
                all_effects |= self.get_effects(path)
            except Exception:
                logger.debug("Ошибка извлечения эффектов из %s", path.name)
        return all_effects

    def strip_tags(self, text: str) -> str:
        """Очистить текст субтитров от всех тегов (ASS и HTML)."""
        # Удаляем ASS override-блоки
        cleaned = self.TAG_PATTERN.sub("", text)
        # Удаляем HTML-теги SRT
        cleaned = self.HTML_TAG_PATTERN.sub("", cleaned)
        # Конвертируем переносы и спецсимволы
        cleaned = cleaned.replace("\\N", "\n").replace("\\n", "\n")
        cleaned = cleaned.replace("\\h", " ")
        return cleaned.strip()

    @staticmethod
    def ass_time_to_vtt(ass_time: str) -> str:
        """Конвертировать таймкод ASS (H:MM:SS.CC) в WebVTT (HH:MM:SS.mmm)."""
        try:
            h_str, m_str, s_rest = ass_time.split(":")
            s_str, cs_str = s_rest.split(".")

            total_seconds = (
                int(h_str) * 3600
                + int(m_str) * 60
                + int(s_str)
                + int(cs_str) / 100
            )

            ms = int(round((total_seconds % 1) * 1000))
            total_int = int(total_seconds)
            s = total_int % 60
            m = (total_int // 60) % 60
            h = total_int // 3600

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
        except Exception:
            return "00:00:00.000"

    @staticmethod
    def srt_time_to_ass(srt_time: str) -> str:
        """Конвертировать таймкод SRT (HH:MM:SS,mmm) в ASS (H:MM:SS.CC)."""
        try:
            # Превращаем запятую в точку для универсальности split
            clean_srt = srt_time.replace(",", ".")
            time_part, ms_part = clean_srt.split(".")
            h, m, s = [int(x) for x in time_part.split(":")]
            ms = int(ms_part)
            cs = ms // 10
            return f"{h}:{m:02d}:{s:02d}.{cs:02d}"
        except Exception:
            return "0:00:00.00"

    @staticmethod
    def to_ass_line(dialogue: Any) -> str:
        """Собрать объект диалога обратно в строку формата ASS диалога."""
        text = dialogue.text.replace("\n", "\\N")
        return (
            f"Dialogue: 0,{dialogue.start},{dialogue.end},"
            f"{dialogue.style},{dialogue.actor},0,0,0,"
            f"{dialogue.effect},{text}"
        )

    @staticmethod
    def get_minimal_header() -> str:
        """Получить минимальный заголовок ASS-файла."""
        return (
            "[Script Info]\n"
            "ScriptType: v4.00+\n\n"
            "[Events]\n"
            "Format: Layer, Start, End, Style, Name, "
            "MarginL, MarginR, MarginV, Effect, Text\n"
        )
