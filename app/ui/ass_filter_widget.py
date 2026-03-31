# -*- coding: utf-8 -*-
"""Виджет фильтрации актёров и стилей для конвертации ASS → VTT."""

import html
import logging
import re
from typing import Any, no_type_check
from pathlib import Path

from PyQt6.QtCore import (
    pyqtSignal,
    Qt,
    QModelIndex,
    QAbstractItemModel,
    QThread,
    QSortFilterProxyModel,
)
from functools import lru_cache
from PyQt6.QtGui import QColor, QBrush, QStaticText
from PyQt6.QtWidgets import (
    QWidget,
    QVBoxLayout,
    QHBoxLayout,
    QStackedWidget,
    QStyledItemDelegate,
    QStyleOptionViewItem,
    QStyle,
    QMainWindow,
    QTreeView,
)
from qfluentwidgets import (
    CardWidget,
    CheckBox,
    FluentIcon,
    PushButton,
    StrongBodyLabel,
    CaptionLabel,
    FlowLayout,
    SegmentedWidget,
    SearchLineEdit,
    ToolButton,
    IndeterminateProgressBar,
    InfoBar,
    InfoBarPosition,
    TreeView,
)

from app.infrastructure.ass_parser import AssParser, AssData, AssDialogue
from app.ui.file_list_widget import FileListWidget

logger = logging.getLogger(__name__)


class RichTextDelegate(QStyledItemDelegate):
    """Делегат для отрисовки HTML (RichText) в ячейках QTreeWidget."""

    def __init__(self, parent=None):
        super().__init__(parent)

    @staticmethod
    @lru_cache(maxsize=3000)
    def _get_static_text(html_text: str) -> QStaticText:
        from PyQt6.QtCore import Qt
        from PyQt6.QtGui import QTextOption

        st = QStaticText(html_text)

        opt = QTextOption()
        opt.setWrapMode(QTextOption.WrapMode.NoWrap)
        st.setTextOption(opt)

        st.setTextFormat(Qt.TextFormat.RichText)
        return st

    def paint(self, painter, option, index):
        options = QStyleOptionViewItem(option)
        self.initStyleOption(options, index)

        painter.save()

        # Отрисовываем фон и выделение (стандартно)
        options.text = ""
        style = options.widget.style() if options.widget else None
        if style:
            style.drawControl(
                QStyle.ControlElement.CE_ItemViewItem, options, painter
            )

        from PyQt6.QtCore import Qt, QPointF

        html_text = index.data(Qt.ItemDataRole.DisplayRole)

        # Используем предкомпилированный статический текст
        st = self._get_static_text(html_text)
        painter.setFont(options.font)

        # Центрируем по вертикали
        text_rect = options.rect
        margin = (text_rect.height() - st.size().height()) / 2

        # Жестко обрезаем текст по размеру колонки, чтобы длинный текст
        # не вылезал на соседние ячейки
        painter.setClipRect(text_rect)

        # Рисуем предкомпилированный текст (в сотни раз быстрее QTextDocument)
        painter.drawStaticText(
            QPointF(text_rect.left(), text_rect.top() + margin), st
        )

        painter.restore()

    def sizeHint(self, option, index):
        # ОЧЕНЬ ВАЖНО: Никакого расчета HTML для получения высоты строки.
        # Это убивает производительность (10 FPS) при скроллинге.
        # Все строки в таблице будут фиксированной высоты (например, 36px)
        # под две линии текста. Ширина -1 отдает управление Layout.
        from PyQt6.QtCore import QSize

        return QSize(200, 36)


