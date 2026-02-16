# -*- coding: utf-8 -*-
"""Главное окно приложения K-Tools."""

import logging

from PyQt6.QtGui import QIcon
from PyQt6.QtCore import Qt, QSize
from PyQt6.QtWidgets import QApplication
from qfluentwidgets import (
    FluentIcon,
    NavigationItemPosition,
    FluentWindow,
    setTheme,
    Theme,
)

from app.core.abstract_script import AbstractScript
from app.core.script_registry import ScriptRegistry
from app.core.resource_utils import get_resource_path
from app.ui.work_panel import ScriptPage
from app.ui.settings_page import SettingsPage

logger = logging.getLogger(__name__)


class MainWindow(FluentWindow):
    """Главное окно приложения K-Tools.

    Реализует интерфейс в стиле PowerToys:
    навигация слева, рабочая панель справа.
    """

    WINDOW_WIDTH = 750
    WINDOW_HEIGHT = 550

    def __init__(
        self,
        registry: ScriptRegistry,
    ) -> None:
        """Инициализация главного окна.

        Args:
            registry: Реестр зарегистрированных скриптов.
        """
        super().__init__()
        self._registry = registry
        self._script_pages: dict[str, ScriptPage] = {}

        self._setup_window()
        self._setup_navigation()

        logger.info(
            "Главное окно инициализируется с %d скриптами в реестре",
            len(registry),
        )

    def _setup_window(self) -> None:
        """Настройка параметров окна."""
        self.setWindowTitle("K-Tools")
        icon_path = get_resource_path("app_icon.ico")
        self.setWindowIcon(QIcon(icon_path))
        logger.info(
            "Установлен минимальный размер окна: %dx%d",
            self.WINDOW_WIDTH, self.WINDOW_HEIGHT
        )
        self.resize(self.WINDOW_WIDTH, self.WINDOW_HEIGHT)

        # Центрирование окна на экране
        screen = QApplication.primaryScreen()
        if screen:
            screen_geometry = screen.availableGeometry()
            x = (
                (screen_geometry.width() - self.WINDOW_WIDTH)
                // 2
            )
            y = (
                (screen_geometry.height() - self.WINDOW_HEIGHT)
                // 2
            )
            self.move(x, y)
            logger.info("Окно центрировано на экране в позиции (%d, %d)", x, y)

    def resizeEvent(self, event) -> None:
        """Логирование изменения размера окна."""
        super().resizeEvent(event)
        size = event.size()
        logger.info("Размер окна изменен пользователем: %dx%d", size.width(), size.height())



    def _setup_navigation(self) -> None:
        """Настройка навигационной панели со скриптами."""
        for script in self._registry.scripts:
            page = ScriptPage(script=script, parent=self)
            page.setObjectName(
                f"page_{script.name}"
            )
            self._script_pages[script.name] = page

            icon = self._resolve_icon(script.icon_name)

            self.addSubInterface(
                interface=page,
                icon=icon,
                text=script.name,
            )

        # Добавление страницы настроек вниз
        self._settings_page = SettingsPage(self)
        self.addSubInterface(
            interface=self._settings_page,
            icon=FluentIcon.SETTING,
            text="Настройки",
            position=NavigationItemPosition.BOTTOM
        )

        self.stackedWidget.currentChanged.connect(self._on_current_page_changed)

        logger.info(
            "Навигационная панель успешно настроена. Всего элементов: %d (скрипты) + 1 (настройки)",
            len(self._script_pages),
        )

    def _on_current_page_changed(self, index: int) -> None:
        """Логирование переключения страниц."""
        widget = self.stackedWidget.widget(index)
        page_name = widget.objectName() if widget else "Неизвестно"
        logger.info("Пользователь переключился на страницу: %s (индекс: %d)", page_name, index)

        # Автоматическая настройка ширины боковой панели
        fm = self.fontMetrics()
        max_text_width = 0
        for script in self._registry.scripts:
            width = fm.horizontalAdvance(script.name)
            if width > max_text_width:
                max_text_width = width

        # Ширина панели = ширина текста + иконка (24) + отступы (~46)
        self.navigationInterface.setExpandWidth(max_text_width + 100)

    @staticmethod
    def _resolve_icon(icon_name: str) -> FluentIcon:
        """Преобразовать имя иконки в FluentIcon.

        Args:
            icon_name: Имя иконки (например, 'VIDEO').

        Returns:
            Соответствующая FluentIcon.
        """
        try:
            icon = FluentIcon[icon_name]
            logger.debug("Иконка '%s' успешно разрешена", icon_name)
            return icon
        except KeyError:
            logger.warning(
                "Иконка '%s' не найдена в FluentIcon, "
                "используется значение по умолчанию COMMAND_PROMPT",
                icon_name,
            )
            return FluentIcon.COMMAND_PROMPT
