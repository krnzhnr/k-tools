# -*- coding: utf-8 -*-
"""Таблица для муксинга файлов (Видео, Аудио, Субтитры)."""

from PyQt6.QtWidgets import QStyledItemDelegate, QStyle
import logging
import re
from pathlib import Path

from PyQt6.QtCore import Qt, pyqtSignal
from PyQt6.QtGui import QDragEnterEvent, QDropEvent, QDragMoveEvent
from PyQt6.QtWidgets import (
    QTableWidget,
    QTableWidgetItem,
    QHeaderView,
    QAbstractItemView,
    QApplication,
    QFileDialog,
)
from qfluentwidgets import RoundMenu, Action, FluentIcon

from app.core.constants import (
    VIDEO_CONTAINERS,
    AUDIO_EXTENSIONS,
    SUBTITLE_EXTENSIONS,
)

logger = logging.getLogger(__name__)


class NaturalSortTableWidgetItem(QTableWidgetItem):
    """Элемент таблицы с естественной сортировкой (Natural Sort Order)."""

    def __lt__(self, other):
        return self._natural_key(self.text()) < self._natural_key(other.text())

    @staticmethod
    def _natural_key(text):
        """Преобразует текст в ключ для естественной сортировки."""
        return [
            int(c) if c.isdigit() else c.lower()
            for c in re.split(r"(\d+)", text)
        ]


class ElideMiddleDelegate(QStyledItemDelegate):
    """Делегат для принудительного сокращения текста посередине."""

    def paint(self, painter, option, index):
        """Отрисовка текста с ElideMiddle."""
        # Инициализируем опции стиля
        self.initStyleOption(option, index)

        painter.save()

        # Рисуем фон (выделение и т.д.) через стиль, чтобы не ломать внешний вид  # noqa: E501
        widget = option.widget
        style = widget.style() if widget else QApplication.style()
        style.drawPrimitive(
            QStyle.PrimitiveElement.PE_PanelItemViewItem,
            option,
            painter,
            widget,
        )

        # Получаем текст и прямоугольник для рисования
        text = index.data(Qt.ItemDataRole.DisplayRole)
        if text:
            rect = option.rect
            # Небольшой отступ слева/справа
            text_rect = rect.adjusted(5, 0, -5, 0)

            # Сокращаем текст посередине
            elided_text = option.fontMetrics.elidedText(
                text, Qt.TextElideMode.ElideMiddle, text_rect.width()
            )

            # Настраиваем цвет текста
            if option.state & QStyle.StateFlag.State_Selected:
                color = option.palette.highlightedText().color()
            else:
                color = option.palette.text().color()

            painter.setPen(color)

            # Рисуем текст
            painter.drawText(
                text_rect,
                Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignVCenter,
                elided_text,
            )

        painter.restore()


