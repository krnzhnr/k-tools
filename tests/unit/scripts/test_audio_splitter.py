# -*- coding: utf-8 -*-
from pathlib import Path
from app.scripts.audio_splitter import AudioSplitterScript


def test_audio_splitter_metadata():
    """Тест метаданных скрипта."""
    script = AudioSplitterScript()
    assert "Декомпозиция каналов" == script.name
    assert ".wav" in script.file_extensions
    assert ".mkv" in script.file_extensions
    assert script.icon_name == "SHARE"


def test_audio_splitter_execute_simple(mocker, tmp_path):
    """Тест выполнения разделения аудио без склейки."""
    script = AudioSplitterScript()
    mocker.patch.object(script._runner, "run", return_value=True)

    input_file = tmp_path / "multichannel.wav"
    input_file.write_text("dummy")

    files = [input_file]
    settings = {"merge_stereo": False, "delete_original": False}

    results = script.execute(files, settings, output_path=str(tmp_path))
    assert any("✅ Разделено" in r for r in results)
    script._runner.run.assert_called_once()


def test_audio_splitter_stereo_merge(mocker, tmp_path):
    """Тест автоматической склейки стереопар."""
    script = AudioSplitterScript()

    # Мокаем eac3to: при запуске "создаем" моно-файлы
    def mock_eac3to_run(args, cwd=None):
        # Создаем файлы L, R, C, SL, SR
        stem = Path(args[0]).stem
        for sfx in ["L", "R", "C", "SL", "SR"]:
            (tmp_path / f"{stem}.{sfx}.wav").write_text("pcm")
        return True

    mocker.patch.object(script._runner, "run", side_effect=mock_eac3to_run)
    mock_ffmpeg = mocker.patch.object(script._ffmpeg, "run", return_value=True)

    input_file = tmp_path / "movie.mkv"
    input_file.write_text("video")

    files = [input_file]
    settings = {"merge_stereo": True, "delete_original": False}

    results = script.execute(files, settings, output_path=str(tmp_path))

    # Должно быть 2 вызова FFmpeg: для LR и для SLSR
    assert mock_ffmpeg.call_count == 2

    # Проверяем, что моно-файлы L/R/SL/SR удалены, а C остался
    assert not (tmp_path / "movie.L.wav").exists()
    assert not (tmp_path / "movie.R.wav").exists()
    assert not (tmp_path / "movie.SL.wav").exists()
    assert not (tmp_path / "movie.SR.wav").exists()
    assert (tmp_path / "movie.C.wav").exists()

    # Проверяем записи в результатах
    assert any("🔗 Склеено стерео: movie.LR.wav" in r for r in results)
    assert any("🔗 Склеено стерео: movie.SLSR.wav" in r for r in results)


def test_audio_splitter_delete_source(mocker, tmp_path):
    """Тест удаления исходного файла."""
    script = AudioSplitterScript()
    mocker.patch.object(script._runner, "run", return_value=True)

    input_file = tmp_path / "source.wav"
    input_file.write_text("data")

    files = [input_file]
    settings = {"merge_stereo": False, "delete_original": True}

    script.execute(files, settings, output_path=str(tmp_path))
    assert not input_file.exists()


def test_audio_splitter_skip_merge_for_stereo(mocker, tmp_path):
    """Тест пропуска склейки для обычного стерео (2 канала)."""
    script = AudioSplitterScript()

    # Мокаем eac3to: создает только L и R
    def mock_eac3to_run(args, cwd=None):
        stem = Path(args[0]).stem
        (tmp_path / f"{stem}.L.wav").write_text("pcm")
        (tmp_path / f"{stem}.R.wav").write_text("pcm")
        return True

    mocker.patch.object(script._runner, "run", side_effect=mock_eac3to_run)
    mock_ffmpeg = mocker.patch.object(script._ffmpeg, "run", return_value=True)

    input_file = tmp_path / "stereo_source.wav"
    input_file.write_text("audio")

    files = [input_file]
    settings = {"merge_stereo": True}  # Включено, но должно пропуститься

    script.execute(files, settings, output_path=str(tmp_path))

    # FFmpeg НЕ должен вызываться
    assert mock_ffmpeg.call_count == 0
    # Моно-файлы должны остаться на месте
    assert (tmp_path / "stereo_source.L.wav").exists()
    assert (tmp_path / "stereo_source.R.wav").exists()
