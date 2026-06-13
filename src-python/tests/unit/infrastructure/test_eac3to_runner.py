from unittest.mock import MagicMock
from app.infrastructure.eac3to_runner import Eac3toRunner


def test_eac3to_success(mocker):
    # Тест успешного запуска
    mocker.patch(
        "app.core.path_utils.get_binary_path", return_value="eac3to.exe"
    )
    mock_popen = MagicMock()
    mock_popen.returncode = 0
    mock_popen.communicate.return_value = ("Success output", "")
    mock_popen._was_cancelled = False
    mocker.patch("subprocess.Popen", return_value=mock_popen)

    runner = Eac3toRunner()
    result = runner.run(["-info"])
    assert result is True


def test_eac3to_failure(mocker):
    # Тест ошибки выполнения (returncode != 0)
    mocker.patch(
        "app.core.path_utils.get_binary_path", return_value="eac3to.exe"
    )
    mock_popen = MagicMock()
    mock_popen.returncode = 1
    mock_popen.communicate.return_value = ("Failed stdout", "Error stderr")
    mock_popen._was_cancelled = False
    mocker.patch("subprocess.Popen", return_value=mock_popen)

    runner = Eac3toRunner()
    result = runner.run(["-invalid"])
    assert result is False


def test_eac3to_file_not_found(mocker):
    # Тест отсутствия исполняемого файла
    mocker.patch(
        "app.core.path_utils.get_binary_path", return_value="eac3to.exe"
    )
    mocker.patch("subprocess.Popen", side_effect=FileNotFoundError)

    runner = Eac3toRunner()
    result = runner.run([])
    assert result is False


def test_eac3to_exception(mocker):
    # Тест непредвиденной ошибки
    mocker.patch(
        "app.core.path_utils.get_binary_path", return_value="eac3to.exe"
    )
    mocker.patch("subprocess.Popen", side_effect=RuntimeError("Crash"))

    runner = Eac3toRunner()
    result = runner.run([])
    assert result is False
