from pathlib import Path
from unittest.mock import MagicMock
import app.infrastructure.mkvmerge_runner as mkv_mod
from app.ui.file_list_widget import FileListWidget


def test_eac3to_runner_extra_coverage(mocker):
    from app.infrastructure.eac3to_runner import Eac3toRunner

    runner = Eac3toRunner()

    # Ошибка returncode != 0
    mocker.patch("subprocess.run", return_value=MagicMock(returncode=1))
    assert runner.run(["in", "out"]) is False

    # FileNotFoundError
    mocker.patch("subprocess.run", side_effect=FileNotFoundError)
    assert runner.run(["in", "out"]) is False

    # General Exception
    mocker.patch("subprocess.run", side_effect=RuntimeError)
    assert runner.run(["in", "out"]) is False


def test_mkvmerge_runner_extra_coverage(mocker):
    runner = mkv_mod.MKVMergeRunner()

    # Exception
    mocker.patch("subprocess.run", side_effect=Exception)
    assert runner.run(Path("out"), []) is False

    # Тест непредвиденного исключения в mkvmerge
    mocker.patch(
        "app.core.path_utils.get_binary_path", return_value="mkvmerge"
    )
    mocker.patch("subprocess.run", side_effect=RuntimeError("Boom"))
    result = runner.run(Path("out.mkv"), [])
    assert result is False


def test_file_list_clear_files(qtbot, mocker):
    mocker.patch("pathlib.Path.is_file", return_value=True)
    widget = FileListWidget()
    qtbot.addWidget(widget)
    widget.add_files(["test.mkv"])

    assert widget.count() == 1
    widget.clear_files()
    assert widget.count() == 0
