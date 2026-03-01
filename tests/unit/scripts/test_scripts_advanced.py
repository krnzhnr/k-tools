from pathlib import Path
from unittest.mock import MagicMock, patch, PropertyMock
from app.scripts.muxer import MuxerScript
from app.scripts.audio_converter import AudioConverterScript
from app.core.settings_manager import SettingsManager

# --- Muxer Advanced Tests ---


def test_muxer_complex_grouping():
    # Тест группировки файлов с разными расширениями, но одинаковым именем
    script = MuxerScript()
    files = [
        Path("movie.mkv"),
        Path("movie.aac"),
        Path("movie.srt"),
        Path("other_video.mp4"),
        Path("other_video.mp3"),
    ]

    # Мокаем mkvmerge runner
    script._runner = MagicMock()
    script._runner.run.return_value = True

    settings = {"subs_title": "Test", "clean_tracks": True}
    script.execute(files, settings)

    # Должно быть 2 запуска mkvmerge (для movie и other_video)
    assert script._runner.run.call_count == 2

    # Проверка аргументов первого вызова (movie)
    kwargs = script._runner.run.call_args_list[0][1]
    inputs = kwargs["inputs"]

    # Видео всегда первое
    assert inputs[0]["path"] == Path("movie.mkv")
    # Аудио и сабы должны быть в списке
    input_paths = [i["path"] for i in inputs]
    assert Path("movie.aac") in input_paths
    assert Path("movie.srt") in input_paths


def test_muxer_skip_existing(tmp_path):
    # Тест пропуска, если выходной файл уже есть
    script = MuxerScript()

    video = tmp_path / "test.mkv"
    video.touch()

    # Создаем папку Completed и файл в ней
    completed_dir = tmp_path / "Completed"
    completed_dir.mkdir()
    (completed_dir / "test.mkv").touch()

    script._runner = MagicMock()

    # Принудительно выключаем перезапись и настраиваем подпапку для теста пропуска
    with patch.object(
        SettingsManager,
        "overwrite_existing",
        new_callable=PropertyMock,
        return_value=False,
    ), patch.object(
        SettingsManager,
        "use_auto_subfolder",
        new_callable=PropertyMock,
        return_value=True,
    ), patch.object(
        SettingsManager,
        "default_output_subfolder",
        new_callable=PropertyMock,
        return_value="Completed",
    ):
        results = script.execute([video], {"clean_tracks": False})

    assert any("ПРОПУСК" in r for r in results)
    script._runner.run.assert_not_called()


# --- Audio Converter Advanced Tests ---


def test_audio_converter_all_codecs():
    # Проверка, что для каждого кодека формируются правильные аргументы
    script = AudioConverterScript()

    from app.scripts.audio_converter import AUDIO_FORMATS

    for fmt, config in AUDIO_FORMATS.items():
        script._ffmpeg = MagicMock()
        script._ffmpeg.run.return_value = True
        script._qaac = MagicMock()
        script._qaac.run.return_value = True

        settings = {
            "target_format": fmt,
            "bitrate": "192k",
            "delete_original": False,
        }
        script.execute([Path("test.raw")], settings)

        if fmt == "QAAC":
            # Проверяем вызов qaac
            script._qaac.run.assert_called_once()
            call_kwargs = script._qaac.run.call_args.kwargs
            assert str(call_kwargs["output_path"]).endswith(".aac")
        else:
            # Проверяем вызов ffmpeg
            args = script._ffmpeg.run.call_args[1].get("extra_args", [])
            assert "-c:a" in args
            assert config["codec"] in args
            if fmt in ["MP3", "AAC", "OGG"]:
                assert "192k" in args


def test_audio_converter_delete_and_error():
    script = AudioConverterScript()
    script._ffmpeg = MagicMock()

    # 1. Успех с удалением
    f = Path("test.wav")
    script._ffmpeg.run.return_value = True
    with patch.object(script, "_delete_source") as mock_del:
        script.execute([f], {"target_format": "MP3", "delete_original": True})
        mock_del.assert_called()

    # 2. Ошибка FFmpeg
    script._ffmpeg.run.return_value = False
    results = script.execute([f], {"target_format": "MP3"})
    assert any("ОШИБКА" in r for r in results)


def test_muxer_error_handling():
    script = MuxerScript()
    script._runner = MagicMock()
    script._runner.run.return_value = False  # Ошибка!

    with patch("pathlib.Path.exists", return_value=False), patch(
        "pathlib.Path.mkdir"
    ):
        results = script.execute([Path("v.mkv"), Path("a.ac3")], {})
        assert any("ОШИБКА" in r for r in results)
