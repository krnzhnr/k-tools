# -*- coding: utf-8 -*-
"""Страница управления зависимостями приложения.

Показывает карточки для каждой внешней зависимости
с возможностью скачивания, удаления и отслеживания прогресса.
"""

import logging

from PyQt6.QtCore import Qt, pyqtSignal
from PyQt6.QtGui import QColor, QFont
from PyQt6.QtWidgets import (
    QFrame,
    QHBoxLayout,
    QVBoxLayout,
    QWidget,
)
from qfluentwidgets import (
    BodyLabel,
    CaptionLabel,
    CardWidget,
    FlowLayout,
    FluentIcon,
    IconWidget,
    InfoBar,
    InfoBarPosition,
    PrimaryPushButton,
    ProgressBar,
    SmoothScrollArea,
    StrongBodyLabel,
    SubtitleLabel,
    ToolButton,
)

from app.core.dependency_manager import (
    DependencyDownloadWorker,
    DependencyManager,
    DependencyStatus,
)
from app.core.dependency_manifest import (
    DEPENDENCY_REGISTRY,
    DependencyInfo,
)

logger = logging.getLogger(__name__)

# Маппинг имён иконок на FluentIcon
_ICON_MAP: dict[str, FluentIcon] = {
    "VIDEO": FluentIcon.VIDEO,
    "SHARE": FluentIcon.SHARE,
    "MUSIC": FluentIcon.MUSIC,
    "HEADPHONE": FluentIcon.HEADPHONE,
}


