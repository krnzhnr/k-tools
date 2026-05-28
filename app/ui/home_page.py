# -*- coding: utf-8 -*-
"""Домашняя страница с быстрым доступом к скриптам."""

import logging
from typing import List, Callable

from PyQt6.QtCore import Qt, pyqtSignal
from PyQt6.QtGui import QColor
from PyQt6.QtWidgets import QWidget, QVBoxLayout, QHBoxLayout, QFrame
from qfluentwidgets import (
    FlowLayout,
    StrongBodyLabel,
    BodyLabel,
    CaptionLabel,
    IconWidget,
    CardWidget,
    SmoothScrollArea,
    FluentIcon,
    SubtitleLabel,
    InfoBar,
    InfoBarPosition,
)

from app.core.abstract_script import AbstractScript
from app.core.constants import CATEGORY_CONFIG
from app.core.dependency_manager import DependencyManager

logger = logging.getLogger(__name__)


class ScriptCard(CardWidget):
    """Карточка скрипта для быстрого перехода.

    Отображает иконку, название и краткое описание.
    """

    scriptClicked = pyqtSignal(str)

    def __init__(
        self,
        script: AbstractScript,
        resolve_icon: Callable[[str], FluentIcon],
        parent: QWidget | None = None,
    ) -> None:
        """Инициализация карточки.

        Args:
            script: Объект скрипта.
            resolve_icon: Функция разрешения иконки.
            parent: Родительский виджет.
        """
        super().__init__(parent)
        self._script = script
        self._dep_mgr = DependencyManager()
        self._is_available = True

        self.setFixedSize(330, 100)

        layout = QHBoxLayout(self)
        layout.setContentsMargins(16, 12, 16, 12)
        layout.setSpacing(12)

        self._init_icon_section(layout, resolve_icon)
        self._init_text_section(layout)
        layout.addStretch(1)

        self.update_availability()

    def update_availability(self) -> None:
        """Обновить визуальное состояние доступности скрипта."""
        self._is_available = self._dep_mgr.is_script_available(
            self._script.required_dependencies
        )
        if self._is_available:
            self.setCursor(Qt.CursorShape.PointingHandCursor)
            self.setStyleSheet("")
            self.title_label.setStyleSheet("")
            self.desc_label.setStyleSheet("color: #AAAAAA;")
            self.icon_widget.setEnabled(True)
        else:
            self.setCursor(Qt.CursorShape.ArrowCursor)
            self.setStyleSheet(
                "background: rgba(255, 255, 255, 0.03);"
                "border: 1px solid rgba(255, 255, 255, 0.05);"
            )
            self.title_label.setStyleSheet("color: #777777;")
            self.desc_label.setStyleSheet("color: #555555;")
            self.icon_widget.setEnabled(False)

    def _init_icon_section(
        self,
        layout: QHBoxLayout,
        resolve_icon: Callable[[str], FluentIcon],
    ) -> None:
        """Инициализация секции иконки.

        Args:
            layout: Родительский макет.
            resolve_icon: Функция разрешения иконки.
        """
        cat = self._script.category.strip()
        config = CATEGORY_CONFIG.get(cat, {})
        bg_color, icon_color = config.get(
            "color", ("rgba(255, 255, 255, 0.1)", "#FFFFFF")
        )

        icon_wrapper = QFrame(self)
        icon_wrapper.setFixedSize(40, 40)
        icon_wrapper.setStyleSheet(
            f"background: {bg_color}; border-radius: 8px;"
        )
        icon_layout = QVBoxLayout(icon_wrapper)
        icon_layout.setContentsMargins(8, 8, 8, 8)

        icon = resolve_icon(self._script.icon_name)
        if hasattr(icon, "icon"):
            icon = icon.icon(color=QColor(icon_color))

        self.icon_widget = IconWidget(icon, icon_wrapper)
        self.icon_widget.setFixedSize(24, 24)
        icon_layout.addWidget(self.icon_widget)
        layout.addWidget(icon_wrapper)

    def _init_text_section(self, layout: QHBoxLayout) -> None:
        """Инициализация текстовой секции.

        Args:
            layout: Родительский макет.
        """
        text_layout = QVBoxLayout()
        text_layout.setContentsMargins(0, 4, 0, 0)
        text_layout.setSpacing(2)
        text_layout.setAlignment(Qt.AlignmentFlag.AlignTop)

        self.title_label = StrongBodyLabel(self._script.name, self)
        self.desc_label = CaptionLabel(self._script.description, self)
        self.desc_label.setWordWrap(True)

        text_layout.addWidget(self.title_label)
        text_layout.addWidget(self.desc_label)
        layout.addLayout(text_layout)

    def mouseReleaseEvent(self, event) -> None:
        """Событие клика по карточке.

        Args:
            event: Событие мыши.
        """
        super().mouseReleaseEvent(event)
        if event.button() == Qt.MouseButton.LeftButton:
            if self._is_available:
                logger.info("Клик по карточке скрипта: %s", self._script.name)
                self.scriptClicked.emit(self._script.name)
            else:
                logger.info(
                    "Клик по недоступному скрипту: %s. "
                    "Необходимы зависимости: %s",
                    self._script.name,
                    self._script.required_dependencies,
                )
                missing = self._dep_mgr.get_missing_deps(
                    self._script.required_dependencies
                )
                missing_names = ", ".join(d.display_name for d in missing)
                InfoBar.warning(
                    title="Компонент недоступен",
                    content=(
                        f"Необходимы зависимости: {missing_names}. "
                    ),
                    orient=Qt.Orientation.Horizontal,
                    isClosable=True,
                    position=InfoBarPosition.TOP,
                    duration=5000,
                    parent=self.window(),
                )


