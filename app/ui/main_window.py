# -*- coding: utf-8 -*-
"""Главное окно приложения K-Tools."""

import logging

from PyQt6.QtGui import QIcon
from PyQt6.QtCore import Qt, QSize
from PyQt6.QtWidgets import QApplication, QWidget
from qfluentwidgets import (
    FluentIcon,
    NavigationItemPosition,
    FluentWindow,
    setTheme,
    Theme,
)
from qfluentwidgets.components.widgets.stacked_widget import (
    DrillInTransitionStackedWidget,
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

    WINDOW_WIDTH = 781
    WINDOW_HEIGHT = 960

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
        self._shown = False

        # DrillIn-анимация переходов
        self._replace_stacked_view()

        self._setup_window()
        self._setup_navigation()

        logger.info(
            "Главное окно инициализируется "
            "с %d скриптами в реестре",
            len(registry),
        )

    def _replace_stacked_view(self) -> None:
        """Замена PopUp-анимации на DrillIn."""
        old_view = self.stackedWidget.view
        old_view.hide()
        self.stackedWidget.hBoxLayout.removeWidget(
            old_view
        )
        old_view.currentChanged.disconnect(
            self.stackedWidget.currentChanged
        )
        old_view.deleteLater()

        new_view = DrillInTransitionStackedWidget(
            self.stackedWidget
        )
        self.stackedWidget.view = new_view
        self.stackedWidget.hBoxLayout.addWidget(
            new_view
        )
        new_view.currentChanged.connect(
            self.stackedWidget.currentChanged
        )


        # Патч API: DrillIn принимает (duration, isBack)
        # вместо (popOut, showNext, duration, easing)
        sw = self.stackedWidget
        duration = 500

        def _set_current_widget(
            widget: QWidget,
            popOut: bool = True,  # noqa: ARG001
        ) -> None:
            from PyQt6.QtWidgets import (
                QAbstractScrollArea,
            )
            if isinstance(widget, QAbstractScrollArea):
                widget.verticalScrollBar().setValue(0)
            sw.view.setCurrentWidget(
                widget, duration=duration
            )

        def _set_current_index(
            index: int,
            popOut: bool = True,  # noqa: ARG001
        ) -> None:
            _set_current_widget(
                sw.view.widget(index)
            )

        sw.setCurrentWidget = _set_current_widget
        sw.setCurrentIndex = _set_current_index

        logger.info(
            "DrillIn-анимация переходов "
            "установлена"
        )

    def _setup_window(self) -> None:
        """Настройка параметров окна."""
        self.setWindowTitle("K-Tools")
        icon_path = get_resource_path("app_icon.ico")
        self.setWindowIcon(QIcon(icon_path))
        self.setMinimumSize(
            self.WINDOW_WIDTH, self.WINDOW_HEIGHT
        )
        self.resize(
            self.WINDOW_WIDTH, self.WINDOW_HEIGHT
        )
        logger.info(
            "Установлен минимальный размер "
            "окна: %dx%d",
            self.WINDOW_WIDTH,
            self.WINDOW_HEIGHT,
        )

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
        logger.info(
            "Размер окна изменен пользователем: %dx%d",
            size.width(), size.height(),
        )

    def showEvent(self, event) -> None:
        """Подгонка layout при первом показе окна."""
        super().showEvent(event)
        if not self._shown:
            self._shown = True
            self.adjustSize()
            self.resize(
                self.WINDOW_WIDTH,
                self.WINDOW_HEIGHT,
            )
            logger.info(
                "Layout обновлён при первом показе"
            )

    def _setup_navigation(self) -> None:
        """Настройка навигационной панели со скриптами."""
        # Группировка скриптов по категориям
        categories = {}
        for script in self._registry.scripts:
            cat = script.category
            if cat not in categories:
                categories[cat] = []
            categories[cat].append(script)

        # Порядок категорий для отображения
        ordered_cats = ["Аудио", "Видео", "Муксинг"]
        for cat in categories:
            if cat not in ordered_cats:
                ordered_cats.append(cat)

        # Соответствие иконок и ASCII-ключей
        cat_info = {
            "Аудио": (FluentIcon.MUSIC, "audio"),
            "Видео": (FluentIcon.VIDEO, "video"),
            "Муксинг": (FluentIcon.SHARE, "muxing"),
        }

        for cat_name in ordered_cats:
            if cat_name not in categories:
                continue

            scripts = categories[cat_name]
            icon, cat_key = cat_info.get(
                cat_name, 
                (FluentIcon.FOLDER, f"cat{ordered_cats.index(cat_name)}")
            )
            
            # Для категорий используем addItem напрямую в navigationInterface.
            # Это создает элемент, который может быть родителем, но не привязан к странице.
            parent_item = self.navigationInterface.addItem(
                routeKey=cat_key,
                icon=icon,
                text=cat_name,
                selectable=False
            )
            parent_item.setObjectName(cat_key)

            for script in scripts:
                page = ScriptPage(script=script, parent=self)
                
                # Имя объекта (маршрут) ОБЯЗАТЕЛЬНО должно быть ASCII
                # Для стабильности используем простое имя класса
                safe_id = "".join(c for c in script.__class__.__name__ if c.isalnum())
                page.setObjectName(safe_id)
                
                self._script_pages[script.name] = page

                script_icon = self._resolve_icon(script.icon_name)

                # Добавляем скрипт вложенным в категорию. 
                # addSubInterface сам добавит его в stackedWidget и создаст NavigationItem.
                self.addSubInterface(
                    interface=page,
                    icon=script_icon,
                    text=script.name,
                    parent=parent_item
                )

        # Добавление страницы настроек вниз
        self._settings_page = SettingsPage(self)
        self.addSubInterface(
            interface=self._settings_page,
            icon=FluentIcon.SETTING,
            text="Настройки",
            position=NavigationItemPosition.BOTTOM
        )

        self.stackedWidget.currentChanged.connect(
            self._on_current_page_changed
        )

        # Настройка ширины навигационной панели
        fm = self.fontMetrics()
        max_text_width = 160
        for script in self._registry.scripts:
            width = fm.horizontalAdvance(script.name)
            if width > max_text_width:
                max_text_width = width

        self.navigationInterface.setExpandWidth(
            max_text_width + 120
        )

        logger.info(
            "Навигационная панель успешно настроена (иерархический вид). "
            "Всего скриптов: %d",
            len(self._script_pages),
        )

    def _on_current_page_changed(
        self, index: int
    ) -> None:
        """Логирование переключения страниц."""
        widget = self.stackedWidget.widget(index)
        page_name = (
            widget.objectName()
            if widget
            else "Неизвестно"
        )
        logger.info(
            "Пользователь переключился "
            "на страницу: %s (индекс: %d)",
            page_name,
            index,
        )

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
