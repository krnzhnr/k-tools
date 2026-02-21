# -*- coding: utf-8 -*-
"""Скрипт даунмикса аудио в стерео (Stereo 2.0) через DEE/deew."""

    # Std
import logging
from pathlib import Path
from typing import Any

    # Local
from app.core.abstract_script import AbstractScript, SettingField, SettingType
from app.core.output_resolver import OutputResolver
from app.infrastructure.deew_runner import DeewRunner

logger = logging.getLogger(__name__)


class AudioDeeDownmixerScript(AbstractScript):
    """Скрипт для даунмикса многоканального аудио в Stereo (Dolby Digital Plus / DD)."""

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
        return "Аудио"

    @property
    def name(self) -> str:
        return "Даунмикс в Stereo (DEE)"

    @property
    def description(self) -> str:
        return "Даунмикс 5.1/7.1 в Stereo 2.0 (DDP/DD) через Dolby Encoding Engine"

    @property
    def icon_name(self) -> str:
        return "MIX_VOLUMES"

    @property
    def file_extensions(self) -> list[str]:
        # deew поддерживает множество контейнеров, он сам вытащит аудио
        return [".ac3", ".eac3", ".dts", ".wav", ".flac", ".aac", ".thd", ".mka", ".mkv", ".mp4"]

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
                options=["128", "192", "224", "256", "320", "384", "448", "640"],
                default="256",
            ),
            SettingField(
                key="delete_source",
                label="Удалить исходный файл",
                setting_type=SettingType.CHECKBOX,
                default=False,
            ),
        ]

    def execute(
        self,
        files: list[Path],
        settings: dict[str, Any],
        output_path: str | None = None,
        progress_callback=None,
    ) -> list[str]:
        """Выполнить даунмикс (последовательно)."""
        results = []
        total = len(files)
        
        for i, file_path in enumerate(files):
            if progress_callback:
                progress_callback(i, total, f"Даунмикс: {file_path.name}")
            
            res = self.execute_single(file_path, settings, output_path)
            results.extend(res)
            
            if progress_callback:
                progress_callback(i + 1, total, res[-1] if res else "")
                
        return results

    def execute_single(
        self,
        file_path: Path,
        settings: dict[str, Any],
        output_path: str | None = None,
    ) -> list[str]:
        """Выполнить даунмикс для одного файла."""
        raw_format = settings.get("format", "Dolby Digital Plus (E-AC3)")
        if "Plus" in raw_format or "E-AC3" in raw_format or "ddp" in raw_format.lower():
            output_format = "ddp"
            output_ext = ".eac3"
        else:
            output_format = "dd"
            output_ext = ".ac3"
        
        bitrate = settings.get("bitrate", "192")
        delete_source = settings.get("delete_source", False)

        try:
            target_dir = self._resolver.resolve(file_path, output_path)
            actual_output_name = f"{file_path.stem}{output_ext}"
            target_file_path = self._get_safe_output_path(
                file_path, target_dir / actual_output_name
            )

            from app.core.settings_manager import SettingsManager
            if (
                target_file_path.exists() 
                and not SettingsManager().overwrite_existing
            ):
                return [f"⏭ Пропущен (существует): {target_file_path.name}"]

            success = self._runner.run(
                input_path=file_path,
                output_path=target_file_path,
                bitrate=bitrate,
                output_format=output_format,
                channels=2
            )

            results = []
            if success:
                results.append(f"✅ Успешно: {file_path.name} -> {actual_output_name}")
                if delete_source:
                    try:
                        file_path.unlink()
                        results.append(f"🗑️ Исходник удален: {file_path.name}")
                    except Exception as e:
                        logger.error("Ошибка удаления: %s", e)
            else:
                results.append(f"❌ Ошибка DEE: {file_path.name}")
            return results

        except Exception as e:
            logger.exception("Ошибка при обработке '%s'", file_path.name)
            return [f"❌ Ошибка: {file_path.name} ({e})"]
