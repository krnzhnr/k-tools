# -*- coding: utf-8 -*-
"""Модуль с реализацией паттерна Singleton (Одиночка)."""

import threading
from typing import Any


class SingletonMeta(type):
    """Потокобезопасный метакласс для паттерна Singleton.

    Гарантирует, что у класса будет создан только один экземпляр,
    и метод __init__ будет вызван строго один раз за время жизни
    всего приложения.
    """

    _instances: dict[type, Any] = {}
    _lock: threading.Lock = threading.Lock()

    def __call__(cls, *args: Any, **kwargs: Any) -> Any:
        """Метод вызова класса (создание или возврат Одиночки)."""
        if cls not in cls._instances:
            with cls._lock:
                if cls not in cls._instances:
                    cls._instances[cls] = super().__call__(*args, **kwargs)
        return cls._instances[cls]

    @classmethod
    def _clear_instances(mcs) -> None:
        """Сброс всех экземпляров (только для тестов!)."""
        with mcs._lock:
            mcs._instances.clear()
