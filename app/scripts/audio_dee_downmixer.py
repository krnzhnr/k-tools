# -*- coding: utf-8 -*-
"""Скрипт даунмикса аудио в стерео (Stereo 2.0) через DEE/deew."""

from app.core.settings_manager import SettingsManager
from app.core.constants import (
    AUDIO_EXTENSIONS,
    VIDEO_CONTAINERS,
    ScriptCategory,
    ScriptMetadata,
)

# Std
import logging
from pathlib import Path
from typing import Any

# Local
from app.core.abstract_script import (
    AbstractScript,
    SettingField,
    SettingType,
    ProgressCallback,
)
from app.core.output_resolver import OutputResolver
from app.infrastructure.deew_runner import DeewRunner

logger = logging.getLogger(__name__)


class AudioDeeDownmixerScript(AbstractScript):
    """
    Скрипт для даунмикса многоканального
    аудио в Stereo (Dolby Digital Plus / DD).
    """

    def __init__(self):
        """Инициализация скрипта."""
        self._runner = DeewRunner()
        self._resolver = OutputResolver()

    @property
    def supports_parallel(self) -> bool:
        """DEE поддерживает параллелизм."""
        return True

    @property
    def category(self) -> str:
        """Категория скрипта."""
        return ScriptCategory.AUDIO

    @property
    def name(self) -> str:
        """Отображаемое имя скрипта."""
        return ScriptMetadata.AUDIO_DOWNMIX_NAME

    @property
    def description(self) -> str:
        """Описание скрипта."""
        return ScriptMetadata.AUDIO_DOWNMIX_DESC

    @property
    def icon_name(self) -> str:
        return "MIX_VOLUMES"

    @property
    def file_extensions(self) -> list[str]:
        """Допустимые расширения файлов."""
        return list(AUDIO_EXTENSIONS | VIDEO_CONTAINERS)

    @property
    def settings_schema(self) -> list[SettingField]:
        return [
            SettingField(
                key="format",
                label="Формат вывода",
                setting_type=SettingType.COMBO,
                options=["Dolby Digital Plus (E-AC3)", "Dolby Digital (AC3)"],
                default="Dolby Digital Plus (E-AC3)",
            ),
            SettingField(
                key="bitrate",
                label="Битрейт (kbps)",
                setting_type=SettingType.COMBO,
                options=[
                    "128",
                    "192",
                    "224",
                    "256",
                    "320",
                    "384",
                    "448",
                    "640",
                ],
                default="256",
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
        """Выполнить даунмикс для одного файла."""
        try:
            target_file_path, output_format = self._prepare_downmix_target(
                file_path,
                settings,
                output_path,
            )
            if target_file_path is None:
                return ["⏭ ПРОПУСК (файл уже существует)"]

            result_path = self._runner.run(
                input_path=file_path,
                output_path=target_file_path,
                bitrate=settings.get("bitrate", "192"),
                output_format=output_format,
                channels=2,
            )

            results: list[str] = []
            if result_path:
                # deew может создать файл с другим расширением
                if result_path != target_file_path:
                    final = result_path.rename(target_file_path)
                else:
                    final = result_path
                results.append(
                    f"✅ УСПЕХ: {file_path.name}" f" -> {final.name}"
                )
                if settings.get("delete_source", False):
                    self._delete_source(file_path, results)
            else:
                if self.is_cancelled:
                    self._cleanup_if_cancelled(target_file_path)
                    results.append(f"⚠ Отменено: " f"{target_file_path.name}")
                else:
                    results.append(f"❌ ОШИБКА DEE: {file_path.name}")
            return results

        except Exception as e:
            logger.exception(
                "Ошибка при даунмиксе '%s'",
                file_path.name,
            )
            return [f"❌ ОШИБКА: {file_path.name} ({e})"]

    def _prepare_downmix_target(
        self,
        file_path: Path,
        settings: dict[str, Any],
        output_path: str | None,
    ) -> tuple[Path | None, str]:
        """Определить пути и формат для даунмикса."""
        raw_format = settings.get("format", "Dolby Digital Plus (E-AC3)")
        is_eac3 = (
            "Plus" in raw_format
            or "E-AC3" in raw_format
            or "ddp" in raw_format.lower()
        )
        output_format = "ddp" if is_eac3 else "dd"
        output_ext = ".eac3" if is_eac3 else ".ac3"

        target_dir = self._resolver.resolve(file_path, output_path)
        actual_output_name = f"{file_path.stem}{output_ext}"
        target_file_path = self._get_safe_output_path(
            file_path, target_dir / actual_output_name
        )

        if (
            target_file_path.exists()
            and not SettingsManager().overwrite_existing
        ):
            logger.info(
                "[%s] ПРОПУСК (существует): %s",
                self.name,
                target_file_path.name,
            )
            return None, ""

        return target_file_path, output_format
