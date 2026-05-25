# -*- coding: utf-8 -*-
"""Страница настроек приложения."""

import logging
from PyQt6.QtCore import Qt, pyqtSignal
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
    StateToolTip,
)

import os
from typing import Any
from PyQt6.QtCore import QDateTime
from app.core.settings_manager import SettingsManager
from app.core.version import get_app_version

logger = logging.getLogger(__name__)


class SettingsPage(ScrollArea):
    """Страница настроек.

    Позволяет пользователю изменять глобальные параметры приложения,
    такие как перезапись существующих файлов.
    """

    showLogsChanged = pyqtSignal(bool)

    def __init__(self, parent=None) -> None:
        """Инициализация страницы настроек."""
        super().__init__(parent=parent)
        self._settings_manager = SettingsManager()
        self._tip: Any = None
        self._download_tip: Any = None

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
        self._init_updates_group()
        self._init_maintenance_group()

        self._layout.setContentsMargins(36, 10, 36, 30)
        self._layout.setSpacing(20)
        self._layout.addWidget(self._general_group)
        self._layout.addWidget(self._updates_group)
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
        self._clear_list_card = self._create_clear_list_card()
        self._general_group.addSettingCard(self._clear_list_card)
        self._logs_card = self._create_logs_card()
        self._general_group.addSettingCard(self._logs_card)

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
                "Если файл уже существует, он будет перезаписан без "
                "предупреждения"
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
                "Вкл - сохранять результаты в подпапку. Выкл - сохранять "
                "рядом с исходником"
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
            self.tr(
                "Выберите цветовое оформление (может потребоваться "
                "перезапуск)"
            ),
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
            self.tr(
                "Количество одновременно обрабатываемых файлов (1-16)"
            ),
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

    def _create_clear_list_card(self) -> CardWidget:
        """Карточка настройки автоочистки списка файлов."""
        card = CardWidget(self._general_group)
        card.setMinimumHeight(70)
        layout = QHBoxLayout(card)
        layout.setContentsMargins(16, 16, 16, 16)
        layout.setSpacing(16)

        icon = IconWidget(FluentIcon.BROOM, card)
        icon.setFixedSize(16, 16)
        layout.addWidget(icon)

        text_layout = QVBoxLayout()
        text_layout.setSpacing(2)
        title = BodyLabel(
            self.tr("Очищать список при добавлении новых файлов"), card
        )
        desc = CaptionLabel(
            self.tr(
                "Если в списке уже есть файлы, они будут заменены на новые "
                "при добавлении"
            ),
            card,
        )
        desc.setStyleSheet("color: rgba(255, 255, 255, 0.6)")
        text_layout.addWidget(title)
        text_layout.addWidget(desc)
        layout.addLayout(text_layout)
        layout.addStretch(1)

        self._clear_list_switch = SwitchButton(card)
        self._clear_list_switch.setOnText("")
        self._clear_list_switch.setOffText("")
        self._clear_list_switch.setChecked(
            self._settings_manager.clear_list_on_add
        )
        self._clear_list_switch.checkedChanged.connect(
            self._on_clear_list_changed
        )
        layout.addWidget(self._clear_list_switch)
        return card

    def _create_logs_card(self) -> CardWidget:
        """Карточка настройки отображения вкладок логов."""
        card = CardWidget(self._general_group)
        card.setMinimumHeight(70)
        layout = QHBoxLayout(card)
        layout.setContentsMargins(16, 16, 16, 16)
        layout.setSpacing(16)

        icon = IconWidget(FluentIcon.COMMAND_PROMPT, card)
        icon.setFixedSize(16, 16)
        layout.addWidget(icon)

        text_layout = QVBoxLayout()
        text_layout.setSpacing(2)
        title = BodyLabel(self.tr("Показывать вкладку логов"), card)
        desc = CaptionLabel(
            self.tr(
                "Отображает лог работы программы в реальном времени "
                "в нижнем меню навигации"
            ),
            card,
        )
        desc.setStyleSheet("color: rgba(255, 255, 255, 0.6)")
        text_layout.addWidget(title)
        text_layout.addWidget(desc)
        layout.addLayout(text_layout)
        layout.addStretch(1)

        self._show_logs_switch = SwitchButton(card)
        self._show_logs_switch.setOnText("")
        self._show_logs_switch.setOffText("")
        self._show_logs_switch.setChecked(
            self._settings_manager.show_logs_tab
        )
        self._show_logs_switch.checkedChanged.connect(
            self._on_show_logs_changed
        )
        layout.addWidget(self._show_logs_switch)
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

    def _init_updates_group(self) -> None:
        """Инициализация группы настроек обновлений."""
        self._updates_group = SettingCardGroup(
            self.tr("Обновления"), self._scroll_widget
        )

        # 1. Автоматическая проверка
        self._auto_updates_card = CardWidget(self._updates_group)
        self._auto_updates_card.setMinimumHeight(70)
        auto_layout = QHBoxLayout(self._auto_updates_card)
        auto_layout.setContentsMargins(16, 16, 16, 16)
        auto_layout.setSpacing(16)

        auto_icon = IconWidget(FluentIcon.SYNC, self._auto_updates_card)
        auto_icon.setFixedSize(16, 16)
        auto_layout.addWidget(auto_icon)

        auto_text_layout = QVBoxLayout()
        auto_text_layout.setSpacing(2)
        auto_title = BodyLabel(
            self.tr("Проверять обновления автоматически"),
            self._auto_updates_card,
        )
        auto_desc = CaptionLabel(
            self.tr("Искать новые версии при запуске приложения"),
            self._auto_updates_card,
        )
        auto_desc.setStyleSheet("color: rgba(255, 255, 255, 0.6)")
        auto_text_layout.addWidget(auto_title)
        auto_text_layout.addWidget(auto_desc)
        auto_layout.addLayout(auto_text_layout)
        auto_layout.addStretch(1)

        self._auto_updates_switch = SwitchButton(self._auto_updates_card)
        self._auto_updates_switch.setOnText("")
        self._auto_updates_switch.setOffText("")
        self._auto_updates_switch.setChecked(
            self._settings_manager.auto_check_updates
        )
        self._auto_updates_switch.checkedChanged.connect(
            self._on_auto_updates_changed
        )
        auto_layout.addWidget(self._auto_updates_switch)
        self._updates_group.addSettingCard(self._auto_updates_card)

        # 2. Включать пре-релизы
        self._pre_releases_card = CardWidget(self._updates_group)
        self._pre_releases_card.setMinimumHeight(70)
        pre_layout = QHBoxLayout(self._pre_releases_card)
        pre_layout.setContentsMargins(16, 16, 16, 16)
        pre_layout.setSpacing(16)

        pre_icon = IconWidget(FluentIcon.FEEDBACK, self._pre_releases_card)
        pre_icon.setFixedSize(16, 16)
        pre_layout.addWidget(pre_icon)

        pre_text_layout = QVBoxLayout()
        pre_text_layout.setSpacing(2)
        pre_title = BodyLabel(
            self.tr("Участвовать в предварительном тестировании"),
            self._pre_releases_card,
        )
        pre_desc = CaptionLabel(
            self.tr("Включать пре-релизы (бета-версии) в проверку обновлений"),
            self._pre_releases_card,
        )
        pre_desc.setStyleSheet("color: rgba(255, 255, 255, 0.6)")
        pre_text_layout.addWidget(pre_title)
        pre_text_layout.addWidget(pre_desc)
        pre_layout.addLayout(pre_text_layout)
        pre_layout.addStretch(1)

        self._pre_releases_switch = SwitchButton(self._pre_releases_card)
        self._pre_releases_switch.setOnText("")
        self._pre_releases_switch.setOffText("")
        self._pre_releases_switch.setChecked(
            self._settings_manager.include_pre_releases
        )
        self._pre_releases_switch.checkedChanged.connect(
            self._on_pre_releases_changed
        )
        pre_layout.addWidget(self._pre_releases_switch)
        self._updates_group.addSettingCard(self._pre_releases_card)

        # 3. Ручная проверка
        self._check_now_card = CardWidget(self._updates_group)
        self._check_now_card.setMinimumHeight(70)
        check_layout = QHBoxLayout(self._check_now_card)
        check_layout.setContentsMargins(16, 16, 16, 16)
        check_layout.setSpacing(16)

        check_icon = IconWidget(FluentIcon.SEARCH, self._check_now_card)
        check_icon.setFixedSize(16, 16)
        check_layout.addWidget(check_icon)

        check_text_layout = QVBoxLayout()
        check_text_layout.setSpacing(2)
        check_title = BodyLabel(
            self.tr("Проверить наличие обновлений"), self._check_now_card
        )
        last_t = self._settings_manager.last_check_time
        last_check_str = (
            f"Последняя проверка: {last_t}"
            if last_t
            else "Проверка ещё не проводилась"
        )
        self._last_check_label = CaptionLabel(
            self.tr(last_check_str), self._check_now_card
        )
        self._last_check_label.setStyleSheet("color: rgba(255, 255, 255, 0.6)")
        check_text_layout.addWidget(check_title)
        check_text_layout.addWidget(self._last_check_label)
        check_layout.addLayout(check_text_layout)
        check_layout.addStretch(1)

        self._check_now_btn = PushButton(
            self.tr("Проверить сейчас"), self._check_now_card
        )
        self._check_now_btn.clicked.connect(self._on_check_now_clicked)
        check_layout.addWidget(self._check_now_btn)
        self._updates_group.addSettingCard(self._check_now_card)

    def _on_auto_updates_changed(self, is_checked: bool) -> None:
        """Обработка изменения автопроверки."""
        self._settings_manager.auto_check_updates = is_checked

    def _on_pre_releases_changed(self, is_checked: bool) -> None:
        """Обработка изменения включения пре-релизов."""
        self._settings_manager.include_pre_releases = is_checked

    def _on_check_now_clicked(self) -> None:
        """Ручной запуск проверки обновлений."""
        from app.core.update_worker import UpdateCheckerWorker

        self._check_now_btn.setDisabled(True)
        self._tip = StateToolTip(
            self.tr("Проверка обновлений"),
            self.tr("Пожалуйста, подождите..."),
            self.window(),
        )
        self._tip.move(self._tip.getSuitablePos())
        self._tip.show()

        now_str = QDateTime.currentDateTime().toString("dd.MM.yyyy hh:mm:ss")
        self._settings_manager.last_check_time = now_str
        self._last_check_label.setText(f"Последняя проверка: {now_str}")

        self._checker = UpdateCheckerWorker(
            self._settings_manager.include_pre_releases, self
        )
        self._checker.checkFinished.connect(self._on_check_finished)
        self._checker.checkError.connect(self._on_check_error)
        self._checker.finished.connect(self._checker.deleteLater)
        self._checker.start()

    def _on_check_finished(
        self, available: bool, version: str, changelog: str, download_url: str
    ) -> None:
        """Обработка успешного завершения проверки."""
        self._check_now_btn.setEnabled(True)
        if hasattr(self, "_tip") and self._tip:
            self._tip.setContent(self.tr("Проверка завершена"))
            self._tip.setState(True)
            self._checker_tip_timer = self.startTimer(1000)

        if available:
            self._show_update_dialog(version, changelog, download_url)
        else:
            w = MessageBox(
                self.tr("Обновлений не найдено"),
                self.tr("У вас установлена самая актуальная версия K-Tools."),
                self.window(),
            )
            w.yesButton.setText(self.tr("Отлично"))
            w.cancelButton.hide()
            w.exec()

    def _on_check_error(self, error_msg: str) -> None:
        """Обработка ошибки при проверке."""
        self._check_now_btn.setEnabled(True)
        if hasattr(self, "_tip") and self._tip:
            self._tip.setContent(self.tr("Ошибка проверки"))
            self._tip.setState(False)
            self._checker_tip_timer = self.startTimer(1500)

        w = MessageBox(
            self.tr("Ошибка при проверке обновлений"),
            self.tr(error_msg),
            self.window(),
        )
        w.yesButton.setText(self.tr("ОК"))
        w.cancelButton.hide()
        w.exec()

    def timerEvent(self, event: Any) -> None:
        """Закрытие подсказок по таймеру."""
        super().timerEvent(event)
        if (
            hasattr(self, "_checker_tip_timer")
            and event.timerId() == self._checker_tip_timer
        ):
            self.killTimer(self._checker_tip_timer)
            del self._checker_tip_timer
            if hasattr(self, "_tip") and self._tip:
                try:
                    self._tip.close()
                except RuntimeError:
                    # Логирование на русском языке об удалении C++ объекта
                    logger.debug("Объект _tip уже удален из C++")
                self._tip = None

        if (
            hasattr(self, "_download_timer")
            and event.timerId() == self._download_timer
        ):
            self.killTimer(self._download_timer)
            del self._download_timer
            if hasattr(self, "_download_tip") and self._download_tip:
                try:
                    self._download_tip.close()
                except RuntimeError:
                    # Логирование на русском языке об удалении C++ объекта
                    logger.debug("Объект _download_tip уже удален из C++")
                self._download_tip = None

    def _show_update_dialog(
        self, version: str, changelog: str, download_url: str
    ) -> None:
        """Отобразить диалог с информацией о новой версии."""
        title = self.tr(f"Доступно обновление {version}")
        content = self.tr(
            f"Найдена новая версия K-Tools {version}.\n\n"
            f"Список изменений:\n{changelog[:600]}...\n\n"
            f"Хотите скачать обновление сейчас?"
        )
        w = MessageBox(title, content, self.window())
        w.yesButton.setText(self.tr("Скачать"))
        w.cancelButton.setText(self.tr("Позже"))

        if w.exec():
            self._download_update(download_url)

    def _download_update(self, download_url: str) -> None:
        """Запуск фонового скачивания файла обновления."""
        import tempfile
        from app.core.update_worker import FileDownloader

        file_name = download_url.split("/")[-1] or "k-tools-update.zip"
        if not file_name.endswith((".zip", ".exe")):
            file_name += ".zip"

        downloads_dir = os.path.join(os.path.expanduser("~"), "Downloads")
        if not os.path.exists(downloads_dir):
            downloads_dir = tempfile.gettempdir()

        dest_path = os.path.join(downloads_dir, file_name)

        self._download_tip = StateToolTip(
            self.tr("Скачивание обновления"),
            self.tr("Подготовка к скачиванию..."),
            self.window(),
        )
        self._download_tip.move(self._download_tip.getSuitablePos())
        self._download_tip.show()

        self._downloader = FileDownloader(download_url, dest_path, self)

        def on_progress(percent: int) -> None:
            self._download_tip.setContent(self.tr(f"Скачивание: {percent}%"))

        def on_finished(path: str) -> None:
            self._download_tip.setContent(self.tr("Файл скачан!"))
            self._download_tip.setState(True)

            try:
                # Запускаем как полностью независимый обособленный процесс
                # Это гарантирует запуск даже при работе из-под отладчика IDE
                import subprocess

                if os.name == "nt":
                    # Флаг 0x00000008 (DETACHED_PROCESS) полностью отрывает
                    # процесс от родителя
                    subprocess.Popen(
                        [path],
                        creationflags=0x00000008,
                        close_fds=True
                    )
                else:
                    subprocess.Popen(
                        ["open", path],
                        start_new_session=True,
                        close_fds=True
                    )
            except Exception as launch_err:
                logger.exception(
                    "Ошибка при запуске установщика: %s", launch_err
                )
                # Если не удалось запустить автоматически, показываем проводник
                try:
                    if os.name == "nt":
                        subprocess.Popen(f'explorer /select,"{path}"')
                    else:
                        from PyQt6.QtGui import QDesktopServices
                        from PyQt6.QtCore import QUrl
                        QDesktopServices.openUrl(
                            QUrl.fromLocalFile(os.path.dirname(path))
                        )
                except Exception as ex:
                    logger.exception("Ошибка при открытии проводника: %s", ex)

            self._download_timer = self.startTimer(1500)

        def on_error(err: str) -> None:
            self._download_tip.setContent(self.tr("Ошибка скачивания"))
            self._download_tip.setState(False)
            self._download_timer = self.startTimer(1500)

            we = MessageBox(
                self.tr("Ошибка при скачивании"),
                self.tr(err),
                self.window(),
            )
            we.yesButton.setText(self.tr("ОК"))
            we.cancelButton.hide()
            we.exec()

        self._downloader.progress.connect(on_progress)
        self._downloader.finished.connect(on_finished)
        self._downloader.error.connect(on_error)
        self._downloader.finished.connect(self._downloader.deleteLater)
        self._downloader.start()

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
            "Глобальная настройка 'Перезаписывать файлы' изменена "
            "пользователем на: %s",
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
                "Для применения новой темы необходимо перезапустить "
                "приложение. Перезагрузить сейчас?"
            ),
        )

    def _on_parallel_tasks_changed(self, value: int) -> None:
        """Обработка изменения количества параллельных задач."""
        self._settings_manager.max_parallel_tasks = value
        logger.info(
            "Настройка 'max_parallel_tasks' изменена на: %d", value
        )

    def _on_clear_list_changed(self, is_checked: bool) -> None:
        """Обработка изменения настройки автоочистки списка."""
        self._settings_manager.clear_list_on_add = is_checked
        logger.info(
            "Глобальная настройка 'Очищать список при добавлении' изменена "
            "на: %s",
            "ВКЛ" if is_checked else "ВЫКЛ",
        )

    def _on_show_logs_changed(self, is_checked: bool) -> None:
        """Обработка изменения состояния вкладки логов."""
        self._settings_manager.show_logs_tab = is_checked
        self.showLogsChanged.emit(is_checked)
        logger.info(
            "Глобальная настройка 'Показывать вкладку логов' изменена на: %s",
            "ВКЛ" if is_checked else "ВЫКЛ",
        )

    def _show_reset_dialog(self) -> None:
        """Показать диалог подтверждения сброса."""
        title = self.tr("Сброс настроек")
        content = self.tr(
            "Вы уверены, что хотите сбросить все настройки? Это действие "
            "нельзя отменить."
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
            self._clear_list_switch.setChecked(
                self._settings_manager.clear_list_on_add
            )
            self._show_logs_switch.setChecked(
                self._settings_manager.show_logs_tab
            )

            # Сброс комбобокса темы
            self._theme_combo.setCurrentIndex(0)

            logger.info("Пользователь подтвердил сброс всех настроек")

            # Предлагаем перезапуск
            self._show_restart_dialog(
                self.tr("Перезапуск"),
                self.tr(
                    "Настройки сброшены. Рекомендуется перезапустить "
                    "приложение для полного применения изменений. "
                    "Перезагрузить сейчас?"
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
