import os
from pathlib import Path
from app.core.resource_utils import get_resource_path


def test_get_resource_path_dev_mode(tmp_path, mocker):
    # Тест в режиме разработки (не frozen)
    mocker.patch("sys.frozen", False, create=True)
    mocker.patch("os.path.abspath", return_value=str(tmp_path))

    # Создаем фиктивный ресурс в папке assets
    assets_dir = tmp_path / "assets"
    assets_dir.mkdir()
    icon_file = assets_dir / "test_icon.ico"
    icon_file.touch()

    # Мокаем текущую рабочую директорию для функции
    mocker.patch("os.path.join", side_effect=os.path.join)

    # Мы должны найти файл в assets
    with mocker.patch(
        "app.core.resource_utils.os.path.abspath", return_value=str(tmp_path)
    ):
        path = get_resource_path("test_icon.ico")
        assert Path(path) == icon_file.absolute()


def test_get_resource_path_frozen_mode(tmp_path, mocker):
    # Тест в режиме PyInstaller (frozen)
    mocker.patch("sys.frozen", True, create=True)
    mocker.patch("sys._MEIPASS", str(tmp_path), create=True)

    # В frozen режиме файлы лежат в корне _MEIPASS
    icon_file = tmp_path / "test_icon.ico"
    icon_file.touch()

    path = get_resource_path("test_icon.ico")
    assert Path(path) == icon_file.absolute()


def test_get_resource_path_fallback(tmp_path, mocker):
    # Тест случая, когда ресурс не найден
    mocker.patch("sys.frozen", False, create=True)
    mocker.patch("os.path.abspath", return_value=str(tmp_path))

    path = get_resource_path("non_existent.file")
    assert "non_existent.file" in path