class ParseWorker(QThread):
    """Поток для фонового парсинга ASS-файлов и визуальных данных."""

    progress = pyqtSignal(int, int)
    finished = pyqtSignal(dict, dict)

    def __init__(self, paths: list[Path], parser: AssParser):
        super().__init__()
        self.paths = paths
        self.parser = parser

    def run(self) -> None:
        """Запуск процесса парсинга."""
        file_data: dict[Path, AssData] = {}
        visual_cache: dict[Path, list[dict[str, Any]]] = {}

        total = len(self.paths)
        for i, path in enumerate(self.paths):
            try:
                data = self.parser.parse(path)
                file_data[path] = data

                # Пре-расчет визуальных данных для каждой строки
                cache_list = []
                for d in data.dialogues:
                    orig_text = d.text
                    clean_text = self.parser.strip_tags(orig_text)

                    cache_list.append(
                        {
                            "original": orig_text,
                            "clean": clean_text,
                            "is_changed": orig_text != clean_text,
                            "is_empty": not clean_text.strip(),
                        }
                    )
                visual_cache[path] = cache_list

            except Exception:
                logger.exception("Ошибка фонового парсинга '%s'", path.name)

            self.progress.emit(i + 1, total)

        self.finished.emit(file_data, visual_cache)


class AssPreviewModel(QAbstractItemModel):
    """Модель данных для виртуального древовидного списка предпросмотра.

    Иерархия: Корень -> Файлы -> Строки диалогов.
    """

    _TAG_PATTERN = re.compile(r"\{[^}]*\}")

    def __init__(self, parent: QWidget | None = None):
        super().__init__(parent)
        self._files: list[Path] = []
        self._file_data: dict[Path, AssData] = {}
        self._visual_cache: dict[Path, list[dict[str, Any]]] = {}
        self._excluded_actors: set[str] = set()
        self._excluded_styles: set[str] = set()
        self._manual_exclusions: dict[Path, set[int]] = {}
        self._strip_formatting = True
        self._headers = ["Статус", "Время", "Актёр/Стиль", "Изменения (ASS)"]
        self._folder_icon = FluentIcon.FOLDER.icon()

    def update_data(
        self,
        file_data: dict[Path, AssData],
        visual_cache: dict[Path, list[dict[str, Any]]],
    ) -> None:
        """Обновить данные модели."""
        self.beginResetModel()
        self._file_data = file_data
        self._visual_cache = visual_cache
        self._files = sorted(list(file_data.keys()))
        self.endResetModel()

    def set_filters(
        self,
        excluded_actors: list[str],
        excluded_styles: list[str],
        strip_formatting: bool,
    ) -> None:
        """Обновить фильтры и уведомить об изменении данных."""
        self._excluded_actors = set(excluded_actors)
        self._excluded_styles = set(excluded_styles)
        self._strip_formatting = strip_formatting
        # При изменении фильтров нужно обновить все отображаемые данные
        self.dataChanged.emit(
            self.index(0, 0),
            self.index(self.rowCount() - 1, len(self._headers) - 1),
        )

    def set_manual_exclusions(self, manual: dict[Path, set[int]]) -> None:
        """Обновить список ручных исключений."""
        self._manual_exclusions = manual
        self.dataChanged.emit(
            self.index(0, 0),
            self.index(self.rowCount() - 1, len(self._headers) - 1),
        )

    def index(
        self, row: int, column: int, parent: QModelIndex = QModelIndex()
    ) -> QModelIndex:
        """Создать индекс для элемента."""
        if not self.hasIndex(row, column, parent):
            return QModelIndex()

        if not parent.isValid():
            # Это файл (верхний уровень)
            return self.createIndex(row, column, None)

        # Это строка диалога (вложенный уровень)
        parent_path = self._files[parent.row()]
        return self.createIndex(row, column, parent_path)

    @no_type_check
    def parent(self, index: QModelIndex) -> QModelIndex:
        """Получить родителя элемента."""
        if not index.isValid():
            return QModelIndex()

        internal_ptr = index.internalPointer()
        if not isinstance(internal_ptr, Path):
            return QModelIndex()

        # Если internalPointer - путь из списка файлов,
        # значит родитель - файл
        if internal_ptr in self._files:
            row = self._files.index(internal_ptr)
            return self.createIndex(row, 0, None)

        return QModelIndex()

    def rowCount(self, parent: QModelIndex = QModelIndex()) -> int:
        """Количество строк."""
        if not parent.isValid():
            return len(self._files)

        internal_ptr = parent.internalPointer()
        if internal_ptr is None:
            # Родитель - файл, возвращаем количество диалогов в нем
            path = self._files[parent.row()]
            return len(self._file_data[path].dialogues)

        return 0

    def columnCount(self, parent: QModelIndex = QModelIndex()) -> int:
        """Количество колонок."""
        return len(self._headers)

    def headerData(
        self,
        section: int,
        orientation: Qt.Orientation,
        role: int = Qt.ItemDataRole.DisplayRole,
    ) -> Any:
        """Данные заголовка."""
        if (
            orientation == Qt.Orientation.Horizontal
            and role == Qt.ItemDataRole.DisplayRole
            and section < len(self._headers)
        ):
            return self._headers[section]
        return None

    def data(
        self, index: QModelIndex, role: int = Qt.ItemDataRole.DisplayRole
    ) -> Any:
        """Получить данные для элемента."""
        if not index.isValid():
            return None

        row = index.row()
        col = index.column()
        path_ptr = index.internalPointer()

        # --- Данные для ФАЙЛА (верхний уровень) ---
        if path_ptr is None:
            path = self._files[row]
            if role == Qt.ItemDataRole.DisplayRole:
                if col == 0:
                    count = len(self._file_data[path].dialogues)
                    return f"{path.name} ({count} строк)"
                return ""
            if role == Qt.ItemDataRole.DecorationRole and col == 0:
                # Использование закэшированной иконки
                return self._folder_icon
            return None

        # --- Данные для СТРОКИ ДИАЛОГА (вложенный уровень) ---
        path = path_ptr
        data_list = self._file_data[path].dialogues
        if row >= len(data_list):
            return None
        d = data_list[row]
        v = self._visual_cache[path][row]

        is_excluded = (
            d.actor in self._excluded_actors
            or d.style in self._excluded_styles
        )
        is_manually_excluded = (
            path in self._manual_exclusions
            and row in self._manual_exclusions[path]
        )

        if role == Qt.ItemDataRole.CheckStateRole and col == 0:
            return (
                Qt.CheckState.Unchecked
                if is_excluded or is_manually_excluded
                else Qt.CheckState.Checked
            )

        if role == Qt.ItemDataRole.ForegroundRole:
            if is_excluded or is_manually_excluded:
                return QBrush(QColor("#ff4d4f"))
            if v["is_empty"]:
                return QBrush(QColor("#ffa940"))
            if v["is_changed"]:
                color = (
                    QColor("#1890ff")
                    if self._strip_formatting
                    else QColor("#722ed1")
                )
                return QBrush(color)
            return None

        if role == Qt.ItemDataRole.ToolTipRole and col == 3:
            if is_excluded or is_manually_excluded:
                return "Результат VTT: <СТРОКА БУДЕТ УДАЛЕНА>"
            if v["is_empty"]:
                return "Результат VTT: <ПУСТАЯ СТРОКА>"
            text = v["clean"] if self._strip_formatting else v["original"]
            return f"Результат VTT:\n{text}"

        if role == Qt.ItemDataRole.DisplayRole:
            if col == 0:
                if is_excluded:
                    return "УДАЛЕНО (Фильтр)"
                if is_manually_excluded:
                    return "УДАЛЕНО (Вручную)"
                if v["is_empty"]:
                    return "УДАЛЕНО (Пусто)"
                if v["is_changed"]:
                    return "ИЗМЕНЕНО" if self._strip_formatting else "Очистка"
                return "ОК"
            if col == 1:
                return d.start
            if col == 2:
                return f"{d.actor or '<нет>'} / {d.style}"
            if col == 3:
                # Делегат использует DisplayRole для HTML
                return self._get_html_text(
                    d, v, is_excluded or is_manually_excluded, path, row
                )

        return None

    def _get_html_text(
        self, d: AssDialogue, v: dict, is_deleted: bool, path: Path, row: int
    ) -> str:
        """Подготовить HTML-текст с кэшированием."""
        # Ключ кэша отражает текущее визуальное состояние ячейки
        cache_key = f"html_del:{is_deleted}_strip:{self._strip_formatting}"

        if cache_key in v:
            return v[cache_key]

        orig = html.escape(d.original if hasattr(d, "original") else d.text)
        if is_deleted:
            style = 'style="color: #ff4d4f; text-decoration: line-through;"'
            res = f'<span {style}>{orig}</span>'
            v[cache_key] = res
            return res

        if v["is_changed"]:

            def _wrap_tag(match: Any) -> str:
                res = html.escape(match.group(0))
                color = "#ff3333" if self._strip_formatting else "#9254de"
                st = f'style="color: {color}; text-decoration: line-through;"'
                return f'<span {st}>{res}</span>'

            highlighted = self._TAG_PATTERN.sub(_wrap_tag, orig)
            line_break_color = (
                "#ff3333" if self._strip_formatting else "#9254de"
            )
            style = f'style="color: {line_break_color};"'
            res = highlighted.replace(
                "\\N", f'<span {style}>\\N</span>'
            ).replace(
                "\\n", f'<span {style}>\\n</span>'
            )
            v[cache_key] = res
            return res

        v[cache_key] = orig
        return orig

    def flags(self, index: QModelIndex) -> Qt.ItemFlag:
        """Флаги элемента."""
        if not index.isValid():
            return Qt.ItemFlag.NoItemFlags

        flags = Qt.ItemFlag.ItemIsEnabled | Qt.ItemFlag.ItemIsSelectable
        if index.internalPointer() is not None and index.column() == 0:
            # Чекбоксы только для строк диалогов
            flags |= Qt.ItemFlag.ItemIsUserCheckable
        return flags

    def setData(
        self,
        index: QModelIndex,
        value: Any,
        role: int = Qt.ItemDataRole.EditRole,
    ) -> bool:
        """Изменить данные (для чекбоксов)."""
        if (
            index.isValid()
            and role == Qt.ItemDataRole.CheckStateRole
            and index.column() == 0
        ):
            path = index.internalPointer()
            if path is None:
                return False

            row = index.row()
            is_checked = value == Qt.CheckState.Checked

            if is_checked:
                if path in self._manual_exclusions:
                    self._manual_exclusions[path].discard(row)
            else:
                if path not in self._manual_exclusions:
                    self._manual_exclusions[path] = set()
                self._manual_exclusions[path].add(row)

            self.dataChanged.emit(
                index, index, [Qt.ItemDataRole.CheckStateRole]
            )
            return True
        return False


