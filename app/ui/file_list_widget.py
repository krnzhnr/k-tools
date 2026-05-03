# -*- coding: utf-8 -*-
"""Виджет списка файлов с поддержкой drag-n-drop."""

import logging
from pathlib import Path
from typing import Sequence

from PyQt6.QtCore import (
    Qt, pyqtSignal, QSize, QPropertyAnimation, QEasingCurve
)
from PyQt6.QtWidgets import (
    QAbstractItemView,
    QListWidgetItem,
    QFileDialog,
    QWidget,
    QHBoxLayout,
    QLabel,
)
from qfluentwidgets import (
    ListWidget,
    RoundMenu,
    Action,
    FluentIcon,
    IndeterminateProgressRing,
    ProgressRing,
    IconWidget,
)
from app.core.settings_manager import SettingsManager

logger = logging.getLogger(__name__)


class FileItemWidget(QWidget):
    """Виджет элемента списка файлов с индикацией статуса."""

    def __init__(self, file_path: Path, parent=None):
        super().__init__(parent)
        self.file_path = file_path
        self._current_status = "idle"
        self.setAttribute(Qt.WidgetAttribute.WA_StyledBackground)
        self.setFixedHeight(36)
        self._init_ui()

    def _init_ui(self):
        layout = QHBoxLayout(self)
        layout.setContentsMargins(8, 4, 8, 4)
        layout.setSpacing(8)

        self.name_label = QLabel(self.file_path.name)
        self.name_label.setStyleSheet("font-size: 13px;")

        self.status_icon = IconWidget(FluentIcon.COMPLETED, self)
        self.status_icon.setFixedSize(16, 16)
        self.status_icon.setVisible(False)

        self.spinner = IndeterminateProgressRing(self)
        self.spinner.setFixedSize(16, 16)
        self.spinner.setStrokeWidth(2)
        self.spinner.setVisible(False)

        self.progress_ring = ProgressRing(self)
        self.progress_ring.setFixedSize(16, 16)
        self.progress_ring.setStrokeWidth(2)
        self.progress_ring.setTextVisible(False)
        self.progress_ring.setVisible(False)

        # Анимация для плавного движения кольца
        self.progress_anim = QPropertyAnimation(self.progress_ring, b"value")
        self.progress_anim.setDuration(350)
        self.progress_anim.setEasingCurve(QEasingCurve.Type.OutQuad)

        layout.addWidget(self.name_label)
        layout.addStretch(1)
        layout.addWidget(self.status_icon)
        layout.addWidget(self.spinner)
        layout.addWidget(self.progress_ring)

    def set_status(self, status: str):
        """Установить визуальный статус файла."""
        if self._current_status == status:
            return

        self._current_status = status

        # Сброс
        self.spinner.stop()
        self.spinner.setVisible(False)
        self.progress_ring.setVisible(False)
        self.status_icon.setVisible(False)

        if status == "processing":
            self.spinner.setVisible(True)
            self.spinner.start()
        elif status == "pending":
            self.status_icon.setIcon(FluentIcon.HISTORY)
            self.status_icon.setVisible(True)
        elif status == "success":
            self.status_icon.setIcon(FluentIcon.COMPLETED)
            self.status_icon.setVisible(True)
        elif status == "error":
            self.status_icon.setIcon(FluentIcon.CANCEL)
            self.status_icon.setVisible(True)
        elif status == "idle":
            pass

        # Принудительная немедленная перерисовка виджета
        self.repaint()

    def set_progress(self, value: float):
        """Установить значение прогресса для файла."""
        target_val = int(value)
        if target_val < 1:
            return

        # Если есть прогресс — скрываем спиннер и показываем кольцо
        if self.spinner.isVisible():
            self.spinner.stop()
            self.spinner.setVisible(False)

        if not self.progress_ring.isVisible():
            self.progress_ring.setVisible(True)

        # Запускаем плавную анимацию до нового значения
        if self.progress_anim.endValue() != target_val:
            self.progress_anim.stop()
            self.progress_anim.setEndValue(target_val)
            self.progress_anim.start()

        self.repaint()


