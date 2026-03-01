import pytest
from PyQt6.QtWidgets import QApplication
from app.ui.file_list_widget import FileListWidget
from app.ui.work_panel import ScriptPage
from app.core.abstract_script import SettingField, SettingType
from tests.unit.core.test_core import MockScript


@pytest.fixture
def app(qtbot):
    # QApplication создается qtbot'ом автоматически
    pass


def test_file_list_widget_add_remove(qtbot, mocker):
    mocker.patch("pathlib.Path.is_file", return_value=True)
    widget = FileListWidget()
    qtbot.addWidget(widget)

    # Изначально пусто
    assert widget.count() == 0
    assert len(widget.files) == 0

    # Добавление файлов
    test_files = ["test1.mkv", "test2.mp4"]
    widget.add_files(test_files)

    assert widget.count() == 2
    assert "test1.mkv" in widget.item(0).text()

    # Удаление выбранного (нужно имитировать выбор)
    widget.setCurrentRow(0)
    # Вызываем приватный метод удаления для проверки логики
    widget._remove_selected()

    assert widget.count() == 1
    assert "test2.mp4" in widget.item(0).text()


def test_script_page_settings_generation(qtbot):
    script = MockScript()
    page = ScriptPage(script)
    qtbot.addWidget(page)

    # Проверка сбора настроек
    settings = page.get_settings()
    assert "key" in settings
    assert isinstance(settings["key"], str)


def test_script_page_combo_checkbox(qtbot):
    class ComplexMockScript(MockScript):
        @property
        def settings_schema(self):
            return [
                SettingField(
                    "combo",
                    "Combo",
                    SettingType.COMBO,
                    default="B",
                    options=["A", "B", "C"],
                ),
                SettingField(
                    "check", "Check", SettingType.CHECKBOX, default=True
                ),
            ]

    script = ComplexMockScript()
    page = ScriptPage(script)
    qtbot.addWidget(page)

    settings = page.get_settings()
    assert settings["combo"] == "B"
    assert settings["check"] is True


def test_script_page_dynamic_visibility(qtbot):
    class VisibilityMockScript(MockScript):
        @property
        def settings_schema(self):
            return [
                SettingField(
                    "trigger", "Trigger", SettingType.CHECKBOX, default=False
                ),
                SettingField(
                    "target",
                    "Target",
                    SettingType.TEXT,
                    visible_if={"trigger": [True]},
                ),
            ]

    script = VisibilityMockScript()
    page = ScriptPage(script)
    qtbot.addWidget(page)
    page.show()  # Важно для корректной работы isVisible()

    # Изначально 'target' не должен быть виден
    target_row = page._settings_rows["target"]
    assert target_row.isHidden()

    # Меняем состояние чекбокса напрямую
    trigger_widget = page._settings_widgets["trigger"]
    trigger_widget.setChecked(True)
    QApplication.processEvents()

    # Теперь должен быть виден
    assert not target_row.isHidden()
    assert target_row.isVisible()
