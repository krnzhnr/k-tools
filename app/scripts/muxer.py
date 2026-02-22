# -*- coding: utf-8 -*-
"""Скрипт муксинга видео, аудио и субтитров."""

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


from app.core.constants import VIDEO_EXTENSIONS, AUDIO_EXTENSIONS, SUBTITLE_EXTENSIONS
from app.core.settings_manager import SettingsManager

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
        return "Муксинг"

    @property
    def name(self) -> str:
        """Отображаемое имя скрипта."""
        return "Муксинг"

    @property
    def description(self) -> str:
        """Описание скрипта."""
        return (
            "Собирает MKV из видео, аудио и субтитров. "
            "Файлы сопоставляются по имени."
        )

    @property
    def icon_name(self) -> str:
        """Имя иконки FluentIcon."""
        return "MOVIE"

    @property
    def file_extensions(self) -> list[str]:
        """Допустимые расширения файлов."""
        # Объединяем все поддерживаемые расширения
        return list(VIDEO_EXTENSIONS | AUDIO_EXTENSIONS | SUBTITLE_EXTENSIONS)

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
        file: Path,
        settings: dict[str, Any],
        output_path: str | None = None,
    ) -> list[str]:
        """Не используется в Муксере (переопределен execute)."""
        raise NotImplementedError("MuxerScript использует групповую обработку в execute()")

    def execute(
        self,
        files: list[Path],
        settings: dict[str, Any],
        output_path: str | None = None,
        progress_callback: ProgressCallback | None = None,
    ) -> list[str]:
        """Выполнить муксинг (групповая обработка)."""
        # 1. Группировка файлов по имени (stem)
        groups = defaultdict(lambda: {"video": None, "audio": None, "subs": None})
        overwrite = SettingsManager().overwrite_existing
        
        for file_path in files:
            stem = file_path.stem
            ext = file_path.suffix.lower()
            
            if ext in VIDEO_EXTENSIONS:
                if groups[stem]["video"] is None: 
                    groups[stem]["video"] = file_path
            
            elif ext in AUDIO_EXTENSIONS:
                 if groups[stem]["audio"] is None:
                    groups[stem]["audio"] = file_path
            
            elif ext in SUBTITLE_EXTENSIONS:
                 if groups[stem]["subs"] is None:
                    groups[stem]["subs"] = file_path

        # Отфильтруем группы без видео
        valid_groups = {k: v for k, v in groups.items() if v["video"] is not None}
        
        results: list[str] = []
        total = len(valid_groups)
        completed = 0

        subs_title = settings.get("subs_title", "Russian")
        clean_tracks = settings.get("clean_tracks", True)

        logger.info(
            "Настройки муксинга: заголовок сабов='%s', очистка дорожек=%s, файлов=%d",
            subs_title, clean_tracks, total
        )

        for stem, components in valid_groups.items():
            video_path = components["video"]
            audio_path = components["audio"]
            subs_path = components["subs"]
            
            target_dir = self._resolver.resolve(video_path, output_path)
            output_file_path = self._get_safe_output_path(video_path, target_dir / f"{stem}.mkv")

            if output_file_path.exists() and not overwrite:
                msg = f"⏭ Пропущен (файл существует): {output_file_path.name}"
                logger.info("[%s] %s", self.name, msg)
                results.append(msg)
                completed += 1
                if progress_callback:
                    progress_callback(completed, total, msg)
                continue

            # Подготовка входов для mkvmerge
            inputs = []
            
            # 1. Видео (основа)
            video_args = []
            if clean_tracks:
                if audio_path:
                    video_args.append("--no-audio")
                if subs_path:
                    video_args.append("--no-subtitles")
                
                video_args.extend([
                    "--no-global-tags",
                    "--no-track-tags"
                ])
            
            inputs.append({
                "path": video_path,
                "args": video_args
            })

            # 2. Аудио (доп)
            if audio_path:
                inputs.append({
                    "path": audio_path,
                    "args": ["--language", "0:und"]
                })

            # 3. Субтитры (доп)
            if subs_path:
                inputs.append({
                    "path": subs_path,
                    "args": [
                        "--language", "0:rus",
                        "--track-name", f"0:{subs_title}",
                        "--default-track", "0:yes"
                    ]
                })

            # Запуск
            success = self._runner.run(
                output_path=output_file_path,
                inputs=inputs,
                title=stem,
                overwrite=overwrite
            )

            if success:
                msg = f"✅ Собрано: {output_file_path.name}"
            else:
                msg = f"❌ Ошибка сборки: {output_file_path.name}"
            
            results.append(msg)
            completed += 1
            
            if progress_callback:
                progress_callback(completed, total, msg)

        return results
