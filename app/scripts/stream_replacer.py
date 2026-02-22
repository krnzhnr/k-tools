"""Скрипт подмены потоков MKV/MP4.

Позволяет заменять отдельные дорожки в контейнере
на внешние файлы (аудио, видео, субтитры).
MKV собирается через mkvmerge, MP4 — через ffmpeg.
"""

import logging
from pathlib import Path
from typing import Any

from app.core.abstract_script import (
    AbstractScript,
    ProgressCallback,
)
from app.core.settings_manager import SettingsManager
from app.core.output_resolver import OutputResolver
from app.infrastructure.mkvmerge_runner import (
    MKVMergeRunner,
)
from app.infrastructure.mkvprobe_runner import (
    MKVProbeRunner,
    TrackInfo,
)
from app.infrastructure.ffmpeg_runner import (
    FFmpegRunner,
)
from app.infrastructure.ffprobe_runner import (
    FFProbeRunner,
    StreamInfo,
)

logger = logging.getLogger(__name__)

from app.core.constants import VIDEO_EXTENSIONS, AUDIO_EXTENSIONS, SUBTITLE_EXTENSIONS
from app.core.settings_manager import SettingsManager

class StreamReplacerScript(AbstractScript):
    """Скрипт подмены потоков в контейнере."""

    def __init__(self) -> None:
        """Инициализация скрипта."""
        self._runner = MKVMergeRunner()
        self._probe = MKVProbeRunner()
        self._ffmpeg = FFmpegRunner()
        self._ffprobe = FFProbeRunner()
        self._resolver = OutputResolver()
        logger.info("Скрипт подмены потоков создан")

    @property
    def category(self) -> str:
        """Категория скрипта."""
        return "Муксинг"

    @property
    def name(self) -> str:
        """Отображаемое имя скрипта."""
        return "Подмена потоков (MKV/MP4)"

    @property
    def description(self) -> str:
        """Описание скрипта."""
        return (
            "Заменяет дорожки в MKV/MP4 "
            "на внешние файлы (видео, аудио, "
            "субтитры)."
        )

    @property
    def icon_name(self) -> str:
        """Имя иконки FluentIcon."""
        return "SYNC"

    @property
    def file_extensions(self) -> list[str]:
        """Допустимые расширения файлов."""
        return [".mkv", ".mp4"]

    @property
    def use_custom_widget(self) -> bool:
        """Использовать кастомный виджет."""
        return True

    def execute_single(
        self,
        file: Path,
        settings: dict[str, Any],
        output_path: str | None = None,
    ) -> list[str]:
        """Не используется в Подмене потоков (переопределен execute)."""
        raise NotImplementedError("StreamReplacerScript использует групповую обработку в execute()")

    def execute(
        self,
        files: list[Path],
        settings: dict[str, Any],
        output_path: str | None = None,
        progress_callback: ProgressCallback | None = None,
    ) -> list[str]:
        """Выполнить подмену потоков."""
        results: list[str] = []
        overwrite = SettingsManager().overwrite_existing

        container_str = settings.get("container_path", "")
        if not container_str:
            msg = "❌ Контейнер не указан"
            results.append(msg)
            logger.error(msg)
            return results

        container = Path(container_str)
        if not container.exists():
            msg = f"❌ Контейнер не найден: {container.name}"
            results.append(msg)
            logger.error(msg)
            return results

        # Словарь замен: {track_id_цели: {"path": str, "src_id": int}}
        raw_replacements: dict[str, Any] = settings.get("replacements", {})
        if not raw_replacements:
            msg = "❌ Не назначено ни одной замены"
            results.append(msg)
            logger.error(msg)
            return results

        replacements: dict[int, dict[str, Any]] = {}
        for tid_str, data in raw_replacements.items():
            if isinstance(data, dict):
                replacements[int(tid_str)] = {
                    "path": Path(data["path"]),
                    "src_id": int(data.get("src_id", 0))
                }
            else:
                replacements[int(tid_str)] = {
                    "path": Path(data),
                    "src_id": 0
                }

        logger.info(
            "Подмена потоков: контейнер='%s', количество замен=%d",
            container.name, len(replacements)
        )

        target_dir = self._resolver.resolve(container, output_path)
        output_file_path = self._get_safe_output_path(
            container, target_dir / f"{container.stem}{container.suffix}"
        )

        if output_file_path.exists() and not overwrite:
            msg = f"⏭ Пропущен (файл существует): {output_file_path.name}"
            results.append(msg)
            logger.info(msg)
            if progress_callback:
                progress_callback(1, 1, msg)
            return results

        is_mp4 = container.suffix.lower() == ".mp4"

        if is_mp4:
            msg = self._do_execute_mp4(
                container=container,
                output_path=output_file_path,
                replacements=replacements,
                overwrite=overwrite,
                progress_callback=progress_callback,
            )
        else:
            msg = self._do_execute_mkv(
                container=container,
                output_path=output_file_path,
                replacements=replacements,
                overwrite=overwrite,
                progress_callback=progress_callback,
            )

        results.append(msg)
        if progress_callback:
            progress_callback(1, 1, msg)
        return results

    def _do_execute_mkv(
        self,
        container: Path,
        output_path: Path,
        replacements: dict[int, dict[str, Any]],
        overwrite: bool = False,
        progress_callback: ProgressCallback | None = None,
    ) -> str:
        """Подмена дорожек в MKV через mkvmerge."""
        try:
            all_tracks = self._probe.get_tracks(container)
        except Exception:
            logger.exception("Ошибка анализа MKV '%s'", container.name)
            return f"❌ Ошибка анализа: {container.name}"

        replaced_ids = set(replacements.keys())
        container_args = self._build_container_args(all_tracks, replaced_ids)

        inputs: list[dict[str, Any]] = [
            {
                "path": container,
                "args": container_args,
            }
        ]

        for track_id, repl_data in replacements.items():
            repl_path = repl_data["path"]
            src_id = repl_data["src_id"]
            track = self._find_track(all_tracks, track_id)
            if track is None:
                continue

            repl_args = self._build_replacement_args(track, src_id)
            inputs.append({
                "path": repl_path,
                "args": repl_args,
            })

        if progress_callback:
            progress_callback(0, 1, f"Сборка {container.stem}...")

        success = self._runner.run(
            output_path=output_path,
            inputs=inputs,
            title=container.stem,
            overwrite=overwrite
        )

        if success:
            msg = f"✅ Собрано: {output_path.name}"
        else:
            msg = f"❌ Ошибка сборки: {output_path.name}"
        return msg

    def _do_execute_mp4(
        self,
        container: Path,
        output_path: Path,
        replacements: dict[int, dict[str, Any]],
        overwrite: bool = False,
        progress_callback: ProgressCallback | None = None,
    ) -> str:
        """Подмена дорожек в MP4 через ffmpeg."""
        try:
            streams = self._ffprobe.get_streams(container)
        except Exception:
            logger.exception("Ошибка анализа MP4 '%s'", container.name)
            return f"❌ Ошибка анализа: {container.name}"

        if progress_callback:
            progress_callback(0, 1, f"Сборка {container.stem}...")

        replaced_indices = set(replacements.keys())
        extra_args: list[str] = []
        extra_inputs: list[Path] = []
        input_idx = 1
        out_idx = 0

        for stream in streams:
            sid = stream.stream_index
            if sid in replaced_indices:
                repl_data = replacements[sid]
                repl_path = repl_data["path"]
                src_id = repl_data["src_id"]
                
                extra_inputs.append(repl_path)
                extra_args.extend(["-map", f"{input_idx}:{src_id}"])
                self._add_ffmpeg_metadata(extra_args, out_idx, stream)
                input_idx += 1
            else:
                extra_args.extend(["-map", f"0:{sid}"])
            out_idx += 1

        extra_args.extend(["-c", "copy"])
        input_args: list[str] = []
        for inp in extra_inputs:
            input_args.extend(["-i", str(inp)])

        full_args = input_args + extra_args

        success = self._ffmpeg.run(
            input_path=container,
            output_path=output_path,
            extra_args=full_args,
            overwrite=overwrite
        )

        if success:
            msg = f"✅ Собрано: {output_path.name}"
        else:
            msg = f"❌ Ошибка сборки: {output_path.name}"
        return msg

    @staticmethod
    def _find_track(tracks: list[TrackInfo], track_id: int) -> TrackInfo | None:
        """Найти дорожку по ID."""
        for t in tracks:
            if t.track_id == track_id:
                return t
        return None

    @staticmethod
    def _add_ffmpeg_metadata(args: list[str], stream_idx: int, track: Any) -> None:
        """Добавить метаданные дорожки для ffmpeg."""
        if track.language and track.language != "und":
            args.extend([f"-metadata:s:{stream_idx}", f"language={track.language}"])
        if track.name:
            args.extend([f"-metadata:s:{stream_idx}", f"title={track.name}"])

    @staticmethod
    def _build_container_args(all_tracks: list[TrackInfo], replaced_ids: set[int]) -> list[str]:
        """Построить аргументы mkvmerge (исключение дорожек)."""
        args: list[str] = []
        replaced_video, replaced_audio, replaced_subs = [], [], []

        for track in all_tracks:
            if track.track_id in replaced_ids:
                if track.track_type == "video":
                    replaced_video.append(track.track_id)
                elif track.track_type == "audio":
                    replaced_audio.append(track.track_id)
                elif track.track_type == "subtitles":
                    replaced_subs.append(track.track_id)

        if replaced_video:
            args.extend(["--video-tracks", f"!{','.join(map(str, replaced_video))}"])
        if replaced_audio:
            args.extend(["--audio-tracks", f"!{','.join(map(str, replaced_audio))}"])
        if replaced_subs:
            args.extend(["--subtitle-tracks", f"!{','.join(map(str, replaced_subs))}"])

        return args

    @staticmethod
    def _build_replacement_args(track: TrackInfo, src_id: int = 0) -> list[str]:
        """Аргументы mkvmerge для файла-замены."""
        args = ["--tracks", str(src_id)]
        if track.language and track.language != "und":
            args.extend(["--language", f"{src_id}:{track.language}"])
        if track.name:
            args.extend(["--track-name", f"{src_id}:{track.name}"])
        return args

