# -*- coding: utf-8 -*-
"""Абстрактный базовый класс для всех скриптов обработки."""

import logging
from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from enum import Enum
from pathlib import Path
import threading
from typing import Any, Callable


logger = logging.getLogger(__name__)


class SettingType(Enum):
    """Типы полей настроек скрипта."""

    TEXT = "text"
    COMBO = "combo"
    CHECKBOX = "checkbox"


@dataclass(frozen=True)
class SettingField:
    """Описание одного поля настроек скрипта.

    Attributes:
        key: Уникальный ключ настройки.
        label: Отображаемое название настройки.
        setting_type: Тип виджета настройки.
        default: Значение по умолчанию.
        options: Варианты выбора для COMBO.
    """

    key: str
    label: str
    setting_type: SettingType
    default: Any = ""
    options: list[str] = field(default_factory=list)
    visible_if: dict[str, list[Any]] = field(default_factory=dict)


# Тип callback-функции для отчёта о прогрессе.
ProgressCallback = Callable[[int, int, str], None]


class AbstractScript(ABC):
    """Абстрактный базовый класс скрипта обработки файлов.

    Определяет контракт, которому должны следовать
    все конкретные реализации скриптов. Каждый скрипт
    предоставляет метаданные (имя, описание, расширения,
    схему настроек) и метод выполнения.
    """

    @property
    @abstractmethod
    def name(self) -> str:
        """Отображаемое имя скрипта."""

    @property
    @abstractmethod
    def category(self) -> str:
        """Категория скрипта (например, 'Аудио', 'Видео')."""

    @property
    @abstractmethod
    def description(self) -> str:
        """Краткое описание скрипта для UI."""

    @property
    @abstractmethod
    def icon_name(self) -> str:
        """Имя иконки из FluentIcon (например, 'VIDEO')."""

    @property
    @abstractmethod
    def file_extensions(self) -> list[str]:
        """Допустимые расширения входных файлов.

        Примеры: ['.mp4', '.mkv', '.avi'].
        """

    @property
    def settings_schema(self) -> list[SettingField]:
        """Схема настроек скрипта.

        По умолчанию возвращает пустой список.
        Переопредели в подклассе для добавления настроек.
        """
        return []

    @property
    def use_custom_widget(self) -> bool:
        """Использовать ли кастомный виджет файлов.

        Если True, WorkPanel должна использовать специфичный
        виджет (например, таблицу муксинга) вместо стандартного
        списка файлов.
        """
        return False

    @property
    def supports_parallel(self) -> bool:
        """Поддерживает ли скрипт параллельную обработку файлов.

        Если True, ScriptWorker может запускать обработку
        нескольких файлов одновременно в разных потоках.
        """
        return False

    @property
    def is_cancelled(self) -> bool:
        """Проверить, был ли скрипт отменен."""
        return getattr(self, "_is_cancelled", False)

    def cancel(self) -> None:
        """Отменить выполнение скрипта."""
        self._is_cancelled = True
        logger.info("Скрипт '%s' получил команду на отмену.", self.name)

    def _delete_source(
        self,
        file_path: Path,
        results: list[str],
    ) -> None:
        """Удалить исходный файл после обработки.

        Args:
            file_path: Путь к удаляемому файлу.
            results: Список результатов для лога.
        """
        try:
            file_path.unlink()
            msg = f"🗑 Удалён исходник: {file_path.name}"
            results.append(msg)
            logger.info(msg)
        except OSError:
            logger.exception(
                "Не удалось удалить файл '%s'",
                file_path.name,
            )
            results.append(f"⚠ Не удалось удалить: " f"{file_path.name}")

    def _replace_source_with_result(
        self,
        source_path: Path,
        result_path: Path,
        results: list[str],
    ) -> bool:
        """Заменить исходный файл результатом (физическая подмена).

        Этот метод удаляет оригинал и переименовывает результат работы скрипта
        так, чтобы он занял место оригинала с сохранением исходного имени.

        Returns:
            True, если замена прошла успешно.
        """
        try:
            # 1. Удаляем оригинал
            source_path.unlink()
            # 2. Переименовываем результат в имя оригинала
            result_path.rename(source_path)

            msg = f"🔄 Подменен оригинал: {source_path.name}"
            results.append(msg)
            logger.info(msg)
            return True
        except OSError as e:
            logger.exception(
                "Ошибка при подмене файла '%s': %s",
                source_path.name,
                e,
            )
            results.append(f"❌ Ошибка подмены: {source_path.name}")
            return False

    def _cleanup_if_cancelled(self, *paths: Path) -> None:
        """Удалить неполные выходные файлы при отмене работы скрипта."""
        if not self.is_cancelled:
            return

        for path in paths:
            if path and path.exists():
                try:
                    path.unlink(missing_ok=True)
                    logger.debug(
                        "🗑 Удален неполный выходной файл: %s", path.name
                    )
                except Exception as e:
                    logger.warning("⚠ Не удалось удалить %s: %s", path.name, e)

    def _get_safe_output_path(
        self,
        input_path: Path,
        output_path: Path,
    ) -> Path:
        """Получить безопасный путь для выходного файла.

        Защищает исходный файл от перезаписи (добавляет '_processed').
        """
        try:
            # Используем resolve() и сравнение строк в нижнем регистре для Windows  # noqa: E501
            in_resolved = str(input_path.resolve()).lower()
            out_resolved = str(output_path.resolve()).lower()

            logger.debug(
                "Сравнение путей: IN=%s, OUT=%s", in_resolved, out_resolved
            )

            # 1. Если выход совпадает с исходником — ОБЯЗАТЕЛЬНО меняем имя
            if in_resolved == out_resolved:
                output_path = (
                    output_path.parent
                    / f"{output_path.stem}_processed{output_path.suffix}"
                )
                logger.info(
                    "Защита исходника: добавлено '_processed' к имени (%s)",
                    output_path.name,
                )

            # 2. Защита от коллизий имен файлов при пакетной обработке
            if not hasattr(self, "_batch_reserved_paths"):
                self.prepare_batch()

            with self._batch_lock:
                original_stem = output_path.stem
                counter = 1
                while (
                    str(output_path.resolve()).lower()
                    in self._batch_reserved_paths
                ):
                    output_path = (
                        output_path.parent
                        / f"{original_stem}_{counter}{output_path.suffix}"
                    )
                    counter += 1

                self._batch_reserved_paths.add(
                    str(output_path.resolve()).lower()
                )

        except Exception as exc:
            logger.exception("Ошибка в _get_safe_output_path: %s", exc)

        return output_path

    def prepare_batch(self, input_files: list[Path] | None = None) -> None:
        """Подготовка к пакетной обработке.

        Очищает список зарезервированных путей для выходных файлов
        и добавляет входные пути пакета для предотвращения их перезаписи.
        Должен быть вызван перед началом обработки новой партии файлов.
        """
        if not hasattr(self, "_batch_reserved_paths"):
            self._batch_reserved_paths: set[str] = set()
            self._batch_lock = threading.Lock()
        with self._batch_lock:
            self._batch_reserved_paths.clear()
            if input_files:
                for f in input_files:
                    self._batch_reserved_paths.add(str(f.resolve()).lower())
        logger.debug(
            "Инициализирован список зарезервированных путей "
            "для пакетной обработки."
        )

    def execute(
        self,
        files: list[Path],
        settings: dict[str, Any],
        output_path: str | None = None,
        progress_callback: ProgressCallback | None = None,
    ) -> list[str]:
        """Выполнить обработку списка файлов (Template Method).

        Обеспечивает базовую логику цикла обработки. Скрипты, требующие
        группировки файлов (например, Муксер), переопределяют этот метод.
        Все остальные реализуют execute_single.

        Args:
            files: Список путей к входным файлам.
            settings: Словарь текущих настроек скрипта.
            output_path: Опциональный путь сохранения (ручной выбор).
            progress_callback: Callback (текущий, всего, сообщение).

        Returns:
            Список строк-результатов выполнения для лога.
        """
        results: list[str] = []
        total = len(files)

        for i, file_path in enumerate(files):
            if self.is_cancelled:
                msg = "⚠ Обработка прервана пользователем."
                logger.info(msg)
                results.append(msg)
                break

            if progress_callback:
                progress_callback(i, total, f"Обработка: {file_path.name}")

            # Обработка одного файла
            try:
                res = self.execute_single(file_path, settings, output_path)
                results.extend(res)
            except Exception as e:
                msg = f"❌ Критическая ошибка при обработке {file_path.name}: {e}"  # noqa: E501
                logger.exception(msg)
                results.append(msg)

            if progress_callback:
                # Показываем результат последнего обработанного файла в статусе
                status_msg = results[-1] if results else ""
                progress_callback(i + 1, total, status_msg)

        return results

    @abstractmethod
    def execute_single(
        self,
        file: Path,
        settings: dict[str, Any],
        output_path: str | None = None,
    ) -> list[str]:
        """Обработать один файл.

        Должен быть реализован во всех скриптах, поддерживающих
        пофайловую или параллельную обработку.

        Args:
            file: Путь к входному файлу.
            settings: Словарь настроек.
            output_path: Опциональный путь сохранения.

        Returns:
            Список строк-результатов.
        """
