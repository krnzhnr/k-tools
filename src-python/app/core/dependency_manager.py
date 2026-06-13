# -*- coding: utf-8 -*-
"""Менеджер внешних зависимостей приложения.

Отвечает за проверку наличия, скачивание, верификацию
и распаковку внешних бинарных зависимостей (bin/).
"""

import ctypes
import ctypes.wintypes
import logging
import tarfile
import urllib.error
import urllib.request
from enum import Enum
from pathlib import Path
from typing import Any

from PyQt6.QtCore import QThread, pyqtSignal

from app.core.dependency_manifest import (
    DEPENDENCY_MAP,
    DEPENDENCY_REGISTRY,
    DependencyInfo,
    get_download_url,
)
from app.core.path_utils import _get_base_dir
from app.core.singleton import SingletonMeta

logger = logging.getLogger(__name__)


class SHELLEXECUTEINFO(ctypes.Structure):
    """Структура для работы с ShellExecuteExW."""

    _fields_ = [
        ("cbSize", ctypes.wintypes.DWORD),
        ("fMask", ctypes.c_ulong),
        ("hwnd", ctypes.wintypes.HWND),
        ("lpVerb", ctypes.wintypes.LPCWSTR),
        ("lpFile", ctypes.wintypes.LPCWSTR),
        ("lpParameters", ctypes.wintypes.LPCWSTR),
        ("lpDirectory", ctypes.wintypes.LPCWSTR),
        ("nShow", ctypes.c_int),
        ("hInstApp", ctypes.wintypes.HINSTANCE),
        ("lpIDList", ctypes.c_void_p),
        ("lpClass", ctypes.wintypes.LPCWSTR),
        ("hkeyClass", ctypes.wintypes.HKEY),
        ("dwHotKey", ctypes.wintypes.DWORD),
        ("hIconOrMonitor", ctypes.c_void_p),
        ("hProcess", ctypes.wintypes.HANDLE),
    ]


SEE_MASK_NOCLOSEPROCESS = 0x00000040


class DependencyStatus(Enum):
    """Статус внешней зависимости."""

    INSTALLED = "installed"
    NOT_INSTALLED = "not_installed"
    DOWNLOADING = "downloading"
    EXTRACTING = "extracting"
    ERROR = "error"


