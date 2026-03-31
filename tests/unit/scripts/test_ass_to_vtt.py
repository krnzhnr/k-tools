# -*- coding: utf-8 -*-
"""Тесты для парсера ASS и конвертера ASS → VTT."""

from textwrap import dedent

import pytest

from app.infrastructure.ass_parser import AssParser
from app.scripts.ass_to_vtt_converter import AssToVttScript

# --- Фикстуры ---


@pytest.fixture
def parser():
    """Фикстура парсера ASS."""
    return AssParser()


@pytest.fixture
def sample_ass_content():
    """Минимальный валидный ASS-файл."""
    return dedent("""\
        [Script Info]
        Title: Test
        ScriptType: v4.00+

        [V4+ Styles]
        Format: Name, Fontname, Fontsize
        Style: Default,Arial,20

        [Events]
        Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
        Dialogue: 0,0:00:01.00,0:00:03.50,Default,Alice,0,0,0,,Hello world
        Dialogue: 0,0:00:04.00,0:00:06.00,Default,Bob,0,0,0,,{\\b1}Goodbye{\\b0} world
        Dialogue: 0,0:00:07.00,0:00:09.00,Default,,0,0,0,,No actor line
        Dialogue: 0,0:00:10.00,0:00:12.00,Default,Alice,0,0,0,,Alice again
    """)  # noqa: E501


@pytest.fixture
def ass_file(tmp_path, sample_ass_content):
    """Создать временный ASS-файл."""
    path = tmp_path / "test.ass"
    path.write_text(sample_ass_content, encoding="utf-8")
    return path


# --- Тесты парсера ---


class TestAssParser:
    """Тесты для AssParser."""

    def test_parse_dialogues(self, parser, ass_file):
        """Корректное извлечение строк диалогов."""
        data = parser.parse(ass_file)
        assert len(data.dialogues) == 4

    def test_parse_dialogue_fields(self, parser, ass_file):
        """Корректность полей диалога."""
        data = parser.parse(ass_file)
        d = data.dialogues[0]
        assert d.start == "0:00:01.00"
        assert d.end == "0:00:03.50"
        assert d.style == "Default"
        assert d.actor == "Alice"
        assert d.text == "Hello world"

    def test_parse_empty_actor(self, parser, ass_file):
        """Строка без актёра парсится корректно."""
        data = parser.parse(ass_file)
        no_actor = data.dialogues[2]
        assert no_actor.actor == ""
        assert no_actor.text == "No actor line"

    def test_get_actors(self, parser, ass_file):
        """Извлечение уникальных актёров."""
        actors = parser.get_actors(ass_file)
        assert actors == {"Alice", "Bob"}

    def test_get_actors_from_files(self, parser, tmp_path):
        """Объединение актёров из нескольких файлов."""
        content1 = dedent("""\
            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            Dialogue: 0,0:00:01.00,0:00:02.00,Default,Alice,0,0,0,,Hi
        """)  # noqa: E501
        content2 = dedent("""\
            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            Dialogue: 0,0:00:01.00,0:00:02.00,Default,Bob,0,0,0,,Hi
            Dialogue: 0,0:00:03.00,0:00:04.00,Default,Alice,0,0,0,,Again
        """)  # noqa: E501

        f1 = tmp_path / "a.ass"
        f2 = tmp_path / "b.ass"
        f1.write_text(content1, encoding="utf-8")
        f2.write_text(content2, encoding="utf-8")

        actors = parser.get_actors_from_files([f1, f2])
        assert actors == {"Alice", "Bob"}

    def test_parse_empty_file(self, parser, tmp_path):
        """Парсинг пустого файла."""
        empty = tmp_path / "empty.ass"
        empty.write_text("", encoding="utf-8")
        data = parser.parse(empty)
        assert len(data.dialogues) == 0

    def test_parse_actor_field_name(self, parser, tmp_path):
        """Парсинг файла с полем Actor вместо Name."""
        content = dedent("""\
            [Events]
            Format: Layer, Start, End, Style, Actor, MarginL, MarginR, MarginV, Effect, Text
            Dialogue: 0,0:00:05.15,0:00:17.88,Default,Наоя Дзэнъин,0,0,0,,Ну и? Сдохла?
            Dialogue: 0,0:05:20.94,0:05:23.32,Default,Мать Маки,0,0,0,,Важно не это
        """)  # noqa: E501
        f = tmp_path / "actor_field.ass"
        f.write_text(content, encoding="utf-8")

        actors = parser.get_actors(f)
        assert actors == {"Наоя Дзэнъин", "Мать Маки"}


