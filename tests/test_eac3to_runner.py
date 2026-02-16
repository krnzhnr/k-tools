import pytest
import subprocess
from pathlib import Path
from unittest.mock import MagicMock
from app.infrastructure.eac3to_runner import Eac3toRunner

def test_eac3to_success(mocker):
    # Тест успешного запуска
    mocker.patch("app.core.path_utils.get_binary_path", return_value="eac3to.exe")
    mock_run = MagicMock()
    mock_run.returncode = 0
    mock_run.stdout = "Success output"
    mock_run.stderr = ""
    mocker.patch("subprocess.run", return_value=mock_run)
    
    runner = Eac3toRunner()
    result = runner.run(["-info"])
    assert result is True

def test_eac3to_failure(mocker):
    # Тест ошибки выполнения (returncode != 0)
    mocker.patch("app.core.path_utils.get_binary_path", return_value="eac3to.exe")
    mock_run = MagicMock()
    mock_run.returncode = 1
    mock_run.stdout = "Failed stdout"
    mock_run.stderr = "Error stderr"
    mocker.patch("subprocess.run", return_value=mock_run)
    
    runner = Eac3toRunner()
    result = runner.run(["-invalid"])
    assert result is False

def test_eac3to_file_not_found(mocker):
    # Тест отсутствия исполняемого файла
    mocker.patch("app.core.path_utils.get_binary_path", return_value="eac3to.exe")
    mocker.patch("subprocess.run", side_effect=FileNotFoundError)
    
    runner = Eac3toRunner()
    result = runner.run([])
    assert result is False

def test_eac3to_exception(mocker):
    # Тест непредвиденной ошибки
    mocker.patch("app.core.path_utils.get_binary_path", return_value="eac3to.exe")
    mocker.patch("subprocess.run", side_effect=RuntimeError("Crash"))
    
    runner = Eac3toRunner()
    result = runner.run([])
    assert result is False
