import pytest
from pathlib import Path
from unittest.mock import MagicMock
from app.scripts.metadata_cleaner import MetadataCleanerScript
from app.scripts.container_converter import ContainerConverterScript

def test_metadata_cleaner_execution(mocker):
    script = MetadataCleanerScript()
    script._ffmpeg = MagicMock()
    script._ffmpeg.run.return_value = True
    
    # Мокаем проверку существования выходного файла, чтобы не пропускать
    mocker.patch("pathlib.Path.exists", return_value=False)
    
    # Видео файл
    file_path = Path("test.mkv")
    script.execute([file_path], {"suffix": "_cl"})
    script._ffmpeg.run.assert_called()

def test_container_converter_logic(mocker):
    script = ContainerConverterScript()
    script._ffmpeg = MagicMock()
    script._ffmpeg.run.return_value = True
    
    # Конвертация в MP4
    mkv = Path("movie.mkv")
    mocker.patch("pathlib.Path.exists", return_value=False)
    
    results = script.execute([mkv], {"target_format": "MP4", "delete_original": True})
    
    assert any("Конвертировано" in r for r in results)
    script._ffmpeg.run.assert_called()
    # Проверка вызова FFmpeg: должен быть кодек copy
    args = script._ffmpeg.run.call_args[1].get("extra_args", [])
    assert "copy" in args

def test_container_converter_skip(mocker):
    script = ContainerConverterScript()
    # Пропуск если формат совпадает
    mp4 = Path("test.mp4")
    results = script.execute([mp4], {"target_format": "MP4"})
    assert any("Пропущен" in r for r in results)
