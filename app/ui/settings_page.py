# -*- coding: utf-8 -*-
"""Страница настроек приложения."""

import logging
from PyQt6.QtCore import Qt
from PyQt6.QtWidgets import QWidget, QVBoxLayout, QHBoxLayout
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
    SpinBox,
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

        self._scroll_widget = QWidget()
        self._layout = ExpandLayout(self._scroll_widget)
        self.setWidget(self._scroll_widget)

        self._init_general_group()
        self._init_maintenance_group()

        self._layout.setContentsMargins(36, 10, 36, 30)
        self._layout.setSpacing(20)
        self._layout.addWidget(self._general_group)
        self._layout.addWidget(self._maintenance_group)

        self._add_version_label()

    def _init_general_group(self) -> None:
        """Инициализация группы общих настроек."""
        self._general_group = SettingCardGroup(
            self.tr("Общие"), self._scroll_widget
        )
        self._overwrite_card = self._create_overwrite_card()
        self._general_group.addSettingCard(self._overwrite_card)
        self._auto_subfolder_card = self._create_auto_subfolder_card()
        self._general_group.addSettingCard(self._auto_subfolder_card)
        self._subfolder_name_card = self._create_subfolder_name_card()
        self._general_group.addSettingCard(self._subfolder_name_card)
        self._theme_card = self._create_theme_card()
        self._general_group.addSettingCard(self._theme_card)
        self._parallel_card = self._create_parallel_card()
        self._general_group.addSettingCard(self._parallel_card)

    def _init_maintenance_group(self) -> None:
        """Инициализация группы обслуживания."""
        self._maintenance_group = SettingCardGroup(
            self.tr("Обслуживание"), self._scroll_widget
        )
        self._maintenance_group.addSettingCard(self._create_reset_card())

    def _create_overwrite_card(self) -> CardWidget:
        """Карточка настройки перезаписи файлов."""
        card = CardWidget(self._general_group)
        card.setCursor(Qt.CursorShape.ArrowCursor)
        card.setMinimumHeight(70)
        layout = QHBoxLayout(card)
        layout.setContentsMargins(16, 16, 16, 16)
        layout.setSpacing(16)

        icon = IconWidget(FluentIcon.FOLDER, card)
        icon.setFixedSize(16, 16)
        layout.addWidget(icon)

        text_layout = QVBoxLayout()
        text_layout.setSpacing(2)
        title = BodyLabel(self.tr("Перезаписывать файлы"), card)
        desc = CaptionLabel(
            self.tr(
                "Если файл уже существует, он будет перезаписан без предупреждения"  # noqa: E501
            ),
            card,
        )
        desc.setStyleSheet("color: rgba(255, 255, 255, 0.6)")
        text_layout.addWidget(title)
        text_layout.addWidget(desc)
        layout.addLayout(text_layout)
        layout.addStretch(1)

        self._switch_btn = SwitchButton(card)
        self._switch_btn.setOnText("")
        self._switch_btn.setOffText("")
        self._switch_btn.setChecked(self._settings_manager.overwrite_existing)
        self._switch_btn.checkedChanged.connect(self._on_overwrite_changed)
        layout.addWidget(self._switch_btn)
        return card

    def _create_auto_subfolder_card(self) -> CardWidget:
        """Карточка автоматического создания подпапок."""
        card = CardWidget(self._general_group)
        card.setCursor(Qt.CursorShape.ArrowCursor)
        card.setMinimumHeight(70)
        layout = QHBoxLayout(card)
        layout.setContentsMargins(16, 16, 16, 16)
        layout.setSpacing(16)

        icon = IconWidget(FluentIcon.FOLDER_ADD, card)
        icon.setFixedSize(16, 16)
        layout.addWidget(icon)

        text_layout = QVBoxLayout()
        text_layout.setSpacing(2)
        title = BodyLabel(
            self.tr("Автоматическое создание подпапки рядом с исходником"),
            card,
        )
        desc = CaptionLabel(
            self.tr(
                "Вкл - сохранять результаты в подпапку. Выкл - сохранять рядом с исходником"  # noqa: E501
            ),
            card,
        )
        desc.setStyleSheet("color: rgba(255, 255, 255, 0.6)")
        text_layout.addWidget(title)
        text_layout.addWidget(desc)
        layout.addLayout(text_layout)
        layout.addStretch(1)

        self._auto_subfolder_switch = SwitchButton(card)
        self._auto_subfolder_switch.setOnText("")
        self._auto_subfolder_switch.setOffText("")
        self._auto_subfolder_switch.setChecked(
            self._settings_manager.use_auto_subfolder
        )
        self._auto_subfolder_switch.checkedChanged.connect(
            self._on_auto_subfolder_changed
        )
        layout.addWidget(self._auto_subfolder_switch)
        return card

    def _create_subfolder_name_card(self) -> CardWidget:
        """Карточка имени автоматической подпапки."""
        card = CardWidget(self._general_group)
        card.setMinimumHeight(70)
        layout = QHBoxLayout(card)
        layout.setContentsMargins(16, 16, 16, 16)
        layout.setSpacing(16)

        icon = IconWidget(FluentIcon.EDIT, card)
        icon.setFixedSize(16, 16)
        layout.addWidget(icon)

        text_layout = QVBoxLayout()
        text_layout.setSpacing(2)
        title = BodyLabel(self.tr("Имя подпапки"), card)
        desc = CaptionLabel(self.tr("Название подпапки для сохранения"), card)
        desc.setStyleSheet("color: rgba(255, 255, 255, 0.6)")
        text_layout.addWidget(title)
        text_layout.addWidget(desc)
        layout.addLayout(text_layout)
        layout.addStretch(1)

        self._subfolder_name_edit = LineEdit(card)
        self._subfolder_name_edit.setText(
            self._settings_manager.default_output_subfolder
        )
        self._subfolder_name_edit.setFixedWidth(200)
        self._subfolder_name_edit.textChanged.connect(
            self._on_subfolder_name_changed
        )
        layout.addWidget(self._subfolder_name_edit)
        return card

    def _create_theme_card(self) -> CardWidget:
        """Карточка выбора темы."""
        card = CardWidget(self._general_group)
        card.setMinimumHeight(70)
        layout = QHBoxLayout(card)
        layout.setContentsMargins(16, 16, 16, 16)
        layout.setSpacing(16)

        icon = IconWidget(FluentIcon.PALETTE, card)
        icon.setFixedSize(16, 16)
        layout.addWidget(icon)

        text_layout = QVBoxLayout()
        text_layout.setSpacing(2)
        title = BodyLabel(self.tr("Тема приложения"), card)
        desc = CaptionLabel(
            self.tr("Выберите цветовое оформление (ожидается перезапуск)"),
            card,
        )
        desc.setStyleSheet("color: rgba(255, 255, 255, 0.6)")
        text_layout.addWidget(title)
        text_layout.addWidget(desc)
        layout.addLayout(text_layout)
        layout.addStretch(1)

        self._theme_combo = ComboBox(card)
        self._theme_combo.addItems(
            [self.tr("Темная"), self.tr("Светлая"), self.tr("Системная")]
        )
        theme_map = {"Dark": 0, "Light": 1, "System": 2}
        self._theme_combo.setCurrentIndex(
            theme_map.get(self._settings_manager.theme, 0)
        )
        self._theme_combo.currentIndexChanged.connect(self._on_theme_changed)
        self._theme_combo.setFixedWidth(200)
        layout.addWidget(self._theme_combo)
        return card

    def _create_parallel_card(self) -> CardWidget:
        """Карточка задания количества параллельных потоков."""
        card = CardWidget(self._general_group)
        card.setMinimumHeight(70)
        layout = QHBoxLayout(card)
        layout.setContentsMargins(16, 16, 16, 16)
        layout.setSpacing(16)

        icon = IconWidget(FluentIcon.TILES, card)
        icon.setFixedSize(16, 16)
        layout.addWidget(icon)

        text_layout = QVBoxLayout()
        text_layout.setSpacing(2)
        title = BodyLabel(self.tr("Максимум параллельных задач"), card)
        desc = CaptionLabel(
            self.tr("Количество одновременно обрабатываемых файлов (1-16)"),
            card,
        )
        desc.setStyleSheet("color: rgba(255, 255, 255, 0.6)")
        text_layout.addWidget(title)
        text_layout.addWidget(desc)
        layout.addLayout(text_layout)
        layout.addStretch(1)

        self._parallel_spin = SpinBox(card)
        self._parallel_spin.setRange(1, 16)
        self._parallel_spin.setValue(self._settings_manager.max_parallel_tasks)
        self._parallel_spin.valueChanged.connect(
            self._on_parallel_tasks_changed
        )
        self._parallel_spin.setFixedWidth(200)
        layout.addWidget(self._parallel_spin)
        return card

    def _create_reset_card(self) -> CardWidget:
        """Карточка сброса настроек."""
        card = CardWidget(self._maintenance_group)
        card.setMinimumHeight(70)
        layout = QHBoxLayout(card)
        layout.setContentsMargins(16, 16, 16, 16)
        layout.setSpacing(16)

        icon = IconWidget(FluentIcon.DELETE, card)
        icon.setFixedSize(16, 16)
        layout.addWidget(icon)

        text_layout = QVBoxLayout()
        text_layout.setSpacing(2)
        title = BodyLabel(self.tr("Сбросить все настройки"), card)
        desc = CaptionLabel(
            self.tr(
                "Вернуть все параметры приложения к значениям по умолчанию"
            ),
            card,
        )
        desc.setStyleSheet("color: rgba(255, 255, 255, 0.6)")
        text_layout.addWidget(title)
        text_layout.addWidget(desc)
        layout.addLayout(text_layout)
        layout.addStretch(1)

        self._reset_btn = PushButton(self.tr("Сбросить"), card)
        self._reset_btn.clicked.connect(self._show_reset_dialog)
        layout.addWidget(self._reset_btn)
        return card

    def _add_version_label(self) -> None:
        """Добавление лейбла с версией приложения."""
        self._version_layout = QHBoxLayout()
        self._version_layout.setContentsMargins(0, 20, 0, 0)
        self._version_layout.addStretch(1)

        v_text = get_app_version()
        label_text = (
            f"K-Tools {v_text}"
            if v_text != "Dev Mode"
            else f"K-Tools ({v_text})"
        )
        self._version_label = CaptionLabel(label_text, self._scroll_widget)
        self._version_label.setStyleSheet("color: rgba(255, 255, 255, 0.4)")
        self._version_layout.addWidget(self._version_label)
        self._version_layout.addStretch(1)

        self._version_container = QWidget(self._scroll_widget)
        self._version_container.setLayout(self._version_layout)
        self._layout.addWidget(self._version_container)

    def _on_overwrite_changed(self, is_checked: bool) -> None:
        """Обработка изменения состояния чекбокса перезаписи."""
        self._settings_manager.overwrite_existing = is_checked
        logger.info(
            "Глобальная настройка 'Перезаписывать файлы' изменена пользователем на: %s",  # noqa: E501
            "ВКЛ" if is_checked else "ВЫКЛ",
        )

    def _on_auto_subfolder_changed(self, is_checked: bool) -> None:
        """Обработка изменения состояния подпапок."""
        self._settings_manager.use_auto_subfolder = is_checked
        logger.info(
            "Глобальная настройка 'Автоматическая подпапка' изменена на: %s",
            "ВКЛ" if is_checked else "ВЫКЛ",
        )

    def _on_subfolder_name_changed(self, text: str) -> None:
        """Обработка изменения имени подпапки."""
        if text.strip():
            self._settings_manager.default_output_subfolder = text.strip()
            logger.info(
                "Имя автоматической подпапки изменено на: '%s'", text.strip()
            )

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
            self.tr(
                "Для применения новой темы необходимо перезапустить приложение. Перезагрузить сейчас?"  # noqa: E501
            ),
        )

    def _on_parallel_tasks_changed(self, value: int) -> None:
        """Обработка изменения количества параллельных задач."""
        self._settings_manager.max_parallel_tasks = value
        logger.info(
            "Настройка 'Максимум параллельных задач' изменена на: %d", value
        )

    def _show_reset_dialog(self) -> None:
        """Показать диалог подтверждения сброса."""
        title = self.tr("Сброс настроек")
        content = self.tr(
            "Вы уверены, что хотите сбросить все настройки? Это действие нельзя отменить."  # noqa: E501
        )
        w = MessageBox(title, content, self.window())
        w.yesButton.setText(self.tr("Сбросить"))
        w.cancelButton.setText(self.tr("Отмена"))

        if w.exec():
            self._settings_manager.reset_all_settings()
            # Обновляем текущие виджеты на странице
            self._switch_btn.setChecked(
                self._settings_manager.overwrite_existing
            )
            self._auto_subfolder_switch.setChecked(
                self._settings_manager.use_auto_subfolder
            )
            self._subfolder_name_edit.setText(
                self._settings_manager.default_output_subfolder
            )
            self._parallel_spin.setValue(
                self._settings_manager.max_parallel_tasks
            )

            # Сброс комбобокса темы
            self._theme_combo.setCurrentIndex(0)

            logger.info("Пользователь подтвердил сброс всех настроек")

            # Предлагаем перезапуск
            self._show_restart_dialog(
                self.tr("Перезапуск"),
                self.tr(
                    "Настройки сброшены. Рекомендуется перезапустить приложение для полного применения изменений. Перезагрузить сейчас?"  # noqa: E501
                ),
            )

    def _show_restart_dialog(self, title: str, content: str) -> None:
        """Показать диалог предложения перезапуска."""
        rw = MessageBox(title, content, self.window())
        rw.yesButton.setText(self.tr("Перезагрузить"))
        rw.cancelButton.setText(self.tr("Позже"))

        if rw.exec():
            from app.core.lifecycle import restart_current_app

            restart_current_app()
