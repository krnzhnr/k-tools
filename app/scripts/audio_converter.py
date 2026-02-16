# -*- coding: utf-8 -*-
"""Скрипт универсальной конвертации аудиофайлов."""

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
from app.infrastructure.ffmpeg_runner import FFmpegRunner

logger = logging.getLogger(__name__)

# Поддерживаемые форматы и их настройки кодеков
AUDIO_FORMATS = {
    "MP3": {"ext": ".mp3", "codec": "libmp3lame"},
    "AAC": {"ext": ".aac", "codec": "aac"},
    "OGG": {"ext": ".ogg", "codec": "libvorbis"},
    "FLAC": {"ext": ".flac", "codec": "flac"},
    "WAV": {"ext": ".wav", "codec": "pcm_s16le"},
    "AC3": {"ext": ".ac3", "codec": "ac3"},
    "EAC3": {"ext": ".eac3", "codec": "eac3"},
    "DTS": {"ext": ".dts", "codec": "dca"},
    "WavPack": {"ext": ".wv", "codec": "wavpack"},
    "AIFF": {"ext": ".aiff", "codec": "pcm_s16be"},
    "ALAC": {"ext": ".m4a", "codec": "alac"},
    "WMA": {"ext": ".wma", "codec": "wmav2"},
    "OPUS": {"ext": ".opus", "codec": "libopus"},
    "ADPCM": {"ext": ".wav", "codec": "adpcm_ima_wav"},
}

# Группы форматов для видимости настроек
LOSSY_FORMATS = [
    "MP3", "AAC", "OGG", "AC3", "EAC3", "DTS", "WMA", "OPUS"
]
LOSSLESS_COMPRESSED = ["FLAC", "WavPack"]


