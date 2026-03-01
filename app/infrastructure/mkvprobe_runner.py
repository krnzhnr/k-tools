# -*- coding: utf-8 -*-
"""Модуль для получения информации о дорожках MKV.

Использует ``mkvmerge -J`` для анализа контейнера
и извлечения метаданных о потоках (видео, аудио, субтитры).
При отсутствии заголовков дорожек дополнительно
запрашивает ``pymediainfo`` для обогащения метаданных.
"""

import json
import logging
import os
import subprocess
from dataclasses import dataclass, replace
from pathlib import Path

from app.core.constants import normalize_language
from app.core import path_utils
from app.core.singleton import SingletonMeta
from app.core.process_manager import ProcessManager

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
    channels: int  # Количество каналов, 0 если не применимо
    is_default: bool = False
    is_forced: bool = False
    is_hearing_impaired: bool = False
    is_commentary: bool = False
    is_original: bool = False
    is_visual_impaired: bool = False

    @property
    def type_label(self) -> str:
        """Русское название типа дорожки."""
        return TRACK_TYPE_LABELS.get(self.track_type, self.track_type)


class MKVProbeRunner(metaclass=SingletonMeta):
    """Утилита для анализа MKV-контейнеров через mkvmerge."""

    def __init__(self) -> None:
        """Инициализация MKVProbeRunner."""
        self._mkvmerge_path = path_utils.get_binary_path("mkvmerge")
        logger.debug(
            "MKVProbeRunner инициализирован. Путь к бинарнику: %s",
            self._mkvmerge_path,
        )

    def identify(self, file_path: Path) -> dict:
        """Получить полный JSON-отчёт о файле."""
        cmd = [
            self._mkvmerge_path,
            "--identify",
            "--identification-format",
            "json",
            str(file_path),
        ]
        logger.info("Анализ файла: '%s'", file_path.name)
        logger.debug("Команда mkvmerge: %s", " ".join(cmd))

        bin_dir = str(Path(self._mkvmerge_path).parent)
        env = os.environ.copy()
        env["PATH"] = bin_dir + os.pathsep + env.get("PATH", "")

        try:
            return self._execute_identify(cmd, bin_dir, env, file_path)
        except FileNotFoundError:
            logger.exception(
                "mkvmerge не найден по пути: %s", self._mkvmerge_path
            )
            raise
        except Exception:
            logger.exception(
                "Непредвиденная ошибка при анализе файла '%s'", file_path.name
            )
            raise

    def _execute_identify(
        self, cmd: list[str], cwd: str, env: dict[str, str], file_path: Path
    ) -> dict:
        """Выполнить процесс идентификации mkvmerge."""
        process = subprocess.Popen(
            cmd,
            cwd=cwd,
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
            error_msg = "Анализ mkvmerge прерван пользователем."
            logger.info(error_msg)
            raise RuntimeError(error_msg)

        if process.returncode > 1:
            error_msg = f"mkvmerge вернул код {process.returncode}: {stderr}"
            logger.error(error_msg)
            raise RuntimeError(error_msg)

        try:
            data = json.loads(stdout)
            logger.info(
                "Файл '%s' успешно проанализирован. Найдено дорожек: %d",
                file_path.name,
                len(data.get("tracks", [])),
            )
            return data
        except json.JSONDecodeError:
            logger.exception(
                "Ошибка парсинга JSON от mkvmerge для файла '%s'",
                file_path.name,
            )
            raise RuntimeError("Невалидный JSON от mkvmerge")

    def get_tracks(self, file_path: Path) -> list[TrackInfo]:
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
            # Если есть тег IETF (н-р es-419), берем его, иначе fallback на IANA/ISO  # noqa: E501
            lang_raw = props.get("language_ietf", props.get("language", "und"))
            lang_norm = normalize_language(lang_raw)

            track = TrackInfo(
                track_id=raw_track.get("id", 0),
                track_type=raw_track.get("type", ""),
                codec=raw_track.get("codec", ""),
                language=lang_norm,
                name=props.get("track_name", ""),
                resolution=props.get(
                    "display_dimensions", props.get("pixel_dimensions", "")
                ),
                channels=props.get("audio_channels", 0),
                is_default=bool(props.get("default_track", False)),
                is_forced=bool(props.get("forced_track", False)),
                is_hearing_impaired=bool(
                    props.get("hearing_impaired_track", False)
                ),
                is_commentary=bool(props.get("commentary_track", False)),
                is_original=bool(props.get("original_network_id", False)),
                is_visual_impaired=bool(
                    props.get("visual_impaired_track", False)
                ),
            )
            # Уточним flag_original
            if "flag_original" in props:
                track = track.__class__(
                    **{
                        **track.__dict__,
                        "is_original": bool(props["flag_original"]),
                    }
                )
            tracks.append(track)
            logger.debug(
                "Дорожка #%d: тип=%s, кодек=%s, " "язык=%s, название='%s'",
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
            len([t for t in tracks if t.track_type == "video"]),
            len([t for t in tracks if t.track_type == "audio"]),
            len([t for t in tracks if t.track_type == "subtitles"]),
        )

        # Обогащение заголовков через ffprobe при их отсутствии.
        has_nameless = any(not t.name for t in tracks)
        if has_nameless:
            tracks = self._enrich_track_names(tracks, file_path)

        return tracks

    def _enrich_track_names(
        self,
        tracks: list[TrackInfo],
        file_path: Path,
    ) -> list[TrackInfo]:
        """Обогатить пустые заголовки дорожек данными из pymediainfo.

        mkvmerge и ffprobe не всегда извлекают поле ``track_name``
        (например, для Timed Text из MP4). В таких случаях
        запрашиваем pymediainfo и подставляем ``Title``.

        Args:
            tracks: Список дорожек с данными от mkvmerge.
            file_path: Путь к анализируемому файлу.

        Returns:
            Список дорожек с дополненными заголовками.
        """
        try:
            from pymediainfo import MediaInfo

            mi = MediaInfo.parse(str(file_path))
        except Exception:
            logger.exception(
                "Не удалось обогатить заголовки дорожек "
                "через pymediainfo для '%s'",
                file_path.name,
            )
            return tracks

        # Маппинг порядкового индекса → Title из pymediainfo.
        # stream_order может быть None (напр. для MP4),
        # поэтому используем позиционный индекс non-General дорожек.
        name_map: dict[int, str] = {}
        stream_idx = 0
        for mi_track in mi.tracks:
            if mi_track.track_type == "General":
                continue

            title = mi_track.title or ""
            # pymediainfo иногда оборачивает в кавычки.
            title = title.strip('"')

            # Определяем ключ: stream_order или позиция.
            if mi_track.stream_order is not None:
                key = int(mi_track.stream_order)
            else:
                key = stream_idx

            logger.debug(
                "pymediainfo дорожка: track_type=%s, " "idx=%d, title='%s'",
                mi_track.track_type,
                key,
                title,
            )

            if title:
                name_map[key] = title
            stream_idx += 1

        logger.debug(
            "pymediainfo name_map для '%s': %s",
            file_path.name,
            name_map,
        )

        if not name_map:
            return tracks

        enriched: list[TrackInfo] = []
        enriched_count = 0
        for track in tracks:
            mi_name = name_map.get(track.track_id, "")
            if not track.name and mi_name:
                track = replace(track, name=mi_name)
                enriched_count += 1
            enriched.append(track)

        if enriched_count:
            logger.info(
                "Обогащено заголовков дорожек из '%s' "
                "через pymediainfo: %d",
                file_path.name,
                enriched_count,
            )

        return enriched
