import pytest
from pathlib import Path
from unittest.mock import MagicMock, patch
from app.infrastructure.ffmpeg_runner import FFmpegRunner
from app.infrastructure.eac3to_runner import Eac3toRunner
from app.infrastructure.mkvmerge_runner import MKVMergeRunner

# --- FFmpegRunner Tests ---

def test_ffmpeg_runner_init(mocker):
    runner = FFmpegRunner()
    assert runner.FFMPEG_BINARY == "ffmpeg"

def test_ffmpeg_run_success(mocker):
    runner = FFmpegRunner()
    mocker.patch("subprocess.run", return_value=MagicMock(returncode=0))
    
    result = runner.run(Path("input.mp4"), Path("output.mp4"))
    assert result is True

def test_ffmpeg_run_failure(mocker):
    runner = FFmpegRunner()
    mocker.patch("subprocess.run", return_value=MagicMock(returncode=1, stderr="Error"))
    
    result = runner.run(Path("input.mp4"), Path("output.mp4"))
    assert result is False

def test_ffmpeg_not_found(mocker):
    runner = FFmpegRunner()
    mocker.patch("subprocess.run", side_effect=FileNotFoundError)
    
    result = runner.run(Path("input.mp4"), Path("output.mp4"))
    assert result is False

# --- Eac3toRunner Tests ---

def test_eac3to_find_executable_path(mocker):
    mocker.patch("shutil.which", return_value="path/to/eac3to")
    runner = Eac3toRunner()
    assert runner._executable == "path/to/eac3to"

def test_eac3to_find_executable_local(mocker):
    mocker.patch("shutil.which", return_value=None)
    mocker.patch("pathlib.Path.exists", side_effect=lambda: True)
    runner = Eac3toRunner()
    assert str(runner._executable).endswith("eac3to.exe")

def test_eac3to_run_success(mocker):
    mocker.patch("app.infrastructure.eac3to_runner.Eac3toRunner._find_executable", return_value="eac3to")
    runner = Eac3toRunner()
    mocker.patch("subprocess.run", return_value=MagicMock(returncode=0, stdout="Success"))
    
    result = runner.run(["input", "output"])
    assert result is True

def test_eac3to_run_failure(mocker):
    mocker.patch("app.infrastructure.eac3to_runner.Eac3toRunner._find_executable", return_value="eac3to")
    runner = Eac3toRunner()
    mocker.patch("subprocess.run", return_value=MagicMock(returncode=1, stderr="Error"))
    
    result = runner.run(["input", "output"])
    assert result is False

# --- MKVMergeRunner Tests ---

def test_mkvmerge_find_executable(mocker):
    mocker.patch("shutil.which", return_value="mkvmerge")
    runner = MKVMergeRunner()
    assert runner._mkvmerge_path == "mkvmerge"

def test_mkvmerge_run_mux_success(mocker):
    mocker.patch("app.infrastructure.mkvmerge_runner.MKVMergeRunner._find_mkvmerge", return_value="mkvmerge")
    runner = MKVMergeRunner()
    mocker.patch("subprocess.run", return_value=MagicMock(returncode=0, stdout="Success"))
    
    inputs = [{"path": Path("input.mp4"), "args": []}]
    result = runner.run(Path("output.mkv"), inputs, title="Test Title")
    assert result is True
