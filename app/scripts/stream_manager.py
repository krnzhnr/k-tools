# -*- coding: utf-8 -*-
"""Скрипт управления потоками MKV и MP4.

Позволяет удалять выбранные дорожки или сохранять
только выбранные из контейнеров MKV и MP4.
"""

import logging
from pathlib import Path
from typing import Any

from app.core.abstract_script import (
    AbstractScript,
    ProgressCallback,
    SettingField,
    SettingType,
)
from app.core.settings_manager import SettingsManager
from app.core.output_resolver import OutputResolver
from app.infrastructure.ffmpeg_runner import FFmpegRunner
from app.infrastructure.mkvmerge_runner import (
    MKVMergeRunner,
)
from app.infrastructure.mkvprobe_runner import (
    MKVProbeRunner,
    TrackInfo,
)

logger = logging.getLogger(__name__)

# Режимы работы скрипта.
MODE_REMOVE = "Удалить выбранные"
MODE_KEEP = "Сохранить только выбранные"

# Маппинг кодеков на расширения для сырого извлечения
RAW_EXTENSIONS = {
    "AC-3": ".ac3",
    "E-AC-3": ".eac3",
    "DTS": ".dts",
    "AAC": ".aac",
    "Opus": ".opus",
    "FLAC": ".flac",
    "Vorbis": ".ogg",
    "MP3": ".mp3",
    "TrueHD": ".thd",
    "PCM": ".wav",
    "MPEG Audio": ".mp3",
}


from app.core.constants import VIDEO_EXTENSIONS

