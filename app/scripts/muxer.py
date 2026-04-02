# -*- coding: utf-8 -*-
"""Скрипт муксинга видео, аудио и субтитров."""

from app.core.constants import (
    VIDEO_CONTAINERS,
    MEDIA_CONTAINERS,
    AUDIO_EXTENSIONS,
    SUBTITLE_EXTENSIONS,
    ScriptCategory,
    ScriptMetadata,
)
import logging
from collections import defaultdict
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
from app.infrastructure.mkvmerge_runner import MKVMergeRunner

logger = logging.getLogger(__name__)


class MuxerScript(AbstractScript):
    """Скрипт для сборки MKV из компонентов."""

    def __init__(self) -> None:
        """Инициализация муксера."""
        self._runner = MKVMergeRunner()
        self._resolver = OutputResolver()
        logger.info("Скрипт муксинга создан")

    @property
    def category(self) -> str:
        """Категория скрипта."""
        return ScriptCategory.CONTAINERS

    @property
    def name(self) -> str:
        """Отображаемое имя скрипта."""
        return ScriptMetadata.MUXER_NAME

    @property
    def description(self) -> str:
        """Описание скрипта."""
        return ScriptMetadata.MUXER_DESC

    @property
    def icon_name(self) -> str:
        """Имя иконки FluentIcon."""
        return "MOVIE"

    @property
    def file_extensions(self) -> list[str]:
        """Допустимые расширения файлов."""
        return list(MEDIA_CONTAINERS | AUDIO_EXTENSIONS | SUBTITLE_EXTENSIONS)

    @property
    def use_custom_widget(self) -> bool:
        """Использовать таблицу муксинга."""
        return True

    @property
    def settings_schema(self) -> list[SettingField]:
        """Схема настроек скрипта."""
        return [
            SettingField(
                key="subs_title",
                label="Заголовок субтитров",
                setting_type=SettingType.TEXT,
                default="[Надписи]",
            ),
            SettingField(
                key="clean_tracks",
                label="Удалить лишние дорожки из источника",
                setting_type=SettingType.CHECKBOX,
                default=True,
            ),
        ]

    def execute_single(
        self,
        file_path: Path,
        settings: dict[str, Any],
        output_path: str | None = None,
        progress_callback: ProgressCallback | None = None,
        current: int = 0,
        total: int = 1,
    ) -> list[str]:
        """Не используется в Муксере (переопределен execute)."""
        raise NotImplementedError(
            "MuxerScript использует групповую обработку в execute()"
        )

    def execute(
        self,
        files: list[Path],
        settings: dict[str, Any],
        output_path: str | None = None,
        progress_callback: ProgressCallback | None = None,
    ) -> list[str]:
        """Выполнить муксинг (групповая обработка)."""
        valid_groups = self._group_files(files)
        overwrite = SettingsManager().overwrite_existing
        results: list[str] = []

        total = len(valid_groups)
        completed = 0

        subs_title = settings.get("subs_title", "Russian")
        clean_tracks = settings.get("clean_tracks", True)

        logger.info(
            "Настройки муксинга: заголовок сабов='%s', очистка дорожек=%s, файлов=%d",  # noqa: E501
            subs_title,
            clean_tracks,
            total,
        )

        for stem, components in valid_groups.items():
            if components["video"] is None:
                continue

            msg = self._process_group(
                stem,
                components,
                output_path,
                subs_title,
                clean_tracks,
                overwrite,
            )
            results.append(msg)

            completed += 1
            if progress_callback:
                progress_callback(completed, total, msg, 0.0)

        return results

    def _process_group(
        self,
        stem: str,
        components: dict[str, Path | None],
        output_path: str | None,
        subs_title: str,
        clean_tracks: bool,
        overwrite: bool,
    ) -> str:
        """Сборка одного MKV из компонентов."""
        video_path = components["video"]
        assert video_path is not None
        target_dir = self._resolver.resolve(video_path, output_path)
        output_file_path = self._get_safe_output_path(
            video_path, target_dir / f"{stem}.mkv"
        )

        if output_file_path.exists() and not overwrite:
            logger.info(
                "[%s] ПРОПУСК (файл существует): %s",
                self.name,
                output_file_path.name,
            )
            return f"⏭ ПРОПУСК (файл существует): {output_file_path.name}"

        inputs = self._build_mkvmerge_inputs(
            video_path,
            components["audio"],
            components["subs"],
            clean_tracks,
            subs_title,
        )

        success = self._runner.run(
            output_path=output_file_path,
            inputs=inputs,
            title=stem,
            overwrite=overwrite,
        )

        if not success:
            if self.is_cancelled:
                self._cleanup_if_cancelled(output_file_path)
                return f"⚠ Отменено: {output_file_path.name}"
            return f"❌ ОШИБКА сборки: {output_file_path.name}"

        return f"✅ Собрано: {output_file_path.name}"

    def _group_files(
        self, files: list[Path]
    ) -> dict[str, dict[str, Path | None]]:
        """Группировка входных файлов по имени (stem)."""
        groups: dict[str, dict[str, Path | None]] = defaultdict(
            lambda: {"video": None, "audio": None, "subs": None}
        )

        for file_path in files:
            stem = file_path.stem
            ext = file_path.suffix.lower()

            if ext in VIDEO_CONTAINERS:
                if groups[stem]["video"] is None:
                    groups[stem]["video"] = file_path
            elif ext in AUDIO_EXTENSIONS:
                if groups[stem]["audio"] is None:
                    groups[stem]["audio"] = file_path
            elif ext in SUBTITLE_EXTENSIONS:
                if groups[stem]["subs"] is None:
                    groups[stem]["subs"] = file_path

        return {k: v for k, v in groups.items() if v["video"] is not None}

    def _build_mkvmerge_inputs(
        self,
        video_path: Path,
        audio_path: Path | None,
        subs_path: Path | None,
        clean_tracks: bool,
        subs_title: str,
    ) -> list[dict[str, Any]]:
        """Формирование аргументов входных файлов для mkvmerge."""
        inputs: list[dict[str, Any]] = []

        video_args = []
        if clean_tracks:
            if audio_path:
                video_args.append("--no-audio")
            if subs_path:
                video_args.append("--no-subtitles")
            video_args.extend(["--no-global-tags", "--no-track-tags"])

        inputs.append({"path": video_path, "args": video_args})

        if audio_path:
            inputs.append(
                {
                    "path": audio_path,
                    "args": [
                        "--audio-tracks",
                        "0",
                        "--language",
                        "0:und",
                        "--default-track",
                        "0:yes",
                        "--forced-display-flag",
                        "0:yes",
                    ],
                }
            )

        if subs_path:
            inputs.append(
                {
                    "path": subs_path,
                    "args": [
                        "--subtitle-tracks",
                        "0",
                        "--language",
                        "0:rus",
                        "--track-name",
                        f"0:{subs_title}",
                        "--default-track",
                        "0:yes",
                        "--forced-display-flag",
                        "0:yes",
                    ],
                }
            )

        return inputs