class AudioConverterScript(AbstractScript):
    """Универсальная конвертация аудиофайлов.

    Позволяет конвертировать аудио между популярными
    форматами (MP3, FLAC, WAV, AAC, OGG и др.) с настройкой
    битрейта/качества и удалением исходника.
    """

    def __init__(self) -> None:
        """Инициализация аудио конвертера."""
        self._ffmpeg = FFmpegRunner()
        logger.info("Скрипт аудио конвертации создан")

    @property
    def name(self) -> str:
        """Отображаемое имя скрипта."""
        return "Аудио Конвертер"

    @property
    def description(self) -> str:
        """Описание скрипта."""
        return (
            "Конвертирует аудиофайлы в MP3, FLAC, WAV, "
            "AAC, OGG, AC3 и др. с настройкой качества"
        )

    @property
    def icon_name(self) -> str:
        """Имя иконки FluentIcon."""
        return "MUSIC"

    @property
    def file_extensions(self) -> list[str]:
        """Допустимые расширения файлов."""
        return [
            ".mp3", ".flac", ".wav", ".m4a", ".ogg",
            ".wma", ".aiff", ".alac", ".ape", ".opus",
            ".ac3", ".eac3", ".dts", ".wv", ".aac"
        ]

    @property
    def settings_schema(self) -> list[SettingField]:
        """Схема настроек скрипта."""
        return [
            SettingField(
                key="target_format",
                label="Целевой формат",
                setting_type=SettingType.COMBO,
                default="MP3",
                options=list(AUDIO_FORMATS.keys()),
            ),
            # Настройка битрейта (для lossy форматов)
            SettingField(
                key="bitrate",
                label="Битрейт (кбит/с)",
                setting_type=SettingType.COMBO,
                default="320k",
                options=[
                    "64k", "96k", "128k", "160k", "192k",
                    "224k", "256k", "320k", "448k", "640k"
                ],
                visible_if={"target_format": LOSSY_FORMATS},
            ),
            # Настройка сжатия (для lossless форматов)
            SettingField(
                key="compression",
                label="Уровень сжатия (0-12)",
                setting_type=SettingType.COMBO,
                default="5",
                options=[str(i) for i in range(13)],
                visible_if={"target_format": LOSSLESS_COMPRESSED},
            ),
            SettingField(
                key="delete_original",
                label="Удалить исходный файл",
                setting_type=SettingType.CHECKBOX,
                default=False,
            ),
        ]

    def execute(
        self,
        files: list[Path],
        settings: dict[str, Any],
        progress_callback: ProgressCallback | None = None,
    ) -> list[str]:
        """Конвертировать аудиофайлы.

        Args:
            files: Список файлов для обработки.
            settings: Настройки конвертации.
            progress_callback: Callback прогресса.

        Returns:
            Список строк-результатов.
        """
        target_fmt_key = settings.get("target_format", "MP3")
        fmt_info = AUDIO_FORMATS.get(
            target_fmt_key, AUDIO_FORMATS["MP3"]
        )
        target_ext = fmt_info["ext"]
        codec = fmt_info["codec"]

        bitrate = settings.get("bitrate", "320k")
        compression = settings.get("compression", "5")
        delete_original = settings.get(
            "delete_original", False
        )

        logger.info(
            "Настройки аудио-конвертации: формат=%s, кодек=%s, битрейт=%s, сжатие=%s, удалять исходник=%s",
            target_fmt_key, codec, bitrate, compression, "ДА" if delete_original else "НЕТ"
        )

        results: list[str] = []
        total = len(files)

        logger.info(
            "Запуск аудио-конвертации для %d файл(ов) в формат %s",
            total,
            target_fmt_key,
        )

        for idx, file_path in enumerate(files):
            logger.info("Обработка аудио-файла [%d/%d]: '%s' (вход)", idx + 1, total, file_path.name)
            # Пропускаем, если файл уже в целевом формате
            if file_path.suffix.lower() == target_ext and \
               target_fmt_key not in LOSSY_FORMATS:
                 if file_path.suffix.lower() == target_ext:
                    msg = (
                        f"⏭ Пропущен (уже {target_fmt_key}): "
                        f"{file_path.name}"
                    )
                    results.append(msg)
                    logger.info("Файл '%s' уже имеет формат %s, пропуск", file_path.name, target_fmt_key)
                    continue

            output_path = file_path.with_suffix(target_ext)
            logger.debug("Целевой путь: '%s'", output_path.name)
            
            if output_path.exists() and not SettingsManager().overwrite_existing:
                msg = f"⏭ Пропущен (файл существует): {output_path.name}"
                logger.info("Пропуск: выходной файл '%s' уже существует", output_path.name)
                results.append(msg)
                if progress_callback:
                    progress_callback(idx + 1, total, msg)
                continue
            else:
                # Формируем аргументы в зависимости от формата
                extra_args = ["-c:a", codec, "-map_metadata", "-1"]

                if target_fmt_key in LOSSLESS_COMPRESSED:
                    extra_args.extend([
                        "-compression_level", str(compression)
                    ])
                elif target_fmt_key in LOSSY_FORMATS:
                    extra_args.extend([
                        "-b:a", bitrate
                    ])
                
                if target_fmt_key == "DTS":
                    extra_args.extend(["-strict", "-2"])

                logger.debug("Вызов FFmpeg для конвертации аудио")
                success = self._ffmpeg.run(
                    input_path=file_path,
                    output_path=output_path,
                    extra_args=extra_args,
                )

                if success:
                    msg = f"✅ Конвертировано: {output_path.name}"
                    logger.info("Успешная конвертация: '%s'", output_path.name)
                    if delete_original:
                        logger.info("Удаление исходника: '%s'", file_path.name)
                        self._delete_source(
                            file_path,
                            results,
                        )
                else:
                    msg = (
                        f"❌ Ошибка: "
                        f"{file_path.name}"
                    )
                    logger.error("Ошибка при конвертации аудио для файла: '%s'", file_path.name)

                results.append(msg)

            if progress_callback:
                progress_callback(
                    idx + 1,
                    total,
                    results[-1],
                )

        success_count = len([r for r in results if r.startswith("✅")])
        logger.info(
            "Процесс аудио-конвертации завершен. Итог: %d успешно, %d всего",
            success_count,
            total,
        )

        return results
