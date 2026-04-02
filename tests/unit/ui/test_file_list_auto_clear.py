# -*- coding: utf-8 -*-
from pathlib import Path
from PyQt6.QtCore import Qt, QMimeData, QUrl, QPointF
from PyQt6.QtGui import QDropEvent
from app.ui.file_list_widget import FileListWidget


def test_file_list_clear_on_add_disabled(qtbot, mocker):
    """Тест: список НЕ очищается, если опция выключена."""
    mocker.patch("pathlib.Path.is_file", return_value=True)
    # Мокаем настройки
    mock_settings = mocker.patch(
        "app.ui.file_list_widget.SettingsManager"
    ).return_value
    mock_settings.clear_list_on_add = False

    widget = FileListWidget()
    qtbot.addWidget(widget)

    # 1. Добавляем первый файл
    widget.add_files(["file1.ass"])
    assert widget.count() == 1

    # 2. Добавляем второй файл
    widget.add_files(["file2.ass"])
    # Должно быть 2 файла (накопительный итог)
    assert widget.count() == 2
    assert len(widget.files) == 2


def test_file_list_clear_on_add_enabled(qtbot, mocker):
    """Тест: список очищается перед добавлением, если опция включена."""
    mocker.patch("pathlib.Path.is_file", return_value=True)
    # Мокаем настройки
    mock_settings = mocker.patch(
        "app.ui.file_list_widget.SettingsManager"
    ).return_value
    mock_settings.clear_list_on_add = True

    widget = FileListWidget()
    qtbot.addWidget(widget)

    # 1. Добавляем первый файл
    widget.add_files(["file1.ass"])
    assert widget.count() == 1

    # 2. Добавляем вторую пачку
    widget.add_files(["file2.ass", "file3.ass"])
    # Старый файл должен удалиться, остаться только новые 2
    assert widget.count() == 2
    assert "file1.ass" not in [widget.item(i).text() for i in range(2)]
    assert len(widget.files) == 2


def test_file_list_drop_event_clear_logic(qtbot, mocker):
    """Тест: dropEvent также соблюдает настройку очистки."""
    mocker.patch("pathlib.Path.is_file", return_value=True)
    mock_settings = mocker.patch(
        "app.ui.file_list_widget.SettingsManager"
    ).return_value
    mock_settings.clear_list_on_add = True

    widget = FileListWidget()
    qtbot.addWidget(widget)

    # 1. Предзаполняем список
    widget.add_files(["existing.ass"])
    assert widget.count() == 1

    # 2. Имитируем Drop
    mime_data = QMimeData()
    mime_data.setUrls([QUrl.fromLocalFile("dropped.ass")])

    # Костыль для создания QDropEvent в тестах
    event = QDropEvent(
        QPointF(0, 0),
        Qt.DropAction.CopyAction,
        mime_data,
        Qt.MouseButton.LeftButton,
        Qt.KeyboardModifier.NoModifier,
    )

    widget.dropEvent(event)

    # Список должен очиститься перед добавлением сброшенного файла
    assert widget.count() == 1
    assert widget.item(0).text() == "dropped.ass"
    assert len(widget.files) == 1