class AssPreviewProxyModel(QSortFilterProxyModel):
    """Прокси-модель для фильтрации предпросмотра по тексту."""

    def __init__(self, parent: QWidget | None = None):
        super().__init__(parent)
        self.setRecursiveFilteringEnabled(True)
        self.setFilterCaseSensitivity(Qt.CaseSensitivity.CaseInsensitive)
        self.setFilterKeyColumn(3)  # Фильтруем по 4-й колонке (текст)

    def filterAcceptsRow(
        self, source_row: int, source_parent: QModelIndex
    ) -> bool:
        """Определить, должна ли строка быть видимой."""
        # Если это строка диалога
        if source_parent.isValid():
            return super().filterAcceptsRow(source_row, source_parent)

        # Если это файл (верхний уровень)
        # Мы принимаем файл по имени или если любой его потомок принят.
        # QSortFilterProxyModel с setRecursiveFilteringEnabled(True)
        # делает это почти сам, но нам нужно также проверять название
        # файла в 1-й колонке (индекс 0).

        # Проверяем название файла (колонка 0)
        source_model = self.sourceModel()
        if source_model is None:
            return False

        idx = source_model.index(source_row, 0, source_parent)
        file_name = source_model.data(idx, Qt.ItemDataRole.DisplayRole)

        re_pattern = self.filterRegularExpression()
        if re_pattern.isValid() and re_pattern.match(file_name).hasMatch():
            return True

        # Если имя файла не совпало, проверяем, есть ли подходящие дети
        return super().filterAcceptsRow(source_row, source_parent)


