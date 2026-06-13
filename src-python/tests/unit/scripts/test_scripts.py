import pytest

from app.scripts.metadata_cleaner import MetadataCleanerScript
from app.scripts.container_converter import ContainerConverterScript
from app.scripts.audio_converter import AudioConverterScript
from app.scripts.audio_speed_changer import AudioSpeedChangerScript

# --- MetadataCleanerScript Tests ---


def test_metadata_cleaner_execute(mocker, temp_dir):
    script = MetadataCleanerScript()
    mocker.patch.object(script._ffmpeg, "run", return_value=True)

    files = [temp_dir / "test.mp4"]
    settings = {"suffix": "_cl"}

    results = script.execute(files, settings)

    assert len(results) == 1
    assert "✅" in results[0]
    script._ffmpeg.run.assert_called_once()


# --- ContainerConverterScript Tests ---


def test_container_converter_execute(mocker, temp_dir):
    script = ContainerConverterScript()
    mocker.patch.object(script._ffmpeg, "run", return_value=True)

    files = [temp_dir / "test.mkv"]
    settings = {"target_container": "MP4", "delete_original": False}

    results = script.execute(files, settings)

    assert len(results) == 1
    assert "Конвертировано" in results[0]
    script._ffmpeg.run.assert_called_once()


# --- AudioConverterScript Tests ---


def test_audio_converter_execute(mocker, temp_dir):
    script = AudioConverterScript()
    mocker.patch.object(script._ffmpeg, "run", return_value=True)

    files = [temp_dir / "song.wav"]
    settings = {
        "target_format": "MP3",
        "bitrate": "320k",
        "delete_original": False,
    }

    results = script.execute(files, settings)

    assert len(results) == 1
    assert (
        "Конвертирован" in results[0]
        or "окончен" in results[0].lower()
        or "✅" in results[0]
    )
    # Verify ffmpeg args include bitrate
    call_args = script._ffmpeg.run.call_args
    assert "-b:a" in call_args.kwargs["extra_args"]
    assert "320k" in call_args.kwargs["extra_args"]


# --- AudioSpeedChangerScript Tests ---


@pytest.mark.parametrize(
    "mode, expected_args",
    [
        ("Slowdown (25.000 → 23.976)", ["-slowdown"]),
        ("Speedup (23.976 → 25.000)", ["-speedup"]),
        ("Custom (24.000 → 23.976)", ["-24.000", "-slowdown"]),
        ("Custom (25.000 → 24.000)", ["-25.000", "-changeTo24.000"]),
    ],
)
def test_audio_speed_changer_execute(mocker, temp_dir, mode, expected_args):
    """Тестирование всех режимов изменения скорости аудио."""
    script = AudioSpeedChangerScript()
    mocker.patch.object(script._runner, "run", return_value=True)

    file_path = temp_dir / "audio.ac3"
    mocker.patch("pathlib.Path.exists", return_value=True)

    files = [file_path]
    settings = {
        "mode": mode,
        "output_format": "FLAC",
        "delete_source": False,
    }

    results = script.execute(files, settings)

    assert len(results) == 1
    assert "УСПЕХ" in results[0]

    # Проверка аргументов eac3to
    script._runner.run.assert_called_once()
    actual_args = script._runner.run.call_args[0][0]

    # Аргументы должны быть в конце списка команд
    for arg in expected_args:
        assert arg in actual_args

    assert str(actual_args[1]).endswith(".flac")
