# -*- coding: utf-8 -*-
import pytest
from pathlib import Path
from app.scripts.stream_manager import (
    StreamManagerScript,
    MODE_KEEP,
    MODE_REMOVE,
)
from app.infrastructure.mkvprobe_runner import TrackInfo


@pytest.fixture
def script():
    return StreamManagerScript()


@pytest.fixture
def mock_tracks():
    return [
        TrackInfo(
            track_id=0,
            track_type="video",
            codec="h264",
            language="und",
            name="Video",
            resolution="1920x1080",
            channels=0,
        ),
        TrackInfo(
            track_id=1,
            track_type="audio",
            codec="aac",
            language="und",
            name="Audio AAC",
            resolution="",
            channels=2,
        ),
        TrackInfo(
            track_id=2,
            track_type="audio",
            codec="ac3",
            language="und",
            name="Audio AC3",
            resolution="",
            channels=6,
        ),
    ]


def test_mp4_keep_audio_and_video(script, mock_tracks, mocker, tmp_path):
    mocker.patch.object(script._probe, "get_tracks", return_value=mock_tracks)
    mock_ffmpeg = mocker.patch.object(script._ffmpeg, "run", return_value=True)
    mocker.patch.object(script._resolver, "resolve", return_value=tmp_path)

    files = [Path("test.mp4")]
    # Выбираем видео (0) и одно аудио (1)
    settings = {
        "mode": MODE_KEEP,
        "selected_tracks_per_file": {str(files[0]): [0, 1]},
    }

    script.execute(files, settings)

    assert mock_ffmpeg.called
    args = mock_ffmpeg.call_args[1]["extra_args"]
    # Проверяем маппинг
    assert "-map" in args
    assert "0:0" in args
    assert "0:1" in args
    assert "-c" in args
    assert "copy" in args

    out_path = mock_ffmpeg.call_args[1]["output_path"]
    assert out_path.suffix == ".mp4"


def test_mp4_extract_single_audio_m4a(script, mock_tracks, mocker, tmp_path):
    mocker.patch.object(script._probe, "get_tracks", return_value=mock_tracks)
    mock_ffmpeg = mocker.patch.object(script._ffmpeg, "run", return_value=True)
    mocker.patch.object(script._resolver, "resolve", return_value=tmp_path)

    files = [Path("test.mp4")]
    # Выбираем только аудио (1) и просим M4A
    settings = {
        "mode": MODE_KEEP,
        "selected_tracks_per_file": {str(files[0]): [1]},
        "use_m4a_container_audio_only": True,
    }

    script.execute(files, settings)

    assert mock_ffmpeg.called
    out_path = mock_ffmpeg.call_args[1]["output_path"]
    assert out_path.suffix == ".m4a"

    args = mock_ffmpeg.call_args[1]["extra_args"]
    assert "0:1" in args


def test_mp4_remove_video(script, mock_tracks, mocker, tmp_path):
    mocker.patch.object(script._probe, "get_tracks", return_value=mock_tracks)
    mock_ffmpeg = mocker.patch.object(script._ffmpeg, "run", return_value=True)
    mocker.patch.object(script._resolver, "resolve", return_value=tmp_path)

    files = [Path("test.mp4")]
    # Удаляем видео (0), оставляем всё остальное (1, 2)
    settings = {
        "mode": MODE_REMOVE,
        "selected_tracks_per_file": {str(files[0]): [0]},
    }

    script.execute(files, settings)

    assert mock_ffmpeg.called
    args = mock_ffmpeg.call_args[1]["extra_args"]
    assert "0:1" in args
    assert "0:2" in args
    assert "0:0" not in args


def test_mp4_prevent_overwrite(script, mock_tracks, mocker, tmp_path):
    mocker.patch.object(script._probe, "get_tracks", return_value=mock_tracks)
    mock_ffmpeg = mocker.patch.object(script._ffmpeg, "run", return_value=True)
    # Имитируем ситуацию, когда выходная папка совпадает с входной
    mocker.patch.object(script._resolver, "resolve", return_value=Path("."))

    files = [Path("test.mp4")]
    settings = {
        "mode": MODE_KEEP,
        "selected_tracks_per_file": {str(files[0]): [0, 1]},
    }

    script.execute(files, settings)

    assert mock_ffmpeg.called
    out_path = mock_ffmpeg.call_args[1]["output_path"]
    # Должен добавиться суффикс _processed
    assert out_path.name == "test_processed.mp4"


def test_meta_data_updated(script):
    assert "Управление потоками" == script.name
    assert ".mp4" in script.file_extensions
    assert "MKV и MP4" in script.description
