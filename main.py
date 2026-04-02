# -*- coding: utf-8 -*-
"""Точка входа приложения K-Tools.

Запускает PyQt6-приложение с регистрацией
всех доступных скриптов обработки.
"""

import logging
import sys
from typing import Any
import os
import ctypes
from datetime import datetime

# Принудительная установка UTF-8 для подпроцессов и консоли
os.environ["PYTHONIOENCODING"] = "utf-8"

# Попытка реконфигурации стандартных потоков для поддержки UTF-8 (Python 3.7+)
if hasattr(sys.stdout, "reconfigure"):
    try:
        getattr(sys.stdout, "reconfigure")(encoding="utf-8", errors="replace")
        getattr(sys.stderr, "reconfigure")(encoding="utf-8", errors="replace")
    except Exception:
        pass

# Регистрация AppUserModelID для корректного отображения
# иконки в таскбаре Windows
if sys.platform == "win32":
    try:
        myappid = "krnzhnr.ktools.app.v1"
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

        def _patched_popen(*args: Any, **kwargs: Any) -> Any:
            if sys.platform == "win32":
                if "creationflags" not in kwargs:
                    kwargs["creationflags"] = 0
                kwargs["creationflags"] |= 0x08000000  # CREATE_NO_WINDOW
            return _original_popen(*args, **kwargs)

        subprocess.Popen = _patched_popen  # type: ignore[misc, assignment]

        # Импортируем deew и подавляем логотипы
        import deew.__main__

        try:
            import deew.logos

            deew.logos.logos = [""] * len(deew.logos.logos)
        except (ImportError, AttributeError):
            pass

        # Временно сбрасываем sys.frozen, чтобы deew использовал
        # системный %TEMP%
        # иначе в скомпилированном виде он лезет в папку приложения
        _frozen = getattr(sys, "frozen", False)
        if _frozen:
            delattr(sys, "frozen")

        try:
            deew.__main__.main()
        finally:
            # Восстанавливаем состояние frozen
            if _frozen:
                setattr(sys, "frozen", True)
        sys.exit(0)

logger = logging.getLogger(__name__)

from pathlib import Path  # noqa: E402


def _cleanup_old_logs(log_dir: Path, days: int = 10) -> None:
    """Удалить лог-файлы старше указанного количества дней.

    Args:
        log_dir: Путь к папке с логами.
        days: Срок хранения в днях.
    """
    try:
        if not log_dir.exists():
            return

        now = datetime.now().timestamp()
        max_age_seconds = days * 24 * 60 * 60

        for file in log_dir.glob("ktools_*.log"):
            try:
                if (now - file.stat().st_mtime) > max_age_seconds:
                    file.unlink()
                    print(f"[LogCleanup] Удален старый лог: {file.name}")
            except OSError as e:
                print(f"[LogCleanup] Ошибка при удалении {file.name}: {e}")
    except Exception as e:
        print(f"[LogCleanup] Критическая ошибка при очистке: {e}")


def _setup_logging() -> None:
    """Настройка логирования приложения.

    Создает папку logs/ и настраивает вывод в консоль
    и в файл с временной меткой.
    """
    log_dir = Path("logs")
    _cleanup_old_logs(log_dir)

    try:
        if not log_dir.exists():
            log_dir.mkdir(parents=True, exist_ok=True)

        timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")
        log_file = log_dir / f"ktools_{timestamp}.log"
    except Exception as e:
        print(f"[Logging] Ошибка при подготовке папки логов: {e}")
        import os

        fallback_dir = (
            Path(os.getenv("LOCALAPPDATA", Path.home() / "AppData" / "Local"))
            / "KTools"
            / "logs"
        )
        try:
            fallback_dir.mkdir(parents=True, exist_ok=True)
            timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")
            log_file = fallback_dir / f"ktools_fallback_{timestamp}.log"
        except Exception as fallback_e:
            print(f"[Logging] Ошибка резервной директории: {fallback_e}")
            log_file = Path.home() / "ktools_fallback.log"

    log_format = "%(asctime)s | %(levelname)-8s | " "%(name)s | %(message)s"

    handlers: list[logging.Handler] = [logging.StreamHandler(sys.stdout)]

    if log_file:
        try:
            # Пытаемся сразу открыть файл, чтобы отловить PermissionError
            with open(log_file, "a", encoding="utf-8"):
                pass
            handlers.append(logging.FileHandler(log_file, encoding="utf-8"))
        except Exception as e:
            print(
                f"[Logging] Критическая ошибка доступа к файлу лога "
                f"({log_file}): {e}"
            )
            print(
                "[Logging] Приложение продолжит работу только с "
                "консольным логом."
            )

    # Настройка корневого логгера
    logging.basicConfig(
        level=logging.DEBUG,
        format=log_format,
        datefmt="%H:%M:%S",
        handlers=handlers,
    )


