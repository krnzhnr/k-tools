from pathlib import Path
from PyQt6.QtCore import Qt
from PyQt6.QtWidgets import QApplication
from qfluentwidgets import InfoBar, FluentIcon
from app.ui.main_window import MainWindow
from app.core.script_registry import ScriptRegistry
from app.ui.work_panel import ScriptPage
from tests.unit.core.test_core import MockScript


def test_main_window_init(qtbot, mocker):
    mocker.patch(
        "app.core.resource_utils.get_resource_path", return_value="icon.ico"
    )
    registry = ScriptRegistry()
    registry.register(MockScript())

    window = MainWindow(registry)
    qtbot.addWidget(window)

    assert window.windowTitle() == "K-Tools"
    # Добавлен 1 скрипт + потенциально стандартные элементы
    assert len(window._script_pages) == 1


def test_main_window_resolve_icon():
    # Тест фолбека иконки
    icon = MainWindow._resolve_icon("NON_EXISTENT")
    assert icon == FluentIcon.COMMAND_PROMPT

    icon_valid = MainWindow._resolve_icon("VIDEO")
    assert icon_valid == FluentIcon.VIDEO


def test_work_panel_no_files_warning(qtbot, mocker):
    script = MockScript()
    page = ScriptPage(script)
    qtbot.addWidget(page)

    # Мокаем InfoBar.warning
    mocker.patch("qfluentwidgets.InfoBar.warning")

    # Кликаем "Выполнить" (в списке файлов пусто)
    qtbot.mouseClick(page._execute_btn, Qt.MouseButton.LeftButton)

    InfoBar.warning.assert_called_once()
    assert "Нет файлов" in InfoBar.warning.call_args[1]["title"]


def test_work_panel_execution_start(qtbot, mocker):
    import app.ui.work_panel as wp

    mock_worker_class = mocker.patch.object(wp, "ScriptWorker")
    mock_worker_instance = mock_worker_class.return_value

    script = MockScript()
    page = wp.ScriptPage(script)
    qtbot.addWidget(page)
    page.show()

    # Мокаем список файлов напрямую
    mocker.patch.object(
        page._file_list, "get_file_paths", return_value=[Path("test.tmp")]
    )

    # Запускаем
    page._on_execute_clicked()

    # Даем время Qt обработать события отрисовки
    for _ in range(10):
        QApplication.processEvents()

    # Проверяем что воркер создан и запущен
    mock_worker_class.assert_called_once()
    mock_worker_instance.start.assert_called_once()

    # Проверяем, что какой-то прогресс-бар виден
    is_progress_visible = page._progress.isVisible()
    if (
        hasattr(page, "_indeterminate_progress")
        and page._indeterminate_progress
    ):
        is_progress_visible = (
            is_progress_visible or page._indeterminate_progress.isVisible()
        )

    assert is_progress_visible
