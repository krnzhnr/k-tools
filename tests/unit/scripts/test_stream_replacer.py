# -*- coding: utf-8 -*-
"""Тесты для StreamReplacerScript.

Покрытие: свойства скрипта, execute() (все ветки),
_do_execute_mkv, _do_execute_mp4,
_find_track, _add_ffmpeg_metadata,
_build_container_args, _build_replacement_args.
"""

from unittest.mock import MagicMock

import pytest
from app.core.settings_manager import SettingsManager

from app.infrastructure.mkvprobe_runner import (
    TrackInfo,
)
from app.infrastructure.ffprobe_runner import (
    StreamInfo,
)
from app.core.constants import (
    VIDEO_EXTENSIONS,
    AUDIO_EXTENSIONS,
    SUBTITLE_EXTENSIONS,
)
from app.scripts.stream_replacer import (
    StreamReplacerScript,
)

# -----------------------------------------------
#  Фикстуры
# -----------------------------------------------


@pytest.fixture()
def script(mocker) -> StreamReplacerScript:
    """Экземпляр скрипта с замоканными runners."""
    mocker.patch(
        "app.core.path_utils.get_binary_path",
        return_value="bin",
    )
    return StreamReplacerScript()


@pytest.fixture()
def video_track() -> TrackInfo:
    """Видео-дорожка (TrackInfo)."""
    return TrackInfo(
        track_id=0,
        track_type="video",
        codec="HEVC",
        language="und",
        name="",
        resolution="1920x1080",
        channels=0,
    )


@pytest.fixture()
def audio_track() -> TrackInfo:
    """Аудио-дорожка с русским языком."""
    return TrackInfo(
        track_id=1,
        track_type="audio",
        codec="AAC",
        language="rus",
        name="Русский",
        resolution="",
        channels=2,
        is_default=True,
        is_forced=False,
        is_hearing_impaired=True,
    )


@pytest.fixture()
def subs_track() -> TrackInfo:
    """Дорожка субтитров."""
    return TrackInfo(
        track_id=2,
        track_type="subtitles",
        codec="SRT",
        language="eng",
        name="English",
        resolution="",
        channels=0,
    )


@pytest.fixture()
def video_stream() -> StreamInfo:
    """Видео-поток (StreamInfo)."""
    return StreamInfo(
        stream_index=0,
        stream_type="video",
        codec="hevc",
        language="und",
        name="",
    )


@pytest.fixture()
def audio_stream() -> StreamInfo:
    """Аудио-поток с русским языком."""
    return StreamInfo(
        stream_index=1,
        stream_type="audio",
        codec="aac",
        language="rus",
        name="Русский",
        is_default=True,
        is_forced=False,
        is_hearing_impaired=True,
    )


# -----------------------------------------------
#  Константы расширений
# -----------------------------------------------


class TestExtensionSets:
    """Проверка наборов расширений."""

    def test_video_extensions(self) -> None:
        assert ".mkv" in VIDEO_EXTENSIONS
        assert ".mp4" in VIDEO_EXTENSIONS
        assert ".hevc" in VIDEO_EXTENSIONS

    def test_audio_extensions(self) -> None:
        assert ".aac" in AUDIO_EXTENSIONS
        assert ".flac" in AUDIO_EXTENSIONS
        assert ".opus" in AUDIO_EXTENSIONS

    def test_subtitle_extensions(self) -> None:
        assert ".srt" in SUBTITLE_EXTENSIONS
        assert ".ass" in SUBTITLE_EXTENSIONS


# -----------------------------------------------
#  Свойства скрипта
# -----------------------------------------------


class TestScriptProperties:
    """Тесты свойств StreamReplacerScript."""

    def test_name(self, script) -> None:
        assert script.name == "Замена потоков"

    def test_description(self, script) -> None:
        assert "MKV/MP4" in script.description

    def test_icon_name(self, script) -> None:
        assert script.icon_name == "SYNC"

    def test_file_extensions(self, script) -> None:
        assert ".mkv" in script.file_extensions
        assert ".mp4" in script.file_extensions

    def test_use_custom_widget(self, script) -> None:
        assert script.use_custom_widget is True


