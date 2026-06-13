# -*- coding: utf-8 -*-
"""Менеджер для управления внешними (дочерними) процессами."""

# Std
import logging
import subprocess
import threading
import sys

# Local
from app.core.singleton import SingletonMeta

logger = logging.getLogger(__name__)


class ProcessManager(metaclass=SingletonMeta):
    """Потокобезопасный менеджер для отслеживания и прерывания процессов."""

    def __init__(self) -> None:
        """Инициализация менеджера процессов."""
        self._processes: set[subprocess.Popen] = set()
        self._lock = threading.Lock()

    def register(self, process: subprocess.Popen) -> None:
        """Зарегистрировать новый процесс для отслеживания состояний.

        Args:
            process: Объект Popen запущенного дочернего процесса.
        """
        with self._lock:
            self._processes.add(process)
            logger.debug(
                "Успешно зарегистрирован новый дочерний процесс: PID %s",
                process.pid,
            )

    def unregister(self, process: subprocess.Popen) -> None:
        """Удалить процесс из списка отслеживаемых после завершения.

        Args:
            process: Объект Popen запущенного дочернего процесса.
        """
        with self._lock:
            if process in self._processes:
                self._processes.remove(process)
                logger.debug(
                    "Успешно снят с регистрации процесс: PID %s",
                    process.pid,
                )

    def was_cancelled(self, process: subprocess.Popen) -> bool:
        """Проверить, был ли данный процесс отменен менеджером."""
        return getattr(process, "_was_cancelled", False)

    def cancel_all(self) -> None:
        """Централизованно прервать все зарегистрированные процессы."""
        with self._lock:
            if not self._processes:
                logger.debug(
                    "Состояние отмены: нет ни одного "
                    "активного процесса для прерывания."
                )
                return

            logger.info(
                "Инициирована процедура прерывания работы "
                "всех активных дочерних процессов (всего: %d шт.)",
                len(self._processes),
            )
            for process in list(self._processes):
                try:
                    logger.debug(
                        "Отправка сигнала завершения процессу PID %s "
                        "(и его дочерним процессам)",
                        process.pid,
                    )
                    setattr(process, "_was_cancelled", True)

                    if sys.platform == "win32":
                        # Принудительное завершение всего дерева процессов (/T)
                        subprocess.run(
                            [
                                "taskkill",
                                "/F",
                                "/T",
                                "/PID",
                                str(process.pid),
                            ],
                            capture_output=True,
                            creationflags=subprocess.CREATE_NO_WINDOW,
                            check=False,
                        )
                    else:
                        process.terminate()
                except Exception as exc:
                    logger.exception(
                        "Произошла критическая ошибка при попытке "
                        "завершить процесс с PID %s: %s",
                        process.pid,
                        exc,
                    )
