# -*- coding: utf-8 -*-
"""Обёртка для запуска deew через subprocess."""

    # Std
import logging
import os
import subprocess
from pathlib import Path

    # Local
from app.core import path_utils

logger = logging.getLogger(__name__)


class DeewRunner:
    """Обёртка для безопасного запуска deew.

    Обеспечивает формирование командной строки и
    корректную настройку окружения для Dolby Encoding Engine.
    """

    def __init__(self) -> None:
        """Инициализация runner'а."""
        self._dee_path = path_utils.get_binary_path("dee")
        logger.info("DeewRunner инициализирован. Использование 'python -m deew', dee: %s", self._dee_path)

    def run(
        self,
        input_path: Path,
        output_path: Path,
        bitrate: str,
        output_format: str = "ddp",
        channels: int = 2,
    ) -> bool:
        """Запустить deew через python -m deew.

        Args:
            input_path: Путь к входному файлу.
            output_path: Путь к выходному файлу.
            bitrate: Битрейт (например, '448').
            output_format: Формат (ddp или dd).
            channels: Количество каналов (2 для стерео).

        Returns:
            True при успешном завершении, False при ошибке.
        """
        import sys
        import tempfile
        import shutil

        # Проверка на кириллицу/спецсимволы (DEE их не любит в путях)
        is_safe = all(ord(c) < 128 for c in str(input_path) + str(output_path))
        
        # Директория для выполнения (где лежит dee.exe и конфиг)
        dee_dir = Path(self._dee_path).parent

        # Общие аргументы для deew
        # Используем sys.executable -m deew для портативности внутри KTools.exe
        base_cmd = [
            sys.executable, "-m", "deew",
            "-f", output_format,
            "-dm", str(channels),
            "-b", bitrate,
            "-la",  # local-audio
            "-np",  # no-progress
        ]

        # Настройка окружения
        ffmpeg_dir = str(Path(path_utils.get_binary_path("ffmpeg")).parent)
        env = os.environ.copy()
        
        # Принудительно UTF-8 для предотвращения ошибок кодировки при выводе
        env["PYTHONIOENCODING"] = "utf-8"
        
        paths = [str(dee_dir), ffmpeg_dir]
        env["PATH"] = os.pathsep.join(paths) + os.pathsep + env.get("PATH", "")

        if not is_safe:
            logger.info("Обнаружены не-ASCII символы в пути. Используется режим безопасных имен.")
            with tempfile.TemporaryDirectory() as temp_work_dir:
                temp_dir_path = Path(temp_work_dir)
                temp_input = temp_dir_path / f"input{input_path.suffix}"
                shutil.copy2(input_path, temp_input)
                
                cmd = base_cmd + [
                    "-i", "input" + input_path.suffix,
                    "-o", ".",
                ]

                logger.info("Запуск deew (безопасный режим): %s", " ".join(cmd))
                
                result = subprocess.run(
                    cmd,
                    capture_output=True,
                    text=True,
                    encoding="utf-8",
                    errors="replace",
                    cwd=temp_work_dir,
                    env=env,
                    creationflags=subprocess.CREATE_NO_WINDOW,
                )

                if result.stdout.strip():
                    logger.debug("Вывод deew:\n%s", result.stdout.strip())

                if result.returncode != 0:
                    logger.error("Ошибка deew (безопасный режим): %s", result.stderr.strip() or result.stdout.strip())
                    return False

                ext = ".ec3" if output_format == "ddp" else ".ac3"
                temp_output = temp_dir_path / f"input{ext}"
                if not temp_output.exists():
                    alt_ext = ".eac3" if output_format == "ddp" else ".ac3"
                    temp_output = temp_dir_path / f"input{alt_ext}"
                
                if temp_output.exists():
                    shutil.move(temp_output, output_path)
                    logger.info("deew успешно обработал файл: %s", output_path.name)
                    return True
                else:
                    logger.error("Выходной файл не найден во временной папке!")
                    return False
        else:
            # Обычный режим
            cmd = base_cmd + [
                "-i", str(input_path),
                "-o", str(output_path.parent),
            ]

            try:
                result = subprocess.run(
                    cmd,
                    capture_output=True,
                    text=True,
                    encoding="utf-8",
                    errors="replace",
                    cwd=dee_dir,
                    env=env,
                    creationflags=subprocess.CREATE_NO_WINDOW,
                )

                if result.stdout.strip():
                    logger.debug("Вывод deew:\n%s", result.stdout.strip())

                if result.returncode != 0:
                    logger.error("Ошибка deew: %s", result.stderr.strip() or result.stdout.strip())
                    return False

                logger.info("deew успешно обработал: %s", input_path.name)
                return True

            except Exception:
                logger.exception("Ошибка при запуске deew для '%s'", input_path.name)
                return False
