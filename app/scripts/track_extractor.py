# -*- coding: utf-8 -*-
"""Скрипт массового извлечения дорожек с умными правилами.

Осуществляет извлечение выбранных потоков с использованием
одного прохода FFmpeg (One-Pass Extraction) для скорости,
с умным формированием имен выходных файлов.
"""

import logging
import re
from collections import Counter
from pathlib import Path
from typing import Any, List, Union

from app.core.abstract_script import (
    AbstractScript,
    SettingField,
    SettingType,
)
from app.core.settings_manager import SettingsManager
from app.core.output_resolver import OutputResolver
from app.infrastructure.ffmpeg_runner import FFmpegRunner
from app.infrastructure.mkvprobe_runner import MKVProbeRunner, TrackInfo
from app.core.constants import (
    MEDIA_CONTAINERS,
    ScriptCategory,
    ScriptMetadata,
    RAW_EXTENSIONS,
    SUBTITLE_CONVERT_CODECS,
)

logger = logging.getLogger(__name__)


class TrackExtractorScript(AbstractScript):
    """Скрипт для массового извлечения дорожек из MKV/MP4."""

    def __init__(self) -> None:
        """Инициализация скрипта."""
        self._ffmpeg = FFmpegRunner()
        self._probe = MKVProbeRunner()
        self._resolver = OutputResolver()
        logger.info("Скрипт массового извлечения потоков из контейнера создан")

    @property
    def category(self) -> str:
        """Категория скрипта."""
        return ScriptCategory.CONTAINERS

    @property
    def name(self) -> str:
        """Отображаемое имя скрипта."""
        return ScriptMetadata.TRACK_EXTR_NAME

    @property
    def description(self) -> str:
        """Описание скрипта."""
        return ScriptMetadata.TRACK_EXTR_DESC

    @property
    def icon_name(self) -> str:
        """Имя иконки FluentIcon."""
        return "DOWNLOAD"

    @property
    def file_extensions(self) -> list[str]:
        """Допустимые расширения файлов."""
        return list(MEDIA_CONTAINERS)

    @property
    def settings_schema(self) -> list[SettingField]:
        """Схема настроек скрипта."""
        return [
            SettingField(
                key="name_format",
                label="Формат имени файла",
                setting_type=SettingType.COMBO,
                default="{original}_{lang}_{id}",
                options=[
                    "{original}_{lang}_{id}",
                    "{original}_{id}_{lang}",
                    "{original}_{lang}",
                ],
            )
        ]

    @property
    def use_custom_widget(self) -> bool:
        """Скрипт использует кастомный виджет."""
        return True

    def _get_extension_for_track(self, track: TrackInfo) -> str:
        """Определить расширение файла по кодеку дорожки."""
        if track.codec in RAW_EXTENSIONS:
            return RAW_EXTENSIONS[track.codec]

        if track.track_type == "video":
            return ".mkv"
        elif track.track_type == "audio":
            return ".mka"
        elif track.track_type == "subtitles":
            return ".mks"
        return ".bin"

    def _format_filename(
        self,
        original_stem: str,
        track: TrackInfo,
        ext: str,
        name_format: str,
        name_suffix: str = "",
    ) -> str:
        """Сформировать имя извлеченного файла по шаблону.

        Args:
            original_stem: Имя исходного файла без расширения.
            track: Информация о дорожке.
            ext: Расширение выходного файла.
            name_format: Шаблон формата имени.
            name_suffix: Суффикс для различения дублей.
        """
        lang = (
            track.language
            if track.language and track.language != "und"
            else ""
        )
        if name_suffix:
            lang = f"{lang}_{name_suffix}" if lang else name_suffix
        t_id = f"track{track.track_id:02d}"

        name = name_format.replace("{original}", original_stem)
        name = name.replace("{id}", t_id)

        if "{lang}" in name:
            if lang:
                name = name.replace("{lang}", lang)
            else:
                name = (
                    name.replace("_{lang}", "")
                    .replace("{lang}_", "")
                    .replace("{lang}", "")
                )

        name = name.replace("__", "_").rstrip("_")
        return f"{name}{ext}"

    @staticmethod
    def _sanitize_name(name: str) -> str:
        """Очистить заголовок дорожки для использования в имени файла."""
        # Убираем кавычки и недопустимые символы.
        clean = name.strip("\"' ")
        clean = re.sub(r'[<>:"/\\|?*]', "", clean)
        clean = clean.replace(" ", "_")
        return clean

    def execute_single(
        self,
        file_path: Path,
        settings: dict[str, Any],
        output_path: str | None = None,
    ) -> list[str]:
        """Обработать один файл (массовое извлечение выбранных дорожек)."""
        per_file = settings.get("selected_tracks_per_file", {})
        selected_ids: List[int] = per_file.get(str(file_path), [])
        overwrite = SettingsManager().overwrite_existing
        name_format = settings.get("name_format", "{original}_{lang}_{id}")

        if not selected_ids:
            msg = f"⏭ Пропущен (нет выбранных дорожек): {file_path.name}"
            logger.info("[%s] %s", self.name, msg)
            return [msg]

        tracks_or_msg = self._get_tracks_to_extract(file_path, selected_ids)
        if isinstance(tracks_or_msg, str):
            return [tracks_or_msg]

        target_dir = self._resolver.resolve(file_path, output_path)

        ffmpeg_args, extracted_files, output_results = (
            self._build_extraction_args(
                file_path, tracks_or_msg, name_format, target_dir, overwrite
            )
        )

        if not ffmpeg_args:
            return output_results

        return self._run_extraction_pipeline(
            file_path, ffmpeg_args, extracted_files, overwrite
        )

    def _get_tracks_to_extract(
        self, file_path: Path, selected_ids: List[int]
    ) -> Union[List[TrackInfo], str]:
        """Получить и отфильтровать дорожки файла."""
        try:
            all_tracks = self._probe.get_tracks(file_path)
            tracks_to_extract = [
                t for t in all_tracks if t.track_id in selected_ids
            ]

            if not tracks_to_extract:
                return f"⏭ Пропущен (нет валидных дорожек для извлечения): {file_path.name}"  # noqa: E501

            return tracks_to_extract
        except Exception:
            logger.exception(
                "Ошибка анализа дорожек файла '%s'", file_path.name
            )
            return f"❌ Ошибка анализа: {file_path.name}"

    def _build_extraction_args(
        self,
        file_path: Path,
        tracks: List[TrackInfo],
        name_format: str,
        target_dir: Path,
        overwrite: bool,
    ) -> tuple[List[str], List[Path], List[str]]:
        """Собрать аргументы для FFmpeg и списки выходных файлов."""
        ffmpeg_args: List[str] = []
        extracted_files: List[Path] = []
        output_results: List[str] = []

        # Определение языков-дубликатов для приписки имени.
        lang_counts: Counter[str] = Counter(
            t.language for t in tracks if t.language and t.language != "und"
        )
        duplicate_langs = {
            lang for lang, cnt in lang_counts.items() if cnt > 1
        }

        for track in tracks:
            ext = self._get_extension_for_track(track)

            # Суффикс из заголовка для дорожек-дублей.
            name_suffix = ""
            if track.language in duplicate_langs and track.name:
                name_suffix = self._sanitize_name(track.name)

            out_filename = self._format_filename(
                file_path.stem,
                track,
                ext,
                name_format,
                name_suffix,
            )
            out_filepath = self._get_safe_output_path(
                file_path, target_dir / out_filename
            )

            if out_filepath.exists() and not overwrite:
                logger.info(
                    "[%s] Дорожка пропущена (файл существует): %s",
                    self.name,
                    out_filepath.name,
                )
                output_results.append(
                    f"⏭ Пропущена дорожка {track.track_id}: {out_filename}"
                )
                continue

            # Определение типа и кодека для потока.
            if track.track_type == "video":
                codec_flag = "-c:v"
                codec_value = "copy"
            elif track.track_type == "audio":
                codec_flag = "-c:a"
                codec_value = "copy"
            else:
                codec_flag = "-c:s"
                convert_to = SUBTITLE_CONVERT_CODECS.get(track.codec)
                if convert_to:
                    codec_value = convert_to
                    logger.info(
                        "[%s] Конвертация субтитров "
                        "'%s' → '%s' (дорожка %d)",
                        self.name,
                        track.codec,
                        convert_to,
                        track.track_id,
                    )
                else:
                    codec_value = "copy"

            ffmpeg_args.extend(
                [
                    "-map",
                    f"0:{track.track_id}",
                    codec_flag,
                    codec_value,
                    str(out_filepath),
                ]
            )
            extracted_files.append(out_filepath)
            output_results.append(
                f"✅ Извлечена дорожка {track.track_id}: {out_filename}"
            )

        return ffmpeg_args, extracted_files, output_results

    def _run_extraction_pipeline(
        self,
        file_path: Path,
        ffmpeg_args: List[str],
        extracted_files: List[Path],
        overwrite: bool,
    ) -> List[str]:
        """Запуск процесса извлечения FFmpeg."""
        logger.info(
            "[%s] Запуск FFmpeg для One-Pass извлечения %d дорожек из '%s'",
            self.name,
            len(extracted_files),
            file_path.name,
        )

        main_output = ffmpeg_args[-1]
        args_without_main_output = ffmpeg_args[:-1]

        success = self._ffmpeg.run(
            input_path=file_path,
            output_path=Path(main_output),
            extra_args=args_without_main_output,
            overwrite=overwrite,
        )

        if success:
            logger.info(
                "[%s] Успешно извлечены файлы: %s",
                self.name,
                [f.name for f in extracted_files],
            )
            return [
                f"✅ Извлечено из {file_path.name}: {len(extracted_files)} файл(ов)"  # noqa: E501
            ]
        else:
            if self.is_cancelled:
                self._cleanup_if_cancelled(*extracted_files)
                return [f"⚠ Отменено извлечение из: {file_path.name}"]
            else:
                logger.error(
                    "[%s] Ошибка при массовом извлечении из '%s'",
                    self.name,
                    file_path.name,
                )
                return [f"❌ Ошибка извлечения: {file_path.name}"]