class HomePage(QWidget):
    """Главная страница приложения с обзором всех скриптов.

    Группирует скрипты по категориям в виде карточек.
    """

    scriptRequested = pyqtSignal(str)

    def __init__(
        self,
        scripts: List[AbstractScript],
        resolve_icon: Callable[[str], FluentIcon],
        parent: QWidget | None = None,
    ) -> None:
        """Инициализация домашней страницы.

        Args:
            scripts: Список всех доступных скриптов.
            resolve_icon: Функция для разрешения иконок.
            parent: Родительский виджет.
        """
        super().__init__(parent)
        self.setObjectName("homePage")
        self._scripts = scripts
        self._resolve_icon = resolve_icon

        self._init_ui()

    def _init_ui(self) -> None:
        """Инициализация интерфейса."""
        layout = QVBoxLayout(self)
        layout.setContentsMargins(0, 0, 0, 0)

        self.scroll_area = SmoothScrollArea(self)
        self.scroll_area.verticalScrollBar().setSingleStep(30)

        self.scroll_area.setWidgetResizable(True)
        self.scroll_area.setStyleSheet(
            "background: transparent; border: none;"
        )

        self.container = QWidget()
        self.container.setStyleSheet("background: transparent;")
        self.container_layout = QVBoxLayout(self.container)
        self.container_layout.setContentsMargins(36, 40, 36, 40)
        self.container_layout.setSpacing(32)

        self._setup_header()
        self._populate_categories()

        self.container_layout.addStretch(1)
        self.scroll_area.setWidget(self.container)
        layout.addWidget(self.scroll_area)

    def _setup_header(self) -> None:
        """Настройка заголовка страницы."""
        header_layout = QVBoxLayout()
        header_layout.setSpacing(4)
        from PyQt6.QtGui import QFont

        title = SubtitleLabel("K-Tools", self)
        title.setFont(QFont("Segoe UI", 24, QFont.Weight.Bold))
        desc = BodyLabel(
            "Ваш персональный набор инструментов для обработки медиа", self
        )

        header_layout.addWidget(title)
        header_layout.addWidget(desc)
        self.container_layout.addLayout(header_layout)

    def _populate_categories(self) -> None:
        """Заполнение категорий карточками скриптов."""
        categories: dict[str, List[AbstractScript]] = {}
        for script in self._scripts:
            cat = script.category
            if cat not in categories:
                categories[cat] = []
            categories[cat].append(script)

        # 1. Добавляем известные категории в заданном порядке
        for cat_name in CATEGORY_CONFIG:
            if cat_name in categories:
                self._add_category_section(cat_name, categories[cat_name])

        # 2. Добавляем все остальные категории
        for cat_name, scripts in categories.items():
            if cat_name not in CATEGORY_CONFIG:
                self._add_category_section(cat_name, scripts)

    def _add_category_section(
        self, name: str, scripts: List[AbstractScript]
    ) -> None:
        """Добавить секцию категории с карточками.

        Args:
            name: Название категории.
            scripts: Список скриптов в этой категории.
        """
        section_layout = QVBoxLayout()
        section_layout.setSpacing(16)

        # Заголовок категории
        from PyQt6.QtGui import QFont

        cat_label = StrongBodyLabel(name, self.container)
        cat_label.setFont(QFont("Segoe UI", 14, QFont.Weight.Bold))
        section_layout.addWidget(cat_label)

        # Сетка карточек
        flow_layout = FlowLayout()
        flow_layout.setContentsMargins(0, 0, 0, 0)
        flow_layout.setSpacing(12)

        for script in scripts:
            card = ScriptCard(script, self._resolve_icon, self.container)
            card.scriptClicked.connect(self.scriptRequested.emit)
            flow_layout.addWidget(card)

        section_layout.addLayout(flow_layout)
        self.container_layout.addLayout(section_layout)

    def refresh_availability(self) -> None:
        """Обновить доступность всех карточек скриптов."""
        for card in self.findChildren(ScriptCard):
            card.update_availability()

    def showEvent(self, event) -> None:
        """Событие отображения страницы.

        Args:
            event: Объект события.
        """
        super().showEvent(event)
        self.refresh_availability()