class FileListWidget(ListWidget):
    """Список файлов с поддержкой drag-n-drop.

    Позволяет перетаскивать файлы из проводника,
    фильтрует по допустимым расширениям и предоставляет
    контекстное меню для управления списком.
    """

    filesChanged = pyqtSignal()

    PLACEHOLDER_TEXT = (
        "Перетащите файлы сюда\n" "или используйте контекстное меню"
    )

    def __init__(
        self,
        allowed_extensions: list[str] | None = None,
        context_name: str = "Неизвестный скрипт",
        parent=None,
    ) -> None:
        """Инициализация виджета списка файлов.

        Args:
            allowed_extensions: Допустимые расширения.
            context_name: Имя контекста (скрипта) для логирования.
            parent: Родительский виджет.
        """
        super().__init__(parent)
        self._allowed_extensions: list[str] = allowed_extensions or []
        self._context_name = context_name
        self._file_paths: list[Path] = []
        self._item_map: dict[Path, FileItemWidget] = {}

        self._setup_drag_drop()
        self._setup_context_menu()

        logger.info(
            "[%s] Виджет списка файлов успешно инициализирован. "
            "Список допустимых расширений: %s",
            self._context_name,
            (
                self._allowed_extensions
                if self._allowed_extensions
                else "все файлы"
            ),
        )

    def _setup_drag_drop(self) -> None:
        """Настройка поддержки drag-n-drop."""
        self.setAcceptDrops(True)
        self.setDragDropMode(QAbstractItemView.DragDropMode.DropOnly)
        self.setDefaultDropAction(Qt.DropAction.CopyAction)

    def _setup_context_menu(self) -> None:
        """Настройка контекстного меню."""
        self.setContextMenuPolicy(Qt.ContextMenuPolicy.CustomContextMenu)
        self.customContextMenuRequested.connect(self._show_context_menu)

    def set_allowed_extensions(self, extensions: list[str]) -> None:
        """Обновить список допустимых расширений.

        Args:
            extensions: Новый список расширений.
        """
        self._allowed_extensions = [ext.lower() for ext in extensions]
        logger.info(
            "[%s] Фильтр расширений обновлен пользователем: %s",
            self._context_name,
            (
                self._allowed_extensions
                if self._allowed_extensions
                else "все файлы"
            ),
        )

    def get_file_paths(self) -> list[Path]:
        """Получить список путей к добавленным файлам.

        Returns:
            Копия списка путей к файлам.
        """
        return list(self._file_paths)

    @property
    def files(self) -> list[Path]:
        """Список путей к добавленным файлам.

        Returns:
            Копия списка путей к файлам.
        """
        return list(self._file_paths)

    def add_files(self, paths: Sequence[str | Path]) -> None:
        """Программно добавить файлы в список.

        Args:
            paths: Список путей к файлам.
        """
        if (
            SettingsManager().clear_list_on_add
            and self._file_paths
            and paths
        ):
            self.clear_files()
            logger.info(
                "[%s] Список очищен перед программным добавлением "
                "согласно настройкам",
                self._context_name,
            )

        added_count = 0
        for p in paths:
            file_path = Path(p)
            if self._is_valid_file(file_path):
                self._add_file(file_path)
                added_count += 1

        if added_count > 0:
            self.filesChanged.emit()
            logger.info(
                "[%s] В список успешно добавлено файлов (программно): %d",
                self._context_name,
                added_count,
            )
        else:
            logger.warning(
                "[%s] При попытке программного добавления ни один файл не "
                "прошел валидацию",
                self._context_name,
            )

    def clear_files(self) -> None:
        """Очистить список файлов."""
        self._file_paths.clear()
        self._item_map.clear()
        self.clear()
        self.filesChanged.emit()
        logger.info(
            "[%s] Список файлов полностью очищен пользователем",
            self._context_name,
        )

    def dragEnterEvent(self, event) -> None:
        """Обработка входа перетаскивания.

        Args:
            event: Событие перетаскивания.
        """
        if event.mimeData().hasUrls():
            event.acceptProposedAction()
        else:
            event.ignore()

    def dragMoveEvent(self, event) -> None:
        """Обработка перемещения при перетаскивании.

        Args:
            event: Событие перемещения.
        """
        if event.mimeData().hasUrls():
            event.acceptProposedAction()
        else:
            event.ignore()

    def dropEvent(self, event) -> None:
        """Обработка сброса файлов в виджет.

        Args:
            event: Событие сброса.
        """
        if not event.mimeData().hasUrls():
            event.ignore()
            return

        if SettingsManager().clear_list_on_add and self._file_paths:
            self.clear_files()
            logger.info(
                "[%s] Список очищен перед drag-n-drop согласно настройкам",
                self._context_name,
            )

        added_count = 0
        for url in event.mimeData().urls():
            file_path = Path(url.toLocalFile())
            if self._is_valid_file(file_path):
                self._add_file(file_path)
                added_count += 1

        if added_count > 0:
            logger.info(
                "[%s] Успешно добавлено файлов через drag-n-drop: %d",
                self._context_name,
                added_count,
            )
            self.filesChanged.emit()
        else:
            logger.warning(
                "[%s] Сброшенные файлы не прошли валидацию по расширению "
                "или не являются файлами",
                self._context_name,
            )
        event.acceptProposedAction()

    def _is_valid_file(self, path: Path) -> bool:
        """Проверить, является ли файл допустимым.

        Args:
            path: Путь к файлу.

        Returns:
            True, если файл подходит по расширению.
        """
        if not path.is_file():
            return False

        if not self._allowed_extensions:
            return True

        return path.suffix.lower() in self._allowed_extensions

    def _add_file(self, path: Path) -> None:
        """Добавить файл в список.

        Args:
            path: Путь к файлу.
        """
        if path in self._file_paths:
            logger.info(
                "[%s] Файл пропущен (уже есть в списке): %s",
                self._context_name,
                path.name,
            )
            return

        self._file_paths.append(path)
        item = QListWidgetItem(self)
        item.setSizeHint(QSize(-1, 36))
        item.setToolTip(str(path))

        widget = FileItemWidget(path, self)
        self._item_map[path] = widget

        self.addItem(item)
        self.setItemWidget(item, widget)
        logger.info(
            "[%s] Новый файл успешно добавлен в список: %s (путь: %s)",
            self._context_name,
            path.name,
            path,
        )

    def _show_context_menu(self, position) -> None:
        """Показать контекстное меню.

        Args:
            position: Позиция клика.
        """
        menu = RoundMenu(parent=self)

        add_action = Action(
            FluentIcon.ADD,
            "Добавить файлы",
            triggered=self._on_add_files_clicked,
        )
        remove_action = Action(
            FluentIcon.DELETE,
            "Удалить из списка",
            triggered=self._remove_selected,
        )
        clear_action = Action(
            FluentIcon.BROOM,
            "Очистить список",
            triggered=self.clear_files,
        )

        menu.addAction(add_action)
        menu.addSeparator()
        menu.addAction(remove_action)
        menu.addAction(clear_action)

        menu.exec(self.mapToGlobal(position))

    def _on_add_files_clicked(self) -> None:
        """Обработчик нажатия «Добавить файлы» в меню."""
        # Формируем фильтры для диалога
        if self._allowed_extensions:
            ext_filter = " ".join(
                [f"*{ext}" for ext in self._allowed_extensions]
            )
            filter_str = f"Допустимые файлы ({ext_filter});;Все файлы (*)"
        else:
            filter_str = "Все файлы (*)"

        files, _ = QFileDialog.getOpenFileNames(
            self, "Выберите файлы для добавления", "", filter_str
        )

        if files:
            self.add_files(files)

    def _remove_selected(self) -> None:
        """Удалить выбранные файлы из списка."""
        selected_rows = sorted(
            [idx.row() for idx in self.selectedIndexes()],
            reverse=True,
        )

        for row in selected_rows:
            if 0 <= row < len(self._file_paths):
                removed = self._file_paths.pop(row)
                if removed in self._item_map:
                    del self._item_map[removed]
                self.takeItem(row)
                logger.info(
                    "[%s] Пользователь удалил файл из списка: %s (индекс: %d)",
                    self._context_name,
                    removed.name,
                    row,
                )

        if selected_rows:
            self.filesChanged.emit()

    def update_file_status(self, file_path: Path, status: str):
        """Обновить статус отображения для конкретного файла."""
        # Пытаемся найти по объекту Path или по строке для надежности
        widget = self._item_map.get(file_path)
        if not widget:
            # Поиск по строковому представлению
            search_str = str(file_path).lower()
            for path, w in self._item_map.items():
                if str(path).lower() == search_str:
                    widget = w
                    break

        if widget:
            widget.set_status(status)
            # Принудительно перерисовываем вьюпорт немедленно
            self.viewport().repaint()
        else:
            # Поиск по имени, если путь не совпал (полезно при пересоздании)
            for path, widget in self._item_map.items():
                if path.name == file_path.name:
                    widget.set_status(status)
                    break

    def update_file_progress(self, file_path: Path, value: float):
        """Обновить прогресс обработки для конкретного файла."""
        widget = self._item_map.get(file_path)
        if not widget:
            search_str = str(file_path).lower()
            for path, w in self._item_map.items():
                if str(path).lower() == search_str:
                    widget = w
                    break

        if widget:
            widget.set_progress(value)
            self.viewport().repaint()

    def paintEvent(self, event) -> None:
        """Отрисовка placeholder-текста при пустом списке.

        Args:
            event: Событие отрисовки.
        """
        super().paintEvent(event)

        if self.count() == 0:
            from PyQt6.QtGui import QPainter, QColor

            painter = QPainter(self.viewport())
            try:
                painter.setPen(QColor(128, 128, 128))
                painter.drawText(
                    self.viewport().rect(),
                    Qt.AlignmentFlag.AlignCenter,
                    self.PLACEHOLDER_TEXT,
                )
            finally:
                painter.end()
