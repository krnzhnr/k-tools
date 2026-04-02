# -*- coding: utf-8 -*-
"""Обёртка для запуска FFmpeg через subprocess."""

import json
import logging
import os
import platform
import subprocess
from pathlib import Path

from app.core import path_utils
from app.core.singleton import SingletonMeta
from app.core.process_manager import ProcessManager
from app.core.ffmpeg_output_parser import FFmpegOutputParser, ProgressInfo
from app.core.ffmpeg_utils import escape_ffmpeg_path
from typing import Any, Callable

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

    @property
    def _ffprobe_path(self) -> str:
        """Ленивая загрузка пути к ffprobe."""
        return path_utils.get_binary_path("ffprobe")

    def run(
        self,
        input_path: Path,
        output_path: Path,
        extra_args: list[str] | None = None,
        input_args: list[str] | None = None,
        overwrite: bool = False,
        total_duration: float = 0.0,
        on_progress: Callable[[ProgressInfo], None] | None = None,
        use_cuvid: bool = False,
    ) -> bool:
        """Запустить FFmpeg с указанными параметрами.

        Args:
            input_path: Путь к входному файлу.
            output_path: Путь к выходному файлу.
            extra_args: Дополнительные аргументы FFmpeg.
            input_args: Аргументы ПЕРЕД входным файлом (например, декодеры).
            overwrite: Нужно ли перезаписывать выходной файл.
            total_duration: Общая длительность файла в сек (для прогресса).
            on_progress: Callback с информацией о прогрессе.
            use_cuvid: Использовать аппаратное ускорение NVIDIA (CUVID).

        Returns:
            True при успешном завершении, False при ошибке.
        """
        cmd = self._build_cmd(
            input_path,
            output_path,
            extra_args,
            input_args,
            overwrite,
            use_cuvid,
        )
        return self._execute_process(
            cmd,
            input_path.name,
            output_path.name,
            total_duration,
            on_progress,
        )

    def _build_cmd(
        self,
        input_path: Path,
        output_path: Path,
        extra_args: list[str] | None,
        input_args: list[str] | None,
        overwrite: bool,
        use_cuvid: bool = False,
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
            "info",
            "-stats",
            overwrite_flag,
        ]

        if input_args:
            cmd.extend(input_args)

        cmd.extend(["-i", str(input_path)])

        if extra_args:
            cmd.extend(extra_args)

        cmd.append(str(output_path))
        logger.info("Выполнение команды FFmpeg: %s", " ".join(cmd))
        return cmd

    def _execute_process(
        self,
        cmd: list[str],
        input_name: str,
        output_name: str,
        total_duration: float = 0.0,
        on_progress: Callable[[ProgressInfo], None] | None = None,
    ) -> bool:
        """Выполнение процесса FFmpeg с отслеживанием прогресса."""
        bin_dir = str(Path(self._ffmpeg_path).parent)
        logger.debug("Рабочая директория процесса: %s", bin_dir)

        env = os.environ.copy()
        env["PATH"] = bin_dir + os.pathsep + env.get("PATH", "")

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

            stderr_lines = []
            try:
                if process.stderr is None:
                    logger.error("Не удалось открыть stderr процесса")
                    return False

                # Читаем stderr построчно для парсинга прогресса
                while True:
                    line = process.stderr.readline()

                    # fail-safe для тестов: если readline вернул не строку,
                    # значит мок настроен неверно, прерываем цикл.
                    if not isinstance(line, str):
                        logger.error(
                            "FFmpegRunner: Некорректный тип из stderr: %s. "
                            "Прерывание цикла чтения.",
                            type(line),
                        )
                        break

                    if not line and process.poll() is not None:
                        break
                    if line:
                        line_stripped = line.strip()
                        stderr_lines.append(line_stripped)
                        if len(stderr_lines) > 1000:
                            stderr_lines.pop(0)

                        # Парсинг прогресса
                        if on_progress and total_duration > 0:
                            p_info = FFmpegOutputParser.parse_line(
                                line_stripped, total_duration
                            )
                            if p_info:
                                on_progress(p_info)

                stdout, _ = process.communicate()
            finally:
                ProcessManager().unregister(process)

            if ProcessManager().was_cancelled(process):
                logger.info("FFmpeg прерван пользователем.")
                return False

            if process.returncode != 0:
                stderr_text = "\n".join(
                    stderr_lines[-20:]
                )  # Последние 20 строк
                logger.error(
                    "FFmpeg завершился с ошибкой (код %d)\nSTDERR (конец): %s",
                    process.returncode,
                    stderr_text,
                )
                return False

            logger.info(
                "FFmpeg успешно обработал: %s → %s", input_name, output_name
            )
            return True

        except FileNotFoundError:
            logger.exception(
                "FFmpeg не найден. Убедитесь в правильности путей."
            )
            return False
        except Exception:
            logger.exception(
                "Критическая ошибка при запуске FFmpeg для '%s'", input_name
            )
            return False

    def get_video_info(self, file_path: Path) -> dict[str, Any]:
        """Получить информацию о видеопотоках, субтитрах и вложениях.

        Args:
            file_path: Путь к файлу.

        Returns:
            Словарь с информацией о файле.
        """

        # Формируем команду ffprobe с точным списком полей
        abs_path = str(file_path.absolute())
        cmd = [
            self._ffprobe_path,
            "-v",
            "error",
            "-show_entries",
            "format=duration:stream=index,codec_name,codec_type,disposition,"
            "pix_fmt,width,height:stream_tags",
            "-of",
            "json",
            abs_path,
        ]

        try:
            # Используем shell=False для безопасности,
            # но передаем аргументы списком.
            # В Windows при наличии кириллицы иногда требуется
            # вызов в кодировке cp1251
            # или явная передача байтовых путей, но subprocess.run со списком
            # обычно справляется, если не использовать check=True без нужды.
            process = subprocess.run(
                cmd,
                capture_output=True,
                text=True,
                encoding="utf-8",
                errors="replace",
                creationflags=subprocess.CREATE_NO_WINDOW,
                check=False,  # Не падаем по коду завершения
            )

            if process.returncode != 0:
                logger.warning(
                    "FFprobe вернул код %d для файла '%s'. Stderr: %s",
                    process.returncode,
                    file_path.name,
                    process.stderr,
                )

            if not process.stdout.strip():
                logger.error(
                    "FFprobe не вернул данных (stdout пуст) для '%s'",
                    file_path.name,
                )
                return {}

            return json.loads(process.stdout)

        except Exception:
            logger.exception(
                "Критическая ошибка при вызове FFprobe для '%s'",
                file_path.name,
            )
            return {}

    def extract_fonts(
        self,
        input_file: Path,
        attachments_info: list[dict[str, Any]],
        temp_dir: Path,
    ) -> int:
        """Извлечь шрифты (вложения) из видеофайла.

        Args:
            input_file: Путь к видеофайлу.
            attachments_info: Список словарей с инфо о вложениях (из ffprobe).
            temp_dir: Папка, куда извлекать шрифты.

        Returns:
            Количество успешно извлеченных шрифтов.
        """
        if not attachments_info:
            return 0

        temp_dir.mkdir(parents=True, exist_ok=True)
        extracted_count = 0

        for item in attachments_info:
            idx = item.get("index")
            filename = item.get("tags", {}).get("filename") or item.get(
                "filename"
            )

            if idx is None or not filename:
                continue

            # Имя файла может содержать пути, берем только имя
            output_path = temp_dir / Path(filename).name

            # Формирование команды извлечения вложения для Windows
            # ffmpeg -y -hide_banner -loglevel error
            # -dump_attachment:IDX OUT -i IN
            cmd = [
                self._ffmpeg_path,
                "-y",
                "-hide_banner",
                "-loglevel",
                "error",
                "-dump_attachment:" + str(idx),
                str(output_path),
                "-i",
                str(input_file),
            ]

            try:
                subprocess.run(
                    cmd,
                    check=False,
                    creationflags=(
                        subprocess.CREATE_NO_WINDOW
                        if platform.system() == "Windows"
                        else 0
                    ),
                    timeout=20,
                )
                if output_path.exists() and output_path.stat().st_size > 0:
                    extracted_count += 1
            except Exception as e:
                logger.warning(
                    "Ошибка извлечения вложения %s: %s", filename, e
                )

        return extracted_count

    def extract_subtitle(
        self,
        input_file: Path,
        stream_index: int,
        output_path: Path,
        relative: bool = False,
    ) -> bool:
        """Извлечь дорожку субтитров.

        Args:
            input_file: Путь к медиафайлу.
            stream_index: Индекс потока (глобальный или относительный).
            output_path: Куда сохранить (.ass).
            relative: Использовать ли относительный маппинг 0:s:N.
        """
        map_val = f"0:s:{stream_index}" if relative else f"0:{stream_index}"
        cmd = [
            self._ffmpeg_path,
            "-y",
            "-hide_banner",
            "-loglevel", "error",
            "-i", str(input_file),
            "-map", map_val,
            "-c:s", "ass",
            str(output_path),
        ]

        logger.info(
            "Извлечение субтитров (%s индекс %d) в: %s",
            "относительный" if relative else "глобальный",
            stream_index,
            output_path.name,
        )

        try:
            subprocess.run(
                cmd,
                check=True,
                creationflags=(
                    subprocess.CREATE_NO_WINDOW
                    if platform.system() == "Windows"
                    else 0
                ),
                timeout=30,
            )
            return output_path.exists() and output_path.stat().st_size > 0
        except Exception:
            logger.exception(
                "Ошибка при извлечении субтитров из: %s", input_file.name
            )
            return False

    def check_nvenc_support(self) -> bool:
        """Проверить поддержку аппаратного ускорения NVENC.

        Выполняет двойную проверку:
        1. Наличие энкодера 'hevc_nvenc' в бинарнике FFmpeg.
        2. Наличие GPU NVIDIA и драйверов через 'nvidia-smi'.

        Returns:
            True, если аппаратное ускорение доступно.
        """
        # 1. Проверка FFmpeg
        encoder_ok = False
        try:
            process = subprocess.run(
                [self._ffmpeg_path, "-encoders"],
                capture_output=True,
                text=True,
                encoding="utf-8",
                errors="replace",
                creationflags=subprocess.CREATE_NO_WINDOW,
                check=True,
            )
            encoder_ok = "hevc_nvenc" in process.stdout
            if not encoder_ok:
                logger.debug("Энкодер 'hevc_nvenc' не найден в FFmpeg.")
        except Exception:
            logger.debug("Не удалось получить список энкодеров FFmpeg.")
            return False

        if not encoder_ok:
            return False

        # 2. Проверка железа через nvidia-smi
        gpu_name = self.get_gpu_name()
        if gpu_name:
            logger.info(
                "Проверка оборудования: GPU NVIDIA обнаружен (%s).",
                gpu_name
            )
            return True

        logger.warning(
            "GPU NVIDIA не обнаружен или драйверы не установлены."
        )
        return False

    def get_gpu_name(self) -> str | None:
        """Получить название первой видеокарты через nvidia-smi.

        Returns:
            Название GPU или None, если не удалось определить.
        """
        smi_path = "nvidia-smi"
        # Проверяем стандартный путь в Windows
        win_path = Path("C:/Windows/System32/nvidia-smi.exe")
        if win_path.exists():
            smi_path = str(win_path)

        try:
            # -L выводит список в формате: GPU 0: NVIDIA GeForce RTX 3060...
            result = subprocess.run(
                [smi_path, "-L"],
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True,
                check=False,
                creationflags=subprocess.CREATE_NO_WINDOW,
            )
            if result.returncode == 0 and result.stdout:
                line = result.stdout.strip().split("\n")[0]
                if ":" in line:
                    return line.split(":", 1)[1].strip()
        except Exception:
            logger.debug("Не удалось запустить nvidia-smi для детекции GPU")

        return None

    def get_available_cuvid_decoders(self) -> set[str]:
        """Получить список доступных декодеров CUVID.

        Returns:
            Множество поддерживаемых CUVID-декодеров
            (например, {'h264_cuvid'}).
        """
        try:
            result = subprocess.run(
                [self._ffmpeg_path, "-decoders"],
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True,
                check=False,
                creationflags=subprocess.CREATE_NO_WINDOW,
            )
            if result.returncode == 0:
                decoders = set()
                for line in result.stdout.split("\n"):
                    if "_cuvid" in line:
                        parts = line.split()
                        if len(parts) >= 2:
                            decoders.add(parts[1])
                return decoders
        except Exception:
            logger.debug("Не удалось получить список декодеров FFmpeg")

        return set()

    def is_av1_decode_supported(self) -> bool:
        """Проверить поддержку аппаратного декодирования AV1.

        Returns:
            True, если GPU (>= 3060/Ampere) и FFmpeg поддерживают AV1 CUVID.
        """
        decoders = self.get_available_cuvid_decoders()
        if "av1_cuvid" not in decoders:
            return False

        gpu_name = self.get_gpu_name()
        if not gpu_name:
            return False

        gpu_name_low = gpu_name.lower()
        # Ampere (RTX 30xx) и Ada (RTX 40xx) поддерживают AV1
        if "rtx 30" in gpu_name_low or "rtx 40" in gpu_name_low:
            return True

        for g_prefix in ["rtx 50", "rtx 60", "rtx a", "rtx 6000"]:
            if g_prefix in gpu_name_low:
                return True

        return False

    def extract_attachment(
        self, input_path: Path, index: int, output_path: Path
    ) -> bool:
        """Извлечь конкретное вложение (шрифт) из файла.

        Args:
            input_path: Путь к медиафайлу.
            index: Глобальный индекс потока (stream index).
            output_path: Куда сохранить.

        Returns:
            True в случае успеха, False при ошибке.
        """
        # ffmpeg -y -hide_banner -loglevel error -dump_attachment:IDX OUT -i IN
        cmd = [
            self._ffmpeg_path,
            "-y",
            "-hide_banner",
            "-loglevel",
            "error",
            "-dump_attachment:" + str(index),
            str(output_path),
            "-i",
            str(input_path),
        ]

        try:
            subprocess.run(
                cmd,
                capture_output=True,
                creationflags=(
                    subprocess.CREATE_NO_WINDOW
                    if platform.system() == "Windows"
                    else 0
                ),
                check=True,
                timeout=20,
            )
            return output_path.exists() and output_path.stat().st_size > 0
        except Exception:
            logger.exception(
                "Ошибка извлечения вложения %d из: %s", index, input_path.name
            )
            return False

    def get_relative_index(
        self, info: dict[str, Any], global_index: int, codec_type: str
    ) -> int:
        """Найти относительный индекс потока среди потоков того же типа."""
        relative_idx = 0
        for stream in info.get("streams", []):
            if stream.get("codec_type") == codec_type:
                if stream.get("index") == global_index:
                    return relative_idx
                relative_idx += 1
        return 0

    def escape_filter_path(self, path: Path | str) -> str:
        """Перенаправление на централизованную утилиту экранирования."""
        return escape_ffmpeg_path(path)
