from pathlib import Path
from qfluentwidgets import InfoBar
from app.ui.muxing_table_widget import (
    MuxingTableWidget,
    NaturalSortTableWidgetItem,
)
from app.ui.work_panel import ScriptPage
from tests.unit.core.test_core import MockScript


def test_natural_sort_key():
    # Тест естественной сортировки (1, 2, 10 вместо 1, 10, 2)
    assert NaturalSortTableWidgetItem._natural_key(
        "file2.mkv"
    ) < NaturalSortTableWidgetItem._natural_key("file10.mkv")
    assert NaturalSortTableWidgetItem._natural_key(
        "file1.mkv"
    ) < NaturalSortTableWidgetItem._natural_key("file2.mkv")


def test_muxing_table_grouping(qtbot, mocker):
    mocker.patch("pathlib.Path.is_file", return_value=True)
    table = MuxingTableWidget()
    qtbot.addWidget(table)

    files = [
        Path("episode1.mkv"),
        Path("episode1.srt"),
        Path("episode2.mp4"),
        Path("episode1.aac"),
    ]

    table.add_files(files)

    # Должно быть 2 строки: episode1 и episode2
    assert table.rowCount() == 2

    # Проверка episode1
    row1 = table._find_row_by_stem("episode1")
    assert row1 is not None
    assert "episode1.mkv" in table.item(row1, 0).text()
    assert "episode1.aac" in table.item(row1, 1).text()
    assert "episode1.srt" in table.item(row1, 2).text()


def test_muxing_table_get_tasks(qtbot, mocker):
    mocker.patch("pathlib.Path.is_file", return_value=True)
    table = MuxingTableWidget()
    qtbot.addWidget(table)

    table.add_files([Path("movie.mkv"), Path("movie.mka")])

    tasks = table.get_tasks()
    assert len(tasks) == 1
    assert tasks[0]["video"].name == "movie.mkv"
    assert tasks[0]["audio"].name == "movie.mka"


def test_work_panel_progress_and_error(qtbot, mocker):
    script = MockScript()
    page = ScriptPage(script)
    qtbot.addWidget(page)

    # Тест прогресса
    page._on_progress(1, 2, "Processing")
    assert page._progress.value() == 50
    assert "1/2" in page._status_label.text()

    # Тест ошибки
    mocker.patch("qfluentwidgets.InfoBar.error")
    page._on_error("Crash logic")
    InfoBar.error.assert_called_once()
    assert page._execute_btn.isEnabled()


def test_work_panel_finished_success(qtbot, mocker):
    script = MockScript()
    page = ScriptPage(script)
    qtbot.addWidget(page)

    mocker.patch("qfluentwidgets.InfoBar.success")
    results = ["✅ OK", "✅ Done"]
    page._on_finished(results)

    InfoBar.success.assert_called_once()
    assert "✅ OK" in page._log_area.toPlainText()


def test_work_panel_finished_with_errors(qtbot, mocker):
    script = MockScript()
    page = ScriptPage(script)
    qtbot.addWidget(page)

    mocker.patch("qfluentwidgets.InfoBar.error")
    results = ["✅ OK", "❌ Fail"]
    page._on_finished(results)

    InfoBar.error.assert_called_once()
    assert "✅ OK" in page._log_area.toPlainText()
    assert "❌ Fail" in page._log_area.toPlainText()
