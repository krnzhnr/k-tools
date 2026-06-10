# -*- coding: utf-8 -*-
"""Обёртка для запуска Dolby Encoding Engine через XML-конфигурацию."""

# Std
import ctypes
import logging
import os
import subprocess
import sys
from pathlib import Path

# Third
import xmltodict
from deew.xml_base import xml_dd_ddp_base

# Local
from app.core import path_utils
from app.core.process_manager import ProcessManager
from app.core.singleton import SingletonMeta

logger = logging.getLogger(__name__)


class DeewRunner(metaclass=SingletonMeta):
    """Обёртка для безопасного запуска Dolby Encoding Engine (dee.exe).

    Обеспечивает подготовку аудиофайлов, генерацию XML-конфигурации
    и прямой запуск кодировщика с отслеживанием прогресса и отмены.
    """

    def __init__(self) -> None:
        """Инициализация runner'а."""
        self.__dee_path: str | None = None

    @property
    def _dee_path(self) -> str:
        """Получить путь к исполняемому файлу dee.exe."""
        if self.__dee_path is None:
            self.__dee_path = path_utils.get_binary_path("dee")
            logger.debug(
                "DeewRunner инициализирован. Использование прямого запуска, "
                "dee: %s",
                self.__dee_path,
            )
        return self.__dee_path

    @staticmethod
    def _get_short_path(path_str: str) -> str:
        """Получить короткое имя пути (8.3) для Windows.

        Это необходимо, так как Dolby Encoding Engine (dee.exe) падает
        при обработке путей, содержащих кириллицу или пробелы.

        Args:
            path_str: Исходный длинный путь.

        Returns:
            Короткий путь 8.3 или исходный путь при ошибке.
        """
        if sys.platform != "win32":
            return path_str

        try:
            # Создаем буфер для короткого пути
            buffer_size = 1024
            buffer = ctypes.create_unicode_buffer(buffer_size)
            # Вызываем функцию GetShortPathNameW из Win32 API
            result = ctypes.windll.kernel32.GetShortPathNameW(
                path_str, buffer, buffer_size
            )
            if result > 0:
                logger.debug(
                    "Путь преобразован в формат 8.3: %s -> %s",
                    path_str,
                    buffer.value,
                )
                return buffer.value
        except Exception:
            logger.exception(
                "Ошибка при получении короткого пути для '%s'",
                path_str,
            )
        return path_str

    def _get_input_channels(self, path: Path) -> int:
        """Получить количество аудиоканалов во входном файле.

        Args:
            path: Путь к файлу.

        Returns:
            Количество каналов.
        """
        ffprobe_path = path_utils.get_binary_path("ffprobe")
        cmd = [
            ffprobe_path,
            "-v",
            "error",
            "-select_streams",
            "a:0",
            "-show_entries",
            "stream=channels",
            "-of",
            "default=noprint_wrappers=1:nokey=1",
            str(path.absolute()),
        ]
        try:
            result = subprocess.run(
                cmd,
                capture_output=True,
                text=True,
                encoding="utf-8",
                creationflags=subprocess.CREATE_NO_WINDOW,
                timeout=10,
            )
            if result.returncode == 0 and result.stdout.strip():
                return int(result.stdout.strip())
        except Exception:
            logger.exception(
                "Ошибка при определении числа каналов для '%s'",
                path.name,
            )
        return 2  # По умолчанию стерео при сбое

    def _get_input_samplerate(self, path: Path) -> int:
        """Получить частоту дискретизации аудиовхода через ffprobe.

        Args:
            path: Путь к аудиофайлу.

        Returns:
            Частота дискретизации (Гц).
        """
        ffprobe_path = path_utils.get_binary_path("ffprobe")
        cmd = [
            ffprobe_path,
            "-v",
            "error",
            "-select_streams",
            "a:0",
            "-show_entries",
            "stream=sample_rate",
            "-of",
            "default=noprint_wrappers=1:nokey=1",
            str(path.absolute()),
        ]
        try:
            result = subprocess.run(
                cmd,
                capture_output=True,
                text=True,
                encoding="utf-8",
                creationflags=subprocess.CREATE_NO_WINDOW,
                timeout=10,
            )
            if result.returncode == 0 and result.stdout.strip():
                rate = int(result.stdout.strip())
                logger.debug(
                    "Определена частота дискретизации: %d Гц для %s",
                    rate,
                    path.name,
                )
                return rate
        except Exception:
            logger.exception(
                "Ошибка при получении частоты дискретизации для '%s'",
                path.name,
            )
        return 48000  # По умолчанию 48 кГц при сбое

    def _prepare_intermediate_audio(self, input_path: Path) -> Path | None:
        """Апмикс аудио до стандартного количества каналов (6 или 8).

        Args:
            input_path: Путь к исходному файлу.

        Returns:
            Путь к временному WAV файлу или None, если апмикс не требуется.
        """
        channels = self._get_input_channels(input_path)
        if channels in [1, 2, 6, 8]:
            return None

        # Определяем целевое число каналов для DEE
        target_channels = 6 if channels < 6 else 8
        logger.info(
            "Нестандартное число каналов (%d). "
            "Выполняется апмикс до %d каналов для DEE...",
            channels,
            target_channels,
        )

        from app.core.temp_file_manager import TempFileManager

        temp_dir = TempFileManager().create_temp_dir()
        temp_wav = temp_dir / f"{input_path.stem}_prep.wav"

        ffmpeg_path = path_utils.get_binary_path("ffmpeg")
        cmd = [
            ffmpeg_path,
            "-i",
            str(input_path.absolute()),
            "-ac",
            str(target_channels),
            "-y",
            str(temp_wav.absolute()),
        ]

        try:
            process = subprocess.Popen(
                cmd,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                creationflags=subprocess.CREATE_NO_WINDOW,
            )
            ProcessManager().register(process)
            try:
                process.communicate(timeout=60)
            finally:
                ProcessManager().unregister(process)

            if temp_wav.exists():
                return temp_wav
        except Exception:
            logger.exception(
                "Ошибка при создании временного аудио для '%s'",
                input_path.name,
            )
        return None

    def _convert_to_wav(self, input_path: Path, temp_wav: Path) -> bool:
        """Конвертировать входной аудиофайл в PCM WAV с нужными фильтрами.

        Выполняет перестановку каналов для 8-канального звука
        и ресемплинг до 48 кГц для совместимости с Dolby.

        Args:
            input_path: Путь к исходному аудиофайлу.
            temp_wav: Путь к создаваемому временному WAV.

        Returns:
            True в случае успеха, иначе False.
        """
        ffmpeg_path = path_utils.get_binary_path("ffmpeg")
        channels = self._get_input_channels(input_path)
        samplerate = self._get_input_samplerate(input_path)

        filters = []
        # Swap каналов для 7.1 аудио, так как DEE ожидает иной порядок
        if channels == 8:
            filters.append(
                "pan=7.1|c0=c0|c1=c1|c2=c2|c3=c3|c4=c6|c5=c7|c6=c4|c7=c5"
            )
        # Dolby Encoding Engine поддерживает только 48 кГц (или 96 кГц)
        if samplerate != 48000:
            filters.append(
                "aresample=resampler=soxr" if channels == 8 else "aresample"
            )

        cmd = [
            ffmpeg_path,
            "-y",
            "-drc_scale",
            "0",
            "-i",
            str(input_path.absolute()),
        ]

        if filters:
            filter_str = ",".join(filters)
            cmd.extend(["-filter_complex", f"[0:a:0]{filter_str}"])
        else:
            cmd.extend(["-map", "0:a:0"])

        if samplerate != 48000:
            cmd.extend(["-ar", "48000"])

        cmd.extend(
            [
                "-c",
                "pcm_s24le",
                "-rf64",
                "always",
                str(temp_wav.absolute()),
            ]
        )

        logger.info(
            "Запуск подготовки WAV-файла через FFmpeg: %s",
            " ".join(cmd),
        )

        process = subprocess.Popen(
            cmd,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            creationflags=subprocess.CREATE_NO_WINDOW,
        )
        ProcessManager().register(process)
        try:
            stdout, stderr = process.communicate()
        finally:
            ProcessManager().unregister(process)

        if ProcessManager().was_cancelled(process):
            logger.info("Подготовка WAV прервана пользователем.")
            return False

        if process.returncode != 0:
            logger.error(
                "Ошибка при подготовке WAV через FFmpeg: %s",
                stderr.decode("utf-8", errors="replace").strip(),
            )
            return False

        logger.info(
            "Промежуточный WAV-файл успешно создан: %s",
            temp_wav.name,
        )
        return True

    def _generate_dee_xml(
        self,
        temp_wav_path: Path,
        output_path: Path,
        output_format: str,
        bitrate: str,
        temp_xml_path: Path,
    ) -> None:
        """Сгенерировать XML файл конфигурации для Dolby Encoding Engine.

        Args:
            temp_wav_path: Путь к промежуточному WAV файлу.
            output_path: Целевой путь сохранения закодированного файла.
            output_format: Формат кодирования (ddp или dd).
            bitrate: Битрейт кодирования в kbps.
            temp_xml_path: Путь для сохранения XML-конфигурации.
        """
        temp_dir = temp_wav_path.parent
        # Получаем короткие пути в формате 8.3
        short_temp_dir = self._get_short_path(str(temp_dir.absolute()))
        short_wav_name = temp_wav_path.name
        short_out_dir = self._get_short_path(
            str(output_path.parent.absolute())
        )
        short_out_name = output_path.name

        # Парсим базовый XML шаблон из библиотеки deew
        xml = xmltodict.parse(xml_dd_ddp_base)

        # Конфигурируем входные пути (пути обязательно в кавычках)
        xml["job_config"]["input"]["audio"]["wav"]["storage"]["local"][
            "path"
        ] = f'"{short_temp_dir}"'
        xml["job_config"]["input"]["audio"]["wav"][
            "file_name"
        ] = f'"{short_wav_name}"'

        # Конфигурируем временную директорию
        xml["job_config"]["misc"]["temp_dir"]["path"] = f'"{short_temp_dir}"'

        # Настройки кодека
        pcm_to_ddp = xml["job_config"]["filter"]["audio"]["pcm_to_ddp"]
        pcm_to_ddp["encoder_mode"] = output_format  # 'ddp' или 'dd'
        pcm_to_ddp["downmix_config"] = "stereo"
        pcm_to_ddp["data_rate"] = int(bitrate)
        pcm_to_ddp["custom_dialnorm"] = -31
        pcm_to_ddp["drc"]["line_mode_drc_profile"] = "film_standard"
        pcm_to_ddp["drc"]["rf_mode_drc_profile"] = "film_standard"

        # Конфигурируем выходные пути
        if output_format == "dd":
            xml["job_config"]["output"]["ac3"] = {
                "file_name": f'"{short_out_name}"',
                "storage": {
                    "local": {
                        "path": f'"{short_out_dir}"'
                    }
                },
            }
            if "ec3" in xml["job_config"]["output"]:
                del xml["job_config"]["output"]["ec3"]
        else:
            xml["job_config"]["output"]["ec3"]["storage"]["local"][
                "path"
            ] = f'"{short_out_dir}"'
            xml["job_config"]["output"]["ec3"][
                "file_name"
            ] = f'"{short_out_name}"'

        # Превращаем структуру обратно в XML строку
        xml_str = xmltodict.unparse(xml, pretty=True, indent="  ").replace(
            "&amp;", "&"
        )
        with open(temp_xml_path, "w", encoding="utf-8") as f:
            f.write(xml_str)

        logger.debug(
            "XML-конфигурация Dolby успешно записана в %s",
            temp_xml_path.name,
        )

    def _execute_dee(self, temp_xml_path: Path, env: dict[str, str]) -> bool:
        """Запустить Dolby Encoding Engine с конфигурационным XML.

        Args:
            temp_xml_path: Путь к XML файлу конфигурации.
            env: Окружение процесса.

        Returns:
            True в случае успеха, иначе False.
        """
        dee_path = self._dee_path
        short_xml_path = self._get_short_path(str(temp_xml_path.absolute()))

        cmd = [
            dee_path,
            "--progress-interval",
            "500",
            "--diagnostics-interval",
            "90000",
            "-x",
            short_xml_path,
        ]

        logger.info(
            "Запуск Dolby Encoding Engine напрямую: %s",
            " ".join(cmd),
        )

        process = subprocess.Popen(
            cmd,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            encoding="utf-8",
            errors="replace",
            cwd=str(Path(dee_path).parent),
            env=env,
            creationflags=subprocess.CREATE_NO_WINDOW,
        )
        ProcessManager().register(process)

        try:
            stdout, stderr = process.communicate()
        finally:
            ProcessManager().unregister(process)

        if ProcessManager().was_cancelled(process):
            logger.info("Кодирование Dolby прервано пользователем.")
            return False

        if stdout and stdout.strip():
            logger.debug("Вывод dee.exe:\n%s", stdout.strip())

        if process.returncode != 0:
            logger.error(
                "Ошибка при кодировании в dee.exe (код %d): %s",
                process.returncode,
                stderr.strip() or stdout.strip(),
            )
            return False

        logger.info("Dolby Encoding Engine успешно завершил кодирование.")
        return True

    def run(
        self,
        input_path: Path,
        output_path: Path,
        bitrate: str,
        output_format: str = "ddp",
        channels: int = 2,
    ) -> Path | None:
        """Запустить кодирование в Dolby через прямое управление процессами.

        Args:
            input_path: Путь к исходному аудиофайлу.
            output_path: Целевой путь сохранения файла.
            bitrate: Битрейт (например, '448').
            output_format: Формат (ddp или dd).
            channels: Количество каналов (по умолчанию 2 для стерео).

        Returns:
            Путь к созданному файлу или None при ошибке.
        """
        # Готовим окружение
        env = self._prepare_env()

        # Создаем временную директорию для промежуточных файлов
        from app.core.temp_file_manager import TempFileManager

        temp_dir = TempFileManager().create_temp_dir()

        temp_wav_path = temp_dir / "input_temp.wav"
        temp_xml_path = temp_dir / "config_temp.xml"

        try:
            # 1. Если требуется апмикс нестандартных каналов, делаем его
            intermediate_path = self._prepare_intermediate_audio(input_path)
            actual_input = intermediate_path or input_path

            # 2. Конвертируем в PCM WAV в ASCII-совместимую временную папку
            if not self._convert_to_wav(actual_input, temp_wav_path):
                return None

            # 3. Генерируем XML конфигурацию Dolby
            self._generate_dee_xml(
                temp_wav_path,
                output_path,
                output_format,
                bitrate,
                temp_xml_path,
            )

            # 4. Запускаем кодирование в dee.exe напрямую
            if not self._execute_dee(temp_xml_path, env):
                return None

            # 5. Проверяем, что выходной файл успешно создан
            if output_path.exists():
                logger.info(
                    "Файл успешно закодирован и сохранен: %s",
                    output_path.name,
                )
                return output_path
            else:
                logger.error(
                    "DEE завершился без ошибок, "
                    "но выходной файл отсутствует: %s",
                    output_path.name,
                )
                return None

        except Exception:
            logger.exception(
                "Критическая ошибка при кодировании аудио для '%s'",
                input_path.name,
            )
            return None
        finally:
            # Удаляем временную папку текущей сессии кодирования
            TempFileManager().delete_path(temp_dir)
            if "intermediate_path" in locals() and intermediate_path:
                TempFileManager().delete_path(intermediate_path.parent)

    def _prepare_env(self) -> dict[str, str]:
        """Подготовка окружения для deew."""
        dee_dir = str(Path(self._dee_path).parent)
        ffmpeg_dir = str(Path(path_utils.get_binary_path("ffmpeg")).parent)
        env = os.environ.copy()
        env["PYTHONIOENCODING"] = "utf-8"

        paths = [dee_dir, ffmpeg_dir]
        env["PATH"] = os.pathsep.join(paths) + os.pathsep + env.get("PATH", "")
        return env
