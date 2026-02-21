# -*- coding: utf-8 -*-
"""Управление жизненным циклом приложения."""

import os
import sys
import logging

logger = logging.getLogger(__name__)


def is_debugging() -> bool:
    """Проверить, запущено ли приложение под отладчиком (IDE)."""
    # 1. Проверка через sys.gettrace
    if getattr(sys, 'gettrace', None) and sys.gettrace():
        return True
    
    # 2. Проверка популярных модулей отладки
    if 'debugpy' in sys.modules or 'pydevd' in sys.modules:
        return True
        
    # 3. Проверка переменных окружения
    if os.environ.get('DEBUGPY_RUNNING') == '1':
        return True
        
    return False


def restart_current_app() -> None:
    """Перезапустить текущее приложение.
    
    Завершает текущий процесс и запускает новый с теми же аргументами.
    В режиме отладки просто выходит, чтобы не ломать сессию IDE.
    """
    if is_debugging():
        logger.warning(
            "Режим отладки обнаружен. Автоматический перезапуск пропущен. "
            "Пожалуйста, перезапустите приложение в IDE вручную."
        )
        sys.exit(0)

    logger.info("Выполняется автоматический перезапуск приложения...")
    
    try:
        # Получаем путь к исполняемому файлу (python.exe или скомпилированный exe)
        executable = sys.executable
        # Аргументы командной строки
        args = sys.argv
        
        # В некоторых случаях (например, при запуске через python main.py) 
        # первый аргумент — это скрипт. os.execl требует полный список.
        os.execl(executable, executable, *args)
    except Exception:
        logger.exception("Критическая ошибка при попытке перезапуска приложения")
        # Если execl не сработал (редко, но бывает), просто выходим
        sys.exit(1)
