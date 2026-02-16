# -*- coding: utf-8 -*-
"""Модуль для запуска mkvmerge (MKVToolNix)."""

import logging
import os
import subprocess
import shutil
from pathlib import Path
from typing import Any

from app.core import path_utils

logger = logging.getLogger(__name__)


class MKVMergeRunner:
    """Обертка для запуска mkvmerge."""

    def __init__(self) -> None:
        """Инициализация runner'а."""
        self._mkvmerge_path = path_utils.get_binary_path("mkvmerge")
        logger.info("MKVMergeRunner инициализирован. Путь к бинарнику: %s", self._mkvmerge_path)

    def run(
        self,
        output_path: Path,
        inputs: list[dict[str, Any]],
        title: str | None = None,
    ) -> bool:
        """Запустить mkvmerge.

        Args:
            output_path: Путь к выходному файлу.
            inputs: Список входных файлов с параметрами.
                    Каждый элемент:
                    {
                        "path": Path,
                        "args": list[str]  # Доп. аргументы для этого входа
                    }
            title: Глобальный заголовок файла (опционально).

        Returns:
            True при успешном завершении, иначе False.
        """
        cmd = [self._mkvmerge_path, "--output", str(output_path)]

        if title:
            cmd.extend(["--title", title])

        for inp in inputs:
            if "args" in inp and inp["args"]:
                cmd.extend(inp["args"])
            cmd.append(str(inp["path"]))

        logger.info(
            "Подготовка команды mkvmerge. Выходной файл: '%s'. Количество входов: %d. Глобальный заголовок: '%s'",
            output_path.name, len(inputs), title or "нет"
        )
        logger.info("Выполнение команды mkvmerge: %s", " ".join(cmd))

        # Подготовка окружения для загрузки DLL из папки bin
        bin_dir = str(Path(self._mkvmerge_path).parent)
        logger.debug("Рабочая директория mkvmerge: %s", bin_dir)
        env = os.environ.copy()
        env["PATH"] = bin_dir + os.pathsep + env.get("PATH", "")
        logger.debug("Переменная окружения PATH дополнена для mkvmerge")

        try:
            # mkvmerge возвращает 0 (ок), 1 (warning), 2 (error)
            process = subprocess.run(
                cmd,
                cwd=bin_dir,
                env=env,
                capture_output=True,
                text=True,
                encoding="utf-8",
                errors="replace",
                creationflags=subprocess.CREATE_NO_WINDOW,
            )

            if process.returncode <= 1:
                return True
            else:
                logger.error(
                    "Ошибка mkvmerge (код %d):\n%s",
                    process.returncode,
                    process.stderr,
                )
                return False

        except Exception:
            logger.exception("Ошибка при запуске mkvmerge")
            return False
