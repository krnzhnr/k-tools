# -*- coding: utf-8 -*-
"""Виджет фильтрации актёров и стилей для конвертации ASS → VTT."""

import html
import logging
import re
from typing import Any
from pathlib import Path

from PyQt6.QtCore import pyqtSignal, Qt, QTimer, QRectF, QModelIndex
from PyQt6.QtGui import QColor, QTextDocument
from PyQt6.QtWidgets import (
    QWidget,
    QVBoxLayout,
    QHBoxLayout,
    QStackedWidget,
    QTreeWidgetItem,
    QStyledItemDelegate,
    QStyleOptionViewItem,
    QStyle,
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
    TreeWidget,
    SearchLineEdit,
)

from app.infrastructure.ass_parser import AssParser, AssData
from app.ui.file_list_widget import FileListWidget

logger = logging.getLogger(__name__)


class RichTextDelegate(QStyledItemDelegate):
    """Делегат для отрисовки HTML (RichText) в ячейках QTreeWidget."""

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

        # Отрисовываем HTML-текст
        doc = QTextDocument()
        # Устанавливаем шрифт из опций
        doc.setDefaultFont(options.font)
        # Получаем HTML-текст из данных
        html_text = index.data(Qt.ItemDataRole.DisplayRole)
        doc.setHtml(html_text)

        # Центрируем по вертикали
        text_rect = options.rect
        margin = (text_rect.height() - doc.size().height()) / 2
        painter.translate(text_rect.left(), text_rect.top() + margin)

        # Обрезаем контент по ширине колонки
        clip = QRectF(0, 0, text_rect.width(), text_rect.height())
        doc.drawContents(painter, clip)

        painter.restore()

    def sizeHint(self, option, index):
        options = QStyleOptionViewItem(option)
        self.initStyleOption(options, index)
        doc = QTextDocument()
        doc.setDefaultFont(options.font)
        doc.setHtml(index.data(Qt.ItemDataRole.DisplayRole))
        doc.setTextWidth(-1)  # Отключаем перенос для расчета полной ширины
        return doc.size().toSize()


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
        self._file_data: dict[Path, AssData] = {}
        self._actor_checkboxes: dict[str, CheckBox] = {}
        self._style_checkboxes: dict[str, CheckBox] = {}
        self._strip_formatting = True  # Состояние из настроек скрипта

        # Таймер для отложенного обновления предпросмотра
        self._preview_timer = QTimer(self)
        self._preview_timer.setSingleShot(True)
        self._preview_timer.setInterval(300)
        self._preview_timer.timeout.connect(self._run_preview_update)

        self._init_ui()
        logger.info("Виджет фильтрации ASS инициализирован (Delegate UI)")

    def set_strip_formatting(self, enabled: bool) -> None:
        """Обновить состояние удаления тегов и перезагрузить предпросмотр.

        Args:
            enabled: Флаг удаления тегов.
        """
        if self._strip_formatting != enabled:
            self._strip_formatting = enabled
            logger.debug("Предпросмотр: удаление тегов = %s", enabled)
            self._schedule_preview_update()

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

        # Кнопка загрузки фильтров
        btn_layout = QHBoxLayout()
        btn_layout.setContentsMargins(0, 0, 0, 0)

        self._load_btn = PushButton(
            FluentIcon.SYNC,
            "Просканировать файлы на актёров и стили",
            self,
        )
        self._load_btn.clicked.connect(self._on_load_filters_clicked)
        btn_layout.addWidget(self._load_btn)
        btn_layout.addStretch(1)
        layout.addLayout(btn_layout)

        # Карточка с контентом
        self._content_card = CardWidget(self)
        self._content_card.setVisible(False)
        content_layout = QVBoxLayout(self._content_card)
        content_layout.setContentsMargins(16, 12, 16, 12)
        content_layout.setSpacing(8)

        # SegmentedWidget с вкладками
        self._segmented = SegmentedWidget(self._content_card)
        self._segmented.addItem("actors", "Актёры", self._on_tab_changed)
        self._segmented.addItem("styles", "Стили", self._on_tab_changed)
        self._segmented.addItem(
            "preview", "Предпросмотр", self._on_tab_changed
        )
        content_layout.addWidget(self._segmented)

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
        self._search_box.setPlaceholderText("Поиск...")
        self._search_box.textChanged.connect(self._schedule_preview_update)
        preview_header.addWidget(self._search_box)
        preview_layout.addLayout(preview_header)

        self._preview_tree = TreeWidget(self._preview_panel)
        self._preview_tree.setHeaderLabels(
            ["Статус", "Время", "Актёр/Стиль", "Изменения (ASS)"]
        )

        # Настройка растягивания колонок (4 колонки + скролл)
        from PyQt6.QtWidgets import QHeaderView

        header = self._preview_tree.header()
        header.setSectionResizeMode(0, QHeaderView.ResizeMode.Fixed)
        header.setSectionResizeMode(1, QHeaderView.ResizeMode.ResizeToContents)
        header.setSectionResizeMode(2, QHeaderView.ResizeMode.ResizeToContents)
        header.setSectionResizeMode(3, QHeaderView.ResizeMode.ResizeToContents)
        header.setStretchLastSection(False)

        self._preview_tree.setColumnWidth(0, 200)

        # Включаем горизонтальный скролл
        self._preview_tree.setHorizontalScrollBarPolicy(
            Qt.ScrollBarPolicy.ScrollBarAsNeeded
        )
        self._preview_tree.setHorizontalScrollMode(
            TreeWidget.ScrollMode.ScrollPerPixel
        )

        # Устанавливаем делегат для 4-й колонки (индекс 3)
        self._preview_tree.setItemDelegateForColumn(
            3, RichTextDelegate(self._preview_tree)
        )

        preview_layout.addWidget(self._preview_tree)
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
            self._schedule_preview_update()

    def _on_files_changed(self) -> None:
        self._file_data.clear()
        self._content_card.setVisible(False)
        self.filesChanged.emit()

    def _on_load_filters_clicked(self) -> None:
        paths = self._file_list.get_file_paths()
        if not paths:
            return

        self._file_data.clear()
        all_actors: set[str] = set()
        all_styles: set[str] = set()
        for p in paths:
            try:
                data = self._parser.parse(p)
                self._file_data[p] = data
                all_actors.update(d.actor for d in data.dialogues if d.actor)
                all_styles.update(d.style for d in data.dialogues if d.style)
            except Exception:
                logger.exception("Ошибка загрузки '%s'", p.name)

        self._update_checkboxes(
            all_actors, self._actor_checkboxes, self._actors_flow, "актёр"
        )
        self._update_checkboxes(
            all_styles, self._style_checkboxes, self._styles_flow, "стиль"
        )
        self._content_card.setVisible(True)
        self._segmented.setCurrentItem("actors")
        self._schedule_preview_update()

    def _update_checkboxes(self, values, storage, flow, label) -> None:
        prev = {n: cb.isChecked() for n, cb in storage.items()}
        for cb in storage.values():
            flow.removeWidget(cb)
            cb.deleteLater()
        storage.clear()
        for name in sorted(values):
            cb = CheckBox(name, flow.parent())
            cb.setChecked(prev.get(name, False))
            # QFluentWidgets: используем проверенные сигналы
            cb.checkStateChanged.connect(self._schedule_preview_update)
            flow.addWidget(cb)
            storage[name] = cb

    def _schedule_preview_update(self) -> None:
        self._preview_timer.start()

    def _run_preview_update(self) -> None:
        if not self._content_card.isVisible():
            return
        self._preview_tree.clear()
        excluded_actors = set(self.get_excluded_actors())
        excluded_styles = set(self.get_excluded_styles())
        search_text = self._search_box.text().lower()

        for path, data in self._file_data.items():
            file_item = QTreeWidgetItem([path.name])
            file_item.setIcon(0, FluentIcon.FOLDER.icon())
            added_any = False

            for d in data.dialogues:
                is_excluded = (
                    d.actor in excluded_actors or d.style in excluded_styles
                )
                original_text = d.text

                # Если удаление тегов выключено, считаем что текст "не
                # меняется" (хотя ASS теги в VTT не приветствуются, мы
                # показываем выбор пользователя)
                if self._strip_formatting:
                    cleaned_text = self._parser.strip_tags(original_text)
                else:
                    cleaned_text = original_text

                is_changed = original_text != cleaned_text
                is_empty = not cleaned_text.strip()

                if search_text and search_text not in original_text.lower():
                    continue

                status, color_hex, is_deleted = "", "", False
                if is_excluded:
                    status, color_hex, is_deleted = (
                        "УДАЛЕНО (Фильтр)",
                        "#ff4d4f",
                        True,
                    )
                elif is_empty:
                    # Пустой строка считается только если МЫ ее очищаем.
                    # Если пользователь оставил теги и строка не пуста -
                    # она не удалится.
                    status, color_hex, is_deleted = (
                        "УДАЛЕНО (Пусто)",
                        "#ffa940",
                        True,
                    )
                elif is_changed:
                    status, color_hex = "ИЗМЕНЕНО", "#1890ff"
                else:
                    # Если изменений нет и не удалено, показываем только при
                    # поиске или если это обычная строка (но дерево будет
                    # слишком большим, так что показываем только "активные"
                    # действия)
                    continue

                escaped_orig = html.escape(original_text)
                item = QTreeWidgetItem(
                    [status, d.start, f"{d.actor or '<нет>'} / {d.style}", ""]
                )
                if color_hex:
                    item.setForeground(0, QColor(color_hex))

                # Формируем HTML для отображения в 3-й колонке (делегат)
                if is_deleted:
                    html_text = (
                        f'<span style="color: {color_hex}; '
                        f'text-decoration: line-through;">'
                        f'{escaped_orig}</span>'
                    )
                elif is_changed and self._strip_formatting:
                    # Теги выделяем красным (если удаляем)
                    def _wrap_tag(match: Any) -> str:
                        res = html.escape(match.group(0))
                        return (
                            f'<span style="color: #ff3333; '
                            f'text-decoration: line-through;">{res}</span>'
                        )

                    highlighted = re.sub(r"\{[^}]*\}", _wrap_tag, escaped_orig)
                    highlighted = highlighted.replace(
                        "\\N", '<span style="color: #ff3333;">\\N</span>'
                    ).replace(
                        "\\n", '<span style="color: #ff3333;">\\n</span>'
                    )
                    html_text = highlighted
                else:
                    html_text = escaped_orig

                item.setText(3, html_text)

                # Обновленный ToolTip для удаленных строк
                if is_deleted:
                    item.setToolTip(3, "Результат VTT: <СТРОКА БУДЕТ УДАЛЕНА>")
                else:
                    item.setToolTip(3, f"Результат VTT:\n{cleaned_text}")

                file_item.addChild(item)
                added_any = True

            if added_any:
                self._preview_tree.addTopLevelItem(file_item)
                # Корректный вызов для QTreeWidget: индекс строки
                # и пустой QModelIndex для верхнего уровня
                self._preview_tree.setFirstColumnSpanned(
                    self._preview_tree.indexOfTopLevelItem(file_item),
                    QModelIndex(),
                    True
                )
                file_item.setExpanded(True)

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