class DependencyCard(CardWidget):
    """Карточка одной зависимости.

    Отображает иконку сверху, название и описание по центру,
    зарезервированное место под прогресс-бар и маленькие нативные кнопки
    действий без текста.
    """

    installRequested = pyqtSignal(str)
    removeRequested = pyqtSignal(str)

    def __init__(
        self,
        dep: DependencyInfo,
        status: DependencyStatus,
        parent: QWidget | None = None,
    ) -> None:
        """Инициализация карточки зависимости.

        Args:
            dep: Описание зависимости.
            status: Текущий статус зависимости.
            parent: Родительский виджет.
        """
        super().__init__(parent)
        self._dep = dep
        self._status = status

        # Еще более компактный фиксированный размер вертикальной карточки
        self.setFixedSize(180, 200)

        main_layout = QVBoxLayout(self)
        main_layout.setContentsMargins(10, 12, 10, 12)
        main_layout.setSpacing(6)
        main_layout.setAlignment(Qt.AlignmentFlag.AlignTop)

        self._init_icon(main_layout)
        self._init_content(main_layout)
        self._init_actions(main_layout)

        self._apply_status(status)

    def _init_icon(self, layout: QVBoxLayout) -> None:
        """Инициализация центрированной секции иконки сверху."""
        icon_wrapper = QFrame(self)
        icon_wrapper.setFixedSize(40, 40)
        icon_wrapper.setStyleSheet(
            "background: rgba(255, 255, 255, 0.06);"
            "border-radius: 8px;"
        )
        icon_wrapper_layout = QVBoxLayout(icon_wrapper)
        icon_wrapper_layout.setContentsMargins(8, 8, 8, 8)
        icon_wrapper_layout.setAlignment(Qt.AlignmentFlag.AlignCenter)

        fluent_icon = _ICON_MAP.get(
            self._dep.icon_name, FluentIcon.APPLICATION
        )
        icon_widget = IconWidget(
            fluent_icon.icon(color=QColor("#CCCCCC")),
            icon_wrapper,
        )
        icon_widget.setFixedSize(24, 24)
        icon_wrapper_layout.addWidget(icon_widget)

        layout.addWidget(
            icon_wrapper, alignment=Qt.AlignmentFlag.AlignCenter
        )

    def _init_content(self, layout: QVBoxLayout) -> None:
        """Инициализация текстового контента и прогресс-бара."""
        # Название
        self._title = StrongBodyLabel(self._dep.display_name, self)
        self._title.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self._title.setStyleSheet("font-size: 13px;")
        layout.addWidget(self._title)

        # Описание и размер
        self._desc = CaptionLabel(
            f"{self._dep.description}\nРазмер: ~{self._dep.size_mb:.0f} МБ",
            self,
        )
        self._desc.setWordWrap(True)
        self._desc.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self._desc.setStyleSheet("color: #AAAAAA; font-size: 11px;")
        self._desc.setFixedHeight(30)  # Фиксируем для сохранения геометрии
        layout.addWidget(self._desc)

        # Резервирование места под прогресс-бар для фиксации высоты
        self._progress_container = QWidget(self)
        self._progress_container.setFixedHeight(20)
        progress_layout = QVBoxLayout(self._progress_container)
        progress_layout.setContentsMargins(0, 0, 0, 0)
        progress_layout.setSpacing(2)

        self._progress_bar = ProgressBar(self._progress_container)
        self._progress_bar.setFixedHeight(3)
        self._progress_bar.setValue(0)
        self._progress_bar.hide()
        progress_layout.addWidget(self._progress_bar)

        self._status_label = CaptionLabel("", self._progress_container)
        self._status_label.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self._status_label.setStyleSheet("color: #888888; font-size: 10px;")
        self._status_label.setWordWrap(True)
        self._status_label.hide()
        progress_layout.addWidget(self._status_label)

        layout.addWidget(self._progress_container)

    def _init_actions(self, layout: QVBoxLayout) -> None:
        """Инициализация кнопок действий (галочка и корзина)."""
        actions_layout = QHBoxLayout()
        actions_layout.setContentsMargins(0, 0, 0, 0)
        actions_layout.setSpacing(16)
        actions_layout.setAlignment(Qt.AlignmentFlag.AlignCenter)

        # Нативная непрозрачная кнопка установки (галочка)
        self._install_btn = ToolButton(self)
        self._install_btn.setFixedSize(28, 28)
        self._install_btn.clicked.connect(self._on_install_clicked)
        actions_layout.addWidget(self._install_btn)

        # Нативная непрозрачная кнопка удаления (корзина)
        self._remove_btn = ToolButton(self)
        self._remove_btn.setFixedSize(28, 28)
        self._remove_btn.clicked.connect(self._on_remove_clicked)
        actions_layout.addWidget(self._remove_btn)

        layout.addLayout(actions_layout)

    def _on_install_clicked(self) -> None:
        """Обработка нажатия на кнопку установки."""
        logger.info(
            "Нажата кнопка установки для зависимости %s.", self._dep.key
        )
        self.installRequested.emit(self._dep.key)

    def _on_remove_clicked(self) -> None:
        """Обработка нажатия на кнопку удаления."""
        logger.info(
            "Нажата кнопка удаления для зависимости %s.", self._dep.key
        )
        self.removeRequested.emit(self._dep.key)

    def _apply_status(self, status: DependencyStatus) -> None:
        """Применение визуального состояния в соответствии со статусом."""
        self._status = status
        logger.info(
            "Применение статуса %s к кнопкам карточки %s.",
            status,
            self._dep.key,
        )

        if status == DependencyStatus.INSTALLED:
            # Зеленая галочка (установлено, повторная установка недоступна)
            self._install_btn.setIcon(
                FluentIcon.ACCEPT_MEDIUM.icon(color=QColor("#4CAF50"))
            )
            self._install_btn.setEnabled(False)

            # Активная красная корзина (доступно удаление)
            self._remove_btn.setIcon(
                FluentIcon.DELETE.icon(color=QColor("#F44336"))
            )
            self._remove_btn.setEnabled(True)

            self._progress_bar.hide()
            self._status_label.hide()

        elif status == DependencyStatus.NOT_INSTALLED:
            # Активная стандартная иконка скачивания (доступно для скачивания)
            self._install_btn.setIcon(
                FluentIcon.DOWNLOAD.icon(color=QColor("#ffffff"))
            )
            self._install_btn.setEnabled(True)

            # Неактивная корзина (удалять нечего)
            self._remove_btn.setIcon(
                FluentIcon.DELETE.icon(color=QColor("#555555"))
            )
            self._remove_btn.setEnabled(False)

            self._progress_bar.hide()
            self._status_label.hide()

        elif status == DependencyStatus.DOWNLOADING:
            # Кнопка скачивания становится индикатором загрузки
            self._install_btn.setIcon(
                FluentIcon.DOWNLOAD.icon(color=QColor("#0078d4"))
            )
            self._install_btn.setEnabled(False)

            self._remove_btn.setIcon(
                FluentIcon.DELETE.icon(color=QColor("#555555"))
            )
            self._remove_btn.setEnabled(False)

            self._progress_bar.show()
            self._status_label.show()
            self._status_label.setText("Загрузка...")

        elif status == DependencyStatus.EXTRACTING:
            # Кнопка установки показывает распаковку
            self._install_btn.setIcon(
                FluentIcon.ZIP_FOLDER.icon(color=QColor("#FF9800"))
            )
            self._install_btn.setEnabled(False)

            self._remove_btn.setIcon(
                FluentIcon.DELETE.icon(color=QColor("#555555"))
            )
            self._remove_btn.setEnabled(False)

            self._progress_bar.show()
            self._progress_bar.setValue(100)
            self._status_label.show()
            self._status_label.setText("Распаковка...")

        elif status == DependencyStatus.ERROR:
            # Ошибка
            self._install_btn.setIcon(
                FluentIcon.INFO.icon(color=QColor("#F44336"))
            )
            self._install_btn.setEnabled(True)

            self._remove_btn.setIcon(
                FluentIcon.DELETE.icon(color=QColor("#555555"))
            )
            self._remove_btn.setEnabled(False)

            self._progress_bar.hide()
            self._status_label.show()

    def set_progress(self, percent: int) -> None:
        """Установка текущего прогресса загрузки.

        Args:
            percent: Процент загрузки (0-100).
        """
        self._progress_bar.setValue(percent)

    def set_speed(self, speed: str) -> None:
        """Установка отображаемой скорости загрузки.

        Args:
            speed: Строка скорости (например, '2.5 МБ/с').
        """
        self._status_label.setText(f"Загрузка... {speed}")

    def set_error(self, message: str) -> None:
        """Отображение ошибки скачивания.

        Args:
            message: Текст сообщения об ошибке.
        """
        self._status_label.setText("⚠ Ошибка загрузки")
        self._status_label.setStyleSheet("color: #FF5722; font-size: 10px;")

    def update_status(self, status: DependencyStatus) -> None:
        """Обновление статуса карточки зависимости.

        Args:
            status: Новый статус зависимости.
        """
        self._apply_status(status)


