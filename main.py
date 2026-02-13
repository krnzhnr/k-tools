# -*- coding: utf-8 -*-
"""Точка входа приложения K-Tools.

Запускает PyQt6-приложение с регистрацией
всех доступных скриптов обработки.
"""

import logging
import sys

from PyQt6.QtWidgets import QApplication
from qfluentwidgets import setTheme, Theme

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


def _setup_logging() -> None:
    """Настройка логирования приложения."""
    logging.basicConfig(
        level=logging.INFO,
        format=(
            "%(asctime)s | %(levelname)-8s | "
            "%(name)s | %(message)s"
        ),
        datefmt="%H:%M:%S",
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
    _setup_logging()
    logger.info("Запуск K-Tools")

    app = QApplication(sys.argv)
    setTheme(Theme.DARK)

    registry = _create_registry()
    window = MainWindow(registry=registry)
    window.show()

    logger.info("Окно приложения отображено")
    sys.exit(app.exec())


if __name__ == "__main__":
    main()
