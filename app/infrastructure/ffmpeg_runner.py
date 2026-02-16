# -*- coding: utf-8 -*-
"""Обёртка для запуска FFmpeg через subprocess."""

import logging
import os
import subprocess
from pathlib import Path
from app.core import path_utils

logger = logging.getLogger(__name__)


class FFmpegRunner:
    """Обёртка для безопасного запуска команд FFmpeg.

    Инкапсулирует формирование командной строки,
    запуск процесса и обработку ошибок.
    """

    def __init__(self) -> None:
        """Инициализация runner'а."""
        self._ffmpeg_path = path_utils.get_binary_path("ffmpeg")
        logger.info("FFmpegRunner инициализирован. Путь к бинарнику: %s", self._ffmpeg_path)

    def run(
        self,
        input_path: Path,
        output_path: Path,
        extra_args: list[str] | None = None,
    ) -> bool:
        """Запустить FFmpeg с указанными параметрами.

        Args:
            input_path: Путь к входному файлу.
            output_path: Путь к выходному файлу.
            extra_args: Дополнительные аргументы FFmpeg.

        Returns:
            True при успешном завершении, False при ошибке.
        """
        from app.core.settings_manager import SettingsManager
        
        overwrite = SettingsManager().overwrite_existing
        overwrite_flag = "-y" if overwrite else "-n"
        logger.info(
            "Подготовка команды FFmpeg. Файл на входе: '%s', файл на выходе: '%s'. "
            "Режим перезаписи: %s (флаг: %s)",
            input_path.name, output_path.name, 
            "ВКЛ" if overwrite else "ВЫКЛ", overwrite_flag
        )

        cmd = [
            self._ffmpeg_path,
            "-hide_banner",
            "-loglevel", "error",
            overwrite_flag,
            "-i", str(input_path),
        ]

        if extra_args:
            cmd.extend(extra_args)

        cmd.append(str(output_path))

        logger.info(
            "Выполнение команды FFmpeg: %s",
            " ".join(cmd),
        )

        # Подготовка окружения для загрузки DLL из папки bin
        bin_dir = str(Path(self._ffmpeg_path).parent)
        logger.debug("Рабочая директория процесса: %s", bin_dir)
        env = os.environ.copy()
        env["PATH"] = bin_dir + os.pathsep + env.get("PATH", "")
        logger.debug("Переменная окружения PATH дополнена путем к бинарнику")

        try:
            result = subprocess.run(
                cmd,
                capture_output=True,
                text=True,
                encoding="utf-8",
                errors="replace",
                check=False,
                cwd=bin_dir,
                env=env,
                creationflags=subprocess.CREATE_NO_WINDOW,
            )

            if result.returncode != 0:
                logger.error(
                    "FFmpeg завершился с ошибкой "
                    "(код %d): %s",
                    result.returncode,
                    result.stderr.strip(),
                )
                return False

            logger.info(
                "FFmpeg успешно обработал: %s → %s",
                input_path.name,
                output_path.name,
            )
            return True

        except FileNotFoundError:
            logger.exception(
                "FFmpeg не найден в PATH. "
                "Убедитесь, что ffmpeg установлен"
            )
            return False
        except Exception:
            logger.exception(
                "Непредвиденная ошибка при запуске "
                "FFmpeg для файла '%s'",
                input_path.name,
            )
            return False
