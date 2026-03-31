# -*- coding: utf-8 -*-
"""Обёртка для запуска deew через subprocess."""

# Std
import logging
import os
import subprocess
from pathlib import Path
import sys
import shutil

# Local
from app.core import path_utils
from app.core.singleton import SingletonMeta
from app.core.process_manager import ProcessManager

logger = logging.getLogger(__name__)


class DeewRunner(metaclass=SingletonMeta):
    """Обёртка для безопасного запуска deew.

    Обеспечивает формирование командной строки и
    корректную настройку окружения для Dolby Encoding Engine.
    """

    def __init__(self) -> None:
        """Инициализация runner'а."""
        self.__dee_path: str | None = None
        self._config_ensured = False

    @property
    def _dee_path(self) -> str:
        if self.__dee_path is None:
            self.__dee_path = path_utils.get_binary_path("dee")
            logger.debug(
                "DeewRunner инициализирован. Использование 'python -m deew', "
                "dee: %s",
                self.__dee_path,
            )
        return self.__dee_path

    def _ensure_config_exists(self) -> None:
        """Инициализирующий запуск для генерации config.toml.

        При первом запуске на чистой системе deew пытается создать конфиг.
        Если это делают несколько воркеров одновременно, они падают с ошибкой.
        Синхронный запуск 'deew -v' здесь решает проблему "холодного старта".
        """
        try:
            cmd = [sys.executable, "-m", "deew", "-v"]
            env = self._prepare_env()
            dee_dir = str(Path(self._dee_path).parent)

            subprocess.run(
                cmd,
                capture_output=True,
                cwd=dee_dir,
                env=env,
                creationflags=subprocess.CREATE_NO_WINDOW,
                timeout=5,
            )
        except Exception as exc:
            logger.debug("Ошибка при инициализации конфига deew: %s", exc)

    def run(
        self,
        input_path: Path,
        output_path: Path,
        bitrate: str,
        output_format: str = "ddp",
        channels: int = 2,
    ) -> Path | None:
        """Запустить deew через python -m deew.

        Args:
            input_path: Путь к входному файлу.
            output_path: Путь к выходному файлу.
            bitrate: Битрейт (например, '448').
            output_format: Формат (ddp или dd).
            channels: Количество каналов (2 для стерео).

        Returns:
            Путь к созданному файлу или None при ошибке.
        """
        is_safe = all(ord(c) < 128 for c in str(input_path) + str(output_path))

        if not self._config_ensured:
            self._ensure_config_exists()
            self._config_ensured = True

        base_cmd = self._build_base_cmd(output_format, channels, bitrate)
        env = self._prepare_env()

        if not is_safe:
            return self._run_safe_mode(
                input_path,
                output_path,
                base_cmd,
                env,
                output_format,
            )
        return self._run_normal_mode(
            input_path,
            output_path,
            base_cmd,
            env,
            output_format,
        )

    def _build_base_cmd(
        self, output_format: str, channels: int, bitrate: str
    ) -> list[str]:
        """Сборка базовых аргументов командной строки."""
        return [
            sys.executable,
            "-m",
            "deew",
            "-f",
            output_format,
            "-dm",
            str(channels),
            "-b",
            bitrate,
            "-la",  # local-audio
            "-np",  # no-progress
        ]

    def _prepare_env(self) -> dict[str, str]:
        """Подготовка окружения для deew."""
        dee_dir = str(Path(self._dee_path).parent)
        ffmpeg_dir = str(Path(path_utils.get_binary_path("ffmpeg")).parent)
        env = os.environ.copy()
        env["PYTHONIOENCODING"] = "utf-8"

        paths = [dee_dir, ffmpeg_dir]
        env["PATH"] = os.pathsep.join(paths) + os.pathsep + env.get("PATH", "")
        return env

    def _run_safe_mode(
        self,
        input_path: Path,
        output_path: Path,
        base_cmd: list[str],
        env: dict[str, str],
        output_format: str,
    ) -> Path | None:
        """Запуск deew с обходом кириллицы только для целевого пути.

        FFmpeg (под капотом deew) нормально читает файлы с не-ASCII путями,
        но dee.exe падает, если не-ASCII символы присутствуют в пути вывода.
        Раньше мы копировали входной файл, что переполняло диск диска C:
        при параллельной обработке тяжелых WAV и вызывало 'Unknown source'.
        Теперь мы передаем оригинальный путь, а во временную ASCII-папку
        только направляем вывод.
        """
        logger.info(
            "Обнаружена кириллица. Используется безопасный "
            "режим (вывод во временную папку)."
        )
        dee_dir = str(Path(self._dee_path).parent)
        from app.core.temp_file_manager import TempFileManager

        temp_dir_path = TempFileManager().create_temp_dir()

        try:
            cmd = base_cmd + [
                "-i",
                str(input_path.absolute()),
                "-o",
                str(temp_dir_path),
            ]
            logger.info(
                "Запуск deew (безопасный путь сохранения): %s",
                " ".join(cmd),
            )

            if not self._execute_safe_process(cmd, dee_dir, env):
                return None

            return self._move_safe_output(
                temp_dir_path,
                input_path.stem,
                output_format,
                output_path,
            )
        finally:
            TempFileManager().delete_path(temp_dir_path)

    def _execute_safe_process(
        self, cmd: list[str], cwd: str, env: dict[str, str]
    ) -> bool:
        """Выполнение процесса в безопасном режиме."""
        process = subprocess.Popen(
            cmd,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            encoding="utf-8",
            errors="replace",
            cwd=cwd,
            env=env,
            creationflags=subprocess.CREATE_NO_WINDOW,
        )
        ProcessManager().register(process)
        try:
            stdout, stderr = process.communicate()
        finally:
            ProcessManager().unregister(process)

        if ProcessManager().was_cancelled(process):
            logger.info("deew прерван пользователем.")
            return False

        if stdout and stdout.strip():
            logger.debug("Вывод deew:\n%s", stdout.strip())

        if process.returncode != 0:
            logger.error(
                "Ошибка deew (безопасный режим): %s",
                (stderr and stderr.strip()) or (stdout and stdout.strip()),
            )
            return False
        return True

    def _move_safe_output(
        self,
        temp_dir: Path,
        safe_name: str,
        fmt: str,
        out_path: Path,
    ) -> Path | None:
        """Перемещение выходного файла из временной папки."""
        found = self._find_deew_output(temp_dir, safe_name, fmt)

        if found:
            shutil.move(str(found), out_path)
            logger.info(
                "deew успешно обработал файл: %s",
                out_path.name,
            )
            return out_path

        logger.error("Выходной файл не найден во временной папке!")
        return None

    @staticmethod
    def _find_deew_output(
        directory: Path,
        stem: str,
        fmt: str,
    ) -> Path | None:
        """Найти выходной файл deew в директории.

        deew может создать файл с расширением .ec3 или .eac3
        для формата ddp. Метод проверяет оба варианта.

        Args:
            directory: Директория поиска.
            stem: Имя файла без расширения.
            fmt: Формат вывода (ddp или dd).

        Returns:
            Путь к найденному файлу или None.
        """
        candidates = [".ec3", ".eac3"] if fmt == "ddp" else [".ac3"]
        for ext in candidates:
            candidate = directory / f"{stem}{ext}"
            logger.debug(
                "Поиск выходного файла deew: %s " "(существует: %s)",
                candidate,
                candidate.exists(),
            )
            if candidate.exists():
                return candidate
        return None

    def _run_normal_mode(
        self,
        input_path: Path,
        output_path: Path,
        base_cmd: list[str],
        env: dict[str, str],
        output_format: str,
    ) -> Path | None:
        """Обычный запуск deew."""
        dee_dir = str(Path(self._dee_path).parent)
        out_dir = output_path.parent
        cmd = base_cmd + [
            "-i",
            str(input_path),
            "-o",
            str(out_dir),
        ]

        try:
            process = subprocess.Popen(
                cmd,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True,
                encoding="utf-8",
                errors="replace",
                cwd=dee_dir,
                env=env,
                creationflags=subprocess.CREATE_NO_WINDOW,
            )
            ProcessManager().register(process)
            try:
                stdout, stderr = process.communicate()
            finally:
                ProcessManager().unregister(process)

            if ProcessManager().was_cancelled(process):
                logger.info("deew прерван пользователем.")
                return None

            if stdout and stdout.strip():
                logger.debug("Вывод deew:\n%s", stdout.strip())

            if process.returncode != 0:
                logger.error(
                    "Ошибка deew: %s",
                    (stderr and stderr.strip()) or (stdout and stdout.strip()),
                )
                return None

            # Проверяем фактическое наличие выходного файла
            found = self._find_deew_output(
                out_dir,
                input_path.stem,
                output_format,
            )
            if not found:
                logger.error(
                    "DEE завершился без ошибки, но выходной "
                    "файл не создан: %s",
                    input_path.name,
                )
                return None

            logger.info("deew успешно обработал: %s", input_path.name)
            return found

        except Exception:
            logger.exception(
                "Ошибка при запуске deew для '%s'",
                input_path.name,
            )
            return None
