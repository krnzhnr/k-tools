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
        self._ffmpeg_path = path_utils.get_binary_path("ffmpeg")
        logger.info(
            "QaacRunner инициализирован. "
            "qaac: %s, ffmpeg for pipe: %s",
            self._qaac_path, self._ffmpeg_path
        )

    def run(
        self,
        input_path: Path,
        output_path: Path,
        tvbr: str = "127",
        adts: bool = False,
        extra_args: list[str] | None = None,
    ) -> bool:
        """Запустить qaac64 через конвейер с FFmpeg.

        Это позволяет поддерживать любые входные форматы, которые знает FFmpeg.

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
            logger.error("Бинарник qaac64.exe не найден: %s", self._qaac_path)
            return False

        # 1. Команда FFmpeg для декодирования в WAV через stdout
        ffmpeg_cmd = [
            self._ffmpeg_path,
            "-v", "error",
            "-i", str(input_path),
            "-f", "wav",
            "-",
        ]

        # 2. Команда QAAC для кодирования из stdin
        # ВАЖНО: --ignorelength необходим при чтении из пайпа
        qaac_cmd = [
            self._qaac_path,
            "--tvbr", tvbr,
            "--ignorelength",
            "-",
            "-o", str(output_path),
        ]

        if adts:
            qaac_cmd.insert(1, "--adts")

        if extra_args:
            qaac_cmd.extend(extra_args)

        logger.info(
            "Запуск конвейера: %s | %s",
            " ".join(ffmpeg_cmd), " ".join(qaac_cmd)
        )

        try:
            # Настройка окружения для библиотек Apple
            apple_paths = [
                str(Path(self._qaac_path).parent / "QTFiles64"),
                str(Path(self._qaac_path).parent / "QTFiles"),
                os.path.expandvars(r"%ProgramFiles%\Common Files\Apple\Apple Application Support"),
                os.path.expandvars(r"%ProgramFiles(x86)%\Common Files\Apple\Apple Application Support"),
            ]
            bin_dir = str(Path(self._qaac_path).parent)
            env = os.environ.copy()
            valid_paths = [bin_dir]
            for p in apple_paths:
                if os.path.isdir(p):
                    valid_paths.append(p)
            env["PATH"] = os.pathsep.join(valid_paths) + os.pathsep + env.get("PATH", "")

            # Запуск FFmpeg
            ffmpeg_proc = subprocess.Popen(
                ffmpeg_cmd,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                creationflags=subprocess.CREATE_NO_WINDOW,
            )

            # Запуск QAAC с stdout от FFmpeg на входе
            qaac_proc = subprocess.Popen(
                qaac_cmd,
                stdin=ffmpeg_proc.stdout,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True,
                encoding="utf-8",
                errors="replace",
                env=env,
                creationflags=subprocess.CREATE_NO_WINDOW,
            )

            # Разрешаем FFmpeg получить SIGPIPE, если QAAC закроется раньше
            if ffmpeg_proc.stdout:
                ffmpeg_proc.stdout.close()

            # Ждем завершения и собираем вывод
            qaac_stdout, qaac_stderr = qaac_proc.communicate()
            ffmpeg_stderr = ffmpeg_proc.stderr.read().decode("utf-8", "replace") if ffmpeg_proc.stderr else ""
            
            ffmpeg_proc.wait()

            if qaac_proc.returncode != 0:
                logger.error(
                    "QAAC завершился с ошибкой (%d).\nSTDERR: %s\nFFmpeg STDERR: %s",
                    qaac_proc.returncode, qaac_stderr.strip(), ffmpeg_stderr.strip()
                )
                return False

            if ffmpeg_proc.returncode != 0:
                logger.error(
                    "FFmpeg (декодер) завершился с ошибкой (%d).\nSTDERR: %s",
                    ffmpeg_proc.returncode, ffmpeg_stderr.strip()
                )
                return False

            return True

        except Exception:
            logger.exception("Критическая ошибка конвейера QAAC для '%s'", input_path.name)
            return False

        except Exception:
            logger.exception("Ошибка при запуске QAAC для файла '%s'", input_path.name)
            return False

