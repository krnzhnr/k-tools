# -*- coding: utf-8 -*-
"""Реестр скриптов — хранилище всех доступных скриптов."""

import logging
from typing import Sequence

from app.core.abstract_script import AbstractScript

logger = logging.getLogger(__name__)


class ScriptRegistry:
    """Реестр скриптов приложения.

    Хранит зарегистрированные экземпляры скриптов и
    предоставляет доступ к ним по индексу или имени.
    """

    def __init__(self) -> None:
        """Инициализация пустого реестра."""
        self._scripts: list[AbstractScript] = []
        logger.info("Реестр скриптов инициализирован")

    MAX_DESCRIPTION_LENGTH = 100

    def register(self, script: AbstractScript) -> None:
        """Зарегистрировать скрипт в реестре.

        Args:
            script: Экземпляр скрипта для регистрации.
        
        Raises:
            ValueError: Если описание скрипта слишком длинное.
        """
        if len(script.description) > self.MAX_DESCRIPTION_LENGTH:
            raise ValueError(
                f"Описание скрипта '{script.name}' слишком длинное "
                f"({len(script.description)} > {self.MAX_DESCRIPTION_LENGTH}). "
                f"Пожалуйста, сократите его до {self.MAX_DESCRIPTION_LENGTH} символов."
            )
            
        self._scripts.append(script)
        logger.info(
            "Скрипт '%s' зарегистрирован в реестре",
            script.name,
        )

    @property
    def scripts(self) -> Sequence[AbstractScript]:
        """Неизменяемый список зарегистрированных скриптов."""
        return tuple(self._scripts)

    def get_by_index(self, index: int) -> AbstractScript:
        """Получить скрипт по индексу.

        Args:
            index: Индекс скрипта в реестре.

        Returns:
            Скрипт по указанному индексу.

        Raises:
            IndexError: Если индекс вне диапазона.
        """
        return self._scripts[index]

    def find_by_name(self, name: str) -> AbstractScript | None:
        """Найти скрипт по его имени.

        Args:
            name: Имя скрипта для поиска.

        Returns:
            Экземпляр скрипта или None, если не найден.
        """
        for script in self._scripts:
            if script.name == name:
                return script
        return None

    def __len__(self) -> int:
        """Количество зарегистрированных скриптов."""
        return len(self._scripts)
