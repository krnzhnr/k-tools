from pathlib import Path
from unittest.mock import MagicMock
from app.infrastructure.ffmpeg_runner import FFmpegRunner
from app.infrastructure.eac3to_runner import Eac3toRunner
from app.infrastructure.mkvmerge_runner import MKVMergeRunner

# --- FFmpegRunner Tests ---


def test_ffmpeg_runner_init(mocker):
    mocker.patch("app.core.path_utils.get_binary_path", return_value="ffmpeg")
    runner = FFmpegRunner()
    assert runner._ffmpeg_path == "ffmpeg"


def test_ffmpeg_run_success(mocker):
    runner = FFmpegRunner()
    mock_popen = MagicMock()
    mock_popen.returncode = 0
    mock_popen.communicate.return_value = ("Success", "")
    mock_popen._was_cancelled = False
    mocker.patch("subprocess.Popen", return_value=mock_popen)

    result = runner.run(Path("input.mp4"), Path("output.mp4"))
    assert result is True


def test_ffmpeg_run_failure(mocker):
    runner = FFmpegRunner()
    mock_popen = MagicMock()
    mock_popen.returncode = 1
    mock_popen.communicate.return_value = ("", "Error")
    mock_popen._was_cancelled = False
    mocker.patch("subprocess.Popen", return_value=mock_popen)

    result = runner.run(Path("input.mp4"), Path("output.mp4"))
    assert result is False


def test_ffmpeg_not_found(mocker):
    runner = FFmpegRunner()
    mocker.patch("subprocess.Popen", side_effect=FileNotFoundError)

    result = runner.run(Path("input.mp4"), Path("output.mp4"))
    assert result is False


# --- Eac3toRunner Tests ---


def test_eac3to_init(mocker):
    mocker.patch("app.core.path_utils.get_binary_path", return_value="eac3to")
    runner = Eac3toRunner()
    assert runner._executable == "eac3to"


def test_eac3to_run_success(mocker):
    mocker.patch("app.core.path_utils.get_binary_path", return_value="eac3to")
    runner = Eac3toRunner()
    mock_popen = MagicMock()
    mock_popen.returncode = 0
    mock_popen.communicate.return_value = ("Success", "")
    mock_popen._was_cancelled = False
    mocker.patch("subprocess.Popen", return_value=mock_popen)

    result = runner.run(["input", "output"])
    assert result is True


def test_eac3to_run_failure(mocker):
    mocker.patch("app.core.path_utils.get_binary_path", return_value="eac3to")
    runner = Eac3toRunner()
    mock_popen = MagicMock()
    mock_popen.returncode = 1
    mock_popen.communicate.return_value = ("", "Error")
    mock_popen._was_cancelled = False
    mocker.patch("subprocess.Popen", return_value=mock_popen)

    result = runner.run(["input", "output"])
    assert result is False


# --- MKVMergeRunner Tests ---


def test_mkvmerge_init(mocker):
    mocker.patch(
        "app.core.path_utils.get_binary_path", return_value="mkvmerge"
    )
    runner = MKVMergeRunner()
    assert runner._mkvmerge_path == "mkvmerge"


def test_mkvmerge_run_mux_success(mocker):
    mocker.patch(
        "app.core.path_utils.get_binary_path", return_value="mkvmerge"
    )
    runner = MKVMergeRunner()
    mock_popen = MagicMock()
    mock_popen.returncode = 0
    mock_popen.communicate.return_value = ("Success", "")
    mock_popen._was_cancelled = False
    mocker.patch("subprocess.Popen", return_value=mock_popen)

    inputs = [{"path": Path("input.mp4"), "args": []}]
    result = runner.run(Path("output.mkv"), inputs, title="Test Title")
    assert result is True
