# -*- coding: utf-8 -*-
"""Главное окно приложения K-Tools."""

import logging

from PyQt6.QtCore import Qt
from PyQt6.QtGui import QIcon, QMouseEvent
from PyQt6.QtWidgets import QApplication, QWidget
from qfluentwidgets import (
    FluentIcon,
    NavigationItemPosition,
    FluentWindow,
)
from qfluentwidgets.components.widgets.stacked_widget import (
    DrillInTransitionStackedWidget,
)

from app.core.abstract_script import AbstractScript
from app.core.script_registry import ScriptRegistry
from app.core.constants import CATEGORY_CONFIG
from app.core.resource_utils import get_resource_path
from app.ui.work_panel import ScriptPage
from app.ui.settings_page import SettingsPage
from app.ui.home_page import HomePage

logger = logging.getLogger(__name__)


class MainWindow(FluentWindow):
    """Главное окно приложения K-Tools.

    Реализует интерфейс в стиле PowerToys:
    навигация слева, рабочая панель справа.
    """

    WINDOW_WIDTH = 792
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

        # Инициализация главной страницы
        self._home_page = HomePage(
            list(self._registry.scripts), self._resolve_icon, self
        )
        self._home_page.scriptRequested.connect(self._on_script_requested)

        self._setup_navigation()

        logger.info(
            "Главное окно инициализируется " "с %d скриптами в реестре",
            len(registry),
        )

    def _replace_stacked_view(self) -> None:
        """Замена PopUp-анимации на DrillIn."""
        old_view = self.stackedWidget.view
        old_view.hide()
        self.stackedWidget.hBoxLayout.removeWidget(old_view)
        old_view.currentChanged.disconnect(self.stackedWidget.currentChanged)
        old_view.deleteLater()

        new_view = DrillInTransitionStackedWidget(self.stackedWidget)
        self.stackedWidget.view = new_view
        self.stackedWidget.hBoxLayout.addWidget(new_view)
        new_view.currentChanged.connect(self.stackedWidget.currentChanged)

        # Патч API: DrillIn принимает (duration, isBack)
        # вместо (popOut, showNext, duration, easing)
        sw = self.stackedWidget
        duration = 250

        def _set_current_widget(
            widget: QWidget,
            popOut: bool = True,  # noqa: ARG001
        ) -> None:
            from PyQt6.QtWidgets import (
                QAbstractScrollArea,
            )

            if isinstance(widget, QAbstractScrollArea):
                bar = widget.verticalScrollBar()
                if bar is not None:
                    bar.setValue(0)
            sw.view.setCurrentWidget(widget, duration=duration)

        def _set_current_index(
            index: int,
            popOut: bool = True,  # noqa: ARG001
        ) -> None:
            _set_current_widget(sw.view.widget(index))

        sw.setCurrentWidget = _set_current_widget
        sw.setCurrentIndex = _set_current_index

        logger.info("DrillIn-анимация переходов " "установлена")

    def _setup_window(self) -> None:
        """Настройка параметров окна."""
        self.setWindowTitle("K-Tools")
        icon_path = get_resource_path("app_icon.ico")
        self.setWindowIcon(QIcon(icon_path))
        self.setMinimumSize(self.WINDOW_WIDTH, self.WINDOW_HEIGHT)
        self.resize(self.WINDOW_WIDTH, self.WINDOW_HEIGHT)
        logger.info(
            "Установлен минимальный размер " "окна: %dx%d",
            self.WINDOW_WIDTH,
            self.WINDOW_HEIGHT,
        )

        # Центрирование окна на экране
        screen = QApplication.primaryScreen()
        if screen:
            screen_geometry = screen.availableGeometry()
            x = (screen_geometry.width() - self.WINDOW_WIDTH) // 2
            y = (screen_geometry.height() - self.WINDOW_HEIGHT) // 2
            self.move(x, y)
            logger.info("Окно центрировано на экране в позиции (%d, %d)", x, y)

    def resizeEvent(self, event) -> None:
        """Логирование изменения размера окна."""
        super().resizeEvent(event)
        size = event.size()
        logger.info(
            "Размер окна изменен пользователем: %dx%d",
            size.width(),
            size.height(),
        )

    def mousePressEvent(self, event: QMouseEvent) -> None:
        """Обработка нажатий кнопок мыши для навигации.

        Args:
            event: Событие мыши.
        """
        if event.button() == Qt.MouseButton.XButton1:
            if self.navigationInterface.panel.returnButton.isEnabled():
                logger.info("Навигация назад по кнопке мыши")
                self.navigationInterface.panel.returnButton.click()
        elif event.button() == Qt.MouseButton.XButton2:
            # Навигация вперед (если будет реализована в будущем)
            logger.debug("Нажата кнопка навигации вперед")

        super().mousePressEvent(event)

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
            logger.info("Layout обновлён при первом показе")

    def _setup_navigation(self) -> None:
        """Настройка навигационной панели со скриптами."""
        self.addSubInterface(
            interface=self._home_page, icon=FluentIcon.HOME, text="Главная"
        )

        categories = self._group_scripts()

        # 1. Сначала добавим известные категории (сохраняя порядок)
        for cat_name, config in CATEGORY_CONFIG.items():
            if cat_name in categories:
                self._add_category_to_nav(
                    cat_name,
                    categories[cat_name],
                    icon=self._resolve_icon(str(config["icon"])),
                    route_key=str(config["nav_key"]),
                )

        # 2. Затем добавляем все остальные категории, если они есть
        for cat_name, scripts in categories.items():
            if cat_name not in CATEGORY_CONFIG:
                logger.warning(
                    "Обнаружена неизвестная категория: %s", cat_name
                )
                self._add_category_to_nav(
                    cat_name,
                    scripts,
                    icon=FluentIcon.FOLDER,
                    route_key=f"cat_{cat_name}",
                )

        self._settings_page = SettingsPage(self)
        self.addSubInterface(
            interface=self._settings_page,
            icon=FluentIcon.SETTING,
            text="Настройки",
            position=NavigationItemPosition.BOTTOM,
        )

        self.stackedWidget.currentChanged.connect(
            self._on_current_page_changed
        )

        fm = self.fontMetrics()
        max_width = max(
            (fm.horizontalAdvance(s.name) for s in self._registry.scripts),
            default=160,
        )
        self.navigationInterface.setExpandWidth(max_width + 120)

        logger.info(
            "Навигационная панель успешно настроена. Всего скриптов: %d",
            len(self._script_pages),
        )

    def _group_scripts(self) -> dict[str, list[AbstractScript]]:
        """Группировка скриптов по категориям."""
        categories: dict[str, list[AbstractScript]] = {}
        for script in self._registry.scripts:
            categories.setdefault(script.category, []).append(script)
        return categories

    def _add_category_to_nav(
        self,
        cat_name: str,
        scripts: list[AbstractScript],
        icon: FluentIcon,
        route_key: str,
    ) -> None:
        """Добавление категории и её скриптов в навигацию.

        Args:
            cat_name: Имя категории.
            scripts: Список скриптов.
            icon: Иконка категории.
            route_key: Псевдоним маршрута.
        """
        parent_item = self.navigationInterface.addItem(
            routeKey=route_key, icon=icon, text=cat_name, selectable=False
        )
        parent_item.setObjectName(route_key)

        for script in scripts:
            page = ScriptPage(script=script, parent=self)
            safe_id = "".join(
                c for c in script.__class__.__name__ if c.isalnum()
            )
            page.setObjectName(safe_id)
            self._script_pages[script.name] = page

            self.addSubInterface(
                interface=page,
                icon=self._resolve_icon(script.icon_name),
                text=script.name,
                parent=parent_item,
            )

    def _on_current_page_changed(self, index: int) -> None:
        """Логирование переключения страниц."""
        widget = self.stackedWidget.widget(index)
        page_name = widget.objectName() if widget else "Неизвестно"
        logger.info(
            "Пользователь переключился " "на страницу: %s (индекс: %d)",
            page_name,
            index,
        )

    def _on_script_requested(self, script_name: str) -> None:
        """Переключиться на страницу скрипта по названию.

        Этот метод вызывается при клике по карточке на главной странице.
        """
        if page := self._script_pages.get(script_name):
            logger.info(
                "Переход на страницу скрипта '%s' из Home", script_name
            )
            self.switchTo(page)

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
