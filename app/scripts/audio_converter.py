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
from app.core.output_resolver import OutputResolver
from app.infrastructure.ffmpeg_runner import FFmpegRunner
from app.infrastructure.qaac_runner import QaacRunner

logger = logging.getLogger(__name__)

# Поддерживаемые форматы и их настройки кодеков
AUDIO_FORMATS = {
    "QAAC": {"ext": ".m4a", "codec": "qaac"},
    "AAC": {"ext": ".aac", "codec": "aac"},
    "FLAC": {"ext": ".flac", "codec": "flac"},
    "WAV": {"ext": ".wav", "codec": "pcm_s16le"},
    "AC3": {"ext": ".ac3", "codec": "ac3"},
    "EAC3": {"ext": ".eac3", "codec": "eac3"},
    "MP3": {"ext": ".mp3", "codec": "libmp3lame"},
    "OPUS": {"ext": ".opus", "codec": "libopus"},
    "OGG": {"ext": ".ogg", "codec": "libvorbis"},
    "DTS": {"ext": ".dts", "codec": "dca"},
    "WavPack": {"ext": ".wv", "codec": "wavpack"},
    "ALAC": {"ext": ".m4a", "codec": "alac"},
    "WMA": {"ext": ".wma", "codec": "wmav2"},
    "AIFF": {"ext": ".aiff", "codec": "pcm_s16be"},
    "ADPCM": {"ext": ".wav", "codec": "adpcm_ima_wav"},
}

# Группы форматов для видимости настроек
LOSSY_FORMATS = [
    "MP3", "AAC", "QAAC", "OGG", "AC3", "EAC3", "DTS", "WMA", "OPUS", "ADPCM"
]
# Форматы, для которых нужно отображать выбор битрейта (QAAC использует TVBR)
BITRATE_FORMATS = [f for f in LOSSY_FORMATS if f != "QAAC"]
LOSSLESS_COMPRESSED = ["FLAC", "WavPack"]


from app.core.constants import AUDIO_EXTENSIONS

class AudioConverterScript(AbstractScript):
    """Универсальная конвертация аудиофайлов.

    Позволяет конвертировать аудио между популярными
    форматами (MP3, FLAC, WAV, AAC, OGG и др.) с настройкой
    битрейта/качества и удалением исходника.
    """

    def __init__(self) -> None:
        """Инициализация аудио конвертера."""
        self._ffmpeg = FFmpegRunner()
        self._qaac = QaacRunner()
        self._resolver = OutputResolver()
        logger.info("Скрипт аудио конвертации создан")

    @property
    def category(self) -> str:
        """Категория скрипта."""
        return "Аудио"

    @property
    def name(self) -> str:
        """Отображаемое имя скрипта."""
        return "Транскодирование аудио"

    @property
    def description(self) -> str:
        """Описание скрипта."""
        return (
            "Перекодирует аудиофайлы в QAAC, AAC, FLAC, WAV, "
            "E-AC3, AC3 и др. с настройкой качества"
        )

    @property
    def icon_name(self) -> str:
        """Имя иконки FluentIcon."""
        return "MUSIC"

    @property
    def file_extensions(self) -> list[str]:
        """Допустимые расширения файлов."""
        return list(AUDIO_EXTENSIONS)

    @property
    def supports_parallel(self) -> bool:
        """Аудио-конвертация поддерживает параллелизм."""
        return True

    @property
    def settings_schema(self) -> list[SettingField]:
        """Схема настроек скрипта."""
        return [
            SettingField(
                key="target_format",
                label="Целевой формат",
                setting_type=SettingType.COMBO,
                default="QAAC",
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
                visible_if={"target_format": BITRATE_FORMATS},
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
            # Настройка качества QAAC (TVBR)
            SettingField(
                key="qaac_quality",
                label="Качество QAAC (0-127)",
                setting_type=SettingType.COMBO,
                default="127",
                options=[str(i) for i in range(0, 128, 16)] + ["127"],
                visible_if={"target_format": ["QAAC"]},
            ),
            # Опция контейнера M4A
            SettingField(
                key="use_m4a_container",
                label="Упаковать в контейнер (m4a)",
                setting_type=SettingType.CHECKBOX,
                default=False,
            ),
            SettingField(
                key="delete_original",
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
    ) -> list[str]:
        """Конвертировать один аудиофайл."""
        target_fmt_key = settings.get("target_format", "MP3")
        fmt_info = AUDIO_FORMATS.get(
            target_fmt_key, AUDIO_FORMATS["MP3"]
        )
        target_ext = fmt_info["ext"]
        codec = fmt_info["codec"]

        # Обработка контейнера M4A для форматов на базе AAC
        use_m4a = settings.get("use_m4a_container", False)
        if target_fmt_key in ["AAC", "QAAC"]:
            target_ext = ".m4a" if use_m4a else ".aac"

        bitrate = settings.get("bitrate", "320k")
        compression = settings.get("compression", "5")
        delete_original = settings.get(
            "delete_original", False
        )
        overwrite = SettingsManager().overwrite_existing

        results: list[str] = []
        
        # Пропускаем, если файл уже в целевом формате и это не lossy формат
        if (
            file_path.suffix.lower() == target_ext 
            and target_fmt_key not in LOSSY_FORMATS
        ):
            msg = f"⏭ Пропущен (уже {target_fmt_key}): {file_path.name}"
            logger.info("[%s] %s", self.name, msg)
            results.append(msg)
            return results

        target_dir = self._resolver.resolve(file_path, output_path)
        out_name = file_path.with_suffix(target_ext).name
        output_file_path = self._get_safe_output_path(
            file_path, target_dir / out_name
        )

        if output_file_path.exists() and not overwrite:
            # Превентивная защита (хотя _get_safe_output_path уже должен был решить это)
            msg = f"⏭ Пропущен (файл существует): {output_file_path.name}"
            logger.info("[%s] %s", self.name, msg)
            return [msg]
        
        # Формируем аргументы
        extra_args = ["-c:a", codec, "-map_metadata", "-1"]
        if target_fmt_key in LOSSLESS_COMPRESSED:
            extra_args.extend(["-compression_level", str(compression)])
        elif target_fmt_key in LOSSY_FORMATS:
            extra_args.extend(["-b:a", bitrate])
        
        if target_fmt_key == "DTS":
            extra_args.extend(["-strict", "-2"])

        # Выбор раннера
        if target_fmt_key == "QAAC":
            qaac_quality = settings.get("qaac_quality", "127")
            qaac_adts = not use_m4a
            success = self._qaac.run(
                input_path=file_path,
                output_path=output_file_path,
                tvbr=qaac_quality,
                adts=qaac_adts,
            )
        else:
            success = self._ffmpeg.run(
                input_path=file_path,
                output_path=output_file_path,
                extra_args=extra_args,
                overwrite=overwrite,
            )

        if success:
            results.append(f"✅ Конвертировано: {output_file_path.name}")
            if delete_original:
                self._delete_source(file_path, results)
        else:
            results.append(f"❌ Ошибка: {file_path.name}")

        return results