class MuxingTableWidget(QTableWidget):
    """Таблица для управления файлами муксинга."""

    filesChanged = pyqtSignal()

    COL_VIDEO = 0
    COL_AUDIO = 1
    COL_SUBS = 2

    def __init__(self, parent=None):
        """Инициализация таблицы."""
        super().__init__(parent)
        self.setColumnCount(3)
        self.setHorizontalHeaderLabels(["Видео", "Аудио", "Субтитры"])

        # Настройка заголовков
        header = self.horizontalHeader()
        header.setSectionResizeMode(QHeaderView.ResizeMode.Stretch)
        # Устанавливаем сортировку по первому столбцу (Видео) по возрастанию
        header.setSortIndicator(0, Qt.SortOrder.AscendingOrder)

        # Настройка поведения
        self.setSelectionBehavior(
            QAbstractItemView.SelectionBehavior.SelectRows
        )
        self.setEditTriggers(QAbstractItemView.EditTrigger.NoEditTriggers)
        self.setAlternatingRowColors(True)
        self.verticalHeader().setVisible(False)
        # Сокращение текста посередине, если не влезает
        self.setTextElideMode(Qt.TextElideMode.ElideMiddle)
        self.setItemDelegate(ElideMiddleDelegate(self))

        # Drag-n-Drop
        self.setAcceptDrops(True)

        # Данные: словарь {stem: row_index} для быстрого поиска строки по имени файла  # noqa: E501
        # self._stems: dict[str, int] = {} # Удаляем, т.к. при сортировке индексы "плывут"  # noqa: E501

        # Стилизация (опционально, если не хватает Fluent Style)
        self.setStyleSheet(
            """
            QTableWidget {
                border: 1px solid rgba(0, 0, 0, 0.1);
                border-radius: 5px;
                background-color: transparent;
            }
            QHeaderView::section {
                background-color: transparent;
                padding: 4px;
                border: none;
                font-weight: bold;
            }
        """
        )

        # Включаем сортировку
        self.setSortingEnabled(True)

        self._setup_context_menu()

    def _setup_context_menu(self) -> None:
        """Настройка контекстного меню."""
        self.setContextMenuPolicy(Qt.ContextMenuPolicy.CustomContextMenu)
        self.customContextMenuRequested.connect(self._show_context_menu)

    def _show_context_menu(self, position) -> None:
        """Показать контекстное меню."""
        menu = RoundMenu(parent=self)

        add_action = Action(
            FluentIcon.ADD,
            "Добавить файлы",
            triggered=self._on_add_files_clicked,
        )
        remove_action = Action(
            FluentIcon.DELETE,
            "Удалить выбранные",
            triggered=self._remove_selected,
        )
        clear_action = Action(
            FluentIcon.BROOM,
            "Очистить список",
            triggered=self.clear_all,
        )

        menu.addAction(add_action)
        menu.addSeparator()
        menu.addAction(remove_action)
        menu.addAction(clear_action)

        menu.exec(self.mapToGlobal(position))

    def _on_add_files_clicked(self) -> None:
        """Обработчик нажатия «Добавить файлы» в меню."""
        # Формируем фильтры для всех поддерживаемых типов
        video_filters = "*.mkv *.mp4 *.avi *.mov *.webm"
        audio_filters = (
            "*.mp3 *.aac *.ac3 *.dts *.eac3 *.flac *.wav *.m4a *.ogg *.mka"
        )
        subs_filters = "*.srt *.ass *.ssa *.sub"

        all_exts = f"{video_filters} {audio_filters} {subs_filters}"

        filter_str = (
            f"Мультимедиа ({all_exts});;"
            f"Видео ({video_filters});;"
            f"Аудио ({audio_filters});;"
            f"Субтитры ({subs_filters});;"
            "Все файлы (*)"
        )

        files, _ = QFileDialog.getOpenFileNames(
            self, "Выберите файлы для муксинга", "", filter_str
        )

        if files:
            # Преобразуем строки в Path
            paths = [Path(f) for f in files]
            self.add_files(paths)

    def _remove_selected(self) -> None:
        """Удалить выбранные строки."""
        # Получаем уникальные индексы строк
        rows = sorted(
            set(index.row() for index in self.selectedIndexes()), reverse=True
        )

        for row in rows:
            self.removeRow(row)

        if rows:
            self.filesChanged.emit()

    def dragEnterEvent(self, event: QDragEnterEvent | None) -> None:
        """Обработка входа перетаскивания."""
        if event:
            mime_data = event.mimeData()
            if mime_data and mime_data.hasUrls():
                event.acceptProposedAction()
                return
            event.ignore()

    def dragMoveEvent(self, event: QDragMoveEvent | None) -> None:
        """Обработка движения перетаскивания."""
        if event:
            mime_data = event.mimeData()
            if mime_data and mime_data.hasUrls():
                event.acceptProposedAction()
                return
            event.ignore()

    def dropEvent(self, event: QDropEvent | None) -> None:
        """Обработка сброса файлов."""
        if event:
            mime_data = event.mimeData()
            if mime_data and mime_data.hasUrls():
                files = [
                    Path(url.toLocalFile())
                    for url in mime_data.urls()
                    if url.isLocalFile()
                ]
                self.add_files(files)
                event.acceptProposedAction()
                return
            event.ignore()

    def add_files(self, files: list[Path]) -> None:
        """Добавить файлы в таблицу с умной группировкой."""
        files.sort(
            key=lambda p: NaturalSortTableWidgetItem._natural_key(p.name)
        )

        sorting_enabled = self.isSortingEnabled()
        self.setSortingEnabled(False)

        for file_path in files:
            if not file_path.is_file():
                continue
            self._add_single_file(file_path)

        self.setSortingEnabled(sorting_enabled)
        if sorting_enabled and (header := self.horizontalHeader()):
            self.sortItems(
                header.sortIndicatorSection(), header.sortIndicatorOrder()
            )

        self.filesChanged.emit()

    def _add_single_file(self, file_path: Path) -> None:
        """Добавить один файл в соответствующую колонку таблицы."""
        ext = file_path.suffix.lower()
        if ext in VIDEO_CONTAINERS:
            col = self.COL_VIDEO
        elif ext in AUDIO_EXTENSIONS:
            col = self.COL_AUDIO
        elif ext in SUBTITLE_EXTENSIONS:
            col = self.COL_SUBS
        else:
            logger.warning(
                "Неизвестный формат файла для муксинга: %s", file_path
            )
            return

        row = self._find_row_by_stem(file_path.stem)
        if row is None:
            row = self.rowCount()
            self.insertRow(row)

        item = NaturalSortTableWidgetItem(file_path.name)
        item.setToolTip(str(file_path))
        item.setData(Qt.ItemDataRole.UserRole, str(file_path))
        self.setItem(row, col, item)

    def _find_row_by_stem(self, stem: str) -> int | None:
        """Найти индекс строки, содержащей файл с указанным stem.

        Проверяет все колонки во всех строках.
        Warning: При включенной сортировке индексы меняются,
        но этот метод итерирует по текущему состоянию.
        """
        for row in range(self.rowCount()):
            for col in range(self.columnCount()):
                item = self.item(row, col)
                if item:
                    path_str = item.data(Qt.ItemDataRole.UserRole)
                    if path_str:
                        path_stem = Path(path_str).stem
                        if path_stem == stem:
                            return row
        return None

    def get_tasks(self) -> list[dict[str, Path | None]]:
        """Получить список задач на муксинг.

        Returns:
            Список словарей с путями:
            [
                {
                    "video": Path(...),
                    "audio": Path(...) | None,
                    "subs": Path(...) | None
                },
                ...
            ]
        """
        tasks = []
        for row in range(self.rowCount()):
            video_item = self.item(row, self.COL_VIDEO)
            audio_item = self.item(row, self.COL_AUDIO)
            subs_item = self.item(row, self.COL_SUBS)

            video_path = (
                Path(video_item.data(Qt.ItemDataRole.UserRole))
                if video_item
                else None
            )
            audio_path = (
                Path(audio_item.data(Qt.ItemDataRole.UserRole))
                if audio_item
                else None
            )
            subs_path = (
                Path(subs_item.data(Qt.ItemDataRole.UserRole))
                if subs_item
                else None
            )

            # Для задачи обязательно нужно видео
            if video_path:
                tasks.append(
                    {
                        "video": video_path,
                        "audio": audio_path,
                        "subs": subs_path,
                    }
                )

        return tasks

    def clear_all(self):
        """Очистить таблицу."""
        self.setRowCount(0)
        self.filesChanged.emit()
        # self._stems.clear() # Удалено

    def get_file_paths(self) -> list[Path]:
        """Получить плоский список всех файлов из таблицы.

        Используется для совместимости с интерфейсом AbstractScript. execute.
        """
        files = []
        tasks = self.get_tasks()
        for task in tasks:
            if task["video"]:
                files.append(task["video"])
            if task["audio"]:
                files.append(task["audio"])
            if task["subs"]:
                files.append(task["subs"])
        return files
