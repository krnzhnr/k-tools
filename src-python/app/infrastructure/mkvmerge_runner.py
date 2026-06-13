# -*- coding: utf-8 -*-
"""Модуль для запуска mkvmerge (MKVToolNix)."""

import logging
import os
import subprocess
from pathlib import Path
from typing import Any

from app.core import path_utils
from app.core.singleton import SingletonMeta
from app.core.process_manager import ProcessManager

logger = logging.getLogger(__name__)


class MKVMergeRunner(metaclass=SingletonMeta):
    """Обертка для запуска mkvmerge."""

    def __init__(self) -> None:
        """Инициализация runner'а."""
        self.__mkvmerge_path: str | None = None

    @property
    def _mkvmerge_path(self) -> str:
        """Ленивая загрузка пути к бинарнику."""
        if self.__mkvmerge_path is None:
            self.__mkvmerge_path = path_utils.get_binary_path("mkvmerge")
            logger.debug(
                "MKVMergeRunner инициализирован. Путь к бинарнику: %s",
                self.__mkvmerge_path,
            )
        return self.__mkvmerge_path

    def run(
        self,
        output_path: Path,
        inputs: list[dict[str, Any]],
        title: str | None = None,
        overwrite: bool = False,
        extra_args: list[str] | None = None,
    ) -> bool:
        """Запустить mkvmerge.

        Args:
            output_path: Путь к выходному файлу.
            inputs: Список входных файлов с параметрами.
            title: Глобальный заголовок файла (опционально).

        Returns:
            True при успешном завершении, иначе False.
        """
        cmd = self._build_cmd(output_path, inputs, title, extra_args)
        env, bin_dir = self._prepare_env()
        return self._execute_process(cmd, env, bin_dir)

    def _build_cmd(
        self,
        output_path: Path,
        inputs: list[dict[str, Any]],
        title: str | None,
        extra_args: list[str] | None = None,
    ) -> list[str]:
        """Формирование команды mkvmerge."""
        cmd = [self._mkvmerge_path, "--output", str(output_path)]

        if title:
            cmd.extend(["--title", title])

        if extra_args:
            cmd.extend(extra_args)

        for inp in inputs:
            if "args" in inp and inp["args"]:
                cmd.extend(inp["args"])
            cmd.append(str(inp["path"]))

        logger.info(
            "Подготовка команды mkvmerge. Выходной файл: '%s'. "
            "Количество входов: %d. Глобальный заголовок: '%s'",
            output_path.name,
            len(inputs),
            title or "нет",
        )
        logger.info(
            "Выполнение команды mkvmerge: %s", subprocess.list2cmdline(cmd)
        )
        return cmd

    def _prepare_env(self) -> tuple[dict[str, str], str]:
        """Настройка окружения для запуска mkvmerge."""
        bin_dir = str(Path(self._mkvmerge_path).parent)
        logger.debug("Рабочая директория mkvmerge: %s", bin_dir)
        env = os.environ.copy()
        env["PATH"] = bin_dir + os.pathsep + env.get("PATH", "")
        logger.debug("Переменная окружения PATH дополнена для mkvmerge")
        return env, bin_dir

    def _execute_process(
        self, cmd: list[str], env: dict[str, str], bin_dir: str
    ) -> bool:
        """Выполнение процесса mkvmerge."""
        try:
            # mkvmerge возвращает 0 (ок), 1 (warning), 2 (error)
            process = subprocess.Popen(
                cmd,
                cwd=bin_dir,
                env=env,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True,
                encoding="utf-8",
                errors="replace",
                creationflags=subprocess.CREATE_NO_WINDOW,
            )
            ProcessManager().register(process)
            try:
                stdout, stderr = process.communicate()
            finally:
                ProcessManager().unregister(process)

            if ProcessManager().was_cancelled(process):
                logger.info("mkvmerge прерван пользователем.")
                return False

            if process.returncode <= 1:
                if process.returncode == 1:
                    logger.warning(
                        "mkvmerge завершен с предупреждениями:\n%s",
                        stdout,
                    )
                return True
            else:
                logger.error(
                    "Ошибка mkvmerge (код %d):\nSTDOUT:\n%s\nSTDERR:\n%s",
                    process.returncode,
                    stdout,
                    stderr,
                )
                return False

        except Exception:
            logger.exception("Ошибка при запуске mkvmerge")
            return False
