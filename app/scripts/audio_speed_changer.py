# -*- coding: utf-8 -*-
"""Скрипт изменения скорости аудио (FPS Converter) через eac3to."""

import logging
from pathlib import Path
from typing import Any

from app.core.abstract_script import (
    AbstractScript,
    SettingField,
    SettingType,
    ProgressCallback,
)
from app.core.output_resolver import OutputResolver
from app.infrastructure.eac3to_runner import Eac3toRunner
from app.core.constants import AUDIO_EXTENSIONS, ScriptCategory, ScriptMetadata
from app.core.settings_manager import SettingsManager

logger = logging.getLogger(__name__)


class AudioSpeedChangerScript(AbstractScript):
    """Скрипт для изменения скорости аудио (FPS)."""

    def __init__(self):
        """Инициализация скрипта."""
        self._runner = Eac3toRunner()
        self._resolver = OutputResolver()

    @property
    def supports_parallel(self) -> bool:
        """eac3to поддерживает параллелизм."""
        return True

    @property
    def category(self) -> str:
        """Категория скрипта."""
        return ScriptCategory.AUDIO

    @property
    def name(self) -> str:
        """Отображаемое имя скрипта."""
        return ScriptMetadata.AUDIO_SPEED_NAME

    @property
    def description(self) -> str:
        """Описание скрипта."""
        return ScriptMetadata.AUDIO_SPEED_DESC

    @property
    def icon_name(self) -> str:
        return "SPEED_HIGH"

    @property
    def file_extensions(self) -> list[str]:
        """Допустимые расширения файлов."""
        return list(AUDIO_EXTENSIONS)

    @property
    def settings_schema(self) -> list[SettingField]:
        return [
            SettingField(
                key="mode",
                label="Режим преобразования",
                setting_type=SettingType.COMBO,
                options=[
                    "Slowdown (25.000 → 23.976)",
                    "Speedup (23.976 → 25.000)",
                    "Custom (24.000 → 23.976)",
                    "Custom (25.000 → 24.000)",
                ],
                default="Slowdown (25.000 → 23.976)",
            ),
            SettingField(
                key="output_format",
                label="Формат вывода",
                setting_type=SettingType.COMBO,
                options=["FLAC", "WAV"],
                default="FLAC",
            ),
            SettingField(
                key="delete_source",
                label="Удалить исходный файл",
                setting_type=SettingType.CHECKBOX,
                default=False,
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
        """Выполнить изменение скорости для одного файла."""
        try:
            target_file_path, eac3to_args = self._prepare_speed_change(
                file_path, settings, output_path
            )
            if target_file_path is None:
                return ["⏭ ПРОПУСК (файл уже существует)"]

            current_args = [
                str(file_path),
                str(target_file_path),
            ] + eac3to_args
            success = self._runner.run(current_args, cwd=file_path.parent)

            results: list[str] = []
            if success:
                results.append(
                    f"✅ УСПЕХ: {file_path.name} -> {target_file_path.name}"
                )
                if settings.get("delete_source", False):
                    self._delete_source(file_path, results)
            else:
                if self.is_cancelled:
                    self._cleanup_if_cancelled(target_file_path)
                    results.append(f"⚠ Отменено: {target_file_path.name}")
                else:
                    results.append(f"❌ ОШИБКА eac3to: {file_path.name}")
            return results

        except Exception as e:
            logger.exception(
                "Ошибка при изменении скорости '%s'", file_path.name
            )
            return [f"❌ ОШИБКА: {file_path.name} ({e})"]

    def _prepare_speed_change(
        self,
        file_path: Path,
        settings: dict[str, Any],
        output_path: str | None,
    ) -> tuple[Path | None, list[str]]:
        """Определить пути и аргументы для изменения скорости."""
        mode = settings.get("mode")
        output_ext = f".{settings.get('output_format', 'FLAC').lower()}"

        eac3to_args, suffix = [], ""
        if mode == "Slowdown (25.000 → 23.976)":
            eac3to_args, suffix = ["-slowdown"], "_slowdown"
        elif mode == "Speedup (23.976 → 25.000)":
            eac3to_args, suffix = ["-speedup"], "_speedup"
        elif mode == "Custom (24.000 → 23.976)":
            eac3to_args, suffix = ["-24.000", "-slowdown"], "_24_to_23"
        elif mode == "Custom (25.000 → 24.000)":
            eac3to_args, suffix = ["-25.000", "-changeTo24.000"], "_25_to_24"

        target_dir = self._resolver.resolve(file_path, output_path)
        output_file_path = self._get_safe_output_path(
            file_path, target_dir / f"{file_path.stem}{suffix}{output_ext}"
        )

        if (
            output_file_path.exists()
            and not SettingsManager().overwrite_existing
        ):
            logger.info(
                "[%s] ПРОПУСК (существует): %s",
                self.name,
                output_file_path.name,
            )
            return None, []

        return output_file_path, eac3to_args
