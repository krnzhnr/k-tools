# -*- coding: utf-8 -*-
"""Обёртка для запуска FFprobe через subprocess.

Используется для анализа MP4-контейнеров,
возвращает информацию о потоках с корректными
индексами, совместимыми с ffmpeg ``-map``.
"""

import json
import logging
import os
import subprocess
from dataclasses import dataclass
from pathlib import Path

from app.core import path_utils

logger = logging.getLogger(__name__)

# Маппинг типов потоков ffprobe → внутренние.
_CODEC_TYPE_MAP: dict[str, str] = {
    "video": "video",
    "audio": "audio",
    "subtitle": "subtitles",
}


@dataclass(frozen=True)
class StreamInfo:
    """Информация об одном потоке контейнера.

    Attributes:
        stream_index: Индекс потока (для -map).
        stream_type: Тип (video/audio/subtitles).
        codec: Название кодека.
        language: Код языка или ``und``.
        name: Заголовок потока.
    """

    stream_index: int
    stream_type: str
    codec: str
    language: str
    name: str

    @property
    def type_label(self) -> str:
        """Русское название типа потока."""
        labels = {
            "video": "Видео",
            "audio": "Аудио",
            "subtitles": "Субтитры",
        }
        return labels.get(
            self.stream_type, self.stream_type
        )


class FFProbeRunner:
    """Обёртка для анализа файлов через ffprobe.

    Возвращает информацию о потоках
    с корректными stream-индексами для ffmpeg.
    """

    def __init__(self) -> None:
        """Инициализация FFProbeRunner."""
        self._ffprobe_path = (
            path_utils.get_binary_path("ffprobe")
        )
        logger.info(
            "FFProbeRunner инициализирован. "
            "Путь к бинарнику: %s",
            self._ffprobe_path,
        )

    def get_streams(
        self, file_path: Path
    ) -> list[StreamInfo]:
        """Получить список потоков файла.

        Args:
            file_path: Путь к медиа-файлу.

        Returns:
            Список объектов StreamInfo.

        Raises:
            RuntimeError: При ошибке ffprobe.
        """
        cmd = [
            self._ffprobe_path,
            "-v", "quiet",
            "-print_format", "json",
            "-show_streams",
            str(file_path),
        ]

        logger.info(
            "Анализ файла через ffprobe: '%s'",
            file_path.name,
        )
        logger.debug(
            "Команда ffprobe: %s",
            " ".join(cmd),
        )

        bin_dir = str(
            Path(self._ffprobe_path).parent
        )
        env = os.environ.copy()
        env["PATH"] = (
            bin_dir
            + os.pathsep
            + env.get("PATH", "")
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
                check=False,
                creationflags=(
                    subprocess.CREATE_NO_WINDOW
                ),
            )

            if process.returncode != 0:
                error_msg = (
                    f"ffprobe вернул код "
                    f"{process.returncode}: "
                    f"{process.stderr}"
                )
                logger.error(error_msg)
                raise RuntimeError(error_msg)

            data = json.loads(process.stdout)

        except json.JSONDecodeError:
            logger.exception(
                "Ошибка парсинга JSON "
                "от ffprobe для '%s'",
                file_path.name,
            )
            raise RuntimeError(
                "Невалидный JSON от ffprobe"
            )
        except FileNotFoundError:
            logger.exception(
                "ffprobe не найден: %s",
                self._ffprobe_path,
            )
            raise
        except RuntimeError:
            raise
        except Exception:
            logger.exception(
                "Непредвиденная ошибка "
                "при анализе '%s'",
                file_path.name,
            )
            raise

        streams: list[StreamInfo] = []
        for raw in data.get("streams", []):
            codec_type = raw.get(
                "codec_type", ""
            )
            mapped_type = _CODEC_TYPE_MAP.get(
                codec_type, codec_type
            )
            tags = raw.get("tags", {})

            stream = StreamInfo(
                stream_index=raw.get("index", 0),
                stream_type=mapped_type,
                codec=raw.get(
                    "codec_name", ""
                ),
                language=tags.get(
                    "language", "und"
                ),
                name=tags.get("title", ""),
            )
            streams.append(stream)
            logger.debug(
                "Поток #%d: тип=%s, кодек=%s, "
                "язык=%s, название='%s'",
                stream.stream_index,
                stream.type_label,
                stream.codec,
                stream.language,
                stream.name,
            )

        logger.info(
            "Извлечено потоков из '%s': %d "
            "(видео: %d, аудио: %d, "
            "субтитры: %d)",
            file_path.name,
            len(streams),
            len([
                s for s in streams
                if s.stream_type == "video"
            ]),
            len([
                s for s in streams
                if s.stream_type == "audio"
            ]),
            len([
                s for s in streams
                if s.stream_type == "subtitles"
            ]),
        )
        return streams
