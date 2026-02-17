# -*- coding: utf-8 -*-
"""Тесты для FFProbeRunner.

Покрытие: инициализация, успешный анализ,
ошибки (returncode, JSON, FileNotFoundError,
непредвиденные), маппинг типов потоков,
извлечение метаданных.
"""

import json
import subprocess
from pathlib import Path
from unittest.mock import MagicMock

import pytest

from app.infrastructure.ffprobe_runner import (
    FFProbeRunner,
    StreamInfo,
    _CODEC_TYPE_MAP,
)


# -----------------------------------------------
#  StreamInfo — модель данных
# -----------------------------------------------


class TestStreamInfo:
    """Тесты для dataclass StreamInfo."""

    def test_creation(self) -> None:
        """Проверка создания с полными данными."""
        info = StreamInfo(
            stream_index=0,
            stream_type="video",
            codec="hevc",
            language="und",
            name="",
        )
        assert info.stream_index == 0
        assert info.stream_type == "video"
        assert info.codec == "hevc"
        assert info.language == "und"
        assert info.name == ""

    def test_type_label_video(self) -> None:
        """type_label для видео."""
        info = StreamInfo(
            stream_index=0,
            stream_type="video",
            codec="h264",
            language="und",
            name="",
        )
        assert info.type_label == "Видео"

    def test_type_label_audio(self) -> None:
        """type_label для аудио."""
        info = StreamInfo(
            stream_index=1,
            stream_type="audio",
            codec="aac",
            language="rus",
            name="Русский",
        )
        assert info.type_label == "Аудио"

    def test_type_label_subtitles(self) -> None:
        """type_label для субтитров."""
        info = StreamInfo(
            stream_index=2,
            stream_type="subtitles",
            codec="ass",
            language="eng",
            name="English",
        )
        assert info.type_label == "Субтитры"

    def test_type_label_unknown(self) -> None:
        """type_label для неизвестного типа."""
        info = StreamInfo(
            stream_index=3,
            stream_type="data",
            codec="bin_data",
            language="und",
            name="",
        )
        assert info.type_label == "data"

    def test_frozen(self) -> None:
        """StreamInfo != мутабельный."""
        info = StreamInfo(
            stream_index=0,
            stream_type="video",
            codec="hevc",
            language="und",
            name="",
        )
        with pytest.raises(AttributeError):
            info.stream_index = 5  # type: ignore


# -----------------------------------------------
#  Маппинг типов
# -----------------------------------------------


class TestCodecTypeMap:
    """Тесты маппинга ffprobe codec_type."""

    def test_video_mapped(self) -> None:
        assert _CODEC_TYPE_MAP["video"] == "video"

    def test_audio_mapped(self) -> None:
        assert _CODEC_TYPE_MAP["audio"] == "audio"

    def test_subtitle_mapped(self) -> None:
        """ffprobe: 'subtitle' → 'subtitles'."""
        assert (
            _CODEC_TYPE_MAP["subtitle"]
            == "subtitles"
        )


# -----------------------------------------------
#  FFProbeRunner
# -----------------------------------------------


class TestFFProbeRunnerInit:
    """Тесты инициализации FFProbeRunner."""

    def test_init_sets_path(self, mocker) -> None:
        """Путь к ffprobe берётся из path_utils."""
        mocker.patch(
            "app.core.path_utils.get_binary_path",
            return_value="C:\\bin\\ffprobe.exe",
        )
        runner = FFProbeRunner()
        assert (
            runner._ffprobe_path
            == "C:\\bin\\ffprobe.exe"
        )


# Фабрика для JSON-ответа ffprobe.
def _ffprobe_json(
    streams: list[dict],
) -> str:
    """Сформировать JSON-ответ ffprobe."""
    return json.dumps({"streams": streams})


# Хелпер для создания потока ffprobe.
def _make_raw_stream(
    index: int = 0,
    codec_type: str = "video",
    codec_name: str = "hevc",
    language: str = "und",
    title: str = "",
) -> dict:
    """Сформировать словарь потока ffprobe."""
    tags = {}
    if language:
        tags["language"] = language
    if title:
        tags["title"] = title
    return {
        "index": index,
        "codec_type": codec_type,
        "codec_name": codec_name,
        "tags": tags,
    }


