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

    def register(self, script: AbstractScript) -> None:
        """Зарегистрировать скрипт в реестре.

        Args:
            script: Экземпляр скрипта для регистрации.
        """
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

    def __len__(self) -> int:
        """Количество зарегистрированных скриптов."""
        return len(self._scripts)