def _create_registry() -> Any:
    """Создать и заполнить реестр скриптов.

    Использует как явный список из app.scripts, так и
    динамическое сканирование для максимальной надежности.
    """
    from app.core.script_registry import ScriptRegistry
    from app.core.abstract_script import AbstractScript
    import pkgutil
    import importlib
    import app.scripts

    registry = ScriptRegistry()

    # Сначала пытаемся загрузить из явного списка (надежнее для EXE)
    modules_to_process = []
    if hasattr(app.scripts, "SCRIPT_MODULES"):
        modules_to_process.extend(app.scripts.SCRIPT_MODULES)
        logger.debug("Используется явный список из app.scripts.SCRIPT_MODULES")

    # Затем дополняем динамически, если что-то пропустили
    try:
        for _, name, is_pkg in pkgutil.iter_modules(app.scripts.__path__):
            if is_pkg:
                continue
            full_module_name = f"app.scripts.{name}"
            try:
                module = importlib.import_module(full_module_name)
                if module not in modules_to_process:
                    modules_to_process.append(module)
            except Exception:
                logger.exception(
                    "Ошибка при импорте модуля: %s", full_module_name
                )  # noqa: E501
    except Exception:
        logger.warning("pkgutil не смог просканировать app.scripts.__path__")

    # Регистрация классов из собранного списка модулей
    for module in modules_to_process:
        module_name = getattr(module, "__name__", "unknown")
        try:
            for attribute_name in dir(module):
                attribute = getattr(module, attribute_name)

                # Проверяем, что это класс, наследник AbstractScript
                # и не сам AbstractScript
                if (
                    isinstance(attribute, type)
                    and issubclass(attribute, AbstractScript)
                    and attribute is not AbstractScript
                ):

                    # Инстанцируем один раз для предотвращения дублей
                    script_instance = attribute()

                    # Проверка: если скрипт уже зарегистрирован
                    # (по имени), пропускаем
                    if registry.find_by_name(script_instance.name):
                        continue

                    registry.register(script_instance)
        except Exception:
            logger.exception(
                "Ошибка при регистрации скриптов из модуля: %s",
                module_name,
            )

    logger.info(
        "Всего зарегистрировано скриптов: %d",
        len(registry),
    )
    return registry


def main() -> None:
    """Главная функция запуска приложения."""
    _setup_logging()
    logger.info("Запуск K-Tools")

    # Сначала минимальные и быстрые импорты для отрисовки сплеш-скрина
    from PyQt6.QtWidgets import QApplication, QSplashScreen
    from PyQt6.QtGui import QIcon
    from PyQt6.QtCore import Qt
    from app.core.resource_utils import get_resource_path

    app = QApplication(sys.argv)

    icon_path = get_resource_path("app_icon.ico")
    app.setWindowIcon(QIcon(icon_path))

    # Показываем логотип приложения МГНОВЕННО (до загрузки Theme и MainWindow)
    splash = QSplashScreen(
        QIcon(icon_path).pixmap(128, 128),
        Qt.WindowType.WindowStaysOnTopHint | Qt.WindowType.FramelessWindowHint,
    )
    splash.show()
    app.processEvents()

    # Сплеш-скрин уже на экране. Теперь выполняем тяжелые импорты "под капотом"
    from app.core.temp_file_manager import TempFileManager
    from app.core.settings_manager import SettingsManager
    from qfluentwidgets import setTheme, Theme, qconfig, MessageBox
    from PyQt6.QtCore import QObject, pyqtSignal, QThread
    from app.ui.main_window import MainWindow

    class ThemeSignal(QObject):
        themeChanged = pyqtSignal(str)

    class ThemeWorker(QThread):
        def __init__(self, signal: ThemeSignal) -> None:
            super().__init__()
            self.signal = signal

        def run(self) -> None:
            import darkdetect

            try:
                darkdetect.listener(lambda t: self.signal.themeChanged.emit(t))
            except Exception:
                pass

    # Очистка временных файлов от предыдущих сессий
    TempFileManager().cleanup_on_startup()
    import atexit

    atexit.register(TempFileManager().cleanup)

    # Загрузка настроек темы
    settings = SettingsManager()
    theme_val = settings.theme

    if theme_val == "Dark":
        qconfig.theme = Theme.DARK
        setTheme(Theme.DARK)
    elif theme_val == "Light":
        qconfig.theme = Theme.LIGHT
        setTheme(Theme.LIGHT)
    else:
        # Системная тема
        qconfig.theme = Theme.AUTO
        setTheme(Theme.AUTO)

    theme_signal = ThemeSignal()

    def on_theme_changed(theme: str) -> None:
        try:
            if window and window.isVisible():
                msg = MessageBox(
                    "Смена системной темы",
                    "Обнаружено изменение системной темы. Для "
                    "корректного обновления всех иконок и стилей "
                    "рекомендуется перезапустить приложение. "
                    "Перезагрузить сейчас?",
                    window,
                )
                msg.yesButton.setText("Перезагрузить")
                msg.cancelButton.setText("Позже")
                if msg.exec():
                    from app.core.lifecycle import restart_current_app

                    restart_current_app()
        except (NameError, AttributeError):
            pass

    theme_signal.themeChanged.connect(on_theme_changed)

    theme_worker = ThemeWorker(theme_signal)
    theme_worker.start()

    registry = _create_registry()
    # Инициализация всех настроек по умолчанию в settings.ini
    SettingsManager().initialize_all_defaults(registry)
    window = MainWindow(registry=registry)
    window.show()

    splash.finish(window)

    logger.info("Окно приложения отображено")
    sys.exit(app.exec())


if __name__ == "__main__":
    main()
