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
        """Выполнить даунмикс."""
        total = len(files)
        completed = 0
        results = []

        raw_format = settings.get("format", "Dolby Digital Plus (E-AC3)")
        # Ищем ключевые слова для определения формата
        if "Plus" in raw_format or "E-AC3" in raw_format or "ddp" in raw_format.lower():
            output_format = "ddp"
            output_ext = ".eac3"
        else:
            output_format = "dd"
            output_ext = ".ac3"
        
        bitrate = settings.get("bitrate", "192")
        delete_source = settings.get("delete_source", False)

        logger.info(
            "Настройки даунмикса DEE: формат=%s, битрейт=%s, удалять исходник=%s",
            output_format, bitrate, "ДА" if delete_source else "НЕТ"
        )

        for i, file_path in enumerate(files):
            try:
                logger.info("Обработка файла [%d/%d]: '%s'", i + 1, total, file_path.name)
                
                if progress_callback:
                    progress_callback(i, total, f"Даунмикс: {file_path.name}")

                # Путь через резолвер
                target_dir = self._resolver.resolve(file_path, output_path)
                
                # Т.к. deew сам добавляет расширение или меняет его в зависимости от формата,
                # мы просто передаем папку в раннер, но для результата нам нужно знать имя.
                # Но deew -o принимает директорию. Имя он берет из инпута.
                output_file_name = f"{file_path.stem}.{output_format}{output_ext}" # deew делает так
                # На самом деле deew делает input.eac3 если format ddp
                actual_output_name = f"{file_path.stem}{output_ext}"
                target_file_path = target_dir / actual_output_name

                # Если файл существует и перезапись выключена
                from app.core.settings_manager import SettingsManager
                if target_file_path.exists() and not SettingsManager().overwrite_existing:
                    results.append(f"⏭ Пропущен: {actual_output_name}")
                    completed += 1
                    continue

                success = self._runner.run(
                    input_path=file_path,
                    output_path=target_file_path,
                    bitrate=bitrate,
                    output_format=output_format,
                    channels=2
                )

                if success:
                    completed += 1
                    results.append(f"✅ Успешно: {file_path.name} -> {actual_output_name}")
                    
                    if delete_source:
                        try:
                            file_path.unlink()
                            results.append(f"🗑️ Исходник удален: {file_path.name}")
                        except Exception as e:
                            logger.error("Ошибка удаления: %s", e)
                else:
                    results.append(f"❌ Ошибка DEE: {file_path.name}")

            except Exception as e:
                logger.exception("Ошибка при обработке '%s'", file_path.name)
                results.append(f"❌ Ошибка: {file_path.name} ({e})")

        return results
