# -*- coding: utf-8 -*-
"""Модуль для запуска eac3to."""

import logging
import subprocess
import shutil
from pathlib import Path

logger = logging.getLogger(__name__)


class Eac3toRunner:
    """Обертка для запуска eac3to."""

    def __init__(self):
        """Инициализация runner'а."""
        self._executable = self._find_executable()

    def _find_executable(self) -> Path | str:
        """Найти исполняемый файл eac3to.
        
        Ищет в PATH и рядом с приложением.
        """
        # 1. Поиск в PATH
        executable = shutil.which("eac3to")
        if executable:
            return executable

        # 2. Поиск рядом с main.py (cwd)
        local_path = Path("eac3to.exe")
        if local_path.exists():
            return local_path.absolute()
            
        # 3. Поиск в папке tools/ (если есть)
        tools_path = Path("tools/eac3to.exe")
        if tools_path.exists():
            return tools_path.absolute()

        # Если не найдено, возвращаем просто команду в надежде на чудо или кидаем ошибку при запуске
        return "eac3to"

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
        
        logger.info(f"Запуск eac3to: {cmd_str}")

        try:
            # eac3to пишет статус в stdout/stderr.
            # Используем Popen для (потенциального) чтения вывода в реальном времени,
            # но пока просто блокирующе.
            process = subprocess.run(
                cmd,
                cwd=cwd,
                capture_output=True,
                text=True,
                encoding="utf-8", # eac3to может быть капризным с кодировкой, но попробуем utf-8
                errors="replace"
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