# -----------------------------------------------
#  execute() — ранние возвраты
# -----------------------------------------------


class TestExecuteEarlyReturns:
    """Тесты ранних возвратов из execute()."""

    def test_no_container_path(self, script) -> None:
        """Пустая настройка container_path."""
        results = script.execute([], {})
        assert len(results) == 1
        assert "Контейнер не указан" in results[0]

    def test_container_not_found(self, script, mocker) -> None:
        """Контейнер не существует."""
        mocker.patch(
            "pathlib.Path.exists",
            return_value=False,
        )
        settings = {
            "container_path": "C:\\nope.mkv",
            "replacements": {"0": "a.aac"},
        }
        results = script.execute([], settings)
        assert len(results) == 1
        assert "не найден" in results[0]

    def test_no_replacements(self, script, mocker) -> None:
        """Пустой словарь замен."""
        mocker.patch(
            "pathlib.Path.exists",
            return_value=True,
        )
        settings = {
            "container_path": "C:\\vid.mkv",
            "replacements": {},
        }
        results = script.execute([], settings)
        assert len(results) == 1
        assert "Не назначено" in results[0]

    def test_skip_existing_file(self, script, mocker, tmp_path) -> None:
        """Пропуск при существующем выходе."""
        container = tmp_path / "video.mkv"
        container.touch()
        out_dir = tmp_path / "Completed"
        out_dir.mkdir()
        out_file = out_dir / "video.mkv"
        out_file.touch()

        # Настраиваем резолвер, чтобы он возвращал нашу временную папку
        mocker.patch.object(script._resolver, "resolve", return_value=out_dir)

        # Полностью подменяем SettingsManager в модуле скрипта
        mock_sm = mocker.patch("app.scripts.stream_replacer.SettingsManager")
        mock_sm.return_value.overwrite_existing = False

        cb = MagicMock()
        settings = {
            "container_path": str(container),
            "replacements": {"0": "a.aac"},
        }
        # Передаем только files и settings, progress_callback как именованный аргумент
        results = script.execute([], settings, progress_callback=cb)
        assert len(results) == 1
        assert "Пропущен" in results[0]
        cb.assert_called_once_with(1, 1, results[0], 0.0)


# -----------------------------------------------
#  execute() — делегирование MKV
# -----------------------------------------------