class DependencyManager(metaclass=SingletonMeta):
    """Менеджер внешних зависимостей (Singleton).

    Проверяет наличие бинарников, управляет
    скачиванием и распаковкой архивов из GitHub Releases.
    """

    def __init__(self) -> None:
        """Инициализация менеджера зависимостей."""
        self._base_dir = _get_base_dir()
        self._bin_dir = self._base_dir / "bin"
        self._statuses: dict[str, DependencyStatus] = {}
        self._checksums: dict[str, str] = {}

        self._refresh_all_statuses()
        logger.info(
            "Менеджер зависимостей инициализирован. "
            "Директория bin: %s",
            self._bin_dir,
        )

    @property
    def bin_dir(self) -> Path:
        """Путь к директории bin/."""
        return self._bin_dir

    def _refresh_all_statuses(self) -> None:
        """Обновить статусы всех зависимостей."""
        for dep in DEPENDENCY_REGISTRY:
            if self._is_binary_present(dep):
                self._statuses[dep.key] = (
                    DependencyStatus.INSTALLED
                )
            else:
                self._statuses[dep.key] = (
                    DependencyStatus.NOT_INSTALLED
                )
        logger.debug(
            "Статусы зависимостей обновлены: %s",
            {k: v.value for k, v in self._statuses.items()},
        )

    def _is_binary_present(self, dep: DependencyInfo) -> bool:
        """Проверить наличие контрольного бинарника зависимости.

        Args:
            dep: Описание зависимости.

        Returns:
            True, если контрольный файл существует.
        """
        if dep.key == "eac3to_decoders":
            import os
            # Проверяем DirectShow-фильтр Nero Audio Decoder
            # в системных папках Windows
            windir = os.environ.get("SystemRoot", "C:\\Windows")
            syswow64 = os.path.join(windir, "SysWOW64", "NeAudio2.ax")
            system32 = os.path.join(windir, "System32", "NeAudio2.ax")
            return os.path.exists(syswow64) or os.path.exists(system32)

        verify_path = (
            self._bin_dir / dep.subfolder / dep.verify_binary
        )
        return verify_path.exists()

    def get_status(self, key: str) -> DependencyStatus:
        """Получить текущий статус зависимости.

        Args:
            key: Идентификатор зависимости.

        Returns:
            Текущий статус.
        """
        return self._statuses.get(
            key, DependencyStatus.NOT_INSTALLED
        )

    def set_status(
        self,
        key: str,
        status: DependencyStatus,
    ) -> None:
        """Установить статус зависимости.

        Args:
            key: Идентификатор зависимости.
            status: Новый статус.
        """
        self._statuses[key] = status
        logger.debug(
            "Статус зависимости '%s' установлен: %s",
            key,
            status.value,
        )

    def is_installed(self, key: str) -> bool:
        """Проверить, установлена ли зависимость.

        Args:
            key: Идентификатор зависимости.

        Returns:
            True, если зависимость установлена.
        """
        dep = DEPENDENCY_MAP.get(key)
        if not dep:
            return False
        return self._is_binary_present(dep)

    def is_script_available(
        self,
        required_deps: list[str],
    ) -> bool:
        """Проверить, доступны ли все зависимости скрипта.

        Args:
            required_deps: Список ключей зависимостей.

        Returns:
            True, если все зависимости установлены.
        """
        return all(
            self.is_installed(key)
            for key in required_deps
        )

    def get_missing_deps(
        self,
        required_deps: list[str],
    ) -> list[DependencyInfo]:
        """Получить список недостающих зависимостей.

        Args:
            required_deps: Ключи необходимых зависимостей.

        Returns:
            Список объектов DependencyInfo для
            ненайденных зависимостей.
        """
        missing = []
        for key in required_deps:
            dep = DEPENDENCY_MAP.get(key)
            if dep and not self._is_binary_present(dep):
                missing.append(dep)
        return missing

    def get_all_statuses(
        self,
    ) -> dict[str, DependencyStatus]:
        """Получить актуальные статусы всех зависимостей.

        Returns:
            Словарь {key: DependencyStatus}.
        """
        self._refresh_all_statuses()
        return dict(self._statuses)

    def has_any_missing(self) -> bool:
        """Проверить, есть ли хотя бы одна ненайденная зависимость.

        Returns:
            True, если есть ненайденные зависимости.
        """
        return any(
            not self._is_binary_present(dep)
            for dep in DEPENDENCY_REGISTRY
        )

    def _get_eac3to_decoders_uninstall_string(self) -> str | None:
        """Найти строку деинсталляции eac3to Decoder Pack в реестре."""
        import winreg

        registry_paths = [
            r"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            r"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        ]

        for path in registry_paths:
            try:
                with winreg.OpenKey(
                    winreg.HKEY_LOCAL_MACHINE, path
                ) as key:
                    # Попытка прямого поиска по известному GUID инсталлятора
                    try:
                        guid = "{167887DA-6C4F-4265-8139-8750A543FD52}_is1"
                        with winreg.OpenKey(key, guid) as subkey:
                            val, _ = winreg.QueryValueEx(
                                subkey, "UninstallString"
                            )
                            if val:
                                return str(val)
                    except OSError:
                        pass

                    # Резервный поиск сканированием разделов по DisplayName
                    info = winreg.QueryInfoKey(key)
                    for i in range(info[0]):
                        try:
                            name = winreg.EnumKey(key, i)
                            with winreg.OpenKey(key, name) as subkey:
                                try:
                                    disp, _ = winreg.QueryValueEx(
                                        subkey, "DisplayName"
                                    )
                                    if disp and "eac3to Decoder Pack" in str(
                                        disp
                                    ):
                                        val, _ = winreg.QueryValueEx(
                                            subkey, "UninstallString"
                                        )
                                        if val:
                                            return str(val)
                                except OSError:
                                    pass
                        except OSError:
                            pass
            except OSError:
                pass
        return None

    def remove_dependency(self, key: str) -> bool:
        """Удалить установленную зависимость.

        Args:
            key: Идентификатор зависимости.

        Returns:
            True, если удаление прошло успешно.
        """
        import shutil

        dep = DEPENDENCY_MAP.get(key)
        if not dep:
            logger.warning(
                "Попытка удаления неизвестной зависимости: %s",
                key,
            )
            return False

        if key == "eac3to_decoders":
            logger.info(
                "Запрос на удаление декодеров eac3to: "
                "использование оригинального деинсталлятора"
            )
            uninstall_str = self._get_eac3to_decoders_uninstall_string()
            uninstalled_via_setup = False
            if uninstall_str:
                import os
                exe_path = uninstall_str.strip().strip('"')
                if os.path.exists(exe_path):
                    logger.info(
                        "Запуск оригинального деинсталлятора: %s",
                        exe_path,
                    )

                    sei = SHELLEXECUTEINFO()
                    sei.cbSize = ctypes.sizeof(SHELLEXECUTEINFO)
                    sei.fMask = SEE_MASK_NOCLOSEPROCESS
                    sei.lpVerb = "runas"
                    sei.lpFile = exe_path
                    sei.lpParameters = (
                        "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART"
                    )
                    sei.nShow = 1

                    try:
                        res = ctypes.windll.shell32.ShellExecuteExW(
                            ctypes.byref(sei)
                        )
                        if res:
                            if sei.hProcess:
                                INFINITE = 0xFFFFFFFF
                                ctypes.windll.kernel32.WaitForSingleObject(
                                    sei.hProcess, INFINITE
                                )
                                ctypes.windll.kernel32.CloseHandle(
                                    sei.hProcess
                                )
                                logger.info(
                                    "Оригинальный деинсталлятор завершил"
                                    " работу"
                                )
                                uninstalled_via_setup = True
                    except Exception as e:
                        logger.error(
                            "Ошибка при запуске оригинального "
                            "деинсталлятора: %s",
                            e,
                        )

            if not uninstalled_via_setup:
                logger.warning(
                    "Официальный деинсталлятор не найден. Запуск "
                    "резервного метода безопасной ручной деинсталляции"
                )
                import os
                import tempfile
                import uuid

                # Создаем уникальный временный файл .bat
                temp_dir = tempfile.gettempdir()
                bat_filename = (
                    f"uninstall_eac3to_decoders_"
                    f"{uuid.uuid4().hex}.bat"
                )
                temp_bat_path = os.path.join(temp_dir, bat_filename)

                sys_dirs = [
                    "%SystemRoot%\\SysWOW64",
                    "%SystemRoot%\\System32",
                ]
                filters = [
                    "NeAudio2.ax",
                    "ASAudioHD.ax",
                    "CinemasterAudio.dll",
                ]
                files = [
                    "NeAudio2.ax",
                    "NeDtsDec.dll",
                    "NeEacDec.dll",
                    "AdvrCntr2.dll",
                    "ASAudioHD.ax",
                    "checkactivate.dll",
                    "MagCore.dll",
                    "MagPCMac.dll",
                    "MagUIEngine.dll",
                    "MagUIInter.dll",
                    "dtsdecoderdll.dll",
                    "CinemasterAudio.dll",
                ]

                commands = [
                    "@echo off",
                    "chcp 65001 > nul",
                    "",
                    ":: Разрегистрация DirectShow-фильтров",
                ]
                for d in sys_dirs:
                    for f_name in filters:
                        commands.append(
                            f'if exist "{d}\\{f_name}" '
                            f'"{d}\\regsvr32.exe" /u /s '
                            f'"{d}\\{f_name}"'
                        )

                commands.extend(["", ":: Удаление файлов декодеров"])
                for d in sys_dirs:
                    for f_name in files:
                        commands.append(
                            f'if exist "{d}\\{f_name}" '
                            f'del /f /q "{d}\\{f_name}"'
                        )

                commands.extend(
                    [
                        "",
                        ":: Удаление файлов из директории Windows",
                        (
                            'if exist "%SystemRoot%\\neroAacEnc.exe" '
                            'del /f /q "%SystemRoot%\\neroAacEnc.exe"'
                        ),
                        (
                            'if exist "%SystemRoot%\\surcode" '
                            'rd /s /q "%SystemRoot%\\surcode"'
                        ),
                        "",
                        ":: Очистка разделов реестра",
                        (
                            "reg delete "
                            '"HKLM\\SOFTWARE\\Ahead\\Installation\\'
                            'Families\\Nero 7" /f >nul 2>&1'
                        ),
                        (
                            "reg delete "
                            '"HKLM\\SOFTWARE\\Ahead\\Installation\\'
                            'Families\\Plugins" /f >nul 2>&1'
                        ),
                        (
                            "reg delete "
                            '"HKLM\\SOFTWARE\\Sonic\\CommonMPEGDecoders\\'
                            '4.2\\AudioDecoder" /f >nul 2>&1'
                        ),
                        (
                            "reg delete "
                            '"HKLM\\SOFTWARE\\Minnetonka Audio Software\\'
                            'SurCode DVD-DTS" /f >nul 2>&1'
                        ),
                        (
                            "reg delete "
                            '"HKLM\\SOFTWARE\\WOW6432Node\\Microsoft\\'
                            "Windows\\CurrentVersion\\Uninstall\\"
                            '{167887DA-6C4F-4265-8139-8750A543FD52}_is1" '
                            "/f >nul 2>&1"
                        ),
                        (
                            "reg delete "
                            '"HKLM\\SOFTWARE\\Microsoft\\Windows\\'
                            "CurrentVersion\\Uninstall\\"
                            '{167887DA-6C4F-4265-8139-8750A543FD52}_is1" '
                            "/f >nul 2>&1"
                        ),
                    ]
                )

                try:
                    # Записываем с кодировкой UTF-8
                    with open(temp_bat_path, "w", encoding="utf-8") as f:
                        f.write("\n".join(commands))

                    logger.info(
                        "Запуск временного батника удаления %s "
                        "с правами администратора",
                        temp_bat_path,
                    )

                    sei = SHELLEXECUTEINFO()
                    sei.cbSize = ctypes.sizeof(SHELLEXECUTEINFO)
                    sei.fMask = SEE_MASK_NOCLOSEPROCESS
                    sei.lpVerb = "runas"
                    sei.lpFile = "cmd.exe"
                    sei.lpParameters = f'/c "{temp_bat_path}"'
                    sei.nShow = 0  # Скрытое окно

                    res = ctypes.windll.shell32.ShellExecuteExW(
                        ctypes.byref(sei)
                    )
                    if res:
                        if sei.hProcess:
                            INFINITE = 0xFFFFFFFF
                            ctypes.windll.kernel32.WaitForSingleObject(
                                sei.hProcess, INFINITE
                            )
                            ctypes.windll.kernel32.CloseHandle(
                                sei.hProcess
                            )
                        logger.info(
                            "Резервное удаление декодеров eac3to "
                            "успешно завершено"
                        )
                    else:
                        raise RuntimeError(
                            "Не удалось запустить процесс удаления "
                            "с правами администратора."
                        )
                except Exception as e:
                    logger.error(
                        "Ошибка при резервном удалении декодеров "
                        "Nero: %s",
                        e,
                    )
                finally:
                    if os.path.exists(temp_bat_path):
                        try:
                            os.unlink(temp_bat_path)
                        except Exception as e:
                            logger.warning(
                                "Не удалось удалить временный батник "
                                "%s: %s",
                                temp_bat_path,
                                e,
                            )

        dep_dir = self._bin_dir / dep.subfolder
        if not dep_dir.exists():
            logger.info(
                "Зависимость '%s' уже удалена "
                "(директория не найдена).",
                dep.display_name,
            )
            self._statuses[key] = (
                DependencyStatus.NOT_INSTALLED
            )
            return True

        try:
            shutil.rmtree(dep_dir)
            self._statuses[key] = (
                DependencyStatus.NOT_INSTALLED
            )
            logger.info(
                "Зависимость '%s' успешно удалена: %s",
                dep.display_name,
                dep_dir,
            )
            return True
        except OSError:
            logger.exception(
                "Ошибка удаления зависимости '%s' "
                "из директории '%s'",
                dep.display_name,
                dep_dir,
            )
            return False


