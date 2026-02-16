# -*- coding: utf-8 -*-
"""Модуль для запуска eac3to."""

import logging
import os
import subprocess
import shutil
from pathlib import Path
from app.core import path_utils

logger = logging.getLogger(__name__)


class Eac3toRunner:
    """Обертка для запуска eac3to."""

    def __init__(self):
        """Инициализация runner'а."""
        self._executable = path_utils.get_binary_path("eac3to")
        logger.info("Eac3toRunner инициализирован. Путь к бинарнику: %s", self._executable)

    def run(self, args: list[str], cwd: Path | None = None) -> bool:
        """Запустить eac3to с аргументами.

        Args:
            args: Список аргументов командной строки.
            cwd: Рабочая директория.

        Returns:
            True, если команда выполнена успешно.
        """
        cmd = [str(self._executable)] + [str(arg) for arg in args]
        cmd_str = " ".join(cmd)
        
        logger.info(f"Подготовка команды eac3to с {len(args)} аргументами")
        logger.info(f"Выполнение команды eac3to: {cmd_str}")

        # Подготовка окружения для загрузки DLL из папки bin
        bin_dir = str(Path(self._executable).parent)
        logger.debug("Рабочая директория eac3to: %s", bin_dir if not cwd else cwd)
        env = os.environ.copy()
        env["PATH"] = bin_dir + os.pathsep + env.get("PATH", "")
        logger.debug("Переменная окружения PATH дополнена для eac3to")

        try:
            # eac3to пишет статус в stdout/stderr.
            process = subprocess.run(
                cmd,
                cwd=bin_dir if not cwd else cwd,
                env=env,
                capture_output=True,
                text=True,
                encoding="utf-8",
                errors="replace",
                creationflags=subprocess.CREATE_NO_WINDOW,
            )

            if process.returncode != 0:
                logger.error(f"Ошибка eac3to (code {process.returncode}):\n{process.stderr}\n{process.stdout}")
                return False
            
            # eac3to иногда пишет полезное в stdout даже при успехе
            logger.debug(f"Вывод eac3to:\n{process.stdout}")
            return True

        except FileNotFoundError:
            logger.error(f"Не найден исполняемый файл: {self._executable}")
            return False
        except Exception as e:
            logger.exception(f"Ошибка при запуске eac3to: {e}")
            return False
