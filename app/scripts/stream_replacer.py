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

# Расширения по типам дорожек.
VIDEO_EXTENSIONS = {
    ".mkv", ".mp4", ".avi", ".mov",
    ".webm", ".hevc", ".h264", ".h265",
    ".264", ".265", ".vc1", ".m2v", ".avc",
}
AUDIO_EXTENSIONS = {
    ".aac", ".ac3", ".eac3", ".dts",
    ".flac", ".wav", ".mp3", ".m4a",
    ".ogg", ".mka", ".opus", ".wv",
    ".thd", ".truehd", ".mlp", ".dtshd",
    ".pcm", ".mp2", ".m2a",
}
SUBTITLE_EXTENSIONS = {
    ".srt", ".ass", ".ssa", ".sub",
    ".vtt", ".idx", ".sup",
}


class StreamReplacerScript(AbstractScript):
    """Скрипт подмены потоков в контейнере."""

    def __init__(self) -> None:
        """Инициализация скрипта."""
        self._runner = MKVMergeRunner()
        self._probe = MKVProbeRunner()
        self._ffmpeg = FFmpegRunner()
        self._ffprobe = FFProbeRunner()
        self._resolver = OutputResolver()
        logger.info(
            "Скрипт подмены потоков создан"
        )

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

    def execute(
        self,
        files: list[Path],
        settings: dict[str, Any],
        output_path: str | None = None,
        progress_callback: (
            ProgressCallback | None
        ) = None,
    ) -> list[str]:
        """Выполнить подмену потоков.

        Args:
            files: Список путей (контейнер).
            settings: Настройки с ключами:
                - container_path: путь к контейнеру
                - replacements: {track_id: путь}
            progress_callback: Callback прогресса.

        Returns:
            Результаты выполнения.
        """
        results: list[str] = []

        container_str = settings.get(
            "container_path", ""
        )
        if not container_str:
            msg = "❌ Контейнер не указан"
            results.append(msg)
            logger.error(msg)
            return results

        container = Path(container_str)
        if not container.exists():
            msg = (
                f"❌ Контейнер не найден: "
                f"{container.name}"
            )
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
                # Обратная совместимость (если в настройках просто путь)
                replacements[int(tid_str)] = {
                    "path": Path(data),
                    "src_id": 0
                }

        logger.info(
            "Подмена потоков: контейнер='%s', "
            "количество замен=%d",
            container.name,
            len(replacements),
        )

        # Выходной путь через резолвер
        target_dir = self._resolver.resolve(
            container, output_path
        )
        
        # Выходной путь с расширением оригинала
        output_file_path = self._get_safe_output_path(
            container,
            target_dir / f"{container.stem}{container.suffix}"
        )

        if (
            output_file_path.exists()
            and not SettingsManager()
            .overwrite_existing
        ):
            msg = (
                f"⏭ Пропущен (файл существует): "
                f"{output_file_path.name}"
            )
            results.append(msg)
            logger.info(msg)
            if progress_callback:
                progress_callback(1, 1, msg)
            return results

        is_mp4 = (
            container.suffix.lower() == ".mp4"
        )

        if is_mp4:
            msg = self._do_execute_mp4(
                container=container,
                output_path=output_file_path,
                replacements=replacements,
                progress_callback=(
                    progress_callback
                ),
            )
        else:
            msg = self._do_execute_mkv(
                container=container,
                output_path=output_file_path,
                replacements=replacements,
                progress_callback=(
                    progress_callback
                ),
            )

        results.append(msg)
        if progress_callback:
            progress_callback(1, 1, msg)
        return results

    # ------------------------------------------
    #  MKV — сборка через mkvmerge
    # ------------------------------------------

    def _do_execute_mkv(
        self,
        container: Path,
        output_path: Path,
        replacements: dict[int, Path],
        progress_callback: (
            ProgressCallback | None
        ) = None,
    ) -> str:
        """Подмена дорожек в MKV через mkvmerge.

        Args:
            container: Путь к контейнеру.
            output_path: Путь к результату.
            replacements: Словарь замен.
            progress_callback: Callback прогресса.

        Returns:
            Сообщение о результате.
        """
        try:
            all_tracks = self._probe.get_tracks(
                container
            )
        except Exception:
            logger.exception(
                "Ошибка анализа MKV '%s'",
                container.name,
            )
            return (
                f"❌ Ошибка анализа: "
                f"{container.name}"
            )

        replaced_ids = set(replacements.keys())
        container_args = (
            self._build_container_args(
                all_tracks, replaced_ids,
            )
        )

        inputs: list[dict[str, Any]] = [
            {
                "path": container,
                "args": container_args,
            }
        ]

        for track_id, repl_data in replacements.items():
            repl_path = repl_data["path"]
            src_id = repl_data["src_id"]
            track = self._find_track(
                all_tracks, track_id
            )
            if track is None:
                logger.warning(
                    "Дорожка ID=%d не найдена "
                    "в контейнере, пропущена",
                    track_id,
                )
                continue

            repl_args = (
                self._build_replacement_args(
                    track, src_id
                )
            )
            inputs.append(
                {
                    "path": repl_path,
                    "args": repl_args,
                }
            )
            logger.info(
                "Замена дорожки ID=%d (%s) "
                "на файл '%s' (внутренний ID: %d)",
                track_id,
                track.type_label,
                repl_path.name,
                src_id,
            )

        if progress_callback:
            progress_callback(
                0, 1,
                f"Сборка {container.stem}...",
            )

        success = self._runner.run(
            output_path=output_path,
            inputs=inputs,
            title=container.stem,
        )

        if success:
            msg = (
                f"✅ Собрано: {output_path.name}"
            )
            logger.info(
                "Успешно собран: '%s'",
                output_path.name,
            )
        else:
            msg = (
                f"❌ Ошибка сборки: "
                f"{output_path.name}"
            )
            logger.error(
                "Ошибка mkvmerge: '%s'",
                output_path.name,
            )
        return msg

    # ------------------------------------------
    #  MP4 — сборка через ffmpeg -map -c copy
    # ------------------------------------------

    def _do_execute_mp4(
        self,
        container: Path,
        output_path: Path,
        replacements: dict[int, Path],
        progress_callback: (
            ProgressCallback | None
        ) = None,
    ) -> str:
        """Подмена дорожек в MP4 через ffmpeg.

        Анализирует контейнер через ffprobe для
        получения корректных stream-индексов,
        затем использует ``-map`` и ``-c copy``.

        Args:
            container: Путь к контейнеру.
            output_path: Путь к результату.
            replacements: Словарь замен
                {track_id_из_UI: путь_файла}.
            progress_callback: Callback прогресса.

        Returns:
            Сообщение о результате.
        """
        # Получаем потоки через ffprobe
        try:
            streams = (
                self._ffprobe.get_streams(
                    container
                )
            )
        except Exception:
            logger.exception(
                "Ошибка анализа MP4 '%s'",
                container.name,
            )
            return (
                f"❌ Ошибка анализа: "
                f"{container.name}"
            )

        if progress_callback:
            progress_callback(
                0, 1,
                f"Сборка {container.stem}...",
            )

        # UI назначает замены по track_id из
        # mkvmerge, который для MP4 нумерует
        # дорожки с 0 в том же порядке.
        # ffprobe тоже нумерует с 0.
        # Сопоставляем по позиции: i-я дорожка
        # mkvmerge = i-й поток ffprobe.
        replaced_indices = set(
            replacements.keys()
        )

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
                extra_args.extend([
                    "-map", f"{input_idx}:{src_id}",
                ])
                logger.info(
                    "Замена потока #%d (%s) "
                    "→ '%s' (вход %d, стрим %d)",
                    sid,
                    stream.type_label,
                    repl_path.name,
                    input_idx,
                    src_id,
                )
                self._add_ffmpeg_metadata(
                    extra_args,
                    out_idx,
                    stream,
                )
                input_idx += 1
            else:
                extra_args.extend([
                    "-map", f"0:{sid}",
                ])
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
        )

        if success:
            msg = (
                f"✅ Собрано: {output_path.name}"
            )
            logger.info(
                "Успешно собран MP4: '%s'",
                output_path.name,
            )
        else:
            msg = (
                f"❌ Ошибка сборки: "
                f"{output_path.name}"
            )
            logger.error(
                "Ошибка ffmpeg: '%s'",
                output_path.name,
            )
        return msg

    # ------------------------------------------
    #  Общие вспомогательные методы
    # ------------------------------------------

    @staticmethod
    def _find_track(
        tracks: list[TrackInfo],
        track_id: int,
    ) -> TrackInfo | None:
        """Найти дорожку по ID.

        Args:
            tracks: Все дорожки контейнера.
            track_id: ID искомой дорожки.

        Returns:
            TrackInfo или None.
        """
        for t in tracks:
            if t.track_id == track_id:
                return t
        return None

    @staticmethod
    def _add_ffmpeg_metadata(
        args: list[str],
        stream_idx: int,
        track: TrackInfo | StreamInfo,
    ) -> None:
        """Добавить метаданные дорожки для ffmpeg.

        Args:
            args: Список аргументов для дополнения.
            stream_idx: Индекс выходного потока.
            track: Информация об оригинальной
                дорожке (TrackInfo или StreamInfo).
        """
        if (
            track.language
            and track.language != "und"
        ):
            args.extend([
                f"-metadata:s:{stream_idx}",
                f"language={track.language}",
            ])
        if track.name:
            args.extend([
                f"-metadata:s:{stream_idx}",
                f"title={track.name}",
            ])

    @staticmethod
    def _build_container_args(
        all_tracks: list[TrackInfo],
        replaced_ids: set[int],
    ) -> list[str]:
        """Построить аргументы mkvmerge.

        Исключает заменяемые дорожки по ID.

        Args:
            all_tracks: Все дорожки контейнера.
            replaced_ids: ID заменяемых дорожек.

        Returns:
            Список аргументов mkvmerge.
        """
        args: list[str] = []

        replaced_video: list[int] = []
        replaced_audio: list[int] = []
        replaced_subs: list[int] = []

        for track in all_tracks:
            if track.track_id in replaced_ids:
                if track.track_type == "video":
                    replaced_video.append(
                        track.track_id
                    )
                elif track.track_type == "audio":
                    replaced_audio.append(
                        track.track_id
                    )
                elif (
                    track.track_type == "subtitles"
                ):
                    replaced_subs.append(
                        track.track_id
                    )

        if replaced_video:
            ids_str = ",".join(
                str(i) for i in replaced_video
            )
            args.extend(
                ["--video-tracks", f"!{ids_str}"]
            )
            logger.debug(
                "Исключены видео-дорожки: %s",
                ids_str,
            )

        if replaced_audio:
            ids_str = ",".join(
                str(i) for i in replaced_audio
            )
            args.extend(
                ["--audio-tracks", f"!{ids_str}"]
            )
            logger.debug(
                "Исключены аудио-дорожки: %s",
                ids_str,
            )

        if replaced_subs:
            ids_str = ",".join(
                str(i) for i in replaced_subs
            )
            args.extend(
                [
                    "--subtitle-tracks",
                    f"!{ids_str}",
                ]
            )
            logger.debug(
                "Исключены дорожки субтитров: %s",
                ids_str,
            )

        return args

    @staticmethod
    def _build_replacement_args(
        track: TrackInfo,
        src_id: int = 0
    ) -> list[str]:
        """Аргументы mkvmerge для файла-замены.

        Args:
            track: Информация о заменяемой дорожке.
            src_id: ID дорожки в исходном файле-замене.

        Returns:
            Список аргументов mkvmerge.
        """
        args: list[str] = []

        # Ограничиваем только выбранной дорожкой из входного файла-замены.
        # mkvmerge нумерует дорожки каждого входа отдельно.
        args.extend(["--tracks", str(src_id)])

        if (
            track.language
            and track.language != "und"
        ):
            args.extend([
                "--language",
                f"{src_id}:{track.language}",
            ])

        if track.name:
            args.extend([
                "--track-name",
                f"{src_id}:{track.name}",
            ])

        return args