class DependencyDownloadWorker(QThread):
    """Фоновый поток для скачивания и распаковки зависимости.

    Signals:
        progress: Прогресс скачивания (0-100).
        status_changed: Смена статуса (key, DependencyStatus).
        download_finished: Завершение (key, success, error_msg).
    """

    progress = pyqtSignal(str, int)
    speed_updated = pyqtSignal(str, str)
    status_changed = pyqtSignal(str, object)
    download_finished = pyqtSignal(str, bool, str)

    def __init__(
        self,
        dep: DependencyInfo,
        bin_dir: Path,
        parent: Any = None,
    ) -> None:
        """Инициализация воркера скачивания зависимости.

        Args:
            dep: Описание зависимости для скачивания.
            bin_dir: Путь к директории bin/.
            parent: Родительский QObject.
        """
        super().__init__(parent)
        self._dep = dep
        self._bin_dir = bin_dir
        self._is_cancelled = False

    def cancel(self) -> None:
        """Отменить скачивание."""
        self._is_cancelled = True
        logger.info(
            "Запрошена отмена скачивания "
            "зависимости '%s'",
            self._dep.display_name,
        )

    def run(self) -> None:
        """Выполнение скачивания и распаковки."""
        import tempfile

        key = self._dep.key
        url = get_download_url(self._dep)

        self.status_changed.emit(
            key, DependencyStatus.DOWNLOADING
        )

        try:
            logger.info(
                "Начало скачивания зависимости '%s' "
                "из %s",
                self._dep.display_name,
                url,
            )

            self._bin_dir.mkdir(parents=True, exist_ok=True)

            # Скачивание во временный файл
            tmp_file = tempfile.NamedTemporaryFile(
                suffix=".tar.xz",
                delete=False,
                dir=str(self._bin_dir),
            )
            tmp_path = Path(tmp_file.name)
            tmp_file.close()

            try:
                self._download_file(url, tmp_path)
            except Exception:
                # Удаляем неполный файл
                if tmp_path.exists():
                    tmp_path.unlink(missing_ok=True)
                raise

            if self._is_cancelled:
                tmp_path.unlink(missing_ok=True)
                logger.info(
                    "Скачивание '%s' отменено.",
                    self._dep.display_name,
                )
                self.download_finished.emit(
                    key, False, "Отменено"
                )
                return

            # Распаковка
            self.status_changed.emit(
                key, DependencyStatus.EXTRACTING
            )
            logger.info(
                "Распаковка архива '%s'...",
                self._dep.display_name,
            )

            self._extract_archive(tmp_path)

            # Удаление архива
            tmp_path.unlink(missing_ok=True)

            # Если устанавливаем декодеры eac3to, нужно запустить
            # тихую установку с повышением прав
            if key == "eac3to_decoders":
                setup_path = (
                    self._bin_dir
                    / self._dep.subfolder
                    / self._dep.verify_binary
                )
                if setup_path.exists():
                    logger.info("Запуск тихой установки декодеров eac3to...")

                    sei = SHELLEXECUTEINFO()
                    sei.cbSize = ctypes.sizeof(SHELLEXECUTEINFO)
                    sei.fMask = SEE_MASK_NOCLOSEPROCESS
                    sei.lpVerb = "runas"
                    sei.lpFile = str(setup_path)
                    sei.lpParameters = (
                        "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART"
                    )
                    sei.nShow = 1

                    res = ctypes.windll.shell32.ShellExecuteExW(
                        ctypes.byref(sei)
                    )
                    if res:
                        if sei.hProcess:
                            INFINITE = 0xFFFFFFFF
                            ctypes.windll.kernel32.WaitForSingleObject(
                                sei.hProcess, INFINITE
                            )
                            ctypes.windll.kernel32.CloseHandle(sei.hProcess)
                            logger.info("Установка декодеров eac3to завершена")
                    else:
                        raise RuntimeError(
                            "Не удалось запустить установщик "
                            "декодеров с правами администратора."
                        )

            # Верификация
            dep_mgr = DependencyManager()
            if dep_mgr.is_installed(key):
                dep_mgr.set_status(
                    key, DependencyStatus.INSTALLED
                )
                self.status_changed.emit(
                    key, DependencyStatus.INSTALLED
                )
                self.download_finished.emit(
                    key, True, ""
                )
                logger.info(
                    "Зависимость '%s' успешно установлена.",
                    self._dep.display_name,
                )
            else:
                dep_mgr.set_status(
                    key, DependencyStatus.ERROR
                )
                self.status_changed.emit(
                    key, DependencyStatus.ERROR
                )
                error_msg = (
                    f"Контрольный файл "
                    f"'{self._dep.verify_binary}' "
                    f"не найден после распаковки."
                )
                self.download_finished.emit(
                    key, False, error_msg
                )
                logger.error(
                    "Верификация зависимости '%s' не "
                    "пройдена: %s",
                    self._dep.display_name,
                    error_msg,
                )

        except urllib.error.URLError:
            logger.exception(
                "Сетевая ошибка при скачивании "
                "зависимости '%s'",
                self._dep.display_name,
            )
            self.status_changed.emit(
                key, DependencyStatus.ERROR
            )
            self.download_finished.emit(
                key,
                False,
                "Не удалось подключиться к серверу.",
            )

        except Exception as e:
            logger.exception(
                "Непредвиденная ошибка при установке "
                "зависимости '%s'",
                self._dep.display_name,
            )
            self.status_changed.emit(
                key, DependencyStatus.ERROR
            )
            self.download_finished.emit(
                key, False, str(e)
            )

    def _download_file(
        self,
        url: str,
        dest: Path,
    ) -> None:
        """Скачать файл с прогрессом.

        Args:
            url: URL для скачивания.
            dest: Путь для сохранения.

        Raises:
            urllib.error.URLError: При сетевых ошибках.
            Exception: При прочих ошибках I/O.
        """
        import time

        req = urllib.request.Request(
            url,
            headers={"User-Agent": "K-Tools-DependencyManager"},
        )

        with urllib.request.urlopen(req, timeout=30) as resp:
            total = int(
                resp.headers.get("content-length", 0)
            )
            downloaded = 0
            block = 65536
            start_time = time.monotonic()
            last_speed_update = start_time

            with open(dest, "wb") as f:
                while True:
                    if self._is_cancelled:
                        return

                    buf = resp.read(block)
                    if not buf:
                        break

                    f.write(buf)
                    downloaded += len(buf)

                    if total > 0:
                        pct = int(downloaded * 100 / total)
                        self.progress.emit(
                            self._dep.key, pct
                        )

                    # Обновление скорости раз в секунду
                    now = time.monotonic()
                    if now - last_speed_update >= 1.0:
                        elapsed = now - start_time
                        if elapsed > 0:
                            speed = downloaded / elapsed
                            speed_str = (
                                self._format_speed(speed)
                            )
                            self.speed_updated.emit(
                                self._dep.key, speed_str
                            )
                        last_speed_update = now

    def _extract_archive(self, archive_path: Path) -> None:
        """Распаковать tar.xz-архив в директорию bin/.

        Args:
            archive_path: Путь к tar.xz-архиву на локальном диске.
        """
        dest = self._bin_dir / self._dep.subfolder
        dest.mkdir(parents=True, exist_ok=True)

        with tarfile.open(archive_path, "r:xz") as tf:
            tf.extractall(dest)

        logger.info(
            "Архив распакован в: %s", dest
        )

    @staticmethod
    def _format_speed(bytes_per_sec: float) -> str:
        """Форматировать скорость скачивания.

        Args:
            bytes_per_sec: Байт в секунду.

        Returns:
            Строка вида '15.3 МБ/с' или '1.2 КБ/с'.
        """
        if bytes_per_sec >= 1_048_576:
            return f"{bytes_per_sec / 1_048_576:.1f} МБ/с"
        elif bytes_per_sec >= 1024:
            return f"{bytes_per_sec / 1024:.1f} КБ/с"
        return f"{bytes_per_sec:.0f} Б/с"
