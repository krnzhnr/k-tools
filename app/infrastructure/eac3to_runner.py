# -*- coding: utf-8 -*-
"""Модуль для запуска eac3to."""

import logging
import os
import subprocess
from pathlib import Path
from app.core import path_utils
from app.core.singleton import SingletonMeta
from app.core.process_manager import ProcessManager

logger = logging.getLogger(__name__)


class Eac3toRunner(metaclass=SingletonMeta):
    """Обертка для запуска eac3to."""

    def __init__(self):
        """Инициализация runner'а."""
        self._executable = path_utils.get_binary_path("eac3to")
        logger.debug(
            "Eac3toRunner инициализирован. Путь к бинарнику: %s",
            self._executable,
        )

    def run(
        self, args: list[str], cwd: Path | None = None, overwrite: bool = False
    ) -> bool:
        """Запустить eac3to с аргументами."""
        cmd = [str(self._executable)] + [str(arg) for arg in args]
        logger.info(f"Подготовка команды eac3to с {len(args)} аргументами")
        logger.info(f"Выполнение команды eac3to: {' '.join(cmd)}")

        bin_dir = str(Path(self._executable).parent)
        env = os.environ.copy()
        env["PATH"] = bin_dir + os.pathsep + env.get("PATH", "")
        working_cwd = cwd if cwd else bin_dir

        success = self._execute_process(cmd, working_cwd, env)

        # Очистка логов eac3to после выполнения
        self._cleanup_logs(working_cwd)

        return success

    def _cleanup_logs(self, directory: str | Path) -> None:
        """Удалить файлы логов, созданные eac3to (log*.txt с проверкой)."""
        try:
            path = Path(directory)
            if not path.exists():
                return

            # Ищем файлы по маске log*.txt
            for log_file in path.glob("log*.txt"):
                try:
                    # Проверяем содержимое первой строки для безопасности
                    with open(
                        log_file, "r", encoding="utf-8", errors="ignore"
                    ) as f:
                        first_line = f.readline()
                        if not first_line.lower().startswith("eac3to v"):
                            continue

                    log_file.unlink()
                    logger.debug("🗑 Удален лог eac3to: %s", log_file.name)
                except (OSError, IOError) as e:
                    logger.warning(
                        "⚠ Не удалось обработать лог %s: %s", log_file.name, e
                    )
        except Exception as e:
            logger.exception("Ошибка при очистке логов eac3to: %s", e)

    def _execute_process(
        self, cmd: list[str], cwd: str | Path, env: dict[str, str]
    ) -> bool:
        """Выполнение процесса eac3to."""
        try:
            process = subprocess.Popen(
                cmd,
                cwd=cwd,
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
                logger.info("eac3to прерван пользователем.")
                return False

            if process.returncode != 0:
                logger.error(
                    "Ошибка eac3to (code %d):\n%s\n%s",
                    process.returncode,
                    stderr,
                    stdout,
                )
                return False

            logger.debug("Вывод eac3to:\n%s", stdout)
            return True

        except FileNotFoundError:
            logger.error(f"Не найден исполняемый файл: {self._executable}")
            return False
        except Exception as e:
            logger.exception(f"Ошибка при запуске eac3to: {e}")
            return False
