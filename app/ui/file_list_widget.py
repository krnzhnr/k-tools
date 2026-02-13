# -*- coding: utf-8 -*-
"""Виджет списка файлов с поддержкой drag-n-drop."""

import logging
from pathlib import Path

from PyQt6.QtCore import Qt
from PyQt6.QtWidgets import QAbstractItemView, QListWidgetItem
from qfluentwidgets import ListWidget, RoundMenu, Action, FluentIcon

logger = logging.getLogger(__name__)


class FileListWidget(ListWidget):
    """Список файлов с поддержкой drag-n-drop.

    Позволяет перетаскивать файлы из проводника,
    фильтрует по допустимым расширениям и предоставляет
    контекстное меню для управления списком.
    """

    PLACEHOLDER_TEXT = (
        "Перетащите файлы сюда\n"
        "или используйте контекстное меню"
    )

    def __init__(
        self,
        allowed_extensions: list[str] | None = None,
        parent=None,
    ) -> None:
        """Инициализация виджета списка файлов.

        Args:
            allowed_extensions: Допустимые расширения.
            parent: Родительский виджет.
        """
        super().__init__(parent)
        self._allowed_extensions: list[str] = (
            allowed_extensions or []
        )
        self._file_paths: list[Path] = []

        self._setup_drag_drop()
        self._setup_context_menu()

        logger.info(
            "Виджет списка файлов создан "
            "(расширения: %s)",
            self._allowed_extensions,
        )

    def _setup_drag_drop(self) -> None:
        """Настройка поддержки drag-n-drop."""
        self.setAcceptDrops(True)
        self.setDragDropMode(
            QAbstractItemView.DragDropMode.DropOnly
        )
        self.setDefaultDropAction(Qt.DropAction.CopyAction)

    def _setup_context_menu(self) -> None:
        """Настройка контекстного меню."""
        self.setContextMenuPolicy(
            Qt.ContextMenuPolicy.CustomContextMenu
        )
        self.customContextMenuRequested.connect(
            self._show_context_menu
        )

    def set_allowed_extensions(
        self, extensions: list[str]
    ) -> None:
        """Обновить список допустимых расширений.

        Args:
            extensions: Новый список расширений.
        """
        self._allowed_extensions = [
            ext.lower() for ext in extensions
        ]
        logger.info(
            "Обновлены допустимые расширения: %s",
            self._allowed_extensions,
        )

    def get_file_paths(self) -> list[Path]:
        """Получить список путей к добавленным файлам.

        Returns:
            Копия списка путей к файлам.
        """
        return list(self._file_paths)

    def clear_files(self) -> None:
        """Очистить список файлов."""
        self._file_paths.clear()
        self.clear()
        logger.info("Список файлов очищен")

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

        added_count = 0
        for url in event.mimeData().urls():
            file_path = Path(url.toLocalFile())
            if self._is_valid_file(file_path):
                self._add_file(file_path)
                added_count += 1

        logger.info(
            "Добавлено файлов через drag-n-drop: %d",
            added_count,
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
            logger.debug(
                "Файл уже в списке: %s", path.name
            )
            return

        self._file_paths.append(path)
        item = QListWidgetItem(path.name)
        item.setToolTip(str(path))
        self.addItem(item)
        logger.debug("Файл добавлен: %s", path.name)

    def _show_context_menu(self, position) -> None:
        """Показать контекстное меню.

        Args:
            position: Позиция клика.
        """
        menu = RoundMenu(parent=self)

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

        menu.addAction(remove_action)
        menu.addSeparator()
        menu.addAction(clear_action)

        menu.exec(self.mapToGlobal(position))

    def _remove_selected(self) -> None:
        """Удалить выбранные файлы из списка."""
        selected_rows = sorted(
            [idx.row() for idx in self.selectedIndexes()],
            reverse=True,
        )

        for row in selected_rows:
            if 0 <= row < len(self._file_paths):
                removed = self._file_paths.pop(row)
                self.takeItem(row)
                logger.info(
                    "Файл удалён из списка: %s",
                    removed.name,
                )

    def paintEvent(self, event) -> None:
        """Отрисовка placeholder-текста при пустом списке.

        Args:
            event: Событие отрисовки.
        """
        super().paintEvent(event)

        if self.count() == 0:
            from PyQt6.QtGui import QPainter, QColor
            painter = QPainter(self.viewport())
            painter.setPen(QColor(128, 128, 128))
            painter.drawText(
                self.viewport().rect(),
                Qt.AlignmentFlag.AlignCenter,
                self.PLACEHOLDER_TEXT,
            )
            painter.end()
