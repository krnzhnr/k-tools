# -*- coding: utf-8 -*-
"""Точка входа приложения K-Tools.

Запускает PyQt6-приложение с регистрацией
всех доступных скриптов обработки.
"""

import logging
import sys
import ctypes

from PyQt6.QtWidgets import QApplication
from PyQt6.QtGui import QIcon
from qfluentwidgets import setTheme, Theme

from app.core.resource_utils import get_resource_path
from app.core.script_registry import ScriptRegistry
from app.scripts.container_converter import (
    ContainerConverterScript,
)
from app.scripts.metadata_cleaner import (
    MetadataCleanerScript,
)
from app.scripts.audio_converter import (
    AudioConverterScript,
)
from app.scripts.audio_speed_changer import AudioSpeedChangerScript
from app.scripts.muxer import MuxerScript
from app.ui.main_window import MainWindow

logger = logging.getLogger(__name__)


import os
from datetime import datetime


def _setup_logging() -> None:
    """Настройка логирования приложения.

    Создает папку logs/ и настраивает вывод в консоль
    и в файл с временной меткой.
    """
    log_dir = "logs"
    try:
        if not os.path.exists(log_dir):
            os.makedirs(log_dir)
    except Exception as e:
        print(f"Критическая ошибка: не удалось создать папку логов: {e}")
        return

    # Формируем имя файла: ktools_20260216_001025.log
    timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")
    log_file = os.path.join(log_dir, f"ktools_{timestamp}.log")

    log_format = (
        "%(asctime)s | %(levelname)-8s | "
        "%(name)s | %(message)s"
    )

    # Настройка корневого логгера
    logging.basicConfig(
        level=logging.INFO,
        format=log_format,
        datefmt="%H:%M:%S",
        handlers=[
            logging.StreamHandler(sys.stdout),
            logging.FileHandler(log_file, encoding="utf-8"),
        ],
    )


def _create_registry() -> ScriptRegistry:
    """Создать и заполнить реестр скриптов.

    Returns:
        Заполненный реестр скриптов.
    """
    registry = ScriptRegistry()
    registry.register(MetadataCleanerScript())
    registry.register(ContainerConverterScript())
    registry.register(AudioConverterScript())
    registry.register(AudioSpeedChangerScript())
    registry.register(MuxerScript())

    logger.info(
        "Зарегистрировано скриптов: %d",
        len(registry),
    )
    return registry


def main() -> None:
    """Главная функция запуска приложения."""
    # Регистрация AppUserModelID для корректного отображения иконки в таскбаре
    myappid = 'krnzhnr.ktools.v1' # произвольная, но уникальная строка
    try:
        ctypes.windll.shell32.SetCurrentProcessExplicitAppUserModelID(myappid)
    except Exception:
        pass

    _setup_logging()
    logger.info("Запуск K-Tools")

    app = QApplication(sys.argv)
    app.setWindowIcon(QIcon(get_resource_path("app_icon.ico")))
    setTheme(Theme.DARK)

    registry = _create_registry()
    window = MainWindow(registry=registry)
    window.show()

    logger.info("Окно приложения отображено")
    sys.exit(app.exec())


if __name__ == "__main__":
    main()
