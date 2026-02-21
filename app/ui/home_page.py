# -*- coding: utf-8 -*-
"""Домашняя страница с быстрым доступом к скриптам."""

import logging
from typing import List, Callable

from PyQt6.QtCore import Qt, pyqtSignal
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
    SubtitleLabel
)

from app.core.abstract_script import AbstractScript

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
        parent: QWidget = None
    ) -> None:
        """Инициализация карточки.

        Args:
            script: Объект скрипта.
            resolve_icon: Функция для получения объекта FluentIcon по имени.
            parent: Родительский виджет.
        """
        super().__init__(parent)
        self._script = script
        self.setFixedSize(330, 100)
        self.setCursor(Qt.CursorShape.PointingHandCursor)

        layout = QHBoxLayout(self)
        layout.setContentsMargins(16, 12, 16, 12)
        layout.setSpacing(12)

        # Цветовая схема для категорий (мягкие приглушенные цвета)
        cat_colors = {
            "Видео": ("rgba(27, 157, 227, 0.2)", "#1B9DE3"),    # Голубой
            "Аудио": ("rgba(40, 202, 198, 0.2)", "#28CAC6"),    # Бирюзовый
            "Муксинг": ("rgba(235, 110, 77, 0.2)", "#EB6E4D"),  # Терракотовый
        }
        category = script.category.strip()
        bg_color, icon_color = cat_colors.get(category, ("rgba(255, 255, 255, 0.1)", "#FFFFFF"))

        # Контейнер для иконки
        icon_wrapper = QFrame(self)
        icon_wrapper.setFixedSize(40, 40)
        icon_wrapper.setStyleSheet(
            f"background: {bg_color}; border-radius: 8px;"
        )
        icon_layout = QVBoxLayout(icon_wrapper)
        icon_layout.setContentsMargins(8, 8, 8, 8)

        icon = resolve_icon(script.icon_name)
        # Если это FluentIcon, устанавливаем цвет через штатный метод .icon()
        if hasattr(icon, "icon"):
            from PyQt6.QtGui import QColor
            icon = icon.icon(color=QColor(icon_color))

        self.icon_widget = IconWidget(icon, icon_wrapper)
        self.icon_widget.setFixedSize(24, 24)
        icon_layout.addWidget(self.icon_widget)
        layout.addWidget(icon_wrapper)

        # Текстовая часть
        text_layout = QVBoxLayout()
        text_layout.setContentsMargins(0, 4, 0, 0)
        text_layout.setSpacing(2)
        text_layout.setAlignment(Qt.AlignmentFlag.AlignTop)

        self.title_label = StrongBodyLabel(script.name, self)
        self.desc_label = CaptionLabel(script.description, self)
        self.desc_label.setWordWrap(True)

        text_layout.addWidget(self.title_label)
        text_layout.addWidget(self.desc_label)
        layout.addLayout(text_layout)
        layout.addStretch(1)

    def mouseReleaseEvent(self, event) -> None:
        """Событие клика по карточке."""
        super().mouseReleaseEvent(event)
        if event.button() == Qt.MouseButton.LeftButton:
            logger.info("Клик по карточке скрипта: %s", self._script.name)
            self.scriptClicked.emit(self._script.name)


class HomePage(QWidget):
    """Главная страница приложения с обзором всех скриптов.

    Группирует скрипты по категориям в виде карточек.
    """

    scriptRequested = pyqtSignal(str)

    def __init__(
        self,
        scripts: List[AbstractScript],
        resolve_icon: Callable[[str], FluentIcon],
        parent: QWidget = None
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

        # Область прокрутки
        self.scroll_area = SmoothScrollArea(self)
        self.scroll_area.setWidgetResizable(True)
        self.scroll_area.setStyleSheet("background: transparent; border: none;")

        self.container = QWidget()
        self.container.setStyleSheet("background: transparent;")
        self.container_layout = QVBoxLayout(self.container)
        self.container_layout.setContentsMargins(36, 40, 36, 40)
        self.container_layout.setSpacing(32)

        # Заголовок страницы
        header_layout = QVBoxLayout()
        header_layout.setSpacing(4)
        
        from PyQt6.QtGui import QFont
        title = SubtitleLabel("K-Tools", self)
        title_font = QFont("Segoe UI", 24, QFont.Weight.Bold)
        title.setFont(title_font)
        
        desc = BodyLabel("Ваш персональный набор инструментов для обработки медиа", self)
        header_layout.addWidget(title)
        header_layout.addWidget(desc)
        self.container_layout.addLayout(header_layout)

        # Группировка скриптов по категориям
        categories = {}
        for script in self._scripts:
            cat = script.category
            if cat not in categories:
                categories[cat] = []
            categories[cat].append(script)

        # Порядок категорий как в MainWindow
        ordered_cats = ["Видео", "Аудио", "Муксинг"]
        
        for cat_name in ordered_cats:
            if cat_name not in categories:
                continue
            
            self._add_category_section(cat_name, categories[cat_name])

        self.container_layout.addStretch(1)
        self.scroll_area.setWidget(self.container)
        layout.addWidget(self.scroll_area)

    def _add_category_section(self, name: str, scripts: List[AbstractScript]) -> None:
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
