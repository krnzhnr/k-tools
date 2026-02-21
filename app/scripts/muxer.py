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
        return "Муксер (Video + Audio + Subs)"

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
        return [
            # Видео
            ".mkv", ".mp4", ".avi", ".mov", ".webm", ".hevc", ".h264",
            # Аудио
            ".mp3", ".aac", ".ac3", ".dts", ".eac3", ".flac", ".wav", ".m4a", ".ogg", ".mka",
            # Субтитры
            ".srt", ".ass", ".ssa", ".sub"
        ]

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
            # TODO: Можно добавить выбор языка, задержки и т.д.
        ]

    def execute(
        self,
        files: list[Path],
        settings: dict[str, Any],
        output_path: str | None = None,
        progress_callback: ProgressCallback | None = None,
    ) -> list[str]:
        """Выполнить муксинг.

        Args:
            files: Список всех файлов (видео, аудио, сабы).
            settings: Настройки.
            progress_callback: Callback прогресса.

        Returns:
            Результаты выполнения.
        """
        # 1. Группировка файлов по имени (stem)
        groups = defaultdict(lambda: {"video": None, "audio": None, "subs": None})
        
        for file_path in files:
            stem = file_path.stem
            ext = file_path.suffix.lower()
            
            if ext in [".mkv", ".mp4", ".avi", ".mov", ".webm", ".hevc", ".h264"]:
                if groups[stem]["video"] is None: 
                    groups[stem]["video"] = file_path
                # Если уже есть видео, возможно дубликат или конфликт, пока игнорируем
            
            elif ext in [".mp3", ".aac", ".ac3", ".dts", ".eac3", ".flac", ".wav", ".m4a", ".ogg", ".mka"]:
                 # Берем первый попавшийся аудиофайл с таким именем
                 if groups[stem]["audio"] is None:
                    groups[stem]["audio"] = file_path
            
            elif ext in [".srt", ".ass", ".ssa", ".sub"]:
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
            "Настройки муксинга: заголовок сабов='%s', очистка дорожек=%s",
            subs_title, "ДА" if clean_tracks else "НЕТ"
        )
        logger.info("Найдено групп для муксинга: %d", total)

        for stem, components in valid_groups.items():
            video_path = components["video"]
            audio_path = components["audio"]
            subs_path = components["subs"]
            
            logger.info("Обработка группы [%d/%d]: '%s'", completed + 1, total, stem)
            logger.debug("Компоненты группы: видео=%s, аудио=%s, сабы=%s", 
                         video_path.name, 
                         audio_path.name if audio_path else "нет", 
                         subs_path.name if subs_path else "нет")

            target_dir = self._resolver.resolve(
                video_path, output_path
            )
            output_file_path = target_dir / f"{stem}.mkv"

            if output_file_path.exists() and not SettingsManager().overwrite_existing:
                msg = f"⏭ Пропущен (файл существует): {output_file_path.name}"
                logger.info("Пропуск: выходной файл '%s' уже существует", output_file_path.name)
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
                logger.debug("Применение фильтрации дорожек для видео-источника")
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
                logger.debug("Добавление внешней аудиодорожки")
                inputs.append({
                    "path": audio_path,
                    "args": ["--language", "0:und"]
                })

            # 3. Субтитры (доп)
            if subs_path:
                logger.debug("Добавление внешних субтитров с заголовком '%s'", subs_title)
                inputs.append({
                    "path": subs_path,
                    "args": [
                        "--language", "0:rus",
                        "--track-name", f"0:{subs_title}",
                        "--default-track", "0:yes"
                    ]
                })

            # Запуск
            logger.debug("Вызов раннера mkvmerge")
            success = self._runner.run(
                output_path=output_file_path,
                inputs=inputs,
                title=stem # Заголовок файла = имя файла
            )

            if success:
                msg = f"✅ Собрано: {output_file_path.name}"
                logger.info("Успешно собран файл: '%s'", output_file_path.name)
            else:
                msg = f"❌ Ошибка сборки: {output_file_path.name}"
                logger.error("Ошибка mkvmerge при сборке файла: '%s'", output_file_path.name)
            
            results.append(msg)
            completed += 1
            
            if progress_callback:
                progress_callback(completed, total, msg)

        success_count = len([r for r in results if r.startswith("✅")])
        logger.info(
            "Процесс муксинга завершён. Итог: %d успешно из %d", 
            success_count, 
            total
        )
        return results
