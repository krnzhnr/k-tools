# -*- coding: utf-8 -*-
"""Менеджер внешних зависимостей приложения.

Отвечает за проверку наличия, скачивание, верификацию
и распаковку внешних бинарных зависимостей (bin/).
"""

import logging
import urllib.request
import urllib.error
import zipfile
from enum import Enum
from pathlib import Path
from typing import Any

from PyQt6.QtCore import QThread, pyqtSignal

from app.core.dependency_manifest import (
    DEPENDENCY_MAP,
    DEPENDENCY_REGISTRY,
    DependencyInfo,
    get_download_url,
)
from app.core.path_utils import _get_base_dir
from app.core.singleton import SingletonMeta

logger = logging.getLogger(__name__)


class DependencyStatus(Enum):
    """Статус внешней зависимости."""

    INSTALLED = "installed"
    NOT_INSTALLED = "not_installed"
    DOWNLOADING = "downloading"
    EXTRACTING = "extracting"
    ERROR = "error"


class DependencyManager(metaclass=SingletonMeta):
    """Менеджер внешних зависимостей (Singleton).

    Проверяет наличие бинарников, управляет
    скачиванием и распаковкой архивов из GitHub Releases.
    """

    def __init__(self) -> None:
        """Инициализация менеджера зависимостей."""
        self._base_dir = _get_base_dir()
        self._bin_dir = self._base_dir / "bin"
        self._statuses: dict[str, DependencyStatus] = {}
        self._checksums: dict[str, str] = {}

        self._refresh_all_statuses()
        logger.info(
            "Менеджер зависимостей инициализирован. "
            "Директория bin: %s",
            self._bin_dir,
        )

    @property
    def bin_dir(self) -> Path:
        """Путь к директории bin/."""
        return self._bin_dir

    def _refresh_all_statuses(self) -> None:
        """Обновить статусы всех зависимостей."""
        for dep in DEPENDENCY_REGISTRY:
            if self._is_binary_present(dep):
                self._statuses[dep.key] = (
                    DependencyStatus.INSTALLED
                )
            else:
                self._statuses[dep.key] = (
                    DependencyStatus.NOT_INSTALLED
                )
        logger.debug(
            "Статусы зависимостей обновлены: %s",
            {k: v.value for k, v in self._statuses.items()},
        )

    def _is_binary_present(self, dep: DependencyInfo) -> bool:
        """Проверить наличие контрольного бинарника зависимости.

        Args:
            dep: Описание зависимости.

        Returns:
            True, если контрольный файл существует.
        """
        verify_path = (
            self._bin_dir / dep.subfolder / dep.verify_binary
        )
        return verify_path.exists()

    def get_status(self, key: str) -> DependencyStatus:
        """Получить текущий статус зависимости.

        Args:
            key: Идентификатор зависимости.

        Returns:
            Текущий статус.
        """
        return self._statuses.get(
            key, DependencyStatus.NOT_INSTALLED
        )

    def set_status(
        self,
        key: str,
        status: DependencyStatus,
    ) -> None:
        """Установить статус зависимости.

        Args:
            key: Идентификатор зависимости.
            status: Новый статус.
        """
        self._statuses[key] = status
        logger.debug(
            "Статус зависимости '%s' установлен: %s",
            key,
            status.value,
        )

    def is_installed(self, key: str) -> bool:
        """Проверить, установлена ли зависимость.

        Args:
            key: Идентификатор зависимости.

        Returns:
            True, если зависимость установлена.
        """
        dep = DEPENDENCY_MAP.get(key)
        if not dep:
            return False
        return self._is_binary_present(dep)

    def is_script_available(
        self,
        required_deps: list[str],
    ) -> bool:
        """Проверить, доступны ли все зависимости скрипта.

        Args:
            required_deps: Список ключей зависимостей.

        Returns:
            True, если все зависимости установлены.
        """
        return all(
            self.is_installed(key)
            for key in required_deps
        )

    def get_missing_deps(
        self,
        required_deps: list[str],
    ) -> list[DependencyInfo]:
        """Получить список недостающих зависимостей.

        Args:
            required_deps: Ключи необходимых зависимостей.

        Returns:
            Список объектов DependencyInfo для
            ненайденных зависимостей.
        """
        missing = []
        for key in required_deps:
            dep = DEPENDENCY_MAP.get(key)
            if dep and not self._is_binary_present(dep):
                missing.append(dep)
        return missing

    def get_all_statuses(
        self,
    ) -> dict[str, DependencyStatus]:
        """Получить актуальные статусы всех зависимостей.

        Returns:
            Словарь {key: DependencyStatus}.
        """
        self._refresh_all_statuses()
        return dict(self._statuses)

    def has_any_missing(self) -> bool:
        """Проверить, есть ли хотя бы одна ненайденная зависимость.

        Returns:
            True, если есть ненайденные зависимости.
        """
        return any(
            not self._is_binary_present(dep)
            for dep in DEPENDENCY_REGISTRY
        )

    def remove_dependency(self, key: str) -> bool:
        """Удалить установленную зависимость.

        Args:
            key: Идентификатор зависимости.

        Returns:
            True, если удаление прошло успешно.
        """
        import shutil

        dep = DEPENDENCY_MAP.get(key)
        if not dep:
            logger.warning(
                "Попытка удаления неизвестной зависимости: %s",
                key,
            )
            return False

        dep_dir = self._bin_dir / dep.subfolder
        if not dep_dir.exists():
            logger.info(
                "Зависимость '%s' уже удалена "
                "(директория не найдена).",
                dep.display_name,
            )
            self._statuses[key] = (
                DependencyStatus.NOT_INSTALLED
            )
            return True

        try:
            shutil.rmtree(dep_dir)
            self._statuses[key] = (
                DependencyStatus.NOT_INSTALLED
            )
            logger.info(
                "Зависимость '%s' успешно удалена: %s",
                dep.display_name,
                dep_dir,
            )
            return True
        except OSError:
            logger.exception(
                "Ошибка удаления зависимости '%s' "
                "из директории '%s'",
                dep.display_name,
                dep_dir,
            )
            return False


