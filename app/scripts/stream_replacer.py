"""Скрипт подмены потоков MKV/MP4.

Позволяет заменять отдельные дорожки в контейнере
на внешние файлы (аудио, видео, субтитры).
MKV собирается через mkvmerge, MP4 — через ffmpeg.
"""

import logging
from pathlib import Path
from typing import Any

from app.core.constants import ScriptCategory, ScriptMetadata
from app.core.abstract_script import (
    AbstractScript,
    ProgressCallback,
    SettingField,
    SettingType,
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
)

logger = logging.getLogger(__name__)


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
        return ScriptCategory.CONTAINERS

    @property
    def name(self) -> str:
        """Отображаемое имя скрипта."""
        return ScriptMetadata.STREAM_REPL_NAME

    @property
    def description(self) -> str:
        """Описание скрипта."""
        return ScriptMetadata.STREAM_REPL_DESC

    @property
    def icon_name(self) -> str:
        """Имя иконки FluentIcon."""
        return "SYNC"

    @property
    def file_extensions(self) -> list[str]:
        """Допустимые расширения файлов."""
        return [".mkv", ".mp4"]

    @property
    def settings_schema(self) -> list[SettingField]:
        """Схема настроек скрипта."""
        from app.core.abstract_script import SettingField

        return [
            SettingField(
                key="overwrite_source",
                label="Подменить оригинал финальным файлом",
                setting_type=SettingType.CHECKBOX,
                default=False,
            ),
            SettingField(
                key="delete_source",
                label="Удалить оригинал после обработки",
                setting_type=SettingType.CHECKBOX,
                default=False,
                visible_if={"overwrite_source": [False]},
            ),
        ]

    @property
    def use_custom_widget(self) -> bool:
        """Использовать кастомный виджет."""
        return True

    def execute_single(
        self,
        file_path: Path,
        settings: dict[str, Any],
        output_path: str | None = None,
        progress_callback: ProgressCallback | None = None,
        current: int = 0,
        total: int = 1,
    ) -> list[str]:
        """Не используется в Подмене потоков (переопределен execute)."""
        raise NotImplementedError(
            "StreamReplacerScript использует групповую обработку в execute()"
        )

    def execute(
        self,
        files: list[Path],
        settings: dict[str, Any],
        output_path: str | None = None,
        progress_callback: ProgressCallback | None = None,
    ) -> list[str]:
        """Выполнить подмену потоков."""
        overwrite = SettingsManager().overwrite_existing
        container, err_msg = self._check_container(settings)
        if not container:
            return [err_msg]

        raw_replacements = settings.get("replacements", {})
        if not raw_replacements:
            msg = "❌ Не назначено ни одной замены"
            logger.error(msg)
            return [msg]

        replacements = self._prepare_replacements(raw_replacements)
        assert container is not None
        logger.info(
            "Подмена потоков: контейнер='%s', замен=%d",
            container.name,
            len(replacements),
        )

        overwrite_source = settings.get("overwrite_source", False)
        delete_source = settings.get("delete_source", False)

        target_dir = self._resolver.resolve(container, output_path)
        output_file_path = self._get_safe_output_path(
            container, target_dir / f"{container.stem}{container.suffix}"
        )

        if output_file_path.exists() and not overwrite:
            msg = f"⏭ Пропущен (файл существует): {output_file_path.name}"
            logger.info(msg)
            if progress_callback:
                progress_callback(1, 1, msg, 0.0)
            return [msg]

        is_mp4 = container.suffix.lower() == ".mp4"
        if is_mp4:
            success, msg = self._do_execute_mp4(
                container,
                output_file_path,
                replacements,
                overwrite,
                progress_callback,
            )
        else:
            success, msg = self._do_execute_mkv(
                container,
                output_file_path,
                replacements,
                overwrite,
                progress_callback,
            )

        results = [msg]
        if success:
            if overwrite_source and not output_path:
                self._replace_source_with_result(
                    container, output_file_path, results
                )
            elif delete_source:
                self._delete_source(container, results)

        if progress_callback:
            progress_callback(1, 1, results[0], 100.0)
        return results

    def _check_container(
        self, settings: dict[str, Any]
    ) -> tuple[Path | None, str]:
        """Проверить валидность исходного контейнера."""
        container_str = settings.get("container_path", "")
        if not container_str:
            logger.error("❌ Контейнер не указан")
            return None, "❌ Контейнер не указан"

        container = Path(container_str)
        if not container.exists():
            msg = f"❌ Контейнер не найден: {container.name}"
            logger.error(msg)
            return None, msg

        return container, ""

    def _prepare_replacements(
        self, raw_replacements: dict[str, Any]
    ) -> dict[int, dict[str, Any]]:
        """Подготовить словарь замен из сырых настроек."""
        replacements: dict[int, dict[str, Any]] = {}
        for tid_str, data in raw_replacements.items():
            if isinstance(data, dict):
                replacements[int(tid_str)] = {
                    "path": Path(data["path"]),
                    "src_id": int(data.get("src_id", 0)),
                }
            else:
                replacements[int(tid_str)] = {"path": Path(data), "src_id": 0}
        return replacements

    def _do_execute_mkv(
        self,
        container: Path,
        output_path: Path,
        replacements: dict[int, dict[str, Any]],
        overwrite: bool = False,
        progress_callback: ProgressCallback | None = None,
    ) -> tuple[bool, str]:
        """Подмена дорожек в MKV через mkvmerge.

        Returns:
            (success, message)
        """
        try:
            all_tracks = self._probe.get_tracks(container)
        except Exception:
            logger.exception("Ошибка анализа MKV '%s'", container.name)
            return False, f"❌ Ошибка анализа: {container.name}"

        if progress_callback:
            progress_callback(0, 1, f"Сборка {container.stem}...", 0.0)

        # Вычисляем track-order
        # Формат: вход_id:дорожка_id[,...]
        # Вход 0 - это оригинальный контейнер.
        # Входы 1..N - это файлы замен в порядке добавления в inputs.

        # Для начала подготовим inputs и сопоставим заменяемые дорожки
        # с их новыми входами
        replaced_ids = set(replacements.keys())
        container_args = self._build_container_args(all_tracks, replaced_ids)
        inputs: list[dict[str, Any]] = [
            {"path": container, "args": container_args}
        ]

        # Карта: оригинальный_track_id -> (номер_входа, track_id_во_входе)
        # Изначально все дорожки считаются из входа 0 (оригинал)
        track_map: dict[int, tuple[int, int]] = {
            t.track_id: (0, t.track_id) for t in all_tracks
        }

        current_input_idx = 1
        for track_id in sorted(replacements.keys()):
            repl_data = replacements[track_id]
            repl_path = repl_data["path"]
            if not repl_path.exists():
                return False, f"❌ Файл-замена не найден: {repl_path.name}"

            track_orig = self._find_track(all_tracks, track_id)
            if track_orig:
                logger.info(
                    "Замена дорожки #%d оригинала на файл '%s' (ID %d)",
                    track_id,
                    repl_path.name,
                    repl_data["src_id"],
                )
                repl_args = self._build_replacement_args(
                    track_orig, repl_data["src_id"]
                )
                inputs.append({"path": repl_path, "args": repl_args})
                # Обновляем карту: эта оригинальная позиция теперь занята
                # дорожкой из нового входа
                track_map[track_id] = (current_input_idx, repl_data["src_id"])
                current_input_idx += 1
            else:
                logger.warning(
                    "Дорожка #%d не найдена в оригинале, пропуск замены",
                    track_id,
                )

        # Формируем трек-ордер на основе исходного порядка
        order_parts = []
        for t in all_tracks:
            # Если дорожка была заменена или сохранена - она в результате
            if t.track_id in replaced_ids or t.track_id in [
                cid for (cid, _) in track_map.items() if t.track_id == cid
            ]:
                inp_idx, tid_idx = track_map[t.track_id]
                order_parts.append(f"{inp_idx}:{tid_idx}")
                action = (
                    "ЗАМЕНЕНА" if t.track_id in replaced_ids else "СОХРАНЕНА"
                )
                logger.debug(
                    "Дорожка #%d: %s -> Вход %d, ID %d",
                    t.track_id,
                    action,
                    inp_idx,
                    tid_idx,
                )

        extra_args = ["--track-order", ",".join(order_parts)]

        success = self._runner.run(
            output_path=output_path,
            inputs=inputs,
            title=container.stem,
            overwrite=overwrite,
            extra_args=extra_args,
        )
        if not success:
            if self.is_cancelled:
                self._cleanup_if_cancelled(output_path)
                return False, f"⚠ Отменено: {output_path.name}"
            return False, f"❌ Ошибка сборки: {output_path.name}"

        return True, f"✅ Собрано: {output_path.name}"

    def _do_execute_mp4(
        self,
        container: Path,
        output_path: Path,
        replacements: dict[int, dict[str, Any]],
        overwrite: bool = False,
        progress_callback: ProgressCallback | None = None,
    ) -> tuple[bool, str]:
        """Подмена дорожек в MP4 через ffmpeg.

        Returns:
            (success, message)
        """
        try:
            streams = self._ffprobe.get_streams(container)
        except Exception:
            logger.exception("Ошибка анализа MP4 '%s'", container.name)
            return False, f"❌ Ошибка анализа: {container.name}"

        if progress_callback:
            progress_callback(0, 1, f"Сборка {container.stem}...", 0.0)

        full_args = self._prepare_mp4_args(streams, replacements)

        success = self._ffmpeg.run(
            input_path=container,
            output_path=output_path,
            extra_args=full_args,
            overwrite=overwrite,
        )
        if not success:
            if self.is_cancelled:
                self._cleanup_if_cancelled(output_path)
                return False, f"⚠ Отменено: {output_path.name}"
            return False, f"❌ Ошибка сборки: {output_path.name}"

        return True, f"✅ Собрано: {output_path.name}"

    def _prepare_mp4_args(
        self, streams: list[Any], replacements: dict[int, dict[str, Any]]
    ) -> list[str]:
        """Сформировать параметры FFmpeg для замены потоков MP4."""
        replaced_indices = set(replacements.keys())
        extra_args: list[str] = []
        extra_inputs: list[Path] = []
        input_idx, out_idx = 1, 0

        for stream in streams:
            sid = stream.stream_index
            if sid in replaced_indices:
                repl_path = replacements[sid]["path"]
                src_id = replacements[sid]["src_id"]
                logger.info(
                    "MP4: Замена потока #%d на '%s' (ID %d)",
                    sid,
                    repl_path.name,
                    src_id,
                )
                extra_inputs.append(repl_path)
                extra_args.extend(["-map", f"{input_idx}:{src_id}"])
                # Находим оригинальный стрим для переноса метаданных
                orig_stream = next(
                    (s for s in streams if s.stream_index == sid), None
                )
                self._add_ffmpeg_metadata(
                    extra_args, out_idx, orig_stream or stream
                )
                input_idx += 1
            else:
                extra_args.extend(["-map", f"0:{sid}"])
            out_idx += 1

        extra_args.extend(["-c", "copy"])
        input_args: list[str] = []
        for inp in extra_inputs:
            input_args.extend(["-i", str(inp)])

        return input_args + extra_args

    @staticmethod
    def _find_track(
        tracks: list[TrackInfo], track_id: int
    ) -> TrackInfo | None:
        """Найти дорожку по ID."""
        for t in tracks:
            if t.track_id == track_id:
                return t
        return None

    @staticmethod
    def _add_ffmpeg_metadata(
        args: list[str], stream_idx: int, track: Any
    ) -> None:
        """Добавить метаданные дорожки для ffmpeg."""
        if track.language and track.language != "und":
            args.extend(
                [f"-metadata:s:{stream_idx}", f"language={track.language}"]
            )
        if track.name:
            args.extend([f"-metadata:s:{stream_idx}", f"title={track.name}"])

        # Перенос диспозиций (flags) для ffmpeg
        dispositions = []
        if getattr(track, "is_default", False):
            dispositions.append("default")
        if getattr(track, "is_forced", False):
            dispositions.append("forced")
        if getattr(track, "is_hearing_impaired", False):
            dispositions.append("hearing_impaired")
        if getattr(track, "is_commentary", False):
            dispositions.append("comment")
        if getattr(track, "is_original", False):
            dispositions.append("original")

        if dispositions:
            disp_str = "+".join(dispositions)
            args.extend([f"-disposition:s:{stream_idx}", disp_str])

    @staticmethod
    def _build_container_args(
        all_tracks: list[TrackInfo], replaced_ids: set[int]
    ) -> list[str]:
        """Построить аргументы mkvmerge для контейнера (позитивный выбор)."""
        args: list[str] = []

        # Видео
        keep_video = [
            t.track_id
            for t in all_tracks
            if t.track_type == "video" and t.track_id not in replaced_ids
        ]
        if not [t for t in all_tracks if t.track_type == "video"]:
            pass  # Видео нет изначально
        elif not keep_video:
            args.append("--no-video")
        else:
            args.extend(["--video-tracks", ",".join(map(str, keep_video))])

        # Аудио
        keep_audio = [
            t.track_id
            for t in all_tracks
            if t.track_type == "audio" and t.track_id not in replaced_ids
        ]
        if not [t for t in all_tracks if t.track_type == "audio"]:
            pass
        elif not keep_audio:
            args.append("--no-audio")
        else:
            args.extend(["--audio-tracks", ",".join(map(str, keep_audio))])

        # Субтитры
        keep_subs = [
            t.track_id
            for t in all_tracks
            if t.track_type == "subtitles" and t.track_id not in replaced_ids
        ]
        if not [t for t in all_tracks if t.track_type == "subtitles"]:
            pass
        elif not keep_subs:
            args.append("--no-subtitles")
        else:
            args.extend(["--subtitle-tracks", ",".join(map(str, keep_subs))])

        return args

    @staticmethod
    def _build_replacement_args(
        track: TrackInfo, src_id: int = 0
    ) -> list[str]:
        """Аргументы mkvmerge для файла-замены."""
        all_types = {"video", "audio", "subtitles"}
        type_to_select_flag = {
            "video": "--video-tracks",
            "audio": "--audio-tracks",
            "subtitles": "--subtitle-tracks",
        }
        type_to_exclude_flag = {
            "video": "--no-video",
            "audio": "--no-audio",
            "subtitles": "--no-subtitles",
        }

        args: list[str] = []

        select_flag = type_to_select_flag.get(track.track_type)
        if select_flag:
            args.extend([select_flag, str(src_id)])

        for other_type in all_types - {track.track_type}:
            if exclude := type_to_exclude_flag.get(other_type):
                args.append(exclude)

        args.extend(
            [
                "--no-chapters",
                "--no-global-tags",
                "--no-track-tags",
                "--no-attachments",
            ]
        )

        if track.language and track.language != "und":
            args.extend(["--language", f"{src_id}:{track.language}"])
        if track.name:
            args.extend(["--track-name", f"{src_id}:{track.name}"])

        # Перенос флагов MKV
        if track.is_default:
            args.extend(["--default-track", f"{src_id}:yes"])
        else:
            args.extend(["--default-track", f"{src_id}:no"])

        if track.is_forced:
            args.extend(["--forced-display-flag", f"{src_id}:yes"])
        else:
            args.extend(["--forced-display-flag", f"{src_id}:no"])

        if track.is_hearing_impaired:
            args.extend(["--hearing-impaired-flag", f"{src_id}:yes"])
        if track.is_commentary:
            args.extend(["--commentary-flag", f"{src_id}:yes"])
        if track.is_original:
            args.extend(["--original-flag", f"{src_id}:yes"])
        if track.is_visual_impaired:
            args.extend(["--visual-impaired-flag", f"{src_id}:yes"])

        return args
