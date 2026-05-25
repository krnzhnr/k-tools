# -*- coding: utf-8 -*-
"""Фоновые воркеры для проверки обновлений и скачивания файлов."""

import json
import logging
import urllib.request
import urllib.error
from typing import Any
from PyQt6.QtCore import QThread, pyqtSignal

logger = logging.getLogger(__name__)


def parse_version(v_str: str) -> tuple[int, ...]:
    """Преобразовать строку версии в кортеж чисел для сравнения.

    Игнорирует лидирующие 'v', 'v.' и разбирает пре-релизы.
    """
    if v_str.lower().startswith("v."):
        v_str = v_str[2:]
    elif v_str.lower().startswith("v"):
        v_str = v_str[1:]

    parts = v_str.split("-")
    main_version = parts[0]

    main_nums = []
    for num in main_version.split("."):
        try:
            main_nums.append(int(num))
        except ValueError:
            main_nums.append(0)

    while len(main_nums) < 3:
        main_nums.append(0)

    if len(parts) > 1:
        pre_str = parts[1].lower()
        pre_type = 0
        if "alpha" in pre_str:
            pre_type = 1
        elif "beta" in pre_str:
            pre_type = 2
        elif "rc" in pre_str:
            pre_type = 3

        pre_num = 0
        digits = "".join(c for c in pre_str if c.isdigit())
        if digits:
            try:
                pre_num = int(digits)
            except ValueError:
                pass
        # Устанавливаем 0 в 4-м элементе, чтобы показать, что это пре-релиз
        # Кортеж для "1.7.0-rc8" будет (1, 7, 0, 0, 3, 8)
        # Кортеж для стабильной "1.7.0" будет (1, 7, 0, 1, 0, 0)
        # В таком случае:
        # 1. (1, 7, 0, 0, 3, 8) > (1, 6, 0, 1, 0, 0) - Верно
        # 2. (1, 7, 0, 1, 0, 0) > (1, 7, 0, 0, 3, 8) - Верно
        return (
            main_nums[0],
            main_nums[1],
            main_nums[2],
            0,
            pre_type,
            pre_num,
        )
    else:
        # Для стабильной версии 4-й элемент равен 1
        return (main_nums[0], main_nums[1], main_nums[2], 1, 0, 0)


class UpdateCheckerWorker(QThread):
    """Фоновый поток для проверки обновлений с GitHub."""

    checkFinished = pyqtSignal(bool, str, str, str)
    checkError = pyqtSignal(str)

    def __init__(self, include_prereleases: bool, parent: Any = None) -> None:
        """Инициализация воркера проверки."""
        super().__init__(parent)
        self._include_prereleases = include_prereleases

    def run(self) -> None:
        """Основной цикл выполнения запроса."""
        url = "https://api.github.com/repos/krnzhnr/k-tools/releases"
        try:
            logger.info("Запуск фоновой проверки обновлений...")
            req = urllib.request.Request(
                url, headers={"User-Agent": "K-Tools-Updater"}
            )
            with urllib.request.urlopen(req, timeout=10) as response:
                data = json.loads(response.read().decode("utf-8"))

            if not isinstance(data, list) or not data:
                logger.info("Релизы на сервере не найдены")
                self.checkFinished.emit(False, "", "", "")
                return

            latest_release = None
            max_parsed = None

            for release in data:
                is_prerelease = release.get("prerelease", False)
                if is_prerelease and not self._include_prereleases:
                    continue

                tag = release.get("tag_name", "")
                if tag.lower() == "pre-release" and release.get("name"):
                    tag = release.get("name", "")

                parsed = parse_version(tag)
                if max_parsed is None or parsed > max_parsed:
                    max_parsed = parsed
                    latest_release = release

            if not latest_release:
                logger.info("Подходящие под настройки релизы не найдены")
                self.checkFinished.emit(False, "", "", "")
                return

            tag_name = latest_release.get("tag_name", "")
            # Если тег называется 'pre-release', извлекаем реальную версию
            # из поля 'name'
            if (
                tag_name.lower() == "pre-release"
                and latest_release.get("name")
            ):
                tag_name = latest_release.get("name", "")

            body = latest_release.get("body", "")
            download_url = latest_release.get("zipball_url", "")

            assets = latest_release.get("assets", [])
            if assets:
                download_url = assets[0].get(
                    "browser_download_url", download_url
                )

            from app.core.version import get_app_version

            current_v = get_app_version()

            current_parsed = parse_version(current_v)
            latest_parsed = parse_version(tag_name)

            logger.info(
                "Сравнение версий: Текущая v%s, Доступная v%s",
                current_v,
                tag_name,
            )

            if latest_parsed > current_parsed:
                logger.info("Найдена новая версия: %s", tag_name)
                self.checkFinished.emit(True, tag_name, body, download_url)
            else:
                logger.info("Установлена последняя версия")
                self.checkFinished.emit(False, tag_name, body, download_url)

        except urllib.error.URLError as err:
            logger.exception("Сетевая ошибка проверки обновлений: %s", err)
            self.checkError.emit(
                "Не удалось подключиться к серверу обновлений."
            )
        except Exception as err:
            logger.exception("Непредвиденная ошибка проверки: %s", err)
            self.checkError.emit("Произошла ошибка при разборе обновлений.")


class FileDownloader(QThread):
    """Фоновый поток для скачивания файлов обновлений с прогрессом."""

    progress = pyqtSignal(int)
    finished = pyqtSignal(str)
    error = pyqtSignal(str)

    def __init__(self, url: str, dest_path: str, parent: Any = None) -> None:
        """Инициализация воркера скачивания."""
        super().__init__(parent)
        self._url = url
        self._dest_path = dest_path
        self._is_cancelled = False

    def cancel(self) -> None:
        """Отменить скачивание."""
        self._is_cancelled = True
        logger.info("Запрошена отмена скачивания файла обновления")

    def run(self) -> None:
        """Выполнение скачивания файла по частям."""
        try:
            logger.info("Запуск скачивания: %s", self._url)
            req = urllib.request.Request(
                self._url, headers={"User-Agent": "K-Tools-Updater"}
            )
            with urllib.request.urlopen(req, timeout=15) as response:
                total_size = int(response.headers.get("content-length", 0))
                bytes_downloaded = 0
                block_size = 8192

                with open(self._dest_path, "wb") as f:
                    while True:
                        if self._is_cancelled:
                            break
                        buffer = response.read(block_size)
                        if not buffer:
                            break
                        f.write(buffer)
                        bytes_downloaded += len(buffer)
                        if total_size > 0:
                            percent = int(bytes_downloaded * 100 / total_size)
                            self.progress.emit(percent)

                if self._is_cancelled:
                    import os

                    if os.path.exists(self._dest_path):
                        os.remove(self._dest_path)
                    logger.info("Скачивание отменено, временный файл удален")
                else:
                    logger.info("Скачивание успешно завершено")
                    self.finished.emit(self._dest_path)
        except Exception as err:
            logger.exception("Ошибка при скачивании файла обновления: %s", err)
            self.error.emit(f"Ошибка при скачивании: {str(err)}")