class TestExecuteMKV:
    """Тесты execute() для MKV-контейнера."""

    def test_mkv_success(
        self,
        script,
        mocker,
        tmp_path,
        video_track,
        audio_track,
    ) -> None:
        """Успешная сборка MKV."""
        container = tmp_path / "movie.mkv"
        container.touch()

        mocker.patch.object(
            script._probe,
            "get_tracks",
            return_value=[
                video_track,
                audio_track,
            ],
        )
        mocker.patch.object(
            script._runner,
            "run",
            return_value=True,
        )

        repl_file = tmp_path / "new_audio.aac"
        repl_file.touch()

        settings = {
            "container_path": str(container),
            "replacements": {
                "1": str(repl_file),
            },
        }
        cb = MagicMock()
        results = script.execute([], settings, progress_callback=cb)

        assert len(results) == 1
        assert "Собрано" in results[0]
        script._runner.run.assert_called_once()

        # Проверка track-order в extra_args
        call_kwargs = script._runner.run.call_args[1]
        assert "extra_args" in call_kwargs
        extra = call_kwargs["extra_args"]
        assert "--track-order" in extra
        # 0:0 - видео (оригинал), 1:0 - аудио (замена на месте оригинала)
        assert "0:0,1:0" in extra[extra.index("--track-order") + 1]

        # Проверка вызова callback
        assert cb.call_count == 2
        cb.assert_any_call(0, 1, f"Сборка {container.stem}...", 0.0)
        cb.assert_any_call(1, 1, results[0], 100.0)

    def test_mkv_runner_failure(
        self,
        script,
        mocker,
        tmp_path,
        video_track,
    ) -> None:
        """Ошибка mkvmerge."""
        container = tmp_path / "movie.mkv"
        container.touch()

        mocker.patch.object(
            script._probe,
            "get_tracks",
            return_value=[video_track],
        )
        mocker.patch.object(
            script._runner,
            "run",
            return_value=False,
        )

        repl = tmp_path / "v.hevc"
        repl.touch()

        settings = {
            "container_path": str(container),
            "replacements": {
                "0": str(repl),
            },
        }
        results = script.execute([], settings)
        assert "Ошибка сборки" in results[0]

    def test_mkv_probe_failure(
        self,
        script,
        mocker,
        tmp_path,
    ) -> None:
        """Ошибка mkvprobe → ошибка анализа."""
        container = tmp_path / "movie.mkv"
        container.touch()

        mocker.patch.object(
            script._probe,
            "get_tracks",
            side_effect=RuntimeError("fail"),
        )

        settings = {
            "container_path": str(container),
            "replacements": {"0": "v.hevc"},
        }
        results = script.execute([], settings)
        assert "Ошибка анализа" in results[0]

    def test_mkv_track_not_found_skipped(
        self,
        script,
        mocker,
        tmp_path,
        video_track,
    ) -> None:
        """Замена несуществующей дорожки."""
        container = tmp_path / "movie.mkv"
        container.touch()

        mocker.patch.object(
            script._probe,
            "get_tracks",
            return_value=[video_track],
        )
        mocker.patch.object(
            script._runner,
            "run",
            return_value=True,
        )

        repl = tmp_path / "mystery.aac"
        repl.touch()

        settings = {
            "container_path": str(container),
            "replacements": {
                "99": str(repl),
            },
        }
        results = script.execute([], settings)
        assert "Собрано" in results[0]

        # inputs имеет только контейнер (без замен)
        call_args = script._runner.run.call_args[1]
        assert len(call_args["inputs"]) == 1


# -----------------------------------------------
#  execute() — делегирование MP4
# -----------------------------------------------


