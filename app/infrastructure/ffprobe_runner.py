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
from app.core.singleton import SingletonMeta
from app.core.process_manager import ProcessManager

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
    is_default: bool = False
    is_forced: bool = False
    is_hearing_impaired: bool = False
    is_commentary: bool = False
    is_original: bool = False

    @property
    def type_label(self) -> str:
        """Русское название типа потока."""
        labels = {
            "video": "Видео",
            "audio": "Аудио",
            "subtitles": "Субтитры",
        }
        return labels.get(self.stream_type, self.stream_type)


class FFProbeRunner(metaclass=SingletonMeta):
    """Обёртка для анализа файлов через ffprobe.

    Возвращает информацию о потоках
    с корректными stream-индексами для ffmpeg.
    """

    def __init__(self) -> None:
        """Инициализация FFProbeRunner."""
        self.__ffprobe_path: str | None = None

    @property
    def _ffprobe_path(self) -> str:
        """Ленивая загрузка пути к бинарнику."""
        if self.__ffprobe_path is None:
            self.__ffprobe_path = path_utils.get_binary_path("ffprobe")
            logger.debug(
                "FFProbeRunner инициализирован. Путь к бинарнику: %s",
                self.__ffprobe_path,
            )
        return self.__ffprobe_path

    def get_streams(self, file_path: Path) -> list[StreamInfo]:
        """Получить список потоков файла.

        Args:
            file_path: Путь к медиа-файлу.

        Returns:
            Список объектов StreamInfo.
        """
        data = self._run_ffprobe(file_path)
        return self._parse_streams(data, file_path.name)

    def _run_ffprobe(self, file_path: Path) -> dict:
        """Запуск ffprobe и получение ответа в виде словаря."""
        cmd = [
            self._ffprobe_path,
            "-v",
            "quiet",
            "-print_format",
            "json",
            "-show_streams",
            str(file_path),
        ]
        logger.info("Анализ файла через ffprobe: '%s'", file_path.name)
        logger.debug("Команда: %s", " ".join(cmd))

        bin_dir = str(Path(self._ffprobe_path).parent)
        env = os.environ.copy()
        env["PATH"] = bin_dir + os.pathsep + env.get("PATH", "")

        try:
            process = subprocess.Popen(
                cmd,
                cwd=bin_dir,
                env=env,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True,
                encoding="utf-8",
                errors="replace",
                creationflags=subprocess.CREATE_NO_WINDOW,
            )
            ProcessManager().register(process)
            try:
                stdout, stderr = process.communicate()
            finally:
                ProcessManager().unregister(process)

            if ProcessManager().was_cancelled(process):
                error_msg = "Анализ ffprobe прерван пользователем."
                logger.info(error_msg)
                raise RuntimeError(error_msg)

            if process.returncode != 0:
                err = f"ffprobe вернул код {process.returncode}: {stderr}"  # noqa: E501
                logger.error(err)
                raise RuntimeError(err)
            return json.loads(stdout)
        except json.JSONDecodeError:
            logger.exception(
                "Ошибка парсинга JSON от ffprobe для '%s'", file_path.name
            )
            raise RuntimeError("Невалидный JSON от ffprobe")
        except FileNotFoundError:
            logger.exception("ffprobe не найден: %s", self._ffprobe_path)
            raise
        except RuntimeError:
            raise
        except Exception:
            logger.exception(
                "Непредвиденная ошибка при анализе '%s'", file_path.name
            )
            raise

    def _parse_streams(self, data: dict, file_name: str) -> list[StreamInfo]:
        """Парсинг JSON вывода ffprobe в список объектов StreamInfo."""
        streams: list[StreamInfo] = []
        for raw in data.get("streams", []):
            codec_type = raw.get("codec_type", "")
            mapped_type = _CODEC_TYPE_MAP.get(codec_type, codec_type)
            tags = raw.get("tags", {})

            disposition = raw.get("disposition", {})
            stream = StreamInfo(
                stream_index=raw.get("index", 0),
                stream_type=mapped_type,
                codec=raw.get("codec_name", ""),
                language=tags.get("language", "und"),
                name=tags.get("title", ""),
                is_default=bool(disposition.get("default", False)),
                is_forced=bool(disposition.get("forced", False)),
                is_hearing_impaired=bool(
                    disposition.get("hearing_impaired", False)
                ),
                is_commentary=bool(disposition.get("comment", False)),
                is_original=bool(disposition.get("original", False)),
            )
            streams.append(stream)
            logger.debug(
                "Поток #%d: тип=%s, кодек=%s, язык=%s, название='%s'",
                stream.stream_index,
                stream.type_label,
                stream.codec,
                stream.language,
                stream.name,
            )

        logger.info(
            "Извлечено потоков из '%s': %d (видео: %d, аудио: %d, субтитры: %d)",  # noqa: E501
            file_name,
            len(streams),
            len([s for s in streams if s.stream_type == "video"]),
            len([s for s in streams if s.stream_type == "audio"]),
            len([s for s in streams if s.stream_type == "subtitles"]),
        )
        return streams
