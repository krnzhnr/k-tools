# -*- coding: utf-8 -*-
"""Модуль для управления временными файлами и директориями.

Обеспечивает централизованное отслеживание созданных временных объектов
и их гарантированную очистку при выходе или следующем запуске.
"""

import logging
import os
import shutil
import tempfile
from pathlib import Path
from typing import Set

from app.core.singleton import SingletonMeta

logger = logging.getLogger(__name__)


class TempFileManager(metaclass=SingletonMeta):
    """Менеджер временных файлов.

    Регистрирует созданные временные пути для последующего удаления.
    Использует префикс 'ktools_' для всех объектов для легкой идентификации.
    """

    PREFIX = "ktools_"

    def __init__(self) -> None:
        """Инициализация менеджера."""
        self._tracked_paths: Set[Path] = set()
        logger.debug("TempFileManager инициализирован")

    def create_temp_dir(
        self, suffix: str | None = None, prefix: str | None = None
    ) -> Path:
        """Создать временную директорию и зарегистрировать её.

        Args:
            suffix: Опциональный суффикс имени.
            prefix: Опциональный префикс имени (по умолчанию ktools_).

        Returns:
            Путь к созданной директории.
        """
        effective_prefix = prefix or self.PREFIX
        temp_dir = Path(
            tempfile.mkdtemp(suffix=suffix, prefix=effective_prefix)
        )
        self._tracked_paths.add(temp_dir)
        logger.debug("Создана временная директория: %s", temp_dir)
        return temp_dir

    def create_temp_file(
        self, suffix: str | None = None, prefix: str | None = None
    ) -> Path:
        """Создать временный файл и зарегистрировать его.

        Args:
            suffix: Опциональный суффикс имени.
            prefix: Опциональный префикс имени (по умолчанию ktools_).

        Returns:
            Путь к созданному файлу.
        """
        effective_prefix = prefix or self.PREFIX
        # Используем mkstemp, чтобы файл остался после закрытия дескриптора
        fd, path_str = tempfile.mkstemp(suffix=suffix, prefix=effective_prefix)
        os.close(fd)
        temp_file = Path(path_str)
        self._tracked_paths.add(temp_file)
        logger.debug("Создан временный файл: %s", temp_file)
        return temp_file

    def register_path(self, path: Path) -> None:
        """Зарегистрировать существующий путь для удаления.

        Args:
            path: Путь к файлу или директории.
        """
        if path.exists():
            self._tracked_paths.add(path)
            logger.debug("Путь зарегистрирован в TempFileManager: %s", path)

    def delete_path(self, path: Path) -> None:
        """Немедленно удалить путь и снять его с отслеживания.

        Args:
            path: Путь к файлу или директории.
        """
        if path in self._tracked_paths:
            try:
                self._remove_path(path)
                self._tracked_paths.remove(path)
                logger.debug("Путь удален и снят с отслеживания: %s", path)
            except Exception:
                logger.exception(
                    "Ошибка при удалении пути через delete_path: %s", path
                )
        elif path.exists():
            # Если путь не в списке отслеживания,
            # но существует - просто удаляем
            self._remove_path(path)

    def cleanup(self) -> None:
        """Удалить все зарегистрированные пути текущей сессии."""
        if not self._tracked_paths:
            return

        logger.info("Запуск очистки временных файлов текущей сессии...")
        for path in list(self._tracked_paths):
            try:
                self._remove_path(path)
                self._tracked_paths.remove(path)
            except Exception:
                logger.exception(
                    "Не удалось удалить временный объект во время очистки: %s",
                    path,
                )
        logger.info("Очистка текущей сессии завершена")

    def cleanup_on_startup(self) -> None:
        """Найти и удалить остатки от предыдущих запусков.

        Ищет объекты с префиксом 'ktools_' в системной временной папке.
        """
        try:
            temp_root = Path(tempfile.gettempdir())
            logger.info("Поиск устаревших временных файлов в %s...", temp_root)

            # Безопасный поиск по префиксу
            found_count = 0
            for item in temp_root.glob(f"{self.PREFIX}*"):
                try:
                    self._remove_path(item)
                    found_count += 1
                except Exception:
                    # Логируем как отладку, так как файлы могут быть заняты
                    # другими инстансами или процессами
                    logger.debug(
                        "Не удалось удалить старый временный объект: %s", item
                    )

            if found_count > 0:
                logger.info(
                    "Очистка при запуске: удалено объектов: %d", found_count
                )
            else:
                logger.debug("Устаревших временных файлов не обнаружено")

        except Exception:
            logger.exception("Критическая ошибка при очистке при запуске")

    def _remove_path(self, path: Path) -> None:
        """Внутренний метод удаления файла или директории.

        Args:
            path: Целевой путь.
        """
        if not path.exists():
            return

        if path.is_dir():
            shutil.rmtree(path, ignore_errors=True)
            # Если после rmtree с ignore_errors объект остался (занят),
            # это будет обработано выше
        else:
            try:
                path.unlink()
            except PermissionError:
                # Файл занят, это нормально для Windows
                pass
            except Exception:
                raise
