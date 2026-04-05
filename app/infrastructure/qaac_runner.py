# -*- coding: utf-8 -*-
"""Модуль для запуска кодировщика QAAC."""

import logging
import subprocess
import os
from pathlib import Path

from app.core import path_utils
from app.core.singleton import SingletonMeta
from app.core.process_manager import ProcessManager

logger = logging.getLogger(__name__)


class QaacRunner(metaclass=SingletonMeta):
    """Класс для управления процессом qaac64.exe."""

    def __init__(self) -> None:
        """Инициализация runner'а."""
        self.__qaac_path: str | None = None
        self.__ffmpeg_path: str | None = None

    @property
    def _qaac_path(self) -> str:
        if self.__qaac_path is None:
            self.__qaac_path = path_utils.get_binary_path("qaac64")
            logger.debug(
                "QaacRunner инициализирован. qaac: %s", self.__qaac_path
            )
        return self.__qaac_path

    @property
    def _ffmpeg_path(self) -> str:
        if self.__ffmpeg_path is None:
            self.__ffmpeg_path = path_utils.get_binary_path("ffmpeg")
        return self.__ffmpeg_path

    def run(
        self,
        input_path: Path,
        output_path: Path,
        tvbr: str = "127",
        adts: bool = False,
        extra_args: list[str] | None = None,
        overwrite: bool = False,
    ) -> bool:
        """Запустить qaac64 через конвейер с FFmpeg.

        Это позволяет поддерживать любые входные форматы FFmpeg.

        Args:
            input_path: Путь к входному файлу.
            output_path: Путь к выходному файлу.
            tvbr: Уровень качества.
            adts: Формат ADTS.
            extra_args: Доп. аргументы.
            overwrite: Флаг перезаписи (переопределяется).

        Returns:
            True при успехе, False при ошибке.
        """
        if not Path(self._qaac_path).exists():
            logger.error("Бинарник qaac64.exe не найден: %s", self._qaac_path)
            return False

        ffmpeg_cmd = self._build_ffmpeg_cmd(input_path)
        qaac_cmd = self._build_qaac_cmd(output_path, tvbr, adts, extra_args)
        env = self._prepare_env()

        logger.info(
            "Запуск конвейера: %s | %s",
            " ".join(ffmpeg_cmd),
            " ".join(qaac_cmd),
        )

        return self._execute_pipeline(
            ffmpeg_cmd, qaac_cmd, env, input_path.name
        )

    def _build_ffmpeg_cmd(self, input_path: Path) -> list[str]:
        """Сформировать аргументы декодера ffmpeg."""
        return [
            self._ffmpeg_path,
            "-v",
            "error",
            "-i",
            str(input_path),
            "-f",
            "wav",
            "-",
        ]

    def _build_qaac_cmd(
        self,
        output_path: Path,
        tvbr: str,
        adts: bool,
        extra_args: list[str] | None,
    ) -> list[str]:
        """Сформировать аргументы кодировщика qaac."""
        cmd = [
            self._qaac_path,
            "--tvbr",
            tvbr,
            "-",
            "-o",
            str(output_path),
        ]
        if adts:
            cmd.insert(1, "--adts")
        if extra_args:
            cmd.extend(extra_args)
        return cmd

    def _prepare_env(self) -> dict[str, str]:
        """Настройка окружения для Apple Application Support."""
        base_dir = Path(self._qaac_path).parent
        pf = os.path.expandvars(
            r"%ProgramFiles%\Common Files\Apple\Apple Application Support"
        )
        pfx86 = os.path.expandvars(
            r"%ProgramFiles(x86)%\Common Files\Apple\Apple Application Support"
        )

        apple_paths = [
            str(base_dir / "QTFiles64"),
            str(base_dir / "QTFiles"),
            pf,
            pfx86,
        ]

        env = os.environ.copy()
        valid_paths = [str(base_dir)]

        for p in apple_paths:
            if os.path.isdir(p):
                valid_paths.append(p)

        env["PATH"] = (
            os.pathsep.join(valid_paths) + os.pathsep + env.get("PATH", "")
        )
        return env

    def _execute_pipeline(
        self,
        ffmpeg_cmd: list[str],
        qaac_cmd: list[str],
        env: dict[str, str],
        input_name: str,
    ) -> bool:
        """Запуск конвейера процессов и обработка ошибок."""
        try:
            ffmpeg_proc = subprocess.Popen(
                ffmpeg_cmd,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                creationflags=subprocess.CREATE_NO_WINDOW,
            )
            ProcessManager().register(ffmpeg_proc)

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
            ProcessManager().register(qaac_proc)

            if ffmpeg_proc.stdout:
                ffmpeg_proc.stdout.close()

            try:
                qaac_stdout, qaac_stderr = qaac_proc.communicate()
                ffmpeg_stderr = (
                    ffmpeg_proc.stderr.read().decode("utf-8", "replace")
                    if ffmpeg_proc.stderr
                    else ""
                )

                ffmpeg_proc.wait()
            finally:
                ProcessManager().unregister(qaac_proc)
                ProcessManager().unregister(ffmpeg_proc)

            return self._check_pipeline_results(
                qaac_proc, ffmpeg_proc, qaac_stderr, ffmpeg_stderr
            )

        except Exception:
            logger.exception(
                "Критическая ошибка конвейера QAAC для '%s'", input_name
            )
            return False

    def _check_pipeline_results(
        self,
        qaac_proc: subprocess.Popen,
        ffmpeg_proc: subprocess.Popen,
        qaac_stderr: str,
        ffmpeg_stderr: str,
    ) -> bool:
        """Проверка кодов возврата процессов конвейера."""
        from app.core.process_manager import ProcessManager

        if ProcessManager().was_cancelled(
            qaac_proc
        ) or ProcessManager().was_cancelled(ffmpeg_proc):
            logger.info("Процесс QAAC был прерван пользователем.")
            return False

        if qaac_proc.returncode != 0:
            logger.error(
                "QAAC завершился с ошибкой (%d).\nSTDERR: %s\nFFmpeg STDERR: %s",  # noqa: E501
                qaac_proc.returncode,
                qaac_stderr.strip(),
                ffmpeg_stderr.strip(),
            )
            return False

        if ffmpeg_proc.returncode != 0:
            logger.error(
                "FFmpeg (декодер) завершился с ошибкой (%d).\nSTDERR: %s",
                ffmpeg_proc.returncode,
                ffmpeg_stderr.strip(),
            )
            return False

        return True