class TestExecuteMP4:
    """Тесты execute() для MP4-контейнера."""

    def test_mp4_success(
        self,
        script,
        mocker,
        tmp_path,
        video_stream,
        audio_stream,
    ) -> None:
        """Успешная сборка MP4."""
        container = tmp_path / "movie.mp4"
        container.touch()

        mocker.patch.object(
            script._ffprobe,
            "get_streams",
            return_value=[
                video_stream,
                audio_stream,
            ],
        )
        mocker.patch.object(
            script._ffmpeg,
            "run",
            return_value=True,
        )

        repl_file = tmp_path / "new_audio.aac"
        repl_file.touch()

        settings = {
            "container_path": str(container),
            "replacements": {
                "1": str(repl_file),
            },
        }
        cb = MagicMock()
        results = script.execute([], settings, progress_callback=cb)

        assert len(results) == 1
        assert "Собрано" in results[0]
        script._ffmpeg.run.assert_called_once()

        # Проверяем аргументы ffmpeg
        call_kwargs = script._ffmpeg.run.call_args[1]
        extra_args = call_kwargs["extra_args"]
        assert "-map" in extra_args
        assert "-c" in extra_args
        assert "copy" in extra_args

    def test_mp4_ffmpeg_failure(
        self,
        script,
        mocker,
        tmp_path,
        video_stream,
    ) -> None:
        """Ошибка ffmpeg."""
        container = tmp_path / "movie.mp4"
        container.touch()

        mocker.patch.object(
            script._ffprobe,
            "get_streams",
            return_value=[video_stream],
        )
        mocker.patch.object(
            script._ffmpeg,
            "run",
            return_value=False,
        )

        settings = {
            "container_path": str(container),
            "replacements": {
                "0": str(tmp_path / "v.hevc"),
            },
        }
        results = script.execute([], settings)
        assert "Ошибка сборки" in results[0]

    def test_mp4_ffprobe_failure(
        self,
        script,
        mocker,
        tmp_path,
    ) -> None:
        """Ошибка ffprobe → ошибка анализа."""
        container = tmp_path / "movie.mp4"
        container.touch()

        mocker.patch.object(
            script._ffprobe,
            "get_streams",
            side_effect=RuntimeError("fail"),
        )

        settings = {
            "container_path": str(container),
            "replacements": {"0": "v.hevc"},
        }
        results = script.execute([], settings)
        assert "Ошибка анализа" in results[0]

    def test_mp4_maps_correct_streams(
        self,
        script,
        mocker,
        tmp_path,
        video_stream,
        audio_stream,
    ) -> None:
        """Проверка корректного маппинга потоков."""
        container = tmp_path / "movie.mp4"
        container.touch()

        mocker.patch.object(
            script._ffprobe,
            "get_streams",
            return_value=[
                video_stream,
                audio_stream,
            ],
        )
        mocker.patch.object(
            script._ffmpeg,
            "run",
            return_value=True,
        )

        repl = tmp_path / "new.aac"
        repl.touch()

        settings = {
            "container_path": str(container),
            "replacements": {
                "1": str(repl),
            },
        }
        script.execute([], settings)

        call_kwargs = script._ffmpeg.run.call_args[1]
        extra = call_kwargs["extra_args"]

        # Видео из оригинала
        map_idx = extra.index("-map")
        assert extra[map_idx + 1] == "0:0"

        # Аудио из замены (input 1)
        second_map = extra.index("-map", map_idx + 1)
        assert extra[second_map + 1] == "1:0"

    def test_mp4_metadata_added(
        self,
        script,
        mocker,
        tmp_path,
        video_stream,
        audio_stream,
    ) -> None:
        """Метаданные добавлены при замене."""
        container = tmp_path / "movie.mp4"
        container.touch()

        mocker.patch.object(
            script._ffprobe,
            "get_streams",
            return_value=[
                video_stream,
                audio_stream,
            ],
        )
        mocker.patch.object(
            script._ffmpeg,
            "run",
            return_value=True,
        )

        repl = tmp_path / "new.aac"
        repl.touch()

        settings = {
            "container_path": str(container),
            "replacements": {
                "1": str(repl),
            },
        }
        script.execute([], settings)

        call_kwargs = script._ffmpeg.run.call_args[1]
        extra = call_kwargs["extra_args"]

        # language=rus, title=Русский
        assert "-metadata:s:1" in extra
        assert "language=rus" in extra
        assert "title=Русский" in extra

        # Новые флаги для MP4 (disposition)
        assert "-disposition:s:1" in extra
        idx = extra.index("-disposition:s:1")
        assert "default+hearing_impaired" in extra[idx + 1]

    def test_mp4_no_callback(
        self,
        script,
        mocker,
        tmp_path,
        video_stream,
    ) -> None:
        """Без callback — без ошибок."""
        container = tmp_path / "movie.mp4"
        container.touch()

        mocker.patch.object(
            script._ffprobe,
            "get_streams",
            return_value=[video_stream],
        )
        mocker.patch.object(
            script._ffmpeg,
            "run",
            return_value=True,
        )

        settings = {
            "container_path": str(container),
            "replacements": {
                "0": str(tmp_path / "v.hevc"),
            },
        }
        results = script.execute([], settings, progress_callback=None)
        assert "Собрано" in results[0]

    def test_mp4_output_retains_extension(
        self,
        script,
        mocker,
        tmp_path,
        video_stream,
    ) -> None:
        """Выход сохраняет расширение .mp4."""
        container = tmp_path / "movie.mp4"
        container.touch()

        mocker.patch.object(
            script._ffprobe,
            "get_streams",
            return_value=[video_stream],
        )
        mocker.patch.object(
            script._ffmpeg,
            "run",
            return_value=True,
        )

        settings = {
            "container_path": str(container),
            "replacements": {
                "0": str(tmp_path / "v.hevc"),
            },
        }
        script.execute([], settings)

        call_kwargs = script._ffmpeg.run.call_args[1]
        out = call_kwargs["output_path"]
        assert str(out).endswith(".mp4")

    def test_mp4_extra_inputs_order(
        self,
        script,
        mocker,
        tmp_path,
    ) -> None:
        """Порядок входных файлов при 2 заменах."""
        container = tmp_path / "multi.mp4"
        container.touch()

        streams = [
            StreamInfo(0, "video", "hevc", "und", ""),
            StreamInfo(1, "audio", "aac", "rus", ""),
            StreamInfo(2, "audio", "aac", "eng", ""),
        ]

        mocker.patch.object(
            script._ffprobe,
            "get_streams",
            return_value=streams,
        )
        mocker.patch.object(
            script._ffmpeg,
            "run",
            return_value=True,
        )

        r1 = tmp_path / "rus.aac"
        r2 = tmp_path / "eng.aac"
        r1.touch()
        r2.touch()

        settings = {
            "container_path": str(container),
            "replacements": {
                "1": str(r1),
                "2": str(r2),
            },
        }
        script.execute([], settings)

        call_kwargs = script._ffmpeg.run.call_args[1]
        extra = call_kwargs["extra_args"]

        # Дополнительные -i перед -map
        first_i = extra.index("-i")
        assert extra[first_i + 1] == str(r1)
        second_i = extra.index("-i", first_i + 1)
        assert extra[second_i + 1] == str(r2)


