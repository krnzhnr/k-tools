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
    PushButton,
    MessageBox,
    LineEdit,
    ComboBox,
)

from app.core.settings_manager import SettingsManager
from app.core.version import get_app_version

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

        # 1. Карточка перезаписи
        self._overwrite_card = CardWidget(self._general_group)
        self._overwrite_card.setCursor(Qt.CursorShape.ArrowCursor)
        self._overwrite_card.setMinimumHeight(70)
        
        card_layout = QHBoxLayout(self._overwrite_card)
        card_layout.setContentsMargins(16, 16, 16, 16)
        card_layout.setSpacing(16)

        icon = IconWidget(FluentIcon.FOLDER, self._overwrite_card)
        icon.setFixedSize(16, 16)
        card_layout.addWidget(icon)

        text_layout = QVBoxLayout()
        text_layout.setSpacing(2)
        title_label = BodyLabel(self.tr("Перезаписывать файлы"), self._overwrite_card)
        desc_label = CaptionLabel(
            self.tr("Если файл уже существует, он будет перезаписан без предупреждения"),
            self._overwrite_card
        )
        desc_label.setStyleSheet("color: rgba(255, 255, 255, 0.6)")
        text_layout.addWidget(title_label)
        text_layout.addWidget(desc_label)
        card_layout.addLayout(text_layout)
        card_layout.addStretch(1)

        self._switch_btn = SwitchButton(self._overwrite_card)
        self._switch_btn.setOnText("")
        self._switch_btn.setOffText("")
        self._switch_btn.setChecked(self._settings_manager.overwrite_existing)
        self._switch_btn.checkedChanged.connect(self._on_overwrite_changed)
        card_layout.addWidget(self._switch_btn)

        self._general_group.addSettingCard(self._overwrite_card)

        # 2. Карточка автоматических подпапок
        self._auto_subfolder_card = CardWidget(self._general_group)
        self._auto_subfolder_card.setCursor(Qt.CursorShape.ArrowCursor)
        self._auto_subfolder_card.setMinimumHeight(70)
        
        auto_subfolder_layout = QHBoxLayout(self._auto_subfolder_card)
        auto_subfolder_layout.setContentsMargins(16, 16, 16, 16)
        auto_subfolder_layout.setSpacing(16)

        subfolder_icon = IconWidget(FluentIcon.FOLDER_ADD, self._auto_subfolder_card)
        subfolder_icon.setFixedSize(16, 16)
        auto_subfolder_layout.addWidget(subfolder_icon)

        subfolder_text_layout = QVBoxLayout()
        subfolder_text_layout.setSpacing(2)
        subfolder_title_label = BodyLabel(self.tr("Автоматическое создание подпапки рядом с исходником"), self._auto_subfolder_card)
        subfolder_desc_label = CaptionLabel(
            self.tr("Вкл - сохранять результаты в подпапку. Выкл - сохранять рядом с исходником"),
            self._auto_subfolder_card
        )
        subfolder_desc_label.setStyleSheet("color: rgba(255, 255, 255, 0.6)")
        subfolder_text_layout.addWidget(subfolder_title_label)
        subfolder_text_layout.addWidget(subfolder_desc_label)
        auto_subfolder_layout.addLayout(subfolder_text_layout)
        auto_subfolder_layout.addStretch(1)

        self._auto_subfolder_switch = SwitchButton(self._auto_subfolder_card)
        self._auto_subfolder_switch.setOnText("")
        self._auto_subfolder_switch.setOffText("")
        self._auto_subfolder_switch.setChecked(self._settings_manager.use_auto_subfolder)
        self._auto_subfolder_switch.checkedChanged.connect(self._on_auto_subfolder_changed)
        auto_subfolder_layout.addWidget(self._auto_subfolder_switch)

        self._general_group.addSettingCard(self._auto_subfolder_card)

        # 3. Карточка имени подпапки
        self._subfolder_name_card = CardWidget(self._general_group)
        self._subfolder_name_card.setMinimumHeight(70)
        
        name_layout = QHBoxLayout(self._subfolder_name_card)
        name_layout.setContentsMargins(16, 16, 16, 16)
        name_layout.setSpacing(16)

        name_icon = IconWidget(FluentIcon.EDIT, self._subfolder_name_card)
        name_icon.setFixedSize(16, 16)
        name_layout.addWidget(name_icon)

        name_text_layout = QVBoxLayout()
        name_text_layout.setSpacing(2)
        name_title_label = BodyLabel(self.tr("Имя подпапки"), self._subfolder_name_card)
        name_desc_label = CaptionLabel(
            self.tr("Название подпапки для сохранения"),
            self._subfolder_name_card
        )
        name_desc_label.setStyleSheet("color: rgba(255, 255, 255, 0.6)")
        name_text_layout.addWidget(name_title_label)
        name_text_layout.addWidget(name_desc_label)
        name_layout.addLayout(name_text_layout)
        name_layout.addStretch(1)

        self._subfolder_name_edit = LineEdit(self._subfolder_name_card)
        self._subfolder_name_edit.setText(self._settings_manager.default_output_subfolder)
        self._subfolder_name_edit.setFixedWidth(200)
        self._subfolder_name_edit.textChanged.connect(self._on_subfolder_name_changed)
        name_layout.addWidget(self._subfolder_name_edit)

        self._general_group.addSettingCard(self._subfolder_name_card)

        # 4. Карточка выбора темы
        self._theme_card = CardWidget(self._general_group)
        self._theme_card.setMinimumHeight(70)
        
        theme_layout = QHBoxLayout(self._theme_card)
        theme_layout.setContentsMargins(16, 16, 16, 16)
        theme_layout.setSpacing(16)

        theme_icon = IconWidget(FluentIcon.PALETTE, self._theme_card)
        theme_icon.setFixedSize(16, 16)
        theme_layout.addWidget(theme_icon)

        theme_text_layout = QVBoxLayout()
        theme_text_layout.setSpacing(2)
        theme_title_label = BodyLabel(self.tr("Тема приложения"), self._theme_card)
        theme_desc_label = CaptionLabel(
            self.tr("Выберите цветовую схему оформления (требуется перезапуск)"),
            self._theme_card
        )
        theme_desc_label.setStyleSheet("color: rgba(255, 255, 255, 0.6)")
        theme_text_layout.addWidget(theme_title_label)
        theme_text_layout.addWidget(theme_desc_label)
        theme_layout.addLayout(theme_text_layout)
        theme_layout.addStretch(1)

        self._theme_combo = ComboBox(self._theme_card)
        self._theme_combo.addItems([self.tr("Темная"), self.tr("Светлая"), self.tr("Системная")])
        
        # Установка текущего значения
        current_theme = self._settings_manager.theme
        theme_map = {"Dark": 0, "Light": 1, "System": 2}
        self._theme_combo.setCurrentIndex(theme_map.get(current_theme, 0))
        
        self._theme_combo.currentIndexChanged.connect(self._on_theme_changed)
        self._theme_combo.setFixedWidth(200)
        theme_layout.addWidget(self._theme_combo)

        self._general_group.addSettingCard(self._theme_card)

        # Группа "Обслуживание"
        self._maintenance_group = SettingCardGroup(
            self.tr("Обслуживание"), self._scroll_widget
        )

        self._reset_card = CardWidget(self._maintenance_group)
        self._reset_card.setMinimumHeight(70)
        reset_layout = QHBoxLayout(self._reset_card)
        reset_layout.setContentsMargins(16, 16, 16, 16)
        reset_layout.setSpacing(16)

        reset_icon = IconWidget(FluentIcon.DELETE, self._reset_card)
        reset_icon.setFixedSize(16, 16)
        reset_layout.addWidget(reset_icon)

        reset_text_layout = QVBoxLayout()
        reset_text_layout.setSpacing(2)
        reset_title = BodyLabel(self.tr("Сбросить все настройки"), self._reset_card)
        reset_desc = CaptionLabel(
            self.tr("Вернуть все параметры приложения и скриптов к значениям по умолчанию"),
            self._reset_card
        )
        reset_desc.setStyleSheet("color: rgba(255, 255, 255, 0.6)")
        reset_text_layout.addWidget(reset_title)
        reset_text_layout.addWidget(reset_desc)
        reset_layout.addLayout(reset_text_layout)
        reset_layout.addStretch(1)

        self._reset_btn = PushButton(self.tr("Сбросить"), self._reset_card)
        self._reset_btn.clicked.connect(self._show_reset_dialog)
        reset_layout.addWidget(self._reset_btn)

        self._maintenance_group.addSettingCard(self._reset_card)
        
        # Сборка интерфейса
        self._layout.setContentsMargins(36, 10, 36, 30)
        self._layout.setSpacing(20)
        self._layout.addWidget(self._general_group)
        self._layout.addWidget(self._maintenance_group)

        # Версия приложения в самом низу
        self._version_layout = QHBoxLayout()
        self._version_layout.setContentsMargins(0, 20, 0, 0)
        self._version_layout.addStretch(1)
        
        version_text = get_app_version()
        label_text = f"K-Tools {version_text}"
        if version_text == "Dev Mode":
            label_text = f"K-Tools ({version_text})"
            
        self._version_label = CaptionLabel(label_text, self._scroll_widget)
        self._version_label.setStyleSheet("color: rgba(255, 255, 255, 0.4)")
        self._version_layout.addWidget(self._version_label)
        self._version_layout.addStretch(1)
        
        # Обертываем в виджет, так как ExpandLayout не поддерживает addLayout
        self._version_container = QWidget(self._scroll_widget)
        self._version_container.setLayout(self._version_layout)
        self._layout.addWidget(self._version_container)

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

    def _on_theme_changed(self, index: int) -> None:
        """Обработка изменения темы."""
        theme_map = {0: "Dark", 1: "Light", 2: "System"}
        new_theme = theme_map.get(index, "Dark")
        
        if self._settings_manager.theme == new_theme:
            return
            
        self._settings_manager.theme = new_theme
        logger.info("Тема приложения в настройках изменена на: %s", new_theme)
        
        # Показ диалога перезапуска
        self._show_restart_dialog(
            self.tr("Смена темы"),
            self.tr("Для применения новой темы необходимо перезапустить приложение. Перезагрузить сейчас?")
        )

    def _show_reset_dialog(self) -> None:
        """Показать диалог подтверждения сброса."""
        title = self.tr("Сброс настроек")
        content = self.tr("Вы уверены, что хотите сбросить все настройки? Это действие нельзя отменить.")
        w = MessageBox(title, content, self.window())
        w.yesButton.setText(self.tr("Сбросить"))
        w.cancelButton.setText(self.tr("Отмена"))
        
        if w.exec():
            self._settings_manager.reset_all_settings()
            # Обновляем текущие виджеты на странице
            self._switch_btn.setChecked(self._settings_manager.overwrite_existing)
            self._auto_subfolder_switch.setChecked(self._settings_manager.use_auto_subfolder)
            self._subfolder_name_edit.setText(self._settings_manager.default_output_subfolder)
            
            # Сброс комбобокса темы
            self._theme_combo.setCurrentIndex(0)
            
            logger.info("Пользователь подтвердил сброс всех настроек")
            
            # Предлагаем перезапуск
            self._show_restart_dialog(
                self.tr("Перезапуск"),
                self.tr("Настройки сброшены. Рекомендуется перезапустить приложение для полного применения изменений. Перезагрузить сейчас?")
            )

    def _show_restart_dialog(self, title: str, content: str) -> None:
        """Показать диалог предложения перезапуска."""
        rw = MessageBox(title, content, self.window())
        rw.yesButton.setText(self.tr("Перезагрузить"))
        rw.cancelButton.setText(self.tr("Позже"))
        
        if rw.exec():
            from app.core.lifecycle import restart_current_app
            restart_current_app()