class TestStripTags:
    """Тесты для strip_tags."""

    def test_remove_bold_tags(self):
        """Удаление тегов жирного текста."""
        result = AssParser.strip_tags("{\\b1}Hello{\\b0}")
        assert result == "Hello"

    def test_remove_position_tag(self):
        """Удаление тега позиционирования."""
        result = AssParser.strip_tags("{\\an8}{\\pos(320,50)}Text")
        assert result == "Text"

    def test_convert_newlines(self):
        """Конвертация \\N и \\n в переносы строк."""
        result = AssParser.strip_tags("Line1\\NLine2\\nLine3")
        assert result == "Line1\nLine2\nLine3"

    def test_convert_hard_spaces(self):
        """Конвертация \\h в пробелы."""
        result = AssParser.strip_tags("Word1\\hWord2")
        assert result == "Word1 Word2"

    def test_combined_tags_and_newlines(self):
        """Комбинация тегов и переносов."""
        result = AssParser.strip_tags("{\\an8}{\\b1}Hello\\NWorld{\\b0}")
        assert result == "Hello\nWorld"

    def test_no_tags(self):
        """Текст без тегов остаётся без изменений."""
        result = AssParser.strip_tags("Plain text")
        assert result == "Plain text"

    def test_empty_string(self):
        """Пустая строка."""
        result = AssParser.strip_tags("")
        assert result == ""

    def test_complex_override(self):
        """Сложный override-блок с несколькими тегами."""
        result = AssParser.strip_tags(
            "{\\fad(300,200)\\blur3\\c&HFFFFFF&}Text"
        )
        assert result == "Text"


class TestAssTimeToVtt:
    """Тесты конвертации таймкодов."""

    def test_standard_time(self):
        """Стандартный ASS таймкод."""
        result = AssParser.ass_time_to_vtt("1:23:45.67")
        assert result == "01:23:45.670"

    def test_zero_time(self):
        """Нулевой таймкод."""
        result = AssParser.ass_time_to_vtt("0:00:00.00")
        assert result == "00:00:00.000"

    def test_single_digit_centiseconds(self):
        """Сотые доли с одной цифрой (ASS допускает)."""
        result = AssParser.ass_time_to_vtt("0:00:01.5")
        assert result == "00:00:01.050"

    def test_invalid_time(self):
        """Некорректный формат таймкода."""
        result = AssParser.ass_time_to_vtt("invalid")
        assert result == "00:00:00.000"


# --- Тесты конвертера ---