# -----------------------------------------------
#  _find_track
# -----------------------------------------------


class TestFindTrack:
    """Тесты _find_track."""

    def test_found(self, video_track, audio_track) -> None:
        tracks = [video_track, audio_track]
        result = StreamReplacerScript._find_track(tracks, 1)
        assert result is audio_track

    def test_not_found(self, video_track) -> None:
        result = StreamReplacerScript._find_track([video_track], 99)
        assert result is None

    def test_empty_list(self) -> None:
        result = StreamReplacerScript._find_track([], 0)
        assert result is None


# -----------------------------------------------
#  _add_ffmpeg_metadata
# -----------------------------------------------


class TestAddFFmpegMetadata:
    """Тесты _add_ffmpeg_metadata."""

    def test_with_language_and_name(
        self,
        audio_track,
    ) -> None:
        """Язык + название + диспозиции → 6 аргументов."""
        args: list[str] = []
        StreamReplacerScript._add_ffmpeg_metadata(args, 1, audio_track)
        assert "-metadata:s:1" in args
        assert "language=rus" in args
        assert "title=Русский" in args
        assert "-disposition:s:1" in args
        assert "default+hearing_impaired" in args
        assert len(args) == 6

    def test_und_language_skipped(self) -> None:
        """und не добавляется в метаданные."""
        track = TrackInfo(
            track_id=0,
            track_type="video",
            codec="HEVC",
            language="und",
            name="",
            resolution="1920x1080",
            channels=0,
        )
        args: list[str] = []
        StreamReplacerScript._add_ffmpeg_metadata(args, 0, track)
        assert len(args) == 0

    def test_empty_language_skipped(self) -> None:
        """Пустая строка языка не добавляется."""
        track = TrackInfo(
            track_id=0,
            track_type="video",
            codec="HEVC",
            language="",
            name="",
            resolution="1920x1080",
            channels=0,
        )
        args: list[str] = []
        StreamReplacerScript._add_ffmpeg_metadata(args, 0, track)
        assert len(args) == 0

    def test_only_name(self) -> None:
        """Только название, без языка."""
        track = TrackInfo(
            track_id=0,
            track_type="video",
            codec="HEVC",
            language="und",
            name="Main",
            resolution="1920x1080",
            channels=0,
        )
        args: list[str] = []
        StreamReplacerScript._add_ffmpeg_metadata(args, 0, track)
        assert len(args) == 2
        assert "title=Main" in args

    def test_with_stream_info(
        self,
        audio_stream,
    ) -> None:
        """Работает и со StreamInfo."""
        args: list[str] = []
        StreamReplacerScript._add_ffmpeg_metadata(args, 2, audio_stream)
        assert "-metadata:s:2" in args
        assert "language=rus" in args
        assert "title=Русский" in args


