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

    @abstractmethod
    def execute(
        self,
        files: list[Path],
        settings: dict[str, Any],
        progress_callback: ProgressCallback | None = None,
    ) -> list[str]:
        """Выполнить обработку списка файлов.

        Args:
            files: Список путей к входным файлам.
            settings: Словарь текущих настроек скрипта.
            progress_callback: Callback (текущий, всего, сообщение).

        Returns:
            Список строк-результатов выполнения для лога.
        """
