# -*- coding: utf-8 -*-
"""Обёртка для запуска FFmpeg через subprocess."""

import logging
import os
import subprocess
from pathlib import Path
from app.core import path_utils
from app.core.singleton import SingletonMeta
from app.core.process_manager import ProcessManager

logger = logging.getLogger(__name__)


class FFmpegRunner(metaclass=SingletonMeta):
    """Обёртка для безопасного запуска команд FFmpeg.

    Инкапсулирует формирование командной строки,
    запуск процесса и обработку ошибок.
    """

    def __init__(self) -> None:
        """Инициализация runner'а."""
        self.__ffmpeg_path: str | None = None

    @property
    def _ffmpeg_path(self) -> str:
        """Ленивая загрузка пути к бинарнику."""
        if self.__ffmpeg_path is None:
            self.__ffmpeg_path = path_utils.get_binary_path("ffmpeg")
            logger.debug(
                "FFmpegRunner инициализирован. Путь к бинарнику: %s",
                self.__ffmpeg_path,
            )
        return self.__ffmpeg_path

    def run(
        self,
        input_path: Path,
        output_path: Path,
        extra_args: list[str] | None = None,
        overwrite: bool = False,
    ) -> bool:
        """Запустить FFmpeg с указанными параметрами.

        Args:
            input_path: Путь к входному файлу.
            output_path: Путь к выходному файлу.
            extra_args: Дополнительные аргументы FFmpeg.
            overwrite: Нужно ли перезаписывать выходной файл.

        Returns:
            True при успешном завершении, False при ошибке.
        """
        cmd = self._build_cmd(input_path, output_path, extra_args, overwrite)
        return self._execute_process(cmd, input_path.name, output_path.name)

    def _build_cmd(
        self,
        input_path: Path,
        output_path: Path,
        extra_args: list[str] | None,
        overwrite: bool,
    ) -> list[str]:
        """Формирование команды FFmpeg."""
        overwrite_flag = "-y" if overwrite else "-n"
        logger.info(
            "Подготовка команды FFmpeg. Файл на входе: '%s', файл на выходе: '%s'. "  # noqa: E501
            "Режим перезаписи: %s (флаг: %s)",
            input_path.name,
            output_path.name,
            "ВКЛ" if overwrite else "ВЫКЛ",
            overwrite_flag,
        )

        cmd = [
            self._ffmpeg_path,
            "-hide_banner",
            "-loglevel",
            "error",
            overwrite_flag,
            "-i",
            str(input_path),
        ]

        if extra_args:
            cmd.extend(extra_args)

        cmd.append(str(output_path))
        logger.info("Выполнение команды FFmpeg: %s", " ".join(cmd))
        return cmd

    def _execute_process(
        self, cmd: list[str], input_name: str, output_name: str
    ) -> bool:
        """Выполнение процесса FFmpeg."""
        bin_dir = str(Path(self._ffmpeg_path).parent)
        logger.debug("Рабочая директория процесса: %s", bin_dir)

        env = os.environ.copy()
        env["PATH"] = bin_dir + os.pathsep + env.get("PATH", "")
        logger.debug("Переменная окружения PATH дополнена путем к бинарнику")

        try:
            process = subprocess.Popen(
                cmd,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True,
                encoding="utf-8",
                errors="replace",
                cwd=bin_dir,
                env=env,
                creationflags=subprocess.CREATE_NO_WINDOW,
            )
            ProcessManager().register(process)
            try:
                stdout, stderr = process.communicate()
            finally:
                ProcessManager().unregister(process)

            if ProcessManager().was_cancelled(process):
                logger.info("FFmpeg прерван пользователем.")
                return False

            if process.returncode != 0:
                logger.error(
                    "FFmpeg завершился с ошибкой (код %d)\nSTDOUT: %s\nSTDERR: %s",  # noqa: E501
                    process.returncode,
                    stdout.strip() if stdout else "пусто",
                    stderr.strip() if stderr else "пусто",
                )
                return False

            logger.info(
                "FFmpeg успешно обработал: %s → %s",
                input_name,
                output_name,
            )
            return True

        except FileNotFoundError:
            logger.exception(
                "FFmpeg не найден в PATH. Убедитесь, что ffmpeg установлен"
            )
            return False
        except Exception:
            logger.exception(
                "Непредвиденная ошибка при запуске FFmpeg для файла '%s'",
                input_name,
            )
            return False
