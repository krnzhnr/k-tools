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

        # Карточка настройки использования подпапок
        self._auto_subfolder_card = CardWidget(self._general_group)
        self._auto_subfolder_card.setCursor(Qt.CursorShape.PointingHandCursor)
        self._auto_subfolder_card.setMinimumHeight(70)
        
        auto_subfolder_layout = QHBoxLayout(self._auto_subfolder_card)
        auto_subfolder_layout.setContentsMargins(16, 16, 16, 16)
        auto_subfolder_layout.setSpacing(16)

        # Иконка
        subfolder_icon = IconWidget(FluentIcon.FOLDER_ADD, self._auto_subfolder_card)
        subfolder_icon.setFixedSize(16, 16)
        auto_subfolder_layout.addWidget(subfolder_icon)

        # Текстовый блок
        subfolder_text_layout = QVBoxLayout()
        subfolder_text_layout.setSpacing(2)
        
        subfolder_title_label = BodyLabel(self.tr("Автоматическая подпапка"), self._auto_subfolder_card)
        subfolder_desc_label = CaptionLabel(
            self.tr("Сохранять результаты в отдельную подпапку рядом с исходным файлом"),
            self._auto_subfolder_card
        )
        subfolder_desc_label.setStyleSheet("color: rgba(255, 255, 255, 0.6)")
        
        subfolder_text_layout.addWidget(subfolder_title_label)
        subfolder_text_layout.addWidget(subfolder_desc_label)
        auto_subfolder_layout.addLayout(subfolder_text_layout)

        # Распорка
        auto_subfolder_layout.addStretch(1)

        # Переключатель
        self._auto_subfolder_switch = SwitchButton(self._auto_subfolder_card)
        self._auto_subfolder_switch.setChecked(self._settings_manager.use_auto_subfolder)
        self._auto_subfolder_switch.checkedChanged.connect(self._on_auto_subfolder_changed)
        auto_subfolder_layout.addWidget(self._auto_subfolder_switch)

        self._general_group.addSettingCard(self._auto_subfolder_card)

        # Карточка для имени подпапки
        self._subfolder_name_card = CardWidget(self._general_group)
        self._subfolder_name_card.setMinimumHeight(70)
        
        name_layout = QHBoxLayout(self._subfolder_name_card)
        name_layout.setContentsMargins(16, 16, 16, 16)
        name_layout.setSpacing(16)

        # Иконка
        name_icon = IconWidget(FluentIcon.EDIT, self._subfolder_name_card)
        name_icon.setFixedSize(16, 16)
        name_layout.addWidget(name_icon)

        # Текстовый блок
        name_text_layout = QVBoxLayout()
        name_text_layout.setSpacing(2)
        
        name_title_label = BodyLabel(self.tr("Имя подпапки"), self._subfolder_name_card)
        name_desc_label = CaptionLabel(
            self.tr("Название папки, которая будет создана при автоматическом сохранении"),
            self._subfolder_name_card
        )
        name_desc_label.setStyleSheet("color: rgba(255, 255, 255, 0.6)")
        
        name_text_layout.addWidget(name_title_label)
        name_text_layout.addWidget(name_desc_label)
        name_layout.addLayout(name_text_layout)

        # Распорка
        name_layout.addStretch(1)

        # Поле ввода
        from qfluentwidgets import LineEdit
        self._subfolder_name_edit = LineEdit(self._subfolder_name_card)
        self._subfolder_name_edit.setText(self._settings_manager.default_output_subfolder)
        self._subfolder_name_edit.setFixedWidth(200)
        self._subfolder_name_edit.textChanged.connect(self._on_subfolder_name_changed)
        name_layout.addWidget(self._subfolder_name_edit)

        self._general_group.addSettingCard(self._subfolder_name_card)
        
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

    def _on_auto_subfolder_changed(self, is_checked: bool) -> None:
        """Обработка изменения состояния подпапок."""
        self._settings_manager.use_auto_subfolder = is_checked
        logger.info(
            "Глобальная настройка 'Автоматическая подпапка' изменена на: %s",
            "ВКЛ" if is_checked else "ВЫКЛ"
        )

    def _on_subfolder_name_changed(self, text: str) -> None:
        """Обработка изменения имени подпапки."""
        if text.strip():
            self._settings_manager.default_output_subfolder = text.strip()
            logger.info("Имя автоматической подпапки изменено на: '%s'", text.strip())
