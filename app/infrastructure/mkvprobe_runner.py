# -*- coding: utf-8 -*-
"""Модуль для получения информации о дорожках MKV.

Использует ``mkvmerge -J`` для анализа контейнера
и извлечения метаданных о потоках (видео, аудио, субтитры).
"""

import json
import logging
import os
import subprocess
from dataclasses import dataclass
from pathlib import Path

from app.core.constants import normalize_language
from app.core import path_utils

logger = logging.getLogger(__name__)

# Маппинг типов дорожек на русские названия.
TRACK_TYPE_LABELS: dict[str, str] = {
    "video": "Видео",
    "audio": "Аудио",
    "subtitles": "Субтитры",
}


@dataclass(frozen=True)
class TrackInfo:
    """Информация об одной дорожке MKV-контейнера.

    Attributes:
        track_id: ID потока в контейнере.
        track_type: Тип дорожки (video/audio/subtitles).
        codec: Название кодека.
        language: Код языка (ISO 639-2) или ``und``.
        name: Заголовок дорожки (может быть пустым).
    """

    track_id: int
    track_type: str
    codec: str
    language: str
    name: str
    resolution: str  # Формат: "1920x1080"
    channels: int    # Количество каналов, 0 если не применимо

    @property
    def type_label(self) -> str:
        """Русское название типа дорожки."""
        return TRACK_TYPE_LABELS.get(
            self.track_type, self.track_type
        )


class MKVProbeRunner:
    """Утилита для анализа MKV-контейнеров через mkvmerge."""

    def __init__(self) -> None:
        """Инициализация MKVProbeRunner."""
        self._mkvmerge_path = path_utils.get_binary_path(
            "mkvmerge"
        )
        logger.info(
            "MKVProbeRunner инициализирован. "
            "Путь к бинарнику: %s",
            self._mkvmerge_path,
        )

    def identify(self, file_path: Path) -> dict:
        """Получить полный JSON-отчёт о файле.

        Args:
            file_path: Путь к MKV-файлу.

        Returns:
            Словарь с полным отчётом mkvmerge.

        Raises:
            RuntimeError: При ошибке запуска mkvmerge.
        """
        cmd = [
            self._mkvmerge_path,
            "--identify",
            "--identification-format",
            "json",
            str(file_path),
        ]

        logger.info(
            "Анализ файла: '%s'", file_path.name
        )
        logger.debug(
            "Команда mkvmerge: %s", " ".join(cmd)
        )

        bin_dir = str(Path(self._mkvmerge_path).parent)
        env = os.environ.copy()
        env["PATH"] = (
            bin_dir + os.pathsep + env.get("PATH", "")
        )

        try:
            process = subprocess.run(
                cmd,
                cwd=bin_dir,
                env=env,
                capture_output=True,
                text=True,
                encoding="utf-8",
                errors="replace",
                creationflags=subprocess.CREATE_NO_WINDOW,
            )

            if process.returncode > 1:
                error_msg = (
                    f"mkvmerge вернул код "
                    f"{process.returncode}: "
                    f"{process.stderr}"
                )
                logger.error(error_msg)
                raise RuntimeError(error_msg)

            data = json.loads(process.stdout)
            logger.info(
                "Файл '%s' успешно проанализирован. "
                "Найдено дорожек: %d",
                file_path.name,
                len(data.get("tracks", [])),
            )
            return data

        except json.JSONDecodeError:
            logger.exception(
                "Ошибка парсинга JSON от mkvmerge "
                "для файла '%s'",
                file_path.name,
            )
            raise RuntimeError(
                "Невалидный JSON от mkvmerge"
            )
        except FileNotFoundError:
            logger.exception(
                "mkvmerge не найден по пути: %s",
                self._mkvmerge_path,
            )
            raise
        except Exception:
            logger.exception(
                "Непредвиденная ошибка при анализе "
                "файла '%s'",
                file_path.name,
            )
            raise

    def get_tracks(
        self, file_path: Path
    ) -> list[TrackInfo]:
        """Получить список дорожек файла.

        Args:
            file_path: Путь к MKV-файлу.

        Returns:
            Список объектов TrackInfo.
        """
        data = self.identify(file_path)
        tracks: list[TrackInfo] = []

        for raw_track in data.get("tracks", []):
            props = raw_track.get("properties", {})
            # Если есть тег IETF (н-р es-419), берем его, иначе fallback на IANA/ISO
            lang_raw = props.get("language_ietf", props.get("language", "und"))
            lang_norm = normalize_language(lang_raw)
            
            track = TrackInfo(
                track_id=raw_track.get("id", 0),
                track_type=raw_track.get("type", ""),
                codec=raw_track.get("codec", ""),
                language=lang_norm,
                name=props.get("track_name", ""),
                resolution=props.get("display_dimensions", props.get("pixel_dimensions", "")),
                channels=props.get("audio_channels", 0),
            )
            tracks.append(track)
            logger.debug(
                "Дорожка #%d: тип=%s, кодек=%s, "
                "язык=%s, название='%s'",
                track.track_id,
                track.type_label,
                track.codec,
                track.language,
                track.name,
            )

        logger.info(
            "Извлечено дорожек из '%s': %d "
            "(видео: %d, аудио: %d, субтитры: %d)",
            file_path.name,
            len(tracks),
            len([t for t in tracks
                 if t.track_type == "video"]),
            len([t for t in tracks
                 if t.track_type == "audio"]),
            len([t for t in tracks
                 if t.track_type == "subtitles"]),
        )
        return tracks