class TestFFProbeRunnerGetStreams:
    """Тесты метода get_streams."""

    def test_success_two_streams(
        self, mocker
    ) -> None:
        """Успешный парсинг: видео + аудио."""
        mocker.patch(
            "app.core.path_utils.get_binary_path",
            return_value="ffprobe",
        )
        runner = FFProbeRunner()

        raw = [
            _make_raw_stream(
                index=0,
                codec_type="video",
                codec_name="hevc",
                language="und",
            ),
            _make_raw_stream(
                index=1,
                codec_type="audio",
                codec_name="aac",
                language="rus",
                title="Русский",
            ),
        ]
        mocker.patch(
            "subprocess.run",
            return_value=MagicMock(
                returncode=0,
                stdout=_ffprobe_json(raw),
                stderr="",
            ),
        )

        streams = runner.get_streams(
            Path("test.mp4")
        )

        assert len(streams) == 2

        assert streams[0].stream_index == 0
        assert streams[0].stream_type == "video"
        assert streams[0].codec == "hevc"
        assert streams[0].language == "und"
        assert streams[0].name == ""

        assert streams[1].stream_index == 1
        assert streams[1].stream_type == "audio"
        assert streams[1].codec == "aac"
        assert streams[1].language == "rus"
        assert streams[1].name == "Русский"

    def test_subtitle_type_remapped(
        self, mocker
    ) -> None:
        """subtitle (ffprobe) → subtitles."""
        mocker.patch(
            "app.core.path_utils.get_binary_path",
            return_value="ffprobe",
        )
        runner = FFProbeRunner()

        raw = [
            _make_raw_stream(
                index=0,
                codec_type="subtitle",
                codec_name="ass",
            ),
        ]
        mocker.patch(
            "subprocess.run",
            return_value=MagicMock(
                returncode=0,
                stdout=_ffprobe_json(raw),
                stderr="",
            ),
        )

        streams = runner.get_streams(
            Path("test.mkv")
        )
        assert streams[0].stream_type == "subtitles"

    def test_empty_streams(self, mocker) -> None:
        """Файл без потоков."""
        mocker.patch(
            "app.core.path_utils.get_binary_path",
            return_value="ffprobe",
        )
        runner = FFProbeRunner()

        mocker.patch(
            "subprocess.run",
            return_value=MagicMock(
                returncode=0,
                stdout=_ffprobe_json([]),
                stderr="",
            ),
        )

        streams = runner.get_streams(
            Path("empty.mp4")
        )
        assert streams == []

    def test_missing_tags(self, mocker) -> None:
        """Поток без тегов → 'und' и пустое имя."""
        mocker.patch(
            "app.core.path_utils.get_binary_path",
            return_value="ffprobe",
        )
        runner = FFProbeRunner()

        raw_stream = {
            "index": 0,
            "codec_type": "video",
            "codec_name": "h264",
        }
        mocker.patch(
            "subprocess.run",
            return_value=MagicMock(
                returncode=0,
                stdout=json.dumps(
                    {"streams": [raw_stream]}
                ),
                stderr="",
            ),
        )

        streams = runner.get_streams(
            Path("no_tags.mp4")
        )
        assert streams[0].language == "und"
        assert streams[0].name == ""

    def test_nonzero_returncode_raises(
        self, mocker
    ) -> None:
        """Ненулевой returncode → RuntimeError."""
        mocker.patch(
            "app.core.path_utils.get_binary_path",
            return_value="ffprobe",
        )
        runner = FFProbeRunner()

        mocker.patch(
            "subprocess.run",
            return_value=MagicMock(
                returncode=1,
                stdout="",
                stderr="Invalid data",
            ),
        )

        with pytest.raises(RuntimeError):
            runner.get_streams(
                Path("broken.mp4")
            )

    def test_invalid_json_raises(
        self, mocker
    ) -> None:
        """Невалидный JSON → RuntimeError."""
        mocker.patch(
            "app.core.path_utils.get_binary_path",
            return_value="ffprobe",
        )
        runner = FFProbeRunner()

        mocker.patch(
            "subprocess.run",
            return_value=MagicMock(
                returncode=0,
                stdout="NOT JSON",
                stderr="",
            ),
        )

        with pytest.raises(RuntimeError):
            runner.get_streams(Path("bad.mp4"))

    def test_file_not_found_raises(
        self, mocker
    ) -> None:
        """ffprobe не найден → FileNotFoundError."""
        mocker.patch(
            "app.core.path_utils.get_binary_path",
            return_value="ffprobe",
        )
        runner = FFProbeRunner()

        mocker.patch(
            "subprocess.run",
            side_effect=FileNotFoundError(
                "ffprobe not found"
            ),
        )

        with pytest.raises(FileNotFoundError):
            runner.get_streams(
                Path("test.mp4")
            )

    def test_unexpected_exception_raises(
        self, mocker
    ) -> None:
        """Непредвиденное исключение → проброс."""
        mocker.patch(
            "app.core.path_utils.get_binary_path",
            return_value="ffprobe",
        )
        runner = FFProbeRunner()

        mocker.patch(
            "subprocess.run",
            side_effect=OSError("disk error"),
        )

        with pytest.raises(OSError):
            runner.get_streams(
                Path("test.mp4")
            )

    def test_unknown_codec_type_passthrough(
        self, mocker
    ) -> None:
        """Неизвестный codec_type проходит как есть."""
        mocker.patch(
            "app.core.path_utils.get_binary_path",
            return_value="ffprobe",
        )
        runner = FFProbeRunner()

        raw = [
            _make_raw_stream(
                index=0,
                codec_type="data",
                codec_name="bin_data",
            ),
        ]
        mocker.patch(
            "subprocess.run",
            return_value=MagicMock(
                returncode=0,
                stdout=_ffprobe_json(raw),
                stderr="",
            ),
        )

        streams = runner.get_streams(
            Path("data.mp4")
        )
        assert streams[0].stream_type == "data"

    def test_three_streams_order(
        self, mocker
    ) -> None:
        """Порядок потоков сохраняется."""
        mocker.patch(
            "app.core.path_utils.get_binary_path",
            return_value="ffprobe",
        )
        runner = FFProbeRunner()

        raw = [
            _make_raw_stream(
                index=0, codec_type="video"
            ),
            _make_raw_stream(
                index=1, codec_type="audio"
            ),
            _make_raw_stream(
                index=2,
                codec_type="subtitle",
            ),
        ]
        mocker.patch(
            "subprocess.run",
            return_value=MagicMock(
                returncode=0,
                stdout=_ffprobe_json(raw),
                stderr="",
            ),
        )

        streams = runner.get_streams(
            Path("full.mp4")
        )
        assert len(streams) == 3
        indices = [
            s.stream_index for s in streams
        ]
        assert indices == [0, 1, 2]
        types = [
            s.stream_type for s in streams
        ]
        assert types == [
            "video", "audio", "subtitles"
        ]