# -----------------------------------------------
#  _build_container_args
# -----------------------------------------------


class TestBuildContainerArgs:
    """Тесты _build_container_args."""

    def test_exclude_video(
        self,
        video_track,
        audio_track,
    ) -> None:
        """Исключение видео → --no-video."""
        args = StreamReplacerScript._build_container_args(
            [video_track, audio_track],
            {0},
        )
        assert "--no-video" in args
        # Аудио остаётся
        assert "--audio-tracks" in args
        idx = args.index("--audio-tracks")
        assert args[idx + 1] == "1"

    def test_exclude_audio(
        self,
        video_track,
        audio_track,
    ) -> None:
        """Исключение аудио → --no-audio."""
        args = StreamReplacerScript._build_container_args(
            [video_track, audio_track],
            {1},
        )
        assert "--no-audio" in args
        # Видео остаётся
        assert "--video-tracks" in args
        idx = args.index("--video-tracks")
        assert args[idx + 1] == "0"

    def test_exclude_subtitles(
        self,
        video_track,
        subs_track,
    ) -> None:
        """Исключение субтитров → --no-subtitles."""
        args = StreamReplacerScript._build_container_args(
            [video_track, subs_track],
            {2},
        )
        assert "--no-subtitles" in args
        # Видео остаётся
        assert "--video-tracks" in args

    def test_exclude_all_types(
        self,
        video_track,
        audio_track,
        subs_track,
    ) -> None:
        """Исключение всех типов → --no-xxx."""
        args = StreamReplacerScript._build_container_args(
            [
                video_track,
                audio_track,
                subs_track,
            ],
            {0, 1, 2},
        )
        assert "--no-video" in args
        assert "--no-audio" in args
        assert "--no-subtitles" in args

    def test_no_exclusions(
        self,
        video_track,
    ) -> None:
        """Пустое множество → позитивный выбор."""
        args = StreamReplacerScript._build_container_args([video_track], set())
        assert "--video-tracks" in args
        idx = args.index("--video-tracks")
        assert args[idx + 1] == "0"

    def test_multiple_audio_tracks(self) -> None:
        """Несколько аудио → --no-audio."""
        t1 = TrackInfo(1, "audio", "AAC", "rus", "", resolution="", channels=2)
        t2 = TrackInfo(2, "audio", "AC3", "eng", "", resolution="", channels=6)
        args = StreamReplacerScript._build_container_args([t1, t2], {1, 2})
        assert "--no-audio" in args


# -----------------------------------------------
#  _build_replacement_args
# -----------------------------------------------


