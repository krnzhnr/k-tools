# -*- coding: utf-8 -*-
"""Модуль для запуска mkvmerge (MKVToolNix)."""

import logging
import subprocess
import shutil
from pathlib import Path
from typing import Any

logger = logging.getLogger(__name__)


class MKVMergeRunner:
    """Обертка для запуска mkvmerge."""

    def __init__(self) -> None:
        """Инициализация runner'а."""
        self._mkvmerge_path = self._find_mkvmerge()

    def _find_mkvmerge(self) -> str:
        """Найти исполняемый файл mkvmerge.

        Returns:
            Путь к mkvmerge или 'mkvmerge' если не найден.
        """
        # 1. Проверяем PATH
        if shutil.which("mkvmerge"):
            return "mkvmerge"

        # 2. Проверяем стандартные пути установки Windows
        common_paths = [
            Path(r"C:\Program Files\MKVToolNix\mkvmerge.exe"),
            Path(r"C:\Program Files (x86)\MKVToolNix\mkvmerge.exe"),
        ]

        for path in common_paths:
            if path.exists():
                return str(path)

        # 3. Дефолт
        return "mkvmerge"

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

        logger.info("Запуск mkvmerge: %s", " ".join(cmd))

        try:
            # mkvmerge возвращает 0 (ок), 1 (warning), 2 (error)
            process = subprocess.run(
                cmd,
                capture_output=True,
                text=True,
                encoding="utf-8",
                errors="replace",
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
