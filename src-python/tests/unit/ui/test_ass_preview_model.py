# -*- coding: utf-8 -*-
import pytest
from pathlib import Path
from PyQt6.QtCore import Qt
from app.ui.ass_filter_widget import AssPreviewModel
from app.infrastructure.ass_parser import AssDialogue, AssData


@pytest.fixture
def model():
    """Фикстура для создания модели предпросмотра."""
    m = AssPreviewModel()

    # Подготовка тестовых данных
    path = Path("test.ass")
    dialogues = [
        AssDialogue(
            "0:00:01.00", "0:00:02.00", "Default", "Actor1", "", "Normal text"
        ),
        AssDialogue(
            "0:00:02.00",
            "0:00:03.00",
            "ExcludedStyle",
            "Actor1",
            "",
            "Filtered by style",
        ),
        AssDialogue(
            "0:00:03.00",
            "0:00:04.00",
            "Default",
            "ExcludedActor",
            "",
            "Filtered by actor",
        ),
        AssDialogue(
            "0:00:04.00",
            "0:00:05.00",
            "Default",
            "Actor1",
            "",
            "SHOUTING CAPS",
        ),
        AssDialogue(
            "0:00:05.00", "0:00:06.00", "Default", "Actor1", "", "   "
        ),  # Пустая
        AssDialogue(
            "0:00:06.00",
            "0:00:07.00",
            "Default",
            "Actor1",
            "",
            "Text with {\\tag} and <i>HTML</i>",
        ),
    ]
    file_data = {path: AssData(dialogues)}

    # Создаем визуальный кэш (имитация ParseWorker)
    visual_cache = {
        path: [
            {
                "clean": "Normal text",
                "is_changed": False,
                "is_empty": False,
                "is_originally_empty": False,
                "is_full_caps": False,
            },
            {
                "clean": "Filtered by style",
                "is_changed": False,
                "is_empty": False,
                "is_originally_empty": False,
                "is_full_caps": False,
            },
            {
                "clean": "Filtered by actor",
                "is_changed": False,
                "is_empty": False,
                "is_originally_empty": False,
                "is_full_caps": False,
            },
            {
                "clean": "",
                "is_changed": True,
                "is_empty": True,
                "is_originally_empty": False,
                "is_full_caps": True,
            },
            {
                "clean": "",
                "is_changed": False,
                "is_empty": True,
                "is_originally_empty": True,
                "is_full_caps": False,
            },
            {
                "clean": "Text with  and HTML",
                "is_changed": True,
                "is_empty": False,
                "is_originally_empty": False,
                "is_full_caps": False,
            },
        ]
    }

    m.update_data(file_data, visual_cache)
    return m, path


def test_initial_state(model):
    """Проверка начального состояния модели."""
    m, path = model
    assert m.rowCount() == 1  # Один файл
    file_idx = m.index(0, 0)
    assert m.rowCount(file_idx) == 6  # 6 строк в файле


def test_filtering_logic(model):
    """Проверка автоматической фильтрации."""
    m, path = model
    m.set_filters(
        excluded_actors={"ExcludedActor"},
        excluded_styles={"ExcludedStyle"},
        excluded_effects=set(),
        strip_formatting=True,
        strip_caps=True,
    )

    file_idx = m.index(0, 0)

    # Строка 0: OK
    idx0 = m.index(0, 0, file_idx)
    assert (
        m.data(idx0, Qt.ItemDataRole.CheckStateRole) == Qt.CheckState.Checked
    )
    # Проверяем статус (может быть 'ОК' или 'ИЗМЕНЕНО'
    # в зависимости от форматирования)
    status = m.data(idx0, Qt.ItemDataRole.DisplayRole)
    assert "УДАЛЕНО" not in status

    # Строка 1: Фильтр стиля
    idx1 = m.index(1, 0, file_idx)
    assert (
        m.data(idx1, Qt.ItemDataRole.CheckStateRole) == Qt.CheckState.Unchecked
    )
    assert "Фильтр" in m.data(idx1, Qt.ItemDataRole.DisplayRole)

    # Строка 2: Фильтр актера
    idx2 = m.index(2, 0, file_idx)
    assert (
        m.data(idx2, Qt.ItemDataRole.CheckStateRole) == Qt.CheckState.Unchecked
    )
    assert "Фильтр" in m.data(idx2, Qt.ItemDataRole.DisplayRole)

    # Строка 3: Фильтр КАПСА
    idx3 = m.index(3, 0, file_idx)
    assert (
        m.data(idx3, Qt.ItemDataRole.CheckStateRole) == Qt.CheckState.Unchecked
    )
    assert "CAPS" in m.data(idx3, Qt.ItemDataRole.DisplayRole)


