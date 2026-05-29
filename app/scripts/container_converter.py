# -*- coding: utf-8 -*-
"""Скрипт конвертации контейнера видеофайлов."""

from app.core.constants import (
    VIDEO_CONTAINERS,
    ScriptCategory,
    ScriptMetadata,
)
import logging
from pathlib import Path
from typing import Any

from app.core.abstract_script import (
    AbstractScript,
    SettingField,
    SettingType,
    ProgressCallback,
)
from app.core.settings_manager import SettingsManager
from app.core.output_resolver import OutputResolver
from app.infrastructure.ffmpeg_runner import FFmpegRunner

logger = logging.getLogger(__name__)

# Маппинг отображаемого формата к расширению файла.
FORMAT_MAP: dict[str, str] = {
    "MP4": ".mp4",
    "MKV": ".mkv",
    "MOV": ".mov",
    "WEBM": ".webm",
    "AVI": ".avi",
    "TS": ".ts",
}


class ContainerConverterScript(AbstractScript):
    """Конвертация контейнера видео без перекодирования.

    Аналог 'mkv to mp4.bat' и 'mp4 to mkv.bat' —
    меняет контейнер с копированием потоков (-c copy).
    """

    def __init__(self) -> None:
        """Инициализация скрипта конвертации контейнера."""
        self._ffmpeg = FFmpegRunner()
        self._resolver = OutputResolver()
        logger.info("Скрипт конвертации контейнера создан")

    @property
    def category(self) -> str:
        """Категория скрипта."""
        return ScriptCategory.VIDEO

    @property
    def name(self) -> str:
        """Отображаемое имя скрипта."""
        return ScriptMetadata.CONTAINER_CONV_NAME

    @property
    def description(self) -> str:
        """Описание скрипта."""
        return ScriptMetadata.CONTAINER_CONV_DESC

    @property
    def icon_name(self) -> str:
        """Имя иконки FluentIcon."""
        return "SYNC"

    @property
    def file_extensions(self) -> list[str]:
        """Допустимые расширения файлов."""
        return list(VIDEO_CONTAINERS) + [".gif"]

    @property
    def required_dependencies(self) -> list[str]:
        """Зависимости: FFmpeg."""
        return ["ffmpeg"]

    @property
    def settings_schema(self) -> list[SettingField]:
        """Схема настроек скрипта."""
        return [
            SettingField(
                key="target_format",
                label="Целевой формат",
                setting_type=SettingType.COMBO,
                default="MP4",
                options=list(FORMAT_MAP.keys()),
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
        progress_callback: ProgressCallback | None = None,
        current: int = 0,
        total: int = 1,
    ) -> list[str]:
        """Конвертировать контейнер одного файла."""
        target_key = settings.get("target_format", "MP4")
        target_ext = FORMAT_MAP.get(target_key, ".mp4")

        if file_path.suffix.lower() == target_ext:
            logger.info(
                "Файл '%s' уже имеет расширение %s, пропуск",
                file_path.name,
                target_ext,
            )
            return [f"⏭ ПРОПУСК (уже {target_key}): {file_path.name}"]

        # Получаем информацию о потоках и проверяем совместимость
        info = self._ffmpeg.get_video_info(file_path)
        compatible, reason = self._check_compatibility(
            file_path,
            target_ext,
            info,
        )

        if not compatible:
            msg = (
                f"⚠ ПРОПУСК (требуется перекодирование): {file_path.name}. "
                f"{reason} Для перекодирования используйте "
                f"инструмент «{ScriptMetadata.VIDEO_PROCESSOR_NAME}»."
            )
            logger.warning("[%s] %s", self.name, msg)
            return [msg]

        target_dir = self._resolver.resolve(file_path, output_path)
        output_file_path = self._get_safe_output_path(
            file_path,
            target_dir / file_path.with_suffix(target_ext).name,
        )
        overwrite = SettingsManager().overwrite_existing

        if output_file_path.exists() and not overwrite:
            logger.info(
                "Пропуск: выходной файл '%s' уже существует",
                output_file_path.name,
            )
            return [
                f"⏭ ПРОПУСК (файл существует): {output_file_path.name}"
            ]

        # Вычисляем длительность для прогресса
        format_info = info.get("format", {}) if info else {}
        duration = float(format_info.get("duration", 0))

        return self._run_conversion(
            file_path,
            output_file_path,
            duration,
            settings.get("delete_original", False),
            overwrite,
            progress_callback,
            current,
            total,
        )

    def _check_compatibility(
        self,
        file_path: Path,
        target_ext: str,
        info: dict[str, Any] | None,
    ) -> tuple[bool, str]:
        """Проверить совместимость кодеков для ремуксинга.

        Проверяет, поддерживает ли целевой контейнер исходные кодеки
        видео и аудио без перекодирования.
        """
        if not info:
            return True, ""

        target_ext = target_ext.lower()
        input_ext = file_path.suffix.lower()

        if input_ext == ".gif" or target_ext == ".gif":
            return (
                False,
                "Формат GIF требует обязательного перекодирования.",
            )

        video_codec = ""
        audio_codec = ""
        has_audio = False

        for stream in info.get("streams", []):
            codec_type = stream.get("codec_type")
            if codec_type == "video":
                if not video_codec:
                    video_codec = stream.get("codec_name", "").lower()
            elif codec_type == "audio":
                has_audio = True
                if not audio_codec:
                    audio_codec = stream.get("codec_name", "").lower()

        if target_ext == ".mkv":
            return True, ""

        # Проверка видеокодека
        if video_codec:
            if target_ext in [".mp4", ".mov", ".ts", ".m2ts"]:
                if video_codec not in [
                    "h264",
                    "hevc",
                    "mpeg4",
                    "mpeg2video",
                    "av1",
                ]:
                    return (
                        False,
                        f"Видеокодек {video_codec.upper()} не поддерживается "
                        f"контейнером {target_ext.upper()} без "
                        f"перекодирования.",
                    )
            elif target_ext == ".webm":
                if video_codec not in ["vp8", "vp9", "av1"]:
                    return (
                        False,
                        f"Видеокодек {video_codec.upper()} не поддерживается "
                        f"контейнером WEBM без перекодирования.",
                    )
            elif target_ext == ".avi":
                if video_codec not in ["mpeg4", "h264", "mjpeg"]:
                    return (
                        False,
                        f"Видеокодек {video_codec.upper()} не поддерживается "
                        f"контейнером AVI без перекодирования.",
                    )

        # Проверка аудиокодека
        if has_audio and audio_codec:
            if target_ext in [".mp4", ".mov", ".ts", ".m2ts"]:
                if audio_codec not in [
                    "aac",
                    "mp3",
                    "ac3",
                    "eac3",
                    "mp2",
                ]:
                    return (
                        False,
                        f"Аудиокодек {audio_codec.upper()} не поддерживается "
                        f"контейнером {target_ext.upper()} без "
                        f"перекодирования.",
                    )
            elif target_ext == ".webm":
                if audio_codec not in ["opus", "vorbis"]:
                    return (
                        False,
                        f"Аудиокодек {audio_codec.upper()} не поддерживается "
                        f"контейнером WEBM без перекодирования.",
                    )
            elif target_ext == ".avi":
                if audio_codec not in ["mp3", "ac3", "pcm_s16le"]:
                    return (
                        False,
                        f"Аудиокодек {audio_codec.upper()} не поддерживается "
                        f"контейнером AVI без перекодирования.",
                    )

        return True, ""

    def _run_conversion(
        self,
        file_path: Path,
        output_file_path: Path,
        duration: float,
        delete_original: bool,
        overwrite: bool,
        progress_callback: ProgressCallback | None = None,
        current: int = 0,
        total: int = 1,
    ) -> list[str]:
        """Вызов FFmpeg для смены контейнера."""
        logger.debug("Старт FFmpeg для смены контейнера")

        def on_ffmpeg_progress(p_info: Any) -> None:
            if progress_callback:
                msg = (
                    f"Конвертация | {p_info.percent:.1f}% | "
                    f"Speed: {p_info.speed or 0}x"
                )
                progress_callback(current, total, msg, p_info.percent)

        success = self._ffmpeg.run(
            input_path=file_path,
            output_path=output_file_path,
            extra_args=["-c", "copy"],
            overwrite=overwrite,
            total_duration=duration,
            on_progress=on_ffmpeg_progress,
        )

        results: list[str] = []
        if success:
            msg = f"✅ Конвертировано: {output_file_path.name}"
            logger.info(
                "Успешная смена контейнера: '%s'",
                output_file_path.name,
            )
            if delete_original:
                self._delete_source(file_path, results)
        else:
            if self.is_cancelled:
                self._cleanup_if_cancelled(output_file_path)
                msg = f"⚠ Отменено: {output_file_path.name}"
                logger.info(
                    "Отмена смены контейнера: '%s'",
                    output_file_path.name,
                )
            else:
                msg = f"❌ ОШИБКА: {file_path.name}"
                logger.error(
                    "Ошибка при смене контейнера для файла: '%s'",
                    file_path.name,
                )

        results.insert(0, msg)
        return results