class DependencySetupPage(QWidget):
    """Страница установки зависимостей.

    Отображает все зависимости в виде карточек в QGridLayout-сетке
    с возможностью установки, удаления и отслеживания.
    """

    dependenciesChanged = pyqtSignal()

    def __init__(self, parent: QWidget | None = None) -> None:
        """Инициализация страницы зависимостей.

        Args:
            parent: Родительский виджет.
        """
        super().__init__(parent)
        self.setObjectName("dependencySetupPage")
        self._dep_mgr = DependencyManager()
        self._cards: dict[str, DependencyCard] = {}
        self._workers: dict[str, DependencyDownloadWorker] = {}

        self._init_ui()
        logger.info(
            "Страница управления зависимостями успешно "
            "инициализирована."
        )

    def _init_ui(self) -> None:
        """Инициализация графического интерфейса страницы."""
        layout = QVBoxLayout(self)
        layout.setContentsMargins(0, 0, 0, 0)

        self._scroll = SmoothScrollArea(self)
        self._scroll.setWidgetResizable(True)
        self._scroll.verticalScrollBar().setSingleStep(30)
        self._scroll.setStyleSheet(
            "background: transparent; border: none;"
        )

        container = QWidget()
        container.setStyleSheet("background: transparent;")
        content = QVBoxLayout(container)
        content.setContentsMargins(36, 40, 36, 40)
        content.setSpacing(24)

        # Заголовок страницы
        header = QVBoxLayout()
        header.setSpacing(6)

        title = SubtitleLabel("Управление зависимостями", self)
        title.setFont(QFont("Segoe UI", 24, QFont.Weight.Bold))
        header.addWidget(title)

        desc = BodyLabel(
            "Скачайте необходимые компоненты для работы инструментов. "
            "Инструменты, требующие отсутствующие зависимости, "
            "будут временно отключены.",
            self,
        )
        desc.setWordWrap(True)
        header.addWidget(desc)
        content.addLayout(header)

        # Сетка карточек зависимостей (FlowLayout)
        cards_container = QWidget(container)
        cards_container.setStyleSheet("background: transparent;")
        cards_layout = FlowLayout(cards_container)
        cards_layout.setContentsMargins(0, 0, 0, 0)
        cards_layout.setSpacing(16)

        statuses = self._dep_mgr.get_all_statuses()
        for dep in DEPENDENCY_REGISTRY:
            status = statuses.get(dep.key, DependencyStatus.NOT_INSTALLED)
            card = DependencyCard(dep, status, cards_container)
            card.installRequested.connect(self._on_install_requested)
            card.removeRequested.connect(self._on_remove_requested)
            self._cards[dep.key] = card
            cards_layout.addWidget(card)

        content.addWidget(cards_container)

        # Нижняя панель действий
        btn_layout = QHBoxLayout()
        btn_layout.setSpacing(12)

        self._install_all_btn = PrimaryPushButton("Скачать все", self)
        self._install_all_btn.setFixedWidth(160)
        self._install_all_btn.clicked.connect(self._on_install_all)
        btn_layout.addWidget(self._install_all_btn)

        btn_layout.addStretch()
        content.addLayout(btn_layout)

        content.addStretch()
        self._scroll.setWidget(container)
        layout.addWidget(self._scroll)

    def _on_install_requested(self, key: str) -> None:
        """Запуск скачивания и установки конкретной зависимости.

        Args:
            key: Идентификатор зависимости.
        """
        if key in self._workers:
            logger.warning(
                "Процесс скачивания для '%s' уже запущен.", key
            )
            return

        from app.core.dependency_manifest import DEPENDENCY_MAP

        dep = DEPENDENCY_MAP.get(key)
        if not dep:
            logger.error("Зависимость с ключом %s не найдена.", key)
            return

        logger.info(
            "Запуск процесса скачивания зависимости: %s.",
            dep.display_name,
        )

        worker = DependencyDownloadWorker(
            dep, self._dep_mgr.bin_dir, self
        )
        worker.progress.connect(self._on_progress)
        worker.speed_updated.connect(self._on_speed)
        worker.status_changed.connect(self._on_status_changed)
        worker.download_finished.connect(self._on_download_finished)
        worker.finished.connect(lambda: self._cleanup_worker(key))

        self._workers[key] = worker
        worker.start()

    def _on_remove_requested(self, key: str) -> None:
        """Обработка запроса на удаление установленной зависимости.

        Args:
            key: Идентификатор зависимости.
        """
        success = self._dep_mgr.remove_dependency(key)
        if success:
            card = self._cards.get(key)
            if card:
                card.update_status(DependencyStatus.NOT_INSTALLED)
            self.dependenciesChanged.emit()
            InfoBar.success(
                title="Удалено",
                content="Зависимость успешно удалена с диска.",
                orient=Qt.Orientation.Horizontal,
                isClosable=True,
                position=InfoBarPosition.TOP,
                duration=3000,
                parent=self,
            )
        else:
            InfoBar.error(
                title="Ошибка",
                content="Не удалось корректно удалить файлы зависимости.",
                orient=Qt.Orientation.Horizontal,
                isClosable=True,
                position=InfoBarPosition.TOP,
                duration=5000,
                parent=self,
            )

    def _on_install_all(self) -> None:
        """Скачивание всех недостающих зависимостей."""
        statuses = self._dep_mgr.get_all_statuses()
        for dep in DEPENDENCY_REGISTRY:
            status = statuses.get(dep.key)
            if status != DependencyStatus.INSTALLED:
                self._on_install_requested(dep.key)

    def _on_progress(self, key: str, percent: int) -> None:
        """Обновление прогресса скачивания зависимости.

        Args:
            key: Идентификатор зависимости.
            percent: Процент завершения загрузки.
        """
        card = self._cards.get(key)
        if card:
            card.set_progress(percent)

    def _on_speed(self, key: str, speed: str) -> None:
        """Обновление скорости скачивания зависимости.

        Args:
            key: Идентификатор зависимости.
            speed: Строка скорости.
        """
        card = self._cards.get(key)
        if card:
            card.set_speed(speed)

    def _on_status_changed(self, key: str, status: object) -> None:
        """Обновление статуса зависимости и синхронизация с менеджером.

        Args:
            key: Идентификатор зависимости.
            status: Новый статус.
        """
        card = self._cards.get(key)
        if card and isinstance(status, DependencyStatus):
            card.update_status(status)
            self._dep_mgr.set_status(key, status)

    def _on_download_finished(
        self, key: str, success: bool, error_msg: str
    ) -> None:
        """Обработка завершения процесса скачивания.

        Args:
            key: Идентификатор зависимости.
            success: Признак успешного завершения.
            error_msg: Сообщение об ошибке, если применимо.
        """
        card = self._cards.get(key)
        if success:
            if card:
                card.update_status(DependencyStatus.INSTALLED)
            self.dependenciesChanged.emit()
            InfoBar.success(
                title="Успешно",
                content="Зависимость успешно скачана и распакована.",
                orient=Qt.Orientation.Horizontal,
                isClosable=True,
                position=InfoBarPosition.TOP,
                duration=3000,
                parent=self,
            )
        else:
            if card:
                card.update_status(DependencyStatus.ERROR)
                card.set_error(error_msg)
            InfoBar.error(
                title="Ошибка скачивания",
                content=error_msg,
                orient=Qt.Orientation.Horizontal,
                isClosable=True,
                position=InfoBarPosition.TOP,
                duration=5000,
                parent=self,
            )

    def _cleanup_worker(self, key: str) -> None:
        """Очистка ресурсов воркера после завершения.

        Args:
            key: Идентификатор зависимости.
        """
        worker = self._workers.pop(key, None)
        if worker:
            worker.deleteLater()

    def refresh_statuses(self) -> None:
        """Обновление статусов отображения для всех карточек."""
        statuses = self._dep_mgr.get_all_statuses()
        for key, card in self._cards.items():
            status = statuses.get(key, DependencyStatus.NOT_INSTALLED)
            card.update_status(status)
