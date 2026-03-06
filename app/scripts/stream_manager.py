# -*- coding: utf-8 -*-
"""Скрипт управления потоками MKV и MP4.

Позволяет удалять выбранные дорожки или сохранять
только выбранные из контейнеров MKV и MP4.
"""

from app.core.constants import (
    MEDIA_CONTAINERS,
    RAW_EXTENSIONS,
    ScriptCategory,
    ScriptMetadata,
)
import logging
from pathlib import Path
from typing import Any

from app.core.abstract_script import (
    AbstractScript,
    SettingField,
    SettingType,
)
from app.core.settings_manager import SettingsManager
from app.core.output_resolver import OutputResolver
from app.infrastructure.ffmpeg_runner import FFmpegRunner
from app.infrastructure.mkvmerge_runner import MKVMergeRunner
from app.infrastructure.mkvprobe_runner import MKVProbeRunner, TrackInfo

logger = logging.getLogger(__name__)

MODE_REMOVE = "Удалить выбранные"
MODE_KEEP = "Сохранить только выбранные"


class StreamManagerScript(AbstractScript):
    """Скрипт для управления потоками в MKV."""

    def __init__(self) -> None:
        """Инициализация скрипта."""
        self._runner = MKVMergeRunner()
        self._ffmpeg = FFmpegRunner()
        self._probe = MKVProbeRunner()
        self._resolver = OutputResolver()
        logger.info("Скрипт управления потоками MKV/MP4 создан")

    @property
    def category(self) -> str:
        """Категория скрипта."""
        return ScriptCategory.CONTAINERS

    @property
    def name(self) -> str:
        """Отображаемое имя скрипта."""
        return ScriptMetadata.STREAM_MGR_NAME

    @property
    def description(self) -> str:
        """Описание скрипта."""
        return ScriptMetadata.STREAM_MGR_DESC

    @property
    def icon_name(self) -> str:
        """Имя иконки FluentIcon."""
        return "EDIT"

    @property
    def file_extensions(self) -> list[str]:
        """Допустимые расширения файлов."""
        return list(MEDIA_CONTAINERS)

    @property
    def settings_schema(self) -> list[SettingField]:
        """Схема настроек скрипта."""
        return [
            SettingField(
                key="mode",
                label="Режим работы",
                setting_type=SettingType.COMBO,
                default=MODE_REMOVE,
                options=[MODE_REMOVE, MODE_KEEP],
            ),
            SettingField(
                key="use_m4a_container_audio_only",
                label="Упаковать аудио в M4A (только при сохранении одной дорожки)",  # noqa: E501
                setting_type=SettingType.CHECKBOX,
                default=False,
            ),
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
        """Скрипт использует кастомный виджет."""
        return True

    def execute_single(
        self,
        file_path: Path,
        settings: dict[str, Any],
        output_path: str | None = None,
    ) -> list[str]:
        """Обработать один файл (фильтрация дорожек)."""
        mode = settings.get("mode", MODE_REMOVE)
        selected_ids = settings.get("selected_tracks_per_file", {}).get(
            str(file_path), []
        )

        if not selected_ids:
            logger.info(
                "[%s] Пропущен (нет выбранных дорожек): %s",
                self.name,
                file_path.name,
            )
            return [f"⏭ ПРОПУСК (нет выбранных дорожек): {file_path.name}"]

        try:
            all_tracks = self._probe.get_tracks(file_path)
        except Exception:
            logger.exception(
                "Ошибка анализа дорожек файла '%s'", file_path.name
            )
            return [f"❌ ОШИБКА анализа: {file_path.name}"]

        use_m4a = settings.get("use_m4a_container_audio_only", False)
        return self._process_tracks(
            file_path,
            all_tracks,
            selected_ids,
            mode,
            use_m4a,
            output_path,
            settings,
        )

    def _process_tracks(
        self,
        file_path: Path,
        all_tracks: list[TrackInfo],
        selected_ids: list[int],
        mode: str,
        use_m4a: bool,
        output_path: str | None,
        settings: dict[str, Any],
    ) -> list[str]:
        """Фильтрация дорожек и запуск создания выходного файла."""
        keep_ids = self._compute_keep_ids(all_tracks, selected_ids, mode)
        kept_tracks = [t for t in all_tracks if t.track_id in keep_ids]

        use_ffmpeg, ffmpeg_args, ext = self._prepare_execution_params(
            file_path, all_tracks, kept_tracks, keep_ids, use_m4a
        )

        overwrite_source = settings.get("overwrite_source", False)
        delete_source = settings.get("delete_source", False)

        target_dir = self._resolver.resolve(file_path, output_path)

        # Если подменяем, берем защищенное имя во временной папке или рядом
        # Но проще всего взять _processed версию и потом переименовать
        output_file_path = self._get_safe_output_path(
            file_path, target_dir / (file_path.stem + ext)
        )

        overwrite = SettingsManager().overwrite_existing

        if output_file_path.exists() and not overwrite:
            logger.info(
                "[%s] ПРОПУСК (файл существует): %s",
                self.name,
                output_file_path.name,
            )
            return [f"⏭ ПРОПУСК (файл существует): {output_file_path.name}"]

        if use_ffmpeg:
            success = self._ffmpeg.run(
                input_path=file_path,
                output_path=output_file_path,
                extra_args=ffmpeg_args,
                overwrite=overwrite,
            )
        else:
            mkvmerge_args = self._build_track_args(
                all_tracks, selected_ids, mode
            )
            success = self._runner.run(
                output_path=output_file_path,
                inputs=[{"path": file_path, "args": mkvmerge_args}],
                overwrite=overwrite,
            )

        results = []
        if success:
            results.append(f"✅ ОБРАБОТАНО: {output_file_path.name}")

            if overwrite_source:
                self._replace_source_with_result(
                    file_path, output_file_path, results
                )
            elif delete_source:
                self._delete_source(file_path, results)
        else:
            results.append(f"❌ ОШИБКА: {file_path.name}")

        return results

    def _prepare_execution_params(
        self,
        file_path: Path,
        all_tracks: list[TrackInfo],
        kept_tracks: list[TrackInfo],
        keep_ids: set[int],
        use_m4a: bool,
    ) -> tuple[bool, list[str], str]:
        """Подготовка параметров для FFmpeg и определение расширения."""
        kept_types = {t.track_type for t in kept_tracks}
        is_mp4 = file_path.suffix.lower() == ".mp4"
        use_ffmpeg = is_mp4
        ffmpeg_args: list[str] = []
        ext = file_path.suffix

        if is_mp4:
            for tid in sorted(keep_ids):
                ffmpeg_args.extend(["-map", f"0:{tid}"])
            ffmpeg_args.extend(["-c", "copy"])
            if kept_types == {"audio"}:
                ext = (
                    ".m4a"
                    if use_m4a
                    else (
                        RAW_EXTENSIONS.get(kept_tracks[0].codec, ".mka")
                        if len(kept_tracks) == 1
                        else ".mka"
                    )
                )
        elif len(kept_tracks) == 1 and kept_tracks[0].track_type == "audio":
            track = kept_tracks[0]
            use_ffmpeg = True
            ext = (
                ".m4a" if use_m4a else RAW_EXTENSIONS.get(track.codec, ".mka")
            )
            audio_tracks = [t for t in all_tracks if t.track_type == "audio"]
            try:
                audio_idx = audio_tracks.index(track)
                ffmpeg_args = ["-map", f"0:a:{audio_idx}", "-c", "copy"]
            except ValueError:
                use_ffmpeg = False
        elif kept_types == {"audio"}:
            ext = ".mka"

        return use_ffmpeg, ffmpeg_args, ext

    @staticmethod
    def _compute_keep_ids(
        all_tracks: list[TrackInfo], selected_ids: list[int], mode: str
    ) -> set[int]:
        """Вычислить ID дорожек, которые остаются."""
        if mode == MODE_KEEP:
            return set(selected_ids)
        return {t.track_id for t in all_tracks} - set(selected_ids)

    @staticmethod
    def _get_kept_types(
        all_tracks: list[TrackInfo], selected_ids: list[int], mode: str
    ) -> set[str]:
        """Определить типы оставшихся дорожек."""
        keep_ids = StreamManagerScript._compute_keep_ids(
            all_tracks, selected_ids, mode
        )
        return {t.track_type for t in all_tracks if t.track_id in keep_ids}

    @staticmethod
    def _get_type_flags(
        track_type: str, kept: list[int], all_type_ids: list[int]
    ) -> list[str]:
        """Генерация аргументов mkvmerge для конкретного типа дорожек."""
        flag_map = {
            "video": "--video-tracks",
            "audio": "--audio-tracks",
            "subtitles": "--subtitle-tracks",
        }
        no_flag_map = {
            "video": "--no-video",
            "audio": "--no-audio",
            "subtitles": "--no-subtitles",
        }

        if len(kept) == len(all_type_ids):
            return []
        if not kept:
            return [no_flag_map[track_type]]
        return [flag_map[track_type], ",".join(str(tid) for tid in kept)]

    @staticmethod
    def _build_track_args(
        all_tracks: list[TrackInfo], selected_ids: list[int], mode: str
    ) -> list[str]:
        """Построить аргументы mkvmerge для фильтрации."""
        type_map: dict[str, list[int]] = {
            "video": [],
            "audio": [],
            "subtitles": [],
        }
        for track in all_tracks:
            if track.track_type in type_map:
                type_map[track.track_type].append(track.track_id)

        keep_ids = StreamManagerScript._compute_keep_ids(
            all_tracks, selected_ids, mode
        )
        args: list[str] = []

        for track_type, all_type_ids in type_map.items():
            if not all_type_ids:
                continue
            kept = [tid for tid in all_type_ids if tid in keep_ids]
            args.extend(
                StreamManagerScript._get_type_flags(
                    track_type, kept, all_type_ids
                )
            )

        logger.debug(
            "Построены аргументы фильтрации: режим='%s', результат=%s",
            mode,
            args,
        )
        return args
