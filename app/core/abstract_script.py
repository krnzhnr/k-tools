# -*- coding: utf-8 -*-
"""Абстрактный базовый класс для всех скриптов обработки."""

import logging
from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from enum import Enum
from pathlib import Path
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
    visible_if: dict[str, list[Any]] = field(
        default_factory=dict
    )


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
            results.append(
                f"⚠ Не удалось удалить: "
                f"{file_path.name}"
            )

    def _get_safe_output_path(
        self,
        input_path: Path,
        output_path: Path,
    ) -> Path:
        """Получить безопасный путь для выходного файла.

        Защищает исходный файл от перезаписи и предотвращает пропуски
        обработки, если целевой файл уже существует (добавляет '_processed').
        """
        import os
        from app.core.settings_manager import SettingsManager
        mgr = SettingsManager()

        try:
            in_norm = os.path.normcase(str(input_path.resolve()))
            out_norm = os.path.normcase(str(output_path.resolve()))

            # 1. Если выход совпадает с исходником — ОБЯЗАТЕЛЬНО меняем имя
            if in_norm == out_norm:
                output_path = output_path.parent / f"{output_path.stem}_processed{output_path.suffix}"
                logger.debug("Защита исходника: добавлено '_processed' к имени")

            # 2. Если такой файл уже существует и перезапись ВЫКЛЮЧЕНА
            # — тоже добавляем суффикс, чтобы не пропускать файл
            if output_path.exists() and not mgr.overwrite_existing:
                # Вторичная проверка: если вход и выход все еще совпадают после добавления (маловероятно)
                # или если мы просто хотим избежать пропуска
                output_path = output_path.parent / f"{output_path.stem}_processed{output_path.suffix}"
                logger.debug("Файл существует, перезапись выкл: добавлено '_processed'")
                
        except Exception as exc:
            logger.warning("Ошибка при проверке путей в _get_safe_output_path: %s", exc)

        return output_path

    @abstractmethod
    def execute(
        self,
        files: list[Path],
        settings: dict[str, Any],
        output_path: str | None = None,
        progress_callback: ProgressCallback | None = None,
    ) -> list[str]:
        """Выполнить обработку списка файлов.

        Args:
            files: Список путей к входным файлам.
            settings: Словарь текущих настроек скрипта.
            output_path: Опциональный путь сохранения (ручной выбор).
            progress_callback: Callback (текущий, всего, сообщение).

        Returns:
            Список строк-результатов выполнения для лога.
        """

    def execute_single(
        self,
        file: Path,
        settings: dict[str, Any],
        output_path: str | None = None,
    ) -> list[str]:
        """Обработать один файл (для параллельного режима).

        По умолчанию просто вызывает execute для одного файла.
        Переопредели для оптимизации.

        Args:
            file: Путь к файлу.
            settings: Настройки.
            output_path: Опциональный путь сохранения.

        Returns:
            Список строк-результатов.
        """
        return self.execute(
            files=[file],
            settings=settings,
            output_path=output_path,
            progress_callback=None,
        )