class TestBuildReplacementArgs:
    """Тесты _build_replacement_args."""

    def test_with_language_and_name(
        self,
        audio_track,
    ) -> None:
        """Язык + название + исключение лишних типов."""
        args = StreamReplacerScript._build_replacement_args(audio_track)
        assert "--audio-tracks" in args
        assert "0" in args
        assert "--no-video" in args
        assert "--no-subtitles" in args
        assert "--no-chapters" in args
        assert "--language" in args
        assert "0:rus" in args
        assert "--track-name" in args
        assert "0:Русский" in args

        # Проверка новых флагов MKV
        assert "--default-track" in args
        assert "0:yes" in args
        assert "--hearing-impaired-flag" in args
        assert "0:yes" in args
        assert "--forced-display-flag" in args
        assert "0:no" in args

    def test_und_language_skipped(
        self,
        video_track,
    ) -> None:
        """und не добавляет --language."""
        args = StreamReplacerScript._build_replacement_args(video_track)
        assert "--language" not in args
        assert "--video-tracks" in args
        assert "--no-audio" in args
        assert "--no-subtitles" in args

    def test_no_name(self) -> None:
        """Без названия — нет --track-name."""
        track = TrackInfo(
            0, "audio", "AAC", "jpn", "", resolution="", channels=2
        )
        args = StreamReplacerScript._build_replacement_args(track)
        assert "--audio-tracks" in args
        assert "--language" in args
        assert "0:jpn" in args
        assert "--track-name" not in args

    def test_empty_language_and_name(
        self,
    ) -> None:
        """Пустые язык и имя → только выбор и исключение."""
        track = TrackInfo(
            0, "video", "HEVC", "", "", resolution="1920x1080", channels=0
        )
        args = StreamReplacerScript._build_replacement_args(track)
        assert "--video-tracks" in args
        assert "--no-audio" in args
        assert "--no-subtitles" in args
        assert "--language" not in args
        assert "--track-name" not in args

    def test_custom_src_id(self, audio_track) -> None:
        """Проверка передачи src_id."""
        args = StreamReplacerScript._build_replacement_args(
            audio_track, src_id=5
        )
        assert "--audio-tracks" in args
        assert "5" in args
        assert "5:rus" in args


# -----------------------------------------------
#  Интеграция: создание директории Completed
# -----------------------------------------------


class TestOutputDirectory:
    """Тесты создания выходной директории."""

    def test_completed_dir_created(
        self,
        script,
        mocker,
        tmp_path,
    ) -> None:
        """Completed создаётся если не существует."""
        container = tmp_path / "file.mkv"
        container.touch()

        tracks = [
            TrackInfo(
                0,
                "video",
                "H264",
                "und",
                "",
                resolution="1920x1080",
                channels=0,
            ),
        ]
        mocker.patch.object(
            script._probe,
            "get_tracks",
            return_value=tracks,
        )
        mocker.patch.object(
            script._runner,
            "run",
            return_value=True,
        )

        # Настраиваем резолвер
        mocker.patch.object(
            SettingsManager,
            "use_auto_subfolder",
            new_callable=mocker.PropertyMock,
            return_value=True,
        )
        mocker.patch.object(
            SettingsManager,
            "default_output_subfolder",
            new_callable=mocker.PropertyMock,
            return_value="Completed",
        )

        repl = tmp_path / "v.hevc"
        repl.touch()

        settings = {
            "container_path": str(container),
            "replacements": {
                "0": str(repl),
            },
        }
        script.execute([], settings)

        completed = tmp_path / "Completed"
        assert completed.exists()
        assert completed.is_dir()

    def test_completed_dir_already_exists(
        self,
        script,
        mocker,
        tmp_path,
    ) -> None:
        """Не падает если Completed уже есть."""
        container = tmp_path / "file.mkv"
        container.touch()
        (tmp_path / "Completed").mkdir()

        tracks = [
            TrackInfo(
                0,
                "video",
                "H264",
                "und",
                "",
                resolution="1920x1080",
                channels=0,
            ),
        ]
        mocker.patch.object(
            script._probe,
            "get_tracks",
            return_value=tracks,
        )
        mocker.patch.object(
            script._runner,
            "run",
            return_value=True,
        )

        # Настраиваем резолвер
        mocker.patch.object(
            SettingsManager,
            "use_auto_subfolder",
            new_callable=mocker.PropertyMock,
            return_value=True,
        )
        mocker.patch.object(
            SettingsManager,
            "default_output_subfolder",
            new_callable=mocker.PropertyMock,
            return_value="Completed",
        )

        repl = tmp_path / "v.hevc"
        repl.touch()

        settings = {
            "container_path": str(container),
            "replacements": {
                "0": str(repl),
            },
        }
        results = script.execute([], settings)
        assert "Собрано" in results[0]