class DependencyDownloadWorker(QThread):
    """Фоновый поток для скачивания и распаковки зависимости.

    Signals:
        progress: Прогресс скачивания (0-100).
        status_changed: Смена статуса (key, DependencyStatus).
        download_finished: Завершение (key, success, error_msg).
    """

    progress = pyqtSignal(str, int)
    speed_updated = pyqtSignal(str, str)
    status_changed = pyqtSignal(str, object)
    download_finished = pyqtSignal(str, bool, str)

    def __init__(
        self,
        dep: DependencyInfo,
        bin_dir: Path,
        parent: Any = None,
    ) -> None:
        """Инициализация воркера скачивания зависимости.

        Args:
            dep: Описание зависимости для скачивания.
            bin_dir: Путь к директории bin/.
            parent: Родительский QObject.
        """
        super().__init__(parent)
        self._dep = dep
        self._bin_dir = bin_dir
        self._is_cancelled = False

    def cancel(self) -> None:
        """Отменить скачивание."""
        self._is_cancelled = True
        logger.info(
            "Запрошена отмена скачивания "
            "зависимости '%s'",
            self._dep.display_name,
        )

    def run(self) -> None:
        """Выполнение скачивания и распаковки."""
        import tempfile

        key = self._dep.key
        url = get_download_url(self._dep)

        self.status_changed.emit(
            key, DependencyStatus.DOWNLOADING
        )

        try:
            logger.info(
                "Начало скачивания зависимости '%s' "
                "из %s",
                self._dep.display_name,
                url,
            )

            self._bin_dir.mkdir(parents=True, exist_ok=True)

            # Скачивание во временный файл
            tmp_file = tempfile.NamedTemporaryFile(
                suffix=".zip",
                delete=False,
                dir=str(self._bin_dir),
            )
            tmp_path = Path(tmp_file.name)
            tmp_file.close()

            try:
                self._download_file(url, tmp_path)
            except Exception:
                # Удаляем неполный файл
                if tmp_path.exists():
                    tmp_path.unlink(missing_ok=True)
                raise

            if self._is_cancelled:
                tmp_path.unlink(missing_ok=True)
                logger.info(
                    "Скачивание '%s' отменено.",
                    self._dep.display_name,
                )
                self.download_finished.emit(
                    key, False, "Отменено"
                )
                return

            # Распаковка
            self.status_changed.emit(
                key, DependencyStatus.EXTRACTING
            )
            logger.info(
                "Распаковка архива '%s'...",
                self._dep.display_name,
            )

            self._extract_archive(tmp_path)

            # Удаление архива
            tmp_path.unlink(missing_ok=True)

            # Верификация
            dep_mgr = DependencyManager()
            if dep_mgr.is_installed(key):
                dep_mgr.set_status(
                    key, DependencyStatus.INSTALLED
                )
                self.status_changed.emit(
                    key, DependencyStatus.INSTALLED
                )
                self.download_finished.emit(
                    key, True, ""
                )
                logger.info(
                    "Зависимость '%s' успешно установлена.",
                    self._dep.display_name,
                )
            else:
                dep_mgr.set_status(
                    key, DependencyStatus.ERROR
                )
                self.status_changed.emit(
                    key, DependencyStatus.ERROR
                )
                error_msg = (
                    f"Контрольный файл "
                    f"'{self._dep.verify_binary}' "
                    f"не найден после распаковки."
                )
                self.download_finished.emit(
                    key, False, error_msg
                )
                logger.error(
                    "Верификация зависимости '%s' не "
                    "пройдена: %s",
                    self._dep.display_name,
                    error_msg,
                )

        except urllib.error.URLError:
            logger.exception(
                "Сетевая ошибка при скачивании "
                "зависимости '%s'",
                self._dep.display_name,
            )
            self.status_changed.emit(
                key, DependencyStatus.ERROR
            )
            self.download_finished.emit(
                key,
                False,
                "Не удалось подключиться к серверу.",
            )

        except Exception as e:
            logger.exception(
                "Непредвиденная ошибка при установке "
                "зависимости '%s'",
                self._dep.display_name,
            )
            self.status_changed.emit(
                key, DependencyStatus.ERROR
            )
            self.download_finished.emit(
                key, False, str(e)
            )

    def _download_file(
        self,
        url: str,
        dest: Path,
    ) -> None:
        """Скачать файл с прогрессом.

        Args:
            url: URL для скачивания.
            dest: Путь для сохранения.

        Raises:
            urllib.error.URLError: При сетевых ошибках.
            Exception: При прочих ошибках I/O.
        """
        import time

        req = urllib.request.Request(
            url,
            headers={"User-Agent": "K-Tools-DependencyManager"},
        )

        with urllib.request.urlopen(req, timeout=30) as resp:
            total = int(
                resp.headers.get("content-length", 0)
            )
            downloaded = 0
            block = 65536
            start_time = time.monotonic()
            last_speed_update = start_time

            with open(dest, "wb") as f:
                while True:
                    if self._is_cancelled:
                        return

                    buf = resp.read(block)
                    if not buf:
                        break

                    f.write(buf)
                    downloaded += len(buf)

                    if total > 0:
                        pct = int(downloaded * 100 / total)
                        self.progress.emit(
                            self._dep.key, pct
                        )

                    # Обновление скорости раз в секунду
                    now = time.monotonic()
                    if now - last_speed_update >= 1.0:
                        elapsed = now - start_time
                        if elapsed > 0:
                            speed = downloaded / elapsed
                            speed_str = (
                                self._format_speed(speed)
                            )
                            self.speed_updated.emit(
                                self._dep.key, speed_str
                            )
                        last_speed_update = now

    def _extract_archive(self, archive_path: Path) -> None:
        """Распаковать zip-архив в директорию bin/.

        Args:
            archive_path: Путь к zip-архиву.
        """
        dest = self._bin_dir / self._dep.subfolder
        dest.mkdir(parents=True, exist_ok=True)

        with zipfile.ZipFile(archive_path, "r") as zf:
            zf.extractall(dest)

        logger.info(
            "Архив распакован в: %s", dest
        )

    @staticmethod
    def _format_speed(bytes_per_sec: float) -> str:
        """Форматировать скорость скачивания.

        Args:
            bytes_per_sec: Байт в секунду.

        Returns:
            Строка вида '15.3 МБ/с' или '1.2 КБ/с'.
        """
        if bytes_per_sec >= 1_048_576:
            return f"{bytes_per_sec / 1_048_576:.1f} МБ/с"
        elif bytes_per_sec >= 1024:
            return f"{bytes_per_sec / 1024:.1f} КБ/с"
        return f"{bytes_per_sec:.0f} Б/с"
