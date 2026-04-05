# -*- coding: utf-8 -*-
"""Виджет массового извлечения дорожек с умными правилами."""

import logging
from pathlib import Path
from typing import Dict, List, Set, Any

from PyQt6.QtCore import Qt, pyqtSignal
from PyQt6.QtWidgets import (
    QHBoxLayout,
    QTreeWidgetItem,
    QVBoxLayout,
    QWidget,
    QStackedWidget,
)
from qfluentwidgets import (
    CardWidget,
    FluentIcon,
    PrimaryPushButton,
    StrongBodyLabel,
    TreeWidget,
    SegmentedWidget,
    CheckBox,
    FlowLayout,
)

try:
    from qfluentwidgets import InfoBadge, InfoBadgePosition
except ImportError:
    InfoBadge = None
    InfoBadgePosition = None

from app.infrastructure.mkvprobe_runner import MKVProbeRunner, TrackInfo
from app.ui.file_list_widget import FileListWidget
from app.core.constants import MEDIA_CONTAINERS

logger = logging.getLogger(__name__)


class TrackExtractWidget(QWidget):
    """Виджет массового извлечения дорожек с умными правилами и разделенным UI."""  # noqa: E501

    filesChanged = pyqtSignal()

    # Роли для хранения данных в QTreeWidgetItem
    ROLE_TRACK_ID = Qt.ItemDataRole.UserRole
    ROLE_FILE_PATH = Qt.ItemDataRole.UserRole + 1
    ROLE_TRACK_TYPE = Qt.ItemDataRole.UserRole + 2

    # Структура данных дорожки для динамических фильтров
    ROLE_TRACK_DATA = Qt.ItemDataRole.UserRole + 3

    def __init__(self, parent: QWidget | None = None) -> None:
        """Инициализация виджета."""
        super().__init__(parent)
        self._probe = MKVProbeRunner()
        self._file_tracks: Dict[Path, List[TrackInfo]] = {}

        # Хранение динамических опций по типам: type -> property_name -> set(values)  # noqa: E501
        self._dynamic_options: Dict[str, Dict[str, Set[str]]] = {
            "video": {
                "language": set(),
                "codec": set(),
                "resolution": set(),
                "name": set(),
            },
            "audio": {
                "language": set(),
                "codec": set(),
                "channels": set(),
                "name": set(),
            },
            "subtitles": {"language": set(), "codec": set(), "name": set()},
        }

        # Выбранные правила: type -> property_name -> set(selected_values)
        self._active_rules: Dict[str, Dict[str, Set[str]]] = {
            "video": {
                "language": set(),
                "codec": set(),
                "resolution": set(),
                "name": set(),
            },
            "audio": {
                "language": set(),
                "codec": set(),
                "channels": set(),
                "name": set(),
            },
            "subtitles": {"language": set(), "codec": set(), "name": set()},
        }

        self._badges: Dict[str, Any] = {}
        self._select_all_checkboxes: Dict[str, CheckBox] = {}

        self._init_ui()
        logger.info("Виджет массового извлечения инициализирован (Dynamic UI)")

    def _init_ui(self) -> None:
        """Настройка пользовательского интерфейса с двумя карточками."""
        layout = QVBoxLayout(self)
        layout.setContentsMargins(0, 0, 0, 0)
        layout.setSpacing(16)

        self._init_file_list(layout)
        self._init_filters_card(layout)
        self._init_tree_card(layout)

    def _init_file_list(self, layout: QVBoxLayout) -> None:
        """Инициализация списка файлов и кнопки загрузки."""
        self._file_list = FileListWidget(
            allowed_extensions=list(MEDIA_CONTAINERS),
            context_name="Демуксинг",
            parent=self,
        )
        self._file_list.filesChanged.connect(self._on_files_changed)
        layout.addWidget(self._file_list, stretch=1)

        self._load_tracks_btn = PrimaryPushButton(
            FluentIcon.SYNC, "Загрузить дорожки", self
        )
        self._load_tracks_btn.clicked.connect(self._on_load_tracks_clicked)
        layout.addWidget(self._load_tracks_btn)

    def _init_filters_card(self, layout: QVBoxLayout) -> None:
        """Инициализация панели фильтров."""
        self._filters_card = CardWidget(self)
        self._filters_card.setVisible(False)
        filters_layout = QVBoxLayout(self._filters_card)
        filters_layout.setContentsMargins(16, 16, 16, 16)
        filters_layout.setSpacing(12)

        self._segmented_widget = SegmentedWidget(self._filters_card)
        self._segmented_widget.addItem("video", "Видео", self._on_tab_changed)
        self._segmented_widget.addItem("audio", "Аудио", self._on_tab_changed)
        self._segmented_widget.addItem(
            "subtitles", "Субтитры", self._on_tab_changed
        )
        filters_layout.addWidget(self._segmented_widget)

        self._rules_stack = QStackedWidget(self._filters_card)
        self._rules_panels: Dict[str, QWidget] = {}
        self._rules_layouts: Dict[str, QVBoxLayout] = {}

        for t in ["video", "audio", "subtitles"]:
            content = QWidget()
            content.setStyleSheet("background: transparent;")
            vl = QVBoxLayout(content)
            vl.setAlignment(Qt.AlignmentFlag.AlignTop)
            vl.setSpacing(16)
            vl.setContentsMargins(0, 16, 0, 0)

            self._rules_layouts[t] = vl
            self._rules_panels[t] = content
            self._rules_stack.addWidget(content)

        filters_layout.addWidget(self._rules_stack)
        layout.addWidget(self._filters_card, stretch=0)

    def _init_tree_card(self, layout: QVBoxLayout) -> None:
        """Инициализация дерева дорожек."""
        self._tree_card = CardWidget(self)
        self._tree_card.setVisible(False)
        tree_layout = QVBoxLayout(self._tree_card)
        tree_layout.setContentsMargins(16, 16, 16, 16)

        self._tree = TreeWidget(self._tree_card)
        self._tree.setHeaderHidden(True)
        self._tree.setBorderVisible(False)
        self._tree.itemChanged.connect(self._on_tree_item_changed)
        tree_layout.addWidget(self._tree)
        layout.addWidget(self._tree_card, stretch=2)

    def _on_tab_changed(self) -> None:
        """Обработка переключения вкладки в SegmentedWidget."""
        current_route = self._segmented_widget.currentRouteKey()
        if current_route in self._rules_panels:
            self._rules_stack.setCurrentWidget(
                self._rules_panels[current_route]
            )

    def _collect_dynamic_options(self) -> None:
        """Собрать уникальные свойства дорожек для генерации чекбоксов."""
        # Очистка старых данных
        for t in self._dynamic_options:
            for k in self._dynamic_options[t]:
                self._dynamic_options[t][k].clear()
                self._active_rules[t][k].clear()

        for tracks in self._file_tracks.values():
            for tr in tracks:
                ttype = tr.track_type
                if ttype not in self._dynamic_options:
                    continue

                flags = self._dynamic_options[ttype]

                lang = (
                    tr.language
                    if (tr.language and tr.language != "und")
                    else "Неизвестный"
                )

                # Добавляем в список доступных опций по языкам
                flags["language"].add(lang)

                if tr.codec:
                    flags["codec"].add(tr.codec)

                if tr.name:
                    flags["name"].add(tr.name)

                if ttype == "video" and tr.resolution:
                    flags["resolution"].add(tr.resolution)

                if ttype == "audio" and getattr(tr, "channels", None):
                    ch = str(tr.channels)
                    flags["channels"].add(ch)

    def _build_dynamic_ui(self) -> None:
        """Перестроить чекбоксы в StackedWidget на основе собранных опций."""
        labels_map = {
            "language": "Язык",
            "codec": "Кодек",
            "resolution": "Разрешение",
            "channels": "Каналы",
            "name": "Название",
        }

        for ttype, layout in self._rules_layouts.items():
            self._clear_layout(layout)
            options = self._dynamic_options.get(ttype, {})
            has_any = False

            global_cb_all = CheckBox("Выбрать все", layout.parentWidget())
            self._select_all_checkboxes[ttype] = global_cb_all
            layout.addWidget(global_cb_all)
            all_tab_checkboxes: List[CheckBox] = []

            for prop_key, prop_label in labels_map.items():
                if prop_key not in options:
                    continue
                values = sorted(list(options[prop_key]))
                if not values:
                    continue

                has_any = True
                self._build_filter_group(
                    layout,
                    ttype,
                    prop_key,
                    prop_label,
                    values,
                    global_cb_all,
                    all_tab_checkboxes,
                )

            if has_any:
                global_cb_all.stateChanged.connect(
                    lambda state, tt=ttype, cbs=all_tab_checkboxes: self._on_select_all_changed(  # noqa: E501
                        state, tt, cbs
                    )
                )
                layout.addSpacing(8)
            else:
                global_cb_all.setVisible(False)
                layout.addWidget(
                    StrongBodyLabel(
                        "Нет доступных фильтров для данного типа", None
                    )
                )

            layout.addStretch(1)

    def _clear_layout(self, layout: QVBoxLayout) -> None:
        """Очистить layout."""
        while layout.count():
            item = layout.takeAt(0)
            if item is not None:
                widget = item.widget()
                if widget is not None:
                    widget.deleteLater()

    def _build_filter_group(
        self,
        layout: QVBoxLayout,
        ttype: str,
        prop_key: str,
        prop_label: str,
        values: List[str],
        global_cb: CheckBox,
        all_cbs: List[CheckBox],
    ) -> None:
        """Построить группу фильтров для конкретного свойства."""
        group_widget = QWidget()
        group_layout = QHBoxLayout(group_widget)
        group_layout.setContentsMargins(0, 0, 0, 0)
        group_layout.setAlignment(Qt.AlignmentFlag.AlignLeft)

        lbl = StrongBodyLabel(f"{prop_label}:", group_widget)
        lbl.setFixedWidth(100)
        group_layout.addWidget(lbl)

        flow_widget = QWidget()
        flow_layout = FlowLayout(flow_widget, needAni=False)
        flow_layout.setContentsMargins(0, 0, 0, 0)
        flow_layout.setHorizontalSpacing(10)
        flow_layout.setVerticalSpacing(10)

        for val in values:
            cb = CheckBox(val, flow_widget)
            cb.stateChanged.connect(
                lambda state, tt=ttype, pk=prop_key, pv=val, gcb=global_cb: self._on_rule_changed(  # noqa: E501
                    tt, pk, pv, state, gcb
                )
            )
            flow_layout.addWidget(cb)
            all_cbs.append(cb)

        group_layout.addWidget(flow_widget, stretch=1)
        layout.addWidget(group_widget)

    def _on_select_all_changed(
        self, state: int, track_type: str, checkboxes: List[CheckBox]
    ) -> None:
        """Обработка нажатия на 'Выбрать все' (выделяет дорожки нужного типа в дереве напрямую)."""  # noqa: E501
        is_checked = state == Qt.CheckState.Checked.value

        if is_checked:
            for cb in checkboxes:
                cb.blockSignals(True)
                cb.setChecked(False)
                cb.blockSignals(False)

            if track_type in self._active_rules:
                for k in self._active_rules[track_type]:
                    self._active_rules[track_type][k].clear()

        self._tree.blockSignals(True)
        try:
            self._update_tree_check_states(track_type, is_checked)
        finally:
            self._tree.blockSignals(False)

        self._update_badges()

    def _update_tree_check_states(
        self, track_type: str, is_checked: bool
    ) -> None:
        """Обновить состояние чекбоксов в дереве по типу дорожки."""
        root = self._tree.invisibleRootItem()
        for i in range(root.childCount()):
            file_node = root.child(i)
            checked_count = 0

            for j in range(file_node.childCount()):
                track_node = file_node.child(j)
                if track_node.data(0, self.ROLE_TRACK_TYPE) == track_type:
                    state = (
                        Qt.CheckState.Checked
                        if is_checked
                        else Qt.CheckState.Unchecked
                    )
                    track_node.setCheckState(0, state)
                if track_node.checkState(0) == Qt.CheckState.Checked:
                    checked_count += 1

            if checked_count == 0:
                file_node.setCheckState(0, Qt.CheckState.Unchecked)
            elif checked_count == file_node.childCount():
                file_node.setCheckState(0, Qt.CheckState.Checked)
            else:
                file_node.setCheckState(0, Qt.CheckState.PartiallyChecked)

        self._update_badges()

    def _on_rule_changed(
        self,
        track_type: str,
        prop_key: str,
        prop_val: str,
        state: int,
        global_cb: CheckBox = None,
    ) -> None:
        """Обработка изменения динамического правила."""
        is_checked = state == Qt.CheckState.Checked.value
        rules = self._active_rules[track_type][prop_key]

        if is_checked:
            rules.add(prop_val)
        else:
            rules.discard(prop_val)

        # Если фильтр изменен пользователем вручную - сбрасываем галочку "Выбрать все" (без вызова _on_select_all_changed)  # noqa: E501
        if global_cb and global_cb.isChecked():
            global_cb.blockSignals(True)
            global_cb.setChecked(False)
            global_cb.blockSignals(False)

        logger.info(
            "Изменено правило [%s] %s: %s -> %s",
            track_type,
            prop_key,
            prop_val,
            is_checked,
        )
        self._apply_rules(track_type)

    def _apply_rules(self, track_type: str | None = None) -> None:
        """Применить все умные правила к дереву."""
        self._tree.blockSignals(True)
        try:
            root = self._tree.invisibleRootItem()
            for i in range(root.childCount()):
                file_node = root.child(i)
                for j in range(file_node.childCount()):
                    track_node = file_node.child(j)
                    t_type = track_node.data(0, self.ROLE_TRACK_TYPE)

                    if track_type is not None and t_type != track_type:
                        continue

                    t_data = track_node.data(0, self.ROLE_TRACK_DATA)

                    if t_type is None or not t_data:
                        continue

                    rules = self._active_rules.get(t_type, {})
                    should_check = self._check_node_against_rules(
                        t_data, rules
                    )
                    state = (
                        Qt.CheckState.Checked
                        if should_check
                        else Qt.CheckState.Unchecked
                    )
                    track_node.setCheckState(0, state)

                # После обновления детей файла, Qt с флагом AutoTristate
                # должен обновить родителя, но в blockSignals мы делаем
                # это явно для надежности.
                self._update_parent_check_state(file_node)
        finally:
            self._tree.blockSignals(False)

        self._update_badges()

    def _update_parent_check_state(self, file_node: QTreeWidgetItem) -> None:
        """Принудительно обновить состояние чекбокса файла на основе детей."""
        checked = 0
        unchecked = 0
        total = file_node.childCount()

        if total == 0:
            return

        for i in range(total):
            child = file_node.child(i)
            if child:
                state = child.checkState(0)
                if state == Qt.CheckState.Checked:
                    checked += 1
                elif state == Qt.CheckState.Unchecked:
                    unchecked += 1

        if checked == total:
            file_node.setCheckState(0, Qt.CheckState.Checked)
        elif unchecked == total:
            file_node.setCheckState(0, Qt.CheckState.Unchecked)
        else:
            file_node.setCheckState(0, Qt.CheckState.PartiallyChecked)

    def _check_node_against_rules(
        self, t_data: Dict[str, Any], rules: Dict[str, Set[str]]
    ) -> bool:
        """Проверить, удовлетворяет ли узел заданным правилам."""
        has_any_active_rules = any(len(v) > 0 for v in rules.values())
        if not has_any_active_rules:
            return False

        # ПРИНЦИП "ИЛИ" (Union): дорожка считается подходящей, если она
        # соответствует ХОТЯ БЫ ОДНОМУ из активных критериев
        # (язык ИЛИ имя ИЛИ кодек).
        for prop_key, selected_vals in rules.items():
            if not selected_vals:
                continue

            track_val = t_data.get(prop_key, "")
            if track_val in selected_vals:
                return True

        return False

    def _on_tree_item_changed(
        self, item: QTreeWidgetItem, column: int
    ) -> None:
        """Обработка изменения чекбокса в дереве."""
        # Если это узел файла и он не в Partial-состоянии,
        # распространяем выбор на детей.
        if (
            item.data(0, self.ROLE_FILE_PATH)
            and item.data(0, self.ROLE_TRACK_ID) is None
        ):
            state = item.checkState(0)
            if state != Qt.CheckState.PartiallyChecked:
                self._tree.blockSignals(True)
                for i in range(item.childCount()):
                    child = item.child(i)
                    if child:
                        child.setCheckState(0, state)
                self._tree.blockSignals(False)

        self._update_badges()

    def _update_badges(self) -> None:
        """Обновить бейджи (счетчики) на вкладках SegmentedWidget."""
        counts = {"video": 0, "audio": 0, "subtitles": 0}

        root = self._tree.invisibleRootItem()
        for i in range(root.childCount()):
            file_node = root.child(i)
            for j in range(file_node.childCount()):
                track_node = file_node.child(j)
                if track_node.checkState(0) == Qt.CheckState.Checked:
                    t_type = track_node.data(0, self.ROLE_TRACK_TYPE)
                    if t_type in counts:
                        counts[t_type] += 1

        # Обновляем состояние чекбоксов "Выбрать все"
        total_counts = {"video": 0, "audio": 0, "subtitles": 0}
        for tracks in self._file_tracks.values():
            for tr in tracks:
                if tr.track_type in total_counts:
                    total_counts[tr.track_type] += 1

        for t_type, count in counts.items():
            if t_type in self._select_all_checkboxes:
                cb = self._select_all_checkboxes[t_type]
                total = total_counts.get(t_type, 0)
                is_all = (count == total) if total > 0 else False
                cb.blockSignals(True)
                cb.setChecked(is_all)
                cb.blockSignals(False)

        titles = {"video": "Видео", "audio": "Аудио", "subtitles": "Субтитры"}

        if hasattr(self._segmented_widget, "pivot"):
            pivot = self._segmented_widget.pivot
            for route, title in titles.items():
                count = counts[route]
                display_text = f"{title} 🔴 {count}" if count > 0 else title
                item = pivot.item(route)
                if item:
                    item.setText(display_text)
                    self._manage_info_badge(item, route, count)

    def _manage_info_badge(
        self, target_item: QWidget, route: str, count: int
    ) -> None:
        """Управление жизненным циклом InfoBadge для вкладки."""
        if not InfoBadge:
            return

        badge = self._badges.get(route)
        if count > 0:
            if not badge:
                badge = InfoBadge.info(
                    text=str(count),
                    parent=self._segmented_widget,
                    target=target_item,
                    position=InfoBadgePosition.NAVIGATION_ITEM,
                )
                self._badges[route] = badge
            else:
                badge.setText(str(count))
                badge.show()
        else:
            if badge:
                badge.hide()

    def _on_files_changed(self) -> None:
        """При изменении списка файлов очищаем данные."""
        self._tree.clear()
        self._file_tracks.clear()
        self._filters_card.setVisible(False)
        self._tree_card.setVisible(False)
        self.filesChanged.emit()

    def _on_load_tracks_clicked(self) -> None:
        """Загрузка дорожек из добавленных файлов."""
        paths = self._file_list.get_file_paths()
        if not paths:
            return

        self._tree.clear()
        self._file_tracks.clear()

        for file_path in paths:
            try:
                tracks = self._probe.get_tracks(file_path)
            except Exception:
                logger.exception("Ошибка анализа файла '%s'", file_path.name)
                tracks = []

            self._file_tracks[file_path] = tracks
            self._add_file_node(file_path, tracks)

        self._tree.expandAll()
        self._collect_dynamic_options()
        self._build_dynamic_ui()

        # Обнуляем чекбоксы в дереве
        self._active_rules = {
            "video": {
                "language": set(),
                "codec": set(),
                "resolution": set(),
                "name": set(),
            },
            "audio": {
                "language": set(),
                "codec": set(),
                "channels": set(),
                "name": set(),
            },
            "subtitles": {"language": set(), "codec": set(), "name": set()},
        }
        self._apply_rules()

        self._filters_card.setVisible(True)
        self._tree_card.setVisible(True)
        self._segmented_widget.setCurrentItem("video")

    def _add_file_node(self, file_path: Path, tracks: List[TrackInfo]) -> None:
        """Добавить узел файла с дочерними дорожками."""
        file_item = QTreeWidgetItem(self._tree)
        file_item.setText(0, file_path.name)
        file_item.setIcon(0, FluentIcon.MOVIE.icon())
        file_item.setData(0, self.ROLE_FILE_PATH, str(file_path))
        # Делаем чекбокс файла активным и поддерживающим три состояния
        file_item.setFlags(
            file_item.flags()
            | Qt.ItemFlag.ItemIsUserCheckable
            | Qt.ItemFlag.ItemIsAutoTristate
        )
        file_item.setCheckState(0, Qt.CheckState.Unchecked)

        if not tracks:
            no_track = QTreeWidgetItem(file_item)
            no_track.setText(0, "⚠ Дорожки не обнаружены")
            no_track.setFlags(no_track.flags() & ~Qt.ItemFlag.ItemIsSelectable)
            return

        for track in tracks:
            track_item = QTreeWidgetItem(file_item)
            label_text, track_data = self._format_track_label_data(track)

            track_item.setText(0, label_text)
            track_item.setFlags(
                track_item.flags() | Qt.ItemFlag.ItemIsUserCheckable
            )
            track_item.setCheckState(0, Qt.CheckState.Unchecked)

            track_item.setData(0, self.ROLE_TRACK_ID, track.track_id)
            track_item.setData(0, self.ROLE_FILE_PATH, str(file_path))
            track_item.setData(0, self.ROLE_TRACK_TYPE, track.track_type)
            track_item.setData(0, self.ROLE_TRACK_DATA, track_data)

    def _format_track_label_data(
        self, track: TrackInfo
    ) -> tuple[str, Dict[str, Any]]:
        """Форматировать текст для узла дерева и получить словарь данных фильтрации."""  # noqa: E501
        parts = [track.type_label]
        if track.codec:
            parts.append(track.codec)

        lang = (
            track.language
            if (track.language and track.language != "und")
            else "Неизвестный"
        )
        if lang != "Неизвестный":
            parts.append(lang)

        if getattr(track, "resolution", ""):
            parts.append(track.resolution)

        if getattr(track, "channels", 0):
            parts.append(f"{track.channels} ch")

        if track.name:
            parts.append(f'"{track.name}"')

        parts.append(f"ID: {track.track_id}")

        track_data = {
            "language": lang,
            "codec": track.codec,
            "name": track.name,
            "resolution": getattr(track, "resolution", ""),
            "channels": (
                str(getattr(track, "channels", 0))
                if getattr(track, "channels", 0)
                else ""
            ),
        }

        return "  ·  ".join(parts), track_data

    def get_selected_tracks_per_file(self) -> Dict[str, List[int]]:
        """Получить выбранные дорожки каждого файла."""
        result: Dict[str, List[int]] = {}

        root = self._tree.invisibleRootItem()
        for i in range(root.childCount()):
            file_node = root.child(i)
            file_path = file_node.data(0, self.ROLE_FILE_PATH)
            selected_ids: List[int] = []

            for j in range(file_node.childCount()):
                track_node = file_node.child(j)
                track_id = track_node.data(0, self.ROLE_TRACK_ID)
                if (
                    track_id is not None
                    and track_node.checkState(0) == Qt.CheckState.Checked
                ):
                    selected_ids.append(track_id)

            if file_path:
                result[file_path] = selected_ids

        return result

    def get_file_paths(self) -> List[Path]:
        """Возвращает список добавленных файлов (совместимость)."""
        return self._file_list.get_file_paths()
