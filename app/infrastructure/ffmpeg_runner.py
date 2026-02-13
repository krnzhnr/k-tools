# -*- coding: utf-8 -*-
"""Обёртка для запуска FFmpeg через subprocess."""

import logging
import subprocess
from pathlib import Path

logger = logging.getLogger(__name__)


class FFmpegRunner:
    """Обёртка для безопасного запуска команд FFmpeg.

    Инкапсулирует формирование командной строки,
    запуск процесса и обработку ошибок.
    """

    FFMPEG_BINARY = "ffmpeg"

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
        cmd = [
            self.FFMPEG_BINARY,
            "-hide_banner",
            "-loglevel", "error",
            "-i", str(input_path),
        ]

        if extra_args:
            cmd.extend(extra_args)

        cmd.append(str(output_path))

        logger.info(
            "Запуск FFmpeg: %s",
            " ".join(cmd),
        )

        try:
            result = subprocess.run(
                cmd,
                capture_output=True,
                text=True,
                check=False,
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
