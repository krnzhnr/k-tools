from pathlib import Path
from unittest.mock import MagicMock
from app.infrastructure.ffmpeg_runner import FFmpegRunner


def test_ffmpeg_termination_on_error(mocker):
    # Тест прерывания процесса при ошибке
    runner = FFmpegRunner()

    # Мокаем Popen
    mock_popen = MagicMock()
    mock_popen.poll.return_value = None  # Процесс еще идет
    mock_popen.communicate.return_value = (
        b"",
        "Критическая ошибка FFmpeg".encode("utf-8"),
    )
    mock_popen.returncode = 1
    mock_popen._was_cancelled = False

    mocker.patch("subprocess.Popen", return_value=mock_popen)
    mocker.patch("app.core.path_utils.get_binary_path", return_value="ffmpeg")

    result = runner.run(Path("in.mp4"), Path("out.mp3"))

    assert result is False
    # Проверка, что процесс пытались остановить при ошибке (если бы он завис)
    # В текущей реализации run() использует Popen.communicate(), что не дает зависнуть,
    # но мы проверим, что логика обработки returncode верна.


def test_infrastructure_path_isolation(mocker):
    # Проверка, что bin_dir добавляется в PATH

    # Мокаем Popen чтобы перехватить env
    mock_popen = MagicMock()
    mock_popen.communicate.return_value = (b"", b"")
    mock_popen.returncode = 0
    mock_popen._was_cancelled = False

    stub_popen = mocker.patch("subprocess.Popen", return_value=mock_popen)
    mocker.patch(
        "app.core.path_utils.get_binary_path",
        return_value="C:\\tools\\ffmpeg.exe",
    )

    runner = FFmpegRunner()
    runner.run(Path("in.mp4"), Path("out.mp3"))

    # Проверяем переданное окружение
    args, kwargs = stub_popen.call_args
    env = kwargs.get("env")
    assert env is not None
    assert "C:\\tools" in env["PATH"]


def test_ffmpeg_empty_input(mocker):
    # Тест обработки отсутствующего входного файла
    runner = FFmpegRunner()
    result = runner.run(Path("non_existent.mp4"), Path("out.mp3"))
    assert result is False


def test_ffmpeg_file_not_found(mocker):
    # Тест случая когда файл нашелся в path_utils но не запустился
    mocker.patch("app.core.path_utils.get_binary_path", return_value="ffmpeg")
    mocker.patch("subprocess.Popen", side_effect=FileNotFoundError)

    runner = FFmpegRunner()
    result = runner.run(Path("in.mp4"), Path("out.mp3"))
    assert result is False


def test_ffmpeg_generic_exception(mocker):
    # Тест непредвиденного исключения
    mocker.patch("app.core.path_utils.get_binary_path", return_value="ffmpeg")
    mocker.patch("subprocess.Popen", side_effect=RuntimeError("Generic crash"))

    runner = FFmpegRunner()
    result = runner.run(Path("in.mp4"), Path("out.mp3"))
    assert result is False
