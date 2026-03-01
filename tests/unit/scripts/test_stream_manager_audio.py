# -*- coding: utf-8 -*-
import pytest
from pathlib import Path
from app.scripts.stream_manager import (
    StreamManagerScript,
    MODE_KEEP,
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
            codec="HEVC",
            language="und",
            name="Video",
            resolution="1920x1080",
            channels=0,
        ),
        TrackInfo(
            track_id=1,
            track_type="audio",
            codec="AC-3",
            language="und",
            name="Audio AC3",
            resolution="",
            channels=6,
        ),
        TrackInfo(
            track_id=2,
            track_type="audio",
            codec="DTS",
            language="und",
            name="Audio DTS",
            resolution="",
            channels=6,
        ),
    ]


def test_extract_single_audio_raw_ac3(script, mock_tracks, mocker, tmp_path):
    # Настраиваем моки
    mocker.patch.object(script._probe, "get_tracks", return_value=mock_tracks)
    mock_ffmpeg = mocker.patch.object(script._ffmpeg, "run", return_value=True)
    mock_mkvmerge = mocker.patch.object(
        script._runner, "run", return_value=True
    )
    mocker.patch.object(script._resolver, "resolve", return_value=tmp_path)

    files = [Path("test.mkv")]
    # Выбираем только AC-3 (ID 1)
    settings = {
        "mode": MODE_KEEP,
        "selected_tracks_per_file": {str(files[0]): [1]},
        "use_m4a_container_audio_only": False,
    }

    script.execute(files, settings)

    # Проверяем, что использовался FFmpeg
    assert mock_ffmpeg.called
    assert not mock_mkvmerge.called

    # Проверяем аргументы FFmpeg
    # Индекс дорожки 1 (AC-3) среди аудиодорожек равен 0 (т.к. 0-видео, 1-ац3, 2-дтс)
    # Но в списке аудио-дорожек [1, 2], индекс 1 это 0.
    args = mock_ffmpeg.call_args[1]["extra_args"]
    assert "-map" in args
    assert "0:a:0" in args

    # Проверяем расширение
    out_path = mock_ffmpeg.call_args[1]["output_path"]
    assert out_path.suffix == ".ac3"


def test_extract_single_audio_m4a(script, mock_tracks, mocker, tmp_path):
    mocker.patch.object(script._probe, "get_tracks", return_value=mock_tracks)
    mock_ffmpeg = mocker.patch.object(script._ffmpeg, "run", return_value=True)
    mocker.patch.object(script._resolver, "resolve", return_value=tmp_path)

    files = [Path("test.mkv")]
    # Выбираем только DTS (ID 2), но просим M4A
    settings = {
        "mode": MODE_KEEP,
        "selected_tracks_per_file": {str(files[0]): [2]},
        "use_m4a_container_audio_only": True,
    }

    script.execute(files, settings)

    assert mock_ffmpeg.called
    out_path = mock_ffmpeg.call_args[1]["output_path"]
    assert out_path.suffix == ".m4a"

    args = mock_ffmpeg.call_args[1]["extra_args"]
    # DTS (ID 2) это вторая аудиодорожка -> index 1
    assert "0:a:1" in args


def test_keep_multiple_tracks_mkvmerge(script, mock_tracks, mocker, tmp_path):
    mocker.patch.object(script._probe, "get_tracks", return_value=mock_tracks)
    mock_ffmpeg = mocker.patch.object(script._ffmpeg, "run", return_value=True)
    mock_mkvmerge = mocker.patch.object(
        script._runner, "run", return_value=True
    )
    mocker.patch.object(script._resolver, "resolve", return_value=tmp_path)

    files = [Path("test.mkv")]
    # Выбираем видео и аудио (ID 0 и 1)
    settings = {
        "mode": MODE_KEEP,
        "selected_tracks_per_file": {str(files[0]): [0, 1]},
        "use_m4a_container_audio_only": True,
    }

    script.execute(files, settings)

    # Должен использоваться mkvmerge, так как дорожек больше одной
    assert not mock_ffmpeg.called
    assert mock_mkvmerge.called


def test_extension_mapping(script, mocker, tmp_path):
    codecs = [
        ("AAC", ".aac"),
        ("E-AC-3", ".eac3"),
        ("FLAC", ".flac"),
        ("Opus", ".opus"),
        ("Unknown", ".mka"),
    ]

    mock_ffmpeg = mocker.patch.object(script._ffmpeg, "run", return_value=True)
    mocker.patch.object(script._resolver, "resolve", return_value=tmp_path)

    for codec, expected_ext in codecs:
        track = TrackInfo(
            track_id=1,
            track_type="audio",
            codec=codec,
            language="und",
            name="Test",
            resolution="",
            channels=2,
        )
        mocker.patch.object(script._probe, "get_tracks", return_value=[track])

        files = [Path(f"test_{codec}.mkv")]
        settings = {
            "mode": MODE_KEEP,
            "selected_tracks_per_file": {str(files[0]): [1]},
            "use_m4a_container_audio_only": False,
        }

        script.execute(files, settings)
        out_path = mock_ffmpeg.call_args[1]["output_path"]
        assert out_path.suffix == expected_ext