class ExternalFilterWindow(QMainWindow):
    """Окно предпросмотра и настройки фильтров."""

    closed = pyqtSignal()

    def __init__(self, content_widget: QWidget, parent: QWidget | None = None):
        super().__init__(parent)
        self.setWindowFlags(Qt.WindowType.Window)
        self.content_widget = content_widget

        # QMainWindow оптимизирован для работы как Top-Level окно
        self.setCentralWidget(self.content_widget)

        # Задаем непрозрачный фон для избежания лагов композитора
        from qfluentwidgets import isDarkTheme

        bg_col = "#202020" if isDarkTheme() else "#f3f3f3"
        self.setStyleSheet(
            f"ExternalFilterWindow, QMainWindow {{"
            f" background-color: {bg_col}; }}"
        )

        self.setWindowTitle("Настройка фильтров и предпросмотр")
        self.resize(1000, 800)

        # Центрируем на экране
        from PyQt6.QtWidgets import QApplication

        screen = QApplication.primaryScreen()
        if screen:
            geo = screen.availableGeometry()
            self.move(
                (geo.width() - self.width()) // 2,
                (geo.height() - self.height()) // 2,
            )

    def closeEvent(self, event):
        self.closed.emit()
        super().closeEvent(event)


class AssFilterWidget(QWidget):
    """Виджет для управления файлами ASS и фильтрации.

    Объединяет FileListWidget, панель фильтров (Актёры/Стили)
    и живой предпросмотр изменений.
    """

    filesChanged = pyqtSignal()

    def __init__(
        self,
        parent: QWidget | None = None,
    ) -> None:
        """Инициализация виджета фильтрации.

        Args:
            parent: Родительский виджет.
        """
        super().__init__(parent)
        self._parser = AssParser()
        self._actor_checkboxes: dict[str, CheckBox] = {}
        self._style_checkboxes: dict[str, CheckBox] = {}
        self._strip_formatting = True
        self._manual_exclusions: dict[Path, set[int]] = {}
        self._detached_window: ExternalFilterWindow | None = None

        # Модель, прокси и поток парсинга
        self._model = AssPreviewModel(self)
        self._proxy = AssPreviewProxyModel(self)
        self._proxy.setSourceModel(self._model)
        self._parse_worker: ParseWorker | None = None

        self._init_ui()
        logger.info("Виджет фильтрации ASS инициализирован (Model/View)")

    def set_strip_formatting(self, enabled: bool) -> None:
        """Обновить состояние удаления тегов и перезагрузить предпросмотр.

        Args:
            enabled: Флаг удаления тегов.
        """
        if self._strip_formatting != enabled:
            self._strip_formatting = enabled
            logger.debug("Предпросмотр: удаление тегов = %s", enabled)
            self._model.set_filters(
                self.get_excluded_actors(),
                self.get_excluded_styles(),
                self._strip_formatting,
            )

    def _init_ui(self) -> None:
        """Инициализация пользовательского интерфейса."""
        layout = QVBoxLayout(self)
        layout.setContentsMargins(0, 0, 0, 0)
        layout.setSpacing(12)

        # Список файлов
        self._file_list = FileListWidget(
            allowed_extensions=[".ass", ".ssa"],
            context_name="ASS → VTT",
            parent=self,
        )
        self._file_list.filesChanged.connect(self._on_files_changed)
        layout.addWidget(self._file_list, stretch=1)

        # Кнопка загрузки фильтров + Прогресс-бар
        btn_layout = QHBoxLayout()
        btn_layout.setContentsMargins(0, 0, 0, 0)
        btn_layout.setSpacing(16)

        self._load_btn = PushButton(
            FluentIcon.SYNC,
            "Просканировать файлы на актёров и стили",
            self,
        )
        self._load_btn.clicked.connect(self._on_load_filters_clicked)
        btn_layout.addWidget(self._load_btn)

        self._progress_bar = IndeterminateProgressBar(self)
        self._progress_bar.setVisible(False)
        self._progress_bar.setFixedWidth(200)
        btn_layout.addWidget(self._progress_bar)

        btn_layout.addStretch(1)
        layout.addLayout(btn_layout)

        # Карточка с контентом
        self._content_card = CardWidget(self)
        self._content_card.setVisible(False)
        content_layout = QVBoxLayout(self._content_card)
        content_layout.setContentsMargins(16, 12, 16, 12)
        content_layout.setSpacing(8)

        # SegmentedWidget с вкладками
        tab_header_layout = QHBoxLayout()
        tab_header_layout.setContentsMargins(0, 0, 0, 0)

        self._segmented = SegmentedWidget(self._content_card)
        from PyQt6.QtWidgets import QSizePolicy

        self._segmented.setSizePolicy(
            QSizePolicy.Policy.Expanding, QSizePolicy.Policy.Fixed
        )

        self._segmented.addItem("actors", "Актёры", self._on_tab_changed)
        self._segmented.addItem("styles", "Стили", self._on_tab_changed)
        self._segmented.addItem(
            "preview", "Предпросмотр", self._on_tab_changed
        )
        tab_header_layout.addWidget(self._segmented, 1)

        self._expand_btn = ToolButton(
            FluentIcon.FULL_SCREEN, self._content_card
        )
        self._expand_btn.setToolTip("Открыть в отдельном окне")
        self._expand_btn.clicked.connect(self._on_expand_clicked)
        tab_header_layout.addWidget(self._expand_btn)

        content_layout.addLayout(tab_header_layout)

        # Стек панелей
        self._stack = QStackedWidget(self._content_card)

        # --- Панель «Актёры» ---
        self._actors_panel = self._create_filter_panel(
            "Исключить актёров",
            "Выбранные актёры будут удалены целиком",
            self._on_select_all_actors,
            self._on_deselect_all_actors,
        )
        self._stack.addWidget(self._actors_panel)
        self._actors_flow = self._actors_panel.findChild(FlowLayout)

        # --- Панель «Стили» ---
        self._styles_panel = self._create_filter_panel(
            "Исключить стили",
            "Строки с данными стилями будут удалены целиком",
            self._on_select_all_styles,
            self._on_deselect_all_styles,
        )
        self._stack.addWidget(self._styles_panel)
        self._styles_flow = self._styles_panel.findChild(FlowLayout)

        # --- Панель «Предпросмотр» ---
        self._preview_panel = QWidget()
        preview_layout = QVBoxLayout(self._preview_panel)
        preview_layout.setContentsMargins(0, 8, 0, 0)
        preview_layout.setSpacing(8)

        preview_header = QHBoxLayout()
        preview_header.addWidget(
            StrongBodyLabel("Изменения (Наведите для VTT)")
        )

        self._search_box = SearchLineEdit(self._preview_panel)
        self._search_box.setPlaceholderText(
            "Поиск по тексту или названию файла..."
        )
        self._search_box.textChanged.connect(self._on_search_text_changed)
        preview_header.addWidget(self._search_box)
        preview_layout.addLayout(preview_header)

        # Возвращаем TreeView, но "убиваем" лагающий сглаживатель скролла
        self._preview_view = TreeView(self._preview_panel)

        # моментальную прокрутку колесиком мыши, игнорируя сломанную анимацию
        self._preview_view.wheelEvent = lambda e: QTreeView.wheelEvent(
            self._preview_view, e
        )

        self._preview_view.setModel(self._proxy)

        # Делегат для 4-й колонки (индекс 3)
        self._preview_view.setItemDelegateForColumn(
            3, RichTextDelegate(self._preview_view)
        )

        # Растягивание колонок
        # Это заставляет Qt рассчитывать ширину для тысяч строк,
        # что роняет FPS до 10.
        header = self._preview_view.header()
        from PyQt6.QtWidgets import QHeaderView

        header.setSectionResizeMode(0, QHeaderView.ResizeMode.Interactive)
        header.setSectionResizeMode(1, QHeaderView.ResizeMode.Interactive)
        header.setSectionResizeMode(2, QHeaderView.ResizeMode.Interactive)
        header.setSectionResizeMode(3, QHeaderView.ResizeMode.Interactive)
        header.setStretchLastSection(False)

        self._preview_view.setColumnWidth(0, 160)
        self._preview_view.setColumnWidth(1, 140)
        self._preview_view.setColumnWidth(2, 180)
        self._preview_view.setColumnWidth(3, 800)

        # Экстремальная оптимизация QTreeView:
        # Указывает движку Qt, что все строки имеют одинаковую высоту,
        # благодаря чему он ВООБЩЕ перестает вызывать sizeHint для всех
        # элементов при отрисовке скроллбара.
        self._preview_view.setUniformRowHeights(True)
        self._preview_view.setAnimated(
            False
        )  # Отключаем обсчет анимаций при раскрытии
        self._preview_view.setHorizontalScrollBarPolicy(
            Qt.ScrollBarPolicy.ScrollBarAsNeeded
        )

        preview_layout.addWidget(self._preview_view)
        self._stack.addWidget(self._preview_panel)

        content_layout.addWidget(self._stack)
        layout.addWidget(self._content_card, stretch=2)

    def _create_filter_panel(self, title, hint, on_all, on_none) -> QWidget:
        panel = QWidget()
        panel.setStyleSheet("background: transparent;")
        panel_layout = QVBoxLayout(panel)
        panel_layout.setAlignment(Qt.AlignmentFlag.AlignTop)
        panel_layout.setContentsMargins(0, 8, 0, 0)
        panel_layout.setSpacing(8)

        header = QHBoxLayout()
        header.addWidget(StrongBodyLabel(title))
        header.addStretch(1)
        btn_all = PushButton("Выбрать всё", panel)
        btn_all.setFixedHeight(28)
        btn_all.clicked.connect(on_all)
        header.addWidget(btn_all)
        btn_none = PushButton("Снять всё", panel)
        btn_none.setFixedHeight(28)
        btn_none.clicked.connect(on_none)
        header.addWidget(btn_none)
        panel_layout.addLayout(header)
        panel_layout.addWidget(CaptionLabel(hint))

        flow_container = QWidget(panel)
        flow = FlowLayout(flow_container)
        flow.setContentsMargins(0, 4, 0, 0)
        flow.setSpacing(8)
        panel_layout.addWidget(flow_container)
        return panel

    def _on_tab_changed(self) -> None:
        route = self._segmented.currentRouteKey()
        if route == "actors":
            self._stack.setCurrentWidget(self._actors_panel)
        elif route == "styles":
            self._stack.setCurrentWidget(self._styles_panel)
        elif route == "preview":
            self._stack.setCurrentWidget(self._preview_panel)
            self._model.set_filters(
                self.get_excluded_actors(),
                self.get_excluded_styles(),
                self._strip_formatting,
            )

    def _on_files_changed(self) -> None:
        self._content_card.setVisible(False)
        self.filesChanged.emit()

    def _on_load_filters_clicked(self) -> None:
        paths = self._file_list.get_file_paths()
        if not paths:
            return

        # Запуск фонового парсинга
        self._load_btn.setEnabled(False)
        self._load_btn.setText("Сканирование...")
        self._progress_bar.setVisible(True)
        self._progress_bar.start()

        self._parse_worker = ParseWorker(paths, self._parser)
        self._parse_worker.finished.connect(self._on_parsing_finished)
        self._parse_worker.start()

    def _on_parsing_finished(
        self, file_data: dict, visual_cache: dict
    ) -> None:
        """Обработка результатов фонового парсинга."""
        self._load_btn.setEnabled(True)
        self._load_btn.setText("Просканировать файлы на актёров и стили")
        self._progress_bar.stop()
        self._progress_bar.setVisible(False)

        # Сбор уникальных актёров и стилей
        all_actors: set[str] = set()
        all_styles: set[str] = set()
        for data in file_data.values():
            all_actors.update(d.actor for d in data.dialogues if d.actor)
            all_styles.update(d.style for d in data.dialogues if d.style)

        self._update_checkboxes(
            all_actors, self._actor_checkboxes, self._actors_flow, "актёр"
        )
        self._update_checkboxes(
            all_styles, self._style_checkboxes, self._styles_flow, "стиль"
        )

        # Обновление модели
        self._model.update_data(file_data, visual_cache)

        self._content_card.setVisible(True)
        self._segmented.setCurrentItem("actors")

        InfoBar.success(
            title="Сканирование завершено",
            content=f"Обработано файлов: {len(file_data)}",
            parent=self,
            position=InfoBarPosition.TOP,
            duration=3000,
        )

    def _update_checkboxes(self, values, storage, flow, label) -> None:
        prev = {n: cb.isChecked() for n, cb in storage.items()}
        for cb in storage.values():
            flow.removeWidget(cb)
            cb.deleteLater()
        storage.clear()
        for name in sorted(values):
            cb = CheckBox(name, flow.parent())
            cb.setChecked(prev.get(name, False))
            cb.checkStateChanged.connect(self._on_filters_changed)
            flow.addWidget(cb)
            storage[name] = cb

    def _on_filters_changed(self) -> None:
        """Обновить фильтры в модели."""
        self._model.set_filters(
            self.get_excluded_actors(),
            self.get_excluded_styles(),
            self._strip_formatting,
        )

    def _on_search_text_changed(self, text: str) -> None:
        """Обработка изменения текста в поиске."""
        self._proxy.setFilterFixedString(text)
        if text:
            self._preview_view.expandAll()

    def _on_select_all_actors(self):
        for cb in self._actor_checkboxes.values():
            cb.setChecked(True)

    def _on_deselect_all_actors(self):
        for cb in self._actor_checkboxes.values():
            cb.setChecked(False)

    def _on_select_all_styles(self):
        for cb in self._style_checkboxes.values():
            cb.setChecked(True)

    def _on_deselect_all_styles(self):
        for cb in self._style_checkboxes.values():
            cb.setChecked(False)

    def get_file_paths(self) -> list[Path]:
        return self._file_list.get_file_paths()

    def get_excluded_actors(self) -> list[str]:
        return [
            n for n, cb in self._actor_checkboxes.items() if cb.isChecked()
        ]

    def get_excluded_styles(self) -> list[str]:
        return [
            n for n, cb in self._style_checkboxes.items() if cb.isChecked()
        ]

    def get_manual_exclusions(self) -> dict[str, list[int]]:
        """Получить список вручную исключенных строк для каждого файла.

        Returns:
            Словарь {путь_к_файлу: [список_индексов]}.
        """
        return {
            str(path): sorted(list(indices))
            for path, indices in self._model._manual_exclusions.items()
        }

    def _on_expand_clicked(self) -> None:
        """Обработчик нажатия на кнопку расширения окна."""
        if self._detached_window:
            self._detached_window.activateWindow()
            return

        # Пытаемся найти текущий индекс в макете
        layout = self.layout()
        if not layout:
            return

        self._content_card.setParent(None)

        # Передаем главное окно как родителя для того, чтобы
        # отсоединенное окно использовало общий контекст аппаратного ускорения
        main_win = self.window()
        self._detached_window = ExternalFilterWindow(
            self._content_card, main_win
        )
        self._detached_window.closed.connect(self._on_window_closed)
        self._detached_window.show()

        logger.info("Панель фильтров вынесена в отдельное окно")

    def _on_window_closed(self) -> None:
        """Обработчик закрытия внешнего окна."""
        if not self._detached_window:
            return

        layout = self.layout()
        if layout and hasattr(layout, "addWidget"):
            # Возвращаем в конец основного макета
            layout.addWidget(self._content_card)

        self._content_card.setVisible(True)
        self._detached_window = None
        logger.info("Панель фильтров возвращена в основное окно")
