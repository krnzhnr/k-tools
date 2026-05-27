# -*- coding: utf-8 -*-
"""Фоновый воркер для анализа дорожек MKV-контейнеров.

Выносит синхронные вызовы ``MKVProbeRunner.get_tracks()``
(subprocess + pymediainfo) из UI-потока, предотвращая
блокировку интерфейса при массовой загрузке дорожек.
"""

import logging
from pathlib import Path
from typing import Any

from PyQt6.QtCore import QRunnable, QObject, pyqtSignal

from app.infrastructure.mkvprobe_runner import (
    MKVProbeRunner,
    TrackInfo,
)

logger = logging.getLogger(__name__)


class ProbeWorkerSignals(QObject):
    """Сигналы фонового воркера анализа дорожек.

    Attributes:
        fileReady: Испускается после анализа одного файла.
            Аргументы: (путь, список_дорожек).
        fileError: Испускается при ошибке анализа одного файла.
            Аргументы: (путь, текст_ошибки).
        allFinished: Испускается после завершения анализа
            всех файлов.
    """

    fileReady = pyqtSignal(Path, list)
    fileError = pyqtSignal(Path, str)
    allFinished = pyqtSignal()


class ProbeWorker(QRunnable):
    """Фоновый воркер для анализа списка файлов.

    Выполняет ``MKVProbeRunner.get_tracks()`` для каждого
    файла из переданного списка и передаёт результаты
    обратно в UI-поток через сигналы.

    Args:
        file_paths: Список путей к файлам для анализа.
    """

    def __init__(
        self,
        file_paths: list[Path],
    ) -> None:
        """Инициализация воркера.

        Args:
            file_paths: Список путей к файлам.
        """
        super().__init__()
        self.signals = ProbeWorkerSignals()
        self._file_paths = list(file_paths)
        self._probe = MKVProbeRunner()
        self.setAutoDelete(True)

    def run(self) -> None:
        """Основной метод выполнения в фоновом потоке.

        Анализирует каждый файл последовательно и
        испускает сигналы с результатами.
        """
        logger.info(
            "Фоновый анализ дорожек запущен для %d файлов",
            len(self._file_paths),
        )

        for file_path in self._file_paths:
            try:
                tracks = self._probe.get_tracks(file_path)
                self.signals.fileReady.emit(file_path, tracks)
                logger.debug(
                    "Фоновый анализ завершён: '%s' "
                    "(%d дорожек)",
                    file_path.name,
                    len(tracks),
                )
            except Exception as exc:
                error_msg = str(exc)
                logger.exception(
                    "Ошибка фонового анализа файла '%s': %s",
                    file_path.name,
                    error_msg,
                )
                self.signals.fileError.emit(
                    file_path,
                    error_msg,
                )

        self.signals.allFinished.emit()
        logger.info(
            "Фоновый анализ дорожек завершён "
            "для всех %d файлов",
            len(self._file_paths),
        )