class TestAssToVttScript:
    """Тесты для скрипта конвертации."""

    def test_script_properties(self):
        """Проверка свойств скрипта."""
        script = AssToVttScript()
        assert script.name == "ASS → VTT"
        assert script.category == "Субтитры"
        assert ".ass" in script.file_extensions
        assert ".ssa" in script.file_extensions
        assert script.use_custom_widget is True

    def test_execute_single_basic(self, ass_file, tmp_path):
        """Базовая конвертация без фильтрации."""
        script = AssToVttScript()
        settings = {
            "strip_formatting": True,
            "delete_original": False,
            "excluded_actors": [],
        }

        results = script.execute_single(
            ass_file,
            settings,
            str(tmp_path),
        )

        assert any("✅" in r for r in results)
        vtt_file = tmp_path / "test.vtt"
        assert vtt_file.exists()

        content = vtt_file.read_text(encoding="utf-8")
        assert content.startswith("WEBVTT")
        assert "-->" in content

    def test_execute_with_actor_filter(
        self,
        ass_file,
        tmp_path,
    ):
        """Конвертация с исключением актёра."""
        script = AssToVttScript()
        settings = {
            "strip_formatting": True,
            "delete_original": False,
            "excluded_actors": ["Bob"],
        }

        results = script.execute_single(
            ass_file,
            settings,
            str(tmp_path),
        )

        assert any("✅" in r for r in results)
        vtt = tmp_path / "test.vtt"
        content = vtt.read_text(encoding="utf-8")

        # Bob's line removed, but has stripped text
        assert "Goodbye" not in content
        assert "Hello world" in content
        assert "Alice again" in content

    def test_execute_strips_formatting(
        self,
        ass_file,
        tmp_path,
    ):
        """Проверка удаления тегов форматирования."""
        script = AssToVttScript()
        settings = {
            "strip_formatting": True,
            "delete_original": False,
            "excluded_actors": [],
        }

        script.execute_single(
            ass_file,
            settings,
            str(tmp_path),
        )

        vtt = tmp_path / "test.vtt"
        content = vtt.read_text(encoding="utf-8")
        # Теги {\b1} и {\b0} удалены
        assert "{\\b1}" not in content
        assert "Goodbye world" in content

    def test_execute_all_excluded(self, tmp_path):
        """Все строки исключены фильтром → пропуск."""
        content = dedent("""\
            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            Dialogue: 0,0:00:01.00,0:00:02.00,Default,Alice,0,0,0,,Hi
        """)  # noqa: E501
        ass_file = tmp_path / "all_excluded.ass"
        ass_file.write_text(content, encoding="utf-8")

        script = AssToVttScript()
        settings = {
            "strip_formatting": True,
            "delete_original": False,
            "excluded_actors": ["Alice"],
        }

        results = script.execute_single(
            ass_file,
            settings,
            str(tmp_path),
        )
        assert any("ПРОПУСК" in r for r in results)

    def test_execute_with_style_filter(self, tmp_path):
        """Конвертация с исключением стиля."""
        content = dedent("""\
            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            Dialogue: 0,0:00:01.00,0:00:02.00,Default,Alice,0,0,0,,Keep this
            Dialogue: 0,0:00:03.00,0:00:04.00,Signs,Bob,0,0,0,,Remove this
            Dialogue: 0,0:00:05.00,0:00:06.00,Default,,0,0,0,,Keep too
        """)  # noqa: E501
        ass_file = tmp_path / "style_filter.ass"
        ass_file.write_text(content, encoding="utf-8")

        script = AssToVttScript()
        settings = {
            "strip_formatting": True,
            "delete_original": False,
            "excluded_actors": [],
            "excluded_styles": ["Signs"],
        }

        results = script.execute_single(
            ass_file,
            settings,
            str(tmp_path),
        )

        assert any("✅" in r for r in results)
        vtt = tmp_path / "style_filter.vtt"
        content_vtt = vtt.read_text(encoding="utf-8")
        assert "Keep this" in content_vtt
        assert "Remove this" not in content_vtt
        assert "Keep too" in content_vtt

    def test_execute_empty_file(self, tmp_path):
        """Пустой файл → пропуск."""
        empty = tmp_path / "empty.ass"
        empty.write_text("[Events]\n", encoding="utf-8")

        script = AssToVttScript()
        settings = {
            "strip_formatting": True,
            "delete_original": False,
            "excluded_actors": [],
        }

        results = script.execute_single(
            empty,
            settings,
            str(tmp_path),
        )
        assert any("ПРОПУСК" in r for r in results)

    def test_vtt_timecodes(self, ass_file, tmp_path):
        """Проверка формата таймкодов в VTT."""
        script = AssToVttScript()
        settings = {
            "strip_formatting": True,
            "delete_original": False,
            "excluded_actors": [],
        }

        script.execute_single(
            ass_file,
            settings,
            str(tmp_path),
        )

        vtt = tmp_path / "test.vtt"
        content = vtt.read_text(encoding="utf-8")

        # Первая строка: 0:00:01.00 → 00:01.000 (FFmpeg опускает часы)
        assert "00:01.000 --> 00:03.500" in content
