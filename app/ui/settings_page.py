# -*- coding: utf-8 -*-
"""Страница настроек приложения."""

import logging
from PyQt6.QtCore import Qt
from PyQt6.QtWidgets import QWidget, QVBoxLayout, QHBoxLayout, QSpacerItem, QSizePolicy
from qfluentwidgets import (
    SettingCardGroup,
    CardWidget,
    SwitchButton,
    IconWidget,
    BodyLabel,
    CaptionLabel,
    FluentIcon,
    ScrollArea,
    ExpandLayout,
)

from app.core.settings_manager import SettingsManager

logger = logging.getLogger(__name__)


class SettingsPage(ScrollArea):
    """Страница настроек.

    Позволяет пользователю изменять глобальные параметры приложения,
    такие как перезапись существующих файлов.
    """

    def __init__(self, parent=None) -> None:
        """Инициализация страницы настроек."""
        super().__init__(parent=parent)
        self._settings_manager = SettingsManager()
        
        self._init_ui()
        logger.info("Страница настроек инициализирована")

    def _init_ui(self) -> None:
        """Настройка пользовательского интерфейса."""
        self.setObjectName("settingsPage")
        self.setWidgetResizable(True)
        self.viewport().setStyleSheet("background-color: transparent")
        self.setStyleSheet("background-color: transparent")

        # Основной контейнер
        self._scroll_widget = QWidget()
        self._layout = ExpandLayout(self._scroll_widget)
        self.setWidget(self._scroll_widget)

        # Группа "Общие"
        self._general_group = SettingCardGroup(
            self.tr("Общие"), self._scroll_widget
        )

        # Карточка настройки перезаписи на базе CardWidget (нативный hover)
        self._overwrite_card = CardWidget(self._general_group)
        self._overwrite_card.setCursor(Qt.CursorShape.PointingHandCursor)
        self._overwrite_card.setMinimumHeight(70)
        
        card_layout = QHBoxLayout(self._overwrite_card)
        card_layout.setContentsMargins(16, 16, 16, 16)
        card_layout.setSpacing(16)

        # Иконка
        icon = IconWidget(FluentIcon.FOLDER, self._overwrite_card)
        icon.setFixedSize(16, 16)
        card_layout.addWidget(icon)

        # Текстовый блок
        text_layout = QVBoxLayout()
        text_layout.setSpacing(2)
        
        title_label = BodyLabel(self.tr("Перезаписывать файлы"), self._overwrite_card)
        desc_label = CaptionLabel(
            self.tr("Если файл уже существует, он будет перезаписан без предупреждения"),
            self._overwrite_card
        )
        desc_label.setStyleSheet("color: rgba(255, 255, 255, 0.6)") # Приглушенный текст
        
        text_layout.addWidget(title_label)
        text_layout.addWidget(desc_label)
        card_layout.addLayout(text_layout)

        # Распорка
        card_layout.addStretch(1)

        # Переключатель
        self._switch_btn = SwitchButton(self._overwrite_card)
        self._switch_btn.setOnText("")
        self._switch_btn.setOffText("")
        self._switch_btn.setChecked(self._settings_manager.overwrite_existing)
        self._switch_btn.checkedChanged.connect(self._on_overwrite_changed)
        card_layout.addWidget(self._switch_btn)

        # Добавляем карточку в группу
        self._general_group.addSettingCard(self._overwrite_card)
        
        # Сборка интерфейса
        self._layout.setContentsMargins(36, 10, 36, 30)
        self._layout.setSpacing(20)
        self._layout.addWidget(self._general_group)

    def _on_overwrite_changed(self, is_checked: bool) -> None:
        """Обработка изменения состояния чекбокса перезаписи."""
        self._settings_manager.overwrite_existing = is_checked
        logger.info(
            "Глобальная настройка 'Перезаписывать файлы' изменена пользователем на: %s",
            "ВКЛ" if is_checked else "ВЫКЛ"
        )
