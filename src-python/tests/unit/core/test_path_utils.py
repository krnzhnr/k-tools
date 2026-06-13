from pathlib import Path
import os
from unittest.mock import patch
from app.core.path_utils import get_binary_path


def test_get_binary_path_in_subfolder(tmp_path, mocker):
    # Создаем временную структуру bin/ffmpeg/kt-ffmpeg.exe
    bin_dir = tmp_path / "bin"
    ffmpeg_dir = bin_dir / "ffmpeg"
    ffmpeg_dir.mkdir(parents=True)
    ffmpeg_exe = ffmpeg_dir / "kt-ffmpeg.exe"
    ffmpeg_exe.touch()

    # Мокаем sys.executable и sys.frozen, чтобы base_dir указывала на tmp_path
    mocker.patch("sys.executable", str(tmp_path / "app.exe"))
    mocker.patch("sys.frozen", True, create=True)

    path = get_binary_path("ffmpeg.exe")
    assert Path(path).resolve() == ffmpeg_exe.resolve()


def test_get_binary_path_case_mkvmerge(tmp_path, mocker):
    # Тест mkvmerge -> mkvtoolnix (без маскировки)
    bin_dir = tmp_path / "bin"
    mkv_dir = bin_dir / "mkvtoolnix"
    mkv_dir.mkdir(parents=True)
    mkv_exe = mkv_dir / "mkvmerge.exe"
    mkv_exe.touch()

    mocker.patch("sys.executable", str(tmp_path / "app.exe"))
    mocker.patch("sys.frozen", True, create=True)

    path = get_binary_path("mkvmerge.exe")
    assert "mkvtoolnix" in path.lower()
    assert Path(path).resolve() == mkv_exe.resolve()


def test_get_binary_path_shutil_which_frozen_fallback(mocker):
    # Тест fallback на shutil.which, когда frozen (маскировка активна)
    mocker.patch("sys.executable", "C:\\NonExistent\\app.exe")
    mocker.patch("sys.frozen", True, create=True)
    mocker.patch("app.core.path_utils.Path.exists", return_value=False)
    # Ожидаем, что shutil.which будет вызван с kt-ffmpeg.exe
    mocker.patch(
        "shutil.which", side_effect=lambda x: "C:\\Windows\\System32\\" + x
    )

    path = get_binary_path("ffmpeg.exe")
    assert path == "C:\\Windows\\System32\\kt-ffmpeg.exe"


def test_get_binary_path_shutil_which_not_frozen(mocker):
    # Тест нахождения через системный PATH, когда не frozen
    mocker.patch("sys.frozen", False, create=True)
    mocker.patch("pathlib.Path.exists", return_value=False)
    mocker.patch("shutil.which", return_value="/usr/bin/kt-ffmpeg")

    assert get_binary_path("ffmpeg") == "/usr/bin/kt-ffmpeg"


def test_get_binary_path_absolute_fallback():
    # Тест окончательной неудачи (просто возврат замаскированного имени)
    with patch("pathlib.Path.exists", return_value=False):
        with patch("shutil.which", return_value=None):
            assert (
                get_binary_path("ffmpeg") == "kt-ffmpeg.exe"
                if os.name == "nt"
                else "kt-ffmpeg"
            )


def test_get_binary_path_not_found(mocker):
    # Тест случая, когда ничего не найдено
    mocker.patch("sys.executable", "C:\\NonExistent\\app.exe")
    mocker.patch("sys.frozen", True, create=True)
    mocker.patch("app.core.path_utils.Path.exists", return_value=False)
    mocker.patch("shutil.which", return_value=None)

    path = get_binary_path("unknown_tool.exe")
    assert path == "unknown_tool.exe"
