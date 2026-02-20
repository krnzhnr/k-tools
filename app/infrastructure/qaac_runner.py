# -*- coding: utf-8 -*-
"""Модуль для запуска кодировщика QAAC."""

import logging
import subprocess
import os
from pathlib import Path
from typing import Any

from app.core import path_utils

logger = logging.getLogger(__name__)


class QaacRunner:
    """Класс для управления процессом qaac64.exe."""

    def __init__(self) -> None:
        """Инициализация runner'а."""
        self._qaac_path = path_utils.get_binary_path("qaac64")
        logger.info("QaacRunner инициализирован. Путь: %s", self._qaac_path)

    def run(
        self,
        input_path: Path,
        output_path: Path,
        tvbr: str = "127",
        adts: bool = False,
        extra_args: list[str] | None = None,
    ) -> bool:
        """Запустить qaac64 для кодирования.

        Args:
            input_path: Путь к входному файлу.
            output_path: Путь к выходному файлу.
            tvbr: Уровень качества (True Variable Bit Rate) от 0 до 127.
            adts: Если True, сохранять в формате ADTS (без контейнера).
            extra_args: Дополнительные аргументы командной строки.

        Returns:
            True при успешном завершении, False при ошибке.
        """
        if not Path(self._qaac_path).exists():
            logger.error("Бинарник qaac64.exe не найден по пути: %s", self._qaac_path)
            return False

        # Пример из bat: qaac64 --ignorelength --tvbr 127 "input" -o "output"
        cmd = [
            self._qaac_path,
            "--tvbr", tvbr,
        ]

        if adts:
            cmd.append("--adts")

        cmd.extend([
            str(input_path),
            "-o", str(output_path),
        ])

        if extra_args:
            cmd.extend(extra_args)

        logger.info("Выполнение команды QAAC: %s", " ".join(cmd))

        try:
            # 1. Формируем список потенциальных путей к библиотекам Apple
            apple_paths = [
                # Локальная портативная папка в проекте (рекомендуется)
                str(Path(self._qaac_path).parent / "QTFiles64"),
                str(Path(self._qaac_path).parent / "QTFiles"),
                # Стандартные пути установки Apple
                os.path.expandvars(r"%ProgramFiles%\Common Files\Apple\Apple Application Support"),
                os.path.expandvars(r"%ProgramFiles(x86)%\Common Files\Apple\Apple Application Support"),
                os.path.expandvars(r"%ProgramFiles%\iTunes"),
            ]

            bin_dir = str(Path(self._qaac_path).parent)
            env = os.environ.copy()
            
            # Собираем существующие и найденные пути
            valid_paths = [bin_dir]
            for p in apple_paths:
                if os.path.isdir(p):
                    valid_paths.append(p)
            
            env["PATH"] = os.pathsep.join(valid_paths) + os.pathsep + env.get("PATH", "")

            result = subprocess.run(
                cmd,
                capture_output=True,
                text=True,
                encoding="utf-8",
                errors="replace",
                env=env,
                creationflags=subprocess.CREATE_NO_WINDOW,
            )

            if result.returncode != 0:
                err_msg = result.stderr.strip() or result.stdout.strip()
                # Код 3221225477 (0xC0000005) - Access Violation
                if result.returncode == 3221225477 or result.returncode == -1073741819:
                    logger.error(
                        "QAAC критически завершился (Access Violation). "
                        "Это может быть вызвано конфликтом библиотек Apple или поврежденным входом."
                    )
                elif "CoreAudioToolbox.dll" in err_msg:
                    logger.error(
                        "QAAC не может работать: отсутствуют библиотеки Apple (CoreAudio).\n"
                        "Пожалуйста, установите iTunes или скопируйте библиотеки в папку bin/ffmpeg/QTFiles64."
                    )
                else:
                    logger.error(
                        "QAAC завершился с ошибкой (код %d)\nSTDOUT: %s\nSTDERR: %s",
                        result.returncode,
                        result.stdout.strip() or "пусто",
                        result.stderr.strip() or "пусто",
                    )
                return False

            if result.stdout.strip():
                logger.debug("Вывод QAAC:\n%s", result.stdout.strip())

            return True

        except Exception:
            logger.exception("Ошибка при запуске QAAC для файла '%s'", input_path.name)
            return False