class StreamManagerScript(AbstractScript):
    """Скрипт для управления потоками в MKV."""

    def __init__(self) -> None:
        """Инициализация скрипта."""
        self._runner = MKVMergeRunner()
        self._ffmpeg = FFmpegRunner()
        self._probe = MKVProbeRunner()
        self._resolver = OutputResolver()
        logger.info(
            "Скрипт управления потоками MKV/MP4 создан"
        )

    @property
    def category(self) -> str:
        """Категория скрипта."""
        return "Муксинг"

    @property
    def name(self) -> str:
        """Отображаемое имя скрипта."""
        return "Управление потоками (MKV/MP4)"

    @property
    def description(self) -> str:
        """Описание скрипта."""
        return (
            "Удаление или сохранение выбранных "
            "дорожек (видео, аудио, субтитры) "
            "в MKV и MP4 файлах."
        )

    @property
    def icon_name(self) -> str:
        """Имя иконки FluentIcon."""
        return "EDIT"

    @property
    def file_extensions(self) -> list[str]:
        """Допустимые расширения файлов."""
        return list(VIDEO_EXTENSIONS)

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
                label="Упаковать аудио в M4A (только при сохранении одной дорожки)",
                setting_type=SettingType.CHECKBOX,
                default=False,
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
        per_file = settings.get("selected_tracks_per_file", {})
        file_key = str(file_path)
        selected_ids = per_file.get(file_key, [])
        overwrite = SettingsManager().overwrite_existing

        if not selected_ids:
            msg = f"⏭ Пропущен (нет выбранных дорожек): {file_path.name}"
            logger.info("[%s] %s", self.name, msg)
            return [msg]

        try:
            all_tracks = self._probe.get_tracks(file_path)
        except Exception:
            logger.exception("Ошибка анализа дорожек файла '%s'", file_path.name)
            return [f"❌ Ошибка анализа: {file_path.name}"]

        # Формируем аргументы фильтрации
        mkvmerge_args = self._build_track_args(
            all_tracks=all_tracks,
            selected_ids=selected_ids,
            mode=mode,
        )

        keep_ids = self._compute_keep_ids(all_tracks, selected_ids, mode)
        kept_tracks = [t for t in all_tracks if t.track_id in keep_ids]
        kept_types = {t.track_type for t in kept_tracks}
        
        use_m4a = settings.get("use_m4a_container_audio_only", False)
        is_mp4 = file_path.suffix.lower() == ".mp4"
        ext = file_path.suffix
        use_ffmpeg = is_mp4
        ffmpeg_args = []

        if is_mp4:
            for tid in sorted(keep_ids):
                ffmpeg_args.extend(["-map", f"0:{tid}"])
            ffmpeg_args.extend(["-c", "copy"])
            if kept_types == {"audio"} and len(kept_tracks) == 1:
                ext = ".m4a" if use_m4a else RAW_EXTENSIONS.get(kept_tracks[0].codec, ".mka")
            elif kept_types == {"audio"}:
                ext = ".m4a" if use_m4a else ".mka"
        elif len(kept_tracks) == 1 and kept_tracks[0].track_type == "audio":
            track = kept_tracks[0]
            use_ffmpeg = True
            ext = ".m4a" if use_m4a else RAW_EXTENSIONS.get(track.codec, ".mka")
            audio_tracks = [t for t in all_tracks if t.track_type == "audio"]
            try:
                audio_idx = audio_tracks.index(track)
                ffmpeg_args = ["-map", f"0:a:{audio_idx}", "-c", "copy"]
            except ValueError:
                use_ffmpeg = False
        elif kept_types == {"audio"}:
            ext = ".mka"
        else:
            ext = file_path.suffix

        target_dir = self._resolver.resolve(file_path, output_path)
        output_file_path = self._get_safe_output_path(
            file_path, target_dir / (file_path.stem + ext)
        )

        if output_file_path.exists() and not overwrite:
            msg = f"⏭ Пропущен (файл существует): {output_file_path.name}"
            logger.info("[%s] %s", self.name, msg)
            return [msg]

        if use_ffmpeg:
            success = self._ffmpeg.run(
                input_path=file_path,
                output_path=output_file_path,
                extra_args=ffmpeg_args,
                overwrite=overwrite
            )
        else:
            inputs = [{"path": file_path, "args": mkvmerge_args}]
            success = self._runner.run(
                output_path=output_file_path,
                inputs=inputs,
                overwrite=overwrite
            )

        if success:
            return [f"✅ Обработано: {output_file_path.name}"]
        return [f"❌ Ошибка обработки: {file_path.name}"]

    @staticmethod
    def _compute_keep_ids(
        all_tracks: list[TrackInfo],
        selected_ids: list[int],
        mode: str,
    ) -> set[int]:
        """Вычислить ID дорожек, которые остаются.

        Args:
            all_tracks: Все дорожки файла.
            selected_ids: ID выбранных дорожек.
            mode: Режим работы.

        Returns:
            Набор ID дорожек для сохранения.
        """
        if mode == MODE_KEEP:
            return set(selected_ids)
        all_ids = {t.track_id for t in all_tracks}
        return all_ids - set(selected_ids)

    @staticmethod
    def _get_kept_types(
        all_tracks: list[TrackInfo],
        selected_ids: list[int],
        mode: str,
    ) -> set[str]:
        """Определить типы оставшихся дорожек.

        Args:
            all_tracks: Все дорожки файла.
            selected_ids: ID выбранных дорожек.
            mode: Режим работы.

        Returns:
            Набор типов (video, audio, subtitles).
        """
        keep_ids = StreamManagerScript._compute_keep_ids(
            all_tracks, selected_ids, mode
        )
        return {
            t.track_type
            for t in all_tracks
            if t.track_id in keep_ids
        }

    @staticmethod
    def _build_track_args(
        all_tracks: list[TrackInfo],
        selected_ids: list[int],
        mode: str,
    ) -> list[str]:
        """Построить аргументы mkvmerge для фильтрации.

        Args:
            all_tracks: Все дорожки файла.
            selected_ids: ID выбранных дорожек.
            mode: Режим работы.

        Returns:
            Список аргументов mkvmerge.
        """
        # Группировка всех дорожек по типу
        type_map: dict[str, list[int]] = {
            "video": [],
            "audio": [],
            "subtitles": [],
        }
        for track in all_tracks:
            if track.track_type in type_map:
                type_map[track.track_type].append(
                    track.track_id
                )

        keep_ids = StreamManagerScript._compute_keep_ids(
            all_tracks, selected_ids, mode
        )

        # Для каждого типа формируем аргументы
        args: list[str] = []
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

        for track_type, all_type_ids in type_map.items():
            if not all_type_ids:
                continue

            # ID этого типа, которые нужно оставить
            kept = [
                tid
                for tid in all_type_ids
                if tid in keep_ids
            ]

            if len(kept) == len(all_type_ids):
                # Все дорожки этого типа остаются
                continue
            elif not kept:
                # Ни одна дорожка этого типа не нужна
                args.append(no_flag_map[track_type])
            else:
                # Оставить только конкретные ID
                ids_str = ",".join(
                    str(tid) for tid in kept
                )
                args.extend(
                    [flag_map[track_type], ids_str]
                )

        logger.debug(
            "Построены аргументы фильтрации: "
            "режим='%s', выбрано=%s, результат=%s",
            mode,
            selected_ids,
            args,
        )
        return args
