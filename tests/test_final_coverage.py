import pytest
from pathlib import Path
from unittest.mock import MagicMock
from PyQt6.QtWidgets import QWidget
from app.ui.work_panel import ScriptPage
from app.infrastructure.ffmpeg_runner import FFmpegRunner
from app.infrastructure.mkvmerge_runner import MKVMergeRunner
from app.core.abstract_script import SettingField
from tests.test_core import MockScript

def test_work_panel_signals_direct(qtbot):
    script = MockScript()
    page = ScriptPage(script)
    qtbot.addWidget(page)
    
    # Имитируем состояние выполнения
    page._progress.setVisible(True)
    page._execute_btn.setEnabled(False)
    
    # 1. Прогресс
    page._on_progress(50, 100, "Halfway")
    assert page._progress.value() == 50
    
    # 2. Ошибка
    page._on_error("Something went wrong")
    assert not page._progress.isVisible()
    assert page._execute_btn.isEnabled()
    
    # 3. Успех (повторный запуск)
    page._progress.setVisible(True)
    page._on_finished(["Done 1", "Done 2"])
    assert not page._progress.isVisible()
    assert page._execute_btn.isEnabled()

def test_work_panel_unknown_widget_coverage(qtbot):
    class UnknownWidgetScript(MockScript):
        @property
        def settings_schema(self):
            return [SettingField("unknown", "Label", "UNKNOWN")]
    
    script = UnknownWidgetScript()
    page = ScriptPage(script)
    # Ручной инжект неизвестного виджета
    page._settings_widgets["unknown"] = QWidget()
    settings = page.get_settings()
    assert settings["unknown"] == ""

def test_ffmpeg_runner_exception_coverage(mocker):
    runner = FFmpegRunner()
    mocker.patch("subprocess.run", side_effect=FileNotFoundError)
    assert runner.run(Path("in"), Path("out")) is False
    mocker.patch("subprocess.run", side_effect=RuntimeError)
    assert runner.run(Path("in"), Path("out")) is False

def test_mkvmerge_runner_exception_coverage(mocker):
    runner = MKVMergeRunner()
    mocker.patch("subprocess.run", side_effect=Exception)
    assert runner.run(Path("out"), []) is False