def test_manual_inclusion_override(model):
    """Проверка ручного включения отфильтрованной строки."""
    m, path = model
    m.set_filters(
        excluded_actors={"ExcludedActor"},
        excluded_styles=set(),
        excluded_effects=set(),
        strip_formatting=False,
        strip_caps=False,
    )

    file_idx = m.index(0, 0)
    idx_filtered = m.index(2, 0, file_idx)  # ExcludedActor

    # Убеждаемся, что она выключена
    assert (
        m.data(idx_filtered, Qt.ItemDataRole.CheckStateRole)
        == Qt.CheckState.Unchecked
    )

    # Включаем вручную
    m.setData(
        idx_filtered, Qt.CheckState.Checked, Qt.ItemDataRole.CheckStateRole
    )

    assert (
        m.data(idx_filtered, Qt.ItemDataRole.CheckStateRole)
        == Qt.CheckState.Checked
    )
    assert "Вручную" in m.data(idx_filtered, Qt.ItemDataRole.DisplayRole)
    assert (
        m.data(idx_filtered, Qt.ItemDataRole.ForegroundRole).color().name()
        == "#faad14"
    )  # Желтый


def test_manual_exclusion_override(model):
    """Проверка ручного исключения нормальной строки."""
    m, path = model
    m.set_filters(set(), set(), set(), False, False)
    file_idx = m.index(0, 0)
    idx_ok = m.index(0, 0, file_idx)

    # Выключаем вручную
    m.setData(idx_ok, Qt.CheckState.Unchecked, Qt.ItemDataRole.CheckStateRole)

    assert (
        m.data(idx_ok, Qt.ItemDataRole.CheckStateRole)
        == Qt.CheckState.Unchecked
    )
    assert "Вручную" in m.data(idx_ok, Qt.ItemDataRole.DisplayRole)


def test_cannot_include_originally_empty(model):
    """Проверка того, что изначально пустую строку нельзя включить."""
    m, path = model
    m.set_filters(set(), set(), set(), False, False)
    file_idx = m.index(0, 0)
    idx_empty = m.index(4, 0, file_idx)  # Originally empty

    assert "пустая" in m.data(idx_empty, Qt.ItemDataRole.DisplayRole)

    # Пробуем включить
    m.setData(idx_empty, Qt.CheckState.Checked, Qt.ItemDataRole.CheckStateRole)

    # Она должна остаться выключенной
    assert (
        m.data(idx_empty, Qt.ItemDataRole.CheckStateRole)
        == Qt.CheckState.Unchecked
    )


def test_html_tag_highlighting(model):
    """Проверка подсветки HTML и ASS тегов."""
    m, path = model
    m.set_filters(set(), set(), set(), True, False)
    file_idx = m.index(0, 0)
    idx_tags = m.index(5, 3, file_idx)  # Текст с тегами

    html_res = m.data(idx_tags, Qt.ItemDataRole.DisplayRole)

    # Проверяем наличие зачеркнутых тегов обоих типов
    assert (
        "color: #ff3333" in html_res
    )  # Цвет удаления (или фиолетовый для очистки)
    # При strip_formatting=True в режиме очистки тегов (is_changed=True)
    # используется #ff3333 (красный) для зачеркивания.
    assert "text-decoration: line-through" in html_res
    assert "{\\tag}" in html_res
    assert "&lt;i&gt;" in html_res
    assert "&lt;/i&gt;" in html_res


def test_tooltip_for_manual_inclusion(model):
    """Проверка тултипа при ручном включении КАПСА."""
    m, path = model
    m.set_filters(set(), set(), set(), False, True)
    file_idx = m.index(0, 0)
    idx_caps_status = m.index(3, 0, file_idx)
    idx_caps_text = m.index(3, 3, file_idx)

    # Включаем вручную
    m.setData(
        idx_caps_status, Qt.CheckState.Checked, Qt.ItemDataRole.CheckStateRole
    )

    tooltip = m.data(idx_caps_text, Qt.ItemDataRole.ToolTipRole)
    assert "Результат VTT:" in tooltip
    assert "SHOUTING CAPS" in tooltip
