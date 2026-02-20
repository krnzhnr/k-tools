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
        self._deew_path = path_utils.get_binary_path("deew")
        self._dee_path = path_utils.get_binary_path("dee")
        logger.info("DeewRunner инициализирован. deew: %s, dee: %s", self._deew_path, self._dee_path)

    def run(
        self,
        input_path: Path,
        output_path: Path,
        bitrate: str,
        output_format: str = "ddp",
        channels: int = 2,
    ) -> bool:
        """Запустить deew для даунмикса.

        Args:
            input_path: Путь к входному файлу.
            output_path: Путь к выходному файлу.
            bitrate: Битрейт (например, '448').
            output_format: Формат (ddp или dd).
            channels: Количество каналов (2 для стерео).

        Returns:
            True при успешном завершении, False при ошибке.
        """
        import tempfile
        import shutil

        # Проверка на кириллицу/спецсимволы (DEE их не любит в путях)
        is_safe = all(ord(c) < 128 for c in str(input_path) + str(output_path))
        
        # Директория для выполнения (где лежит dee.exe и конфиг)
        dee_dir = Path(self._dee_path).parent

        if not is_safe:
            logger.info("Обнаружены не-ASCII символы в пути. Используется режим безопасных имен.")
            # Создаем временную папку для работы
            with tempfile.TemporaryDirectory(dir=dee_dir) as temp_work_dir:
                temp_dir_path = Path(temp_work_dir)
                
                # Копируем входной файл под простым именем
                # (Хардлинк быстрее, если на том же диске, но копирование надежнее)
                temp_input = temp_dir_path / f"input{input_path.suffix}"
                shutil.copy2(input_path, temp_input)
                
                # Формируем команду для deew
                # deew по умолчанию создает файл с именем инпута в выходной директории
                cmd = [
                    self._deew_path,
                    "-i", "input" + input_path.suffix,
                    "-f", output_format,
                    "-dm", str(channels),
                    "-b", bitrate,
                    "-o", ".", # Текущая (временная) папка
                    "-np",
                    "-la",
                ]

                # Окружение
                ffmpeg_dir = str(Path(path_utils.get_binary_path("ffmpeg")).parent)
                deew_dir_str = str(Path(self._deew_path).parent)
                
                env = os.environ.copy()
                paths = [str(dee_dir), ffmpeg_dir, deew_dir_str]
                env["PATH"] = os.pathsep.join(paths) + os.pathsep + env.get("PATH", "")

                logger.info("Запуск deew в безопасном режиме: %s", " ".join(cmd))
                
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
                    logger.error("Ошибка deew в безопасном режиме: %s", result.stderr.strip() or result.stdout.strip())
                    return False

                # deew создает .ec3 для ddp и .ac3 для dd
                ext = ".ec3" if output_format == "ddp" else ".ac3"
                temp_output = temp_dir_path / f"input{ext}"
                
                if not temp_output.exists():
                    # Проверяем другие возможные расширения
                    alt_ext = ".eac3" if output_format == "ddp" else ".ac3"
                    temp_output = temp_dir_path / f"input{alt_ext}"
                
                if temp_output.exists():
                    # Перемещаем результат на финальное место с правильным именем
                    shutil.move(temp_output, output_path)
                    logger.info("deew успешно обработал файл (через временный буфер): %s", output_path.name)
                    return True
                else:
                    logger.error("Конвертация завершена успешно, но выходной файл не найден во временной папке!")
                    return False
        else:
            # Обычный режим (без кириллицы)
            cmd = [
                self._deew_path,
                "-i", str(input_path),
                "-f", output_format,
                "-dm", str(channels),
                "-b", bitrate,
                "-o", str(output_path.parent),
                "-np",
                "-la",
            ]

            ffmpeg_dir = str(Path(path_utils.get_binary_path("ffmpeg")).parent)
            deew_dir_str = str(Path(self._deew_path).parent)
            
            env = os.environ.copy()
            paths = [str(dee_dir), ffmpeg_dir, deew_dir_str]
            env["PATH"] = os.pathsep.join(paths) + os.pathsep + env.get("PATH", "")

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
