# -*- coding: utf-8 -*-
"""Точка входа приложения K-Tools.

Запускает PyQt6-приложение с регистрацией
всех доступных скриптов обработки.
"""

import logging
import sys
import os
import ctypes
from datetime import datetime

# Принудительная установка UTF-8 для подпроцессов и консоли
os.environ["PYTHONIOENCODING"] = "utf-8"

# Попытка реконфигурации стандартных потоков для поддержки UTF-8 (Python 3.7+)
if hasattr(sys.stdout, "reconfigure"):
    try:
        sys.stdout.reconfigure(encoding='utf-8', errors='replace')
        sys.stderr.reconfigure(encoding='utf-8', errors='replace')
    except Exception:
        pass

# Регистрация AppUserModelID для корректного отображения иконки в таскбаре Windows
if sys.platform == 'win32':
    try:
        myappid = 'krnzhnr.ktools.app.v1'
        ctypes.windll.shell32.SetCurrentProcessExplicitAppUserModelID(myappid)
    except Exception:
        pass

# Перехват запуска модулей через -m (для deew и других)
if len(sys.argv) >= 3 and sys.argv[1] == "-m":
    module_name = sys.argv[2]
    if module_name == "deew":
        # Очищаем argv ДО импорта, так как deew парсит их на уровне модуля
        sys.argv = [sys.argv[0]] + sys.argv[3:]
        
        # Патчим subprocess.Popen, чтобы deew не открывал окна терминала 
        # при вызове ffmpeg, ffprobe и dee.exe
        import subprocess
        _original_popen = subprocess.Popen
        def _patched_popen(*args, **kwargs):
            if sys.platform == 'win32':
                if 'creationflags' not in kwargs:
                    kwargs['creationflags'] = 0
                kwargs['creationflags'] |= 0x08000000 # CREATE_NO_WINDOW
            return _original_popen(*args, **kwargs)
        subprocess.Popen = _patched_popen

        # Импортируем deew и подавляем логотипы
        import deew.__main__
        try:
            import deew.logos
            deew.logos.logos = [""] * len(deew.logos.logos)
        except (ImportError, AttributeError):
            pass
            
        # Временно сбрасываем sys.frozen, чтобы deew использовал системный %TEMP%
        # иначе в скомпилированном виде он лезет в папку приложения
        _frozen = getattr(sys, 'frozen', False)
        if _frozen:
            delattr(sys, 'frozen')
            
        try:
            deew.__main__.main()
        finally:
            # Восстанавливаем состояние frozen
            if _frozen:
                setattr(sys, 'frozen', True)
        sys.exit(0)

from PyQt6.QtWidgets import QApplication
from PyQt6.QtGui import QIcon
from qfluentwidgets import setTheme, Theme

from app.core.resource_utils import get_resource_path
from app.core.script_registry import ScriptRegistry
from app.scripts.container_converter import ContainerConverterScript
from app.scripts.metadata_cleaner import MetadataCleanerScript
from app.scripts.audio_converter import AudioConverterScript
from app.scripts.audio_dee_downmixer import AudioDeeDownmixerScript
from app.scripts.audio_speed_changer import AudioSpeedChangerScript
from app.scripts.muxer import MuxerScript
from app.scripts.stream_manager import StreamManagerScript
from app.scripts.stream_replacer import StreamReplacerScript
from app.ui.main_window import MainWindow

logger = logging.getLogger(__name__)


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
        level=logging.DEBUG,
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
    registry.register(AudioDeeDownmixerScript())
    registry.register(AudioSpeedChangerScript())
    registry.register(MuxerScript())
    registry.register(StreamManagerScript())
    registry.register(StreamReplacerScript())

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
    app.setWindowIcon(QIcon(get_resource_path("app_icon.ico")))
    setTheme(Theme.DARK)

    registry = _create_registry()
    window = MainWindow(registry=registry)
    window.show()

    logger.info("Окно приложения отображено")
    sys.exit(app.exec())


if __name__ == "__main__":
    main()
