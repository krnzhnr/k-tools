# -*- coding: utf-8 -*-
"""Виджет массового извлечения дорожек с умными правилами."""

import logging
from pathlib import Path
from typing import Dict, List, Set, Any, Tuple

from PyQt6.QtCore import Qt, pyqtSignal
from PyQt6.QtWidgets import (
    QHBoxLayout,
    QTreeWidgetItem,
    QVBoxLayout,
    QWidget,
    QStackedWidget
)
from qfluentwidgets import (
    CardWidget,
    FluentIcon,
    PrimaryPushButton,
    StrongBodyLabel,
    TreeWidget,
    SegmentedWidget,
    CheckBox,
    FlowLayout
)
try:
    from qfluentwidgets import InfoBadge, InfoBadgePosition
except ImportError:
    InfoBadge = None
    InfoBadgePosition = None

from app.infrastructure.mkvprobe_runner import MKVProbeRunner, TrackInfo
from app.ui.file_list_widget import FileListWidget

logger = logging.getLogger(__name__)

class TrackExtractWidget(QWidget):
    """Виджет массового извлечения дорожек с умными правилами и разделенным UI."""

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
        
        # Хранение динамических опций по типам: type -> property_name -> set(values)
        self._dynamic_options: Dict[str, Dict[str, Set[str]]] = {
            "video": {"language": set(), "codec": set(), "resolution": set(), "name": set()},
            "audio": {"language": set(), "codec": set(), "channels": set(), "name": set()},
            "subtitles": {"language": set(), "codec": set(), "name": set()}
        }
        
        # Выбранные правила: type -> property_name -> set(selected_values)
        self._active_rules: Dict[str, Dict[str, Set[str]]] = {
            "video": {"language": set(), "codec": set(), "resolution": set(), "name": set()},
            "audio": {"language": set(), "codec": set(), "channels": set(), "name": set()},
            "subtitles": {"language": set(), "codec": set(), "name": set()}
        }
        
        self._badges: Dict[str, Any] = {}
        
        self._init_ui()
        logger.info("Виджет массового извлечения инициализирован (Dynamic UI)")

    def _init_ui(self) -> None:
        """Настройка пользовательского интерфейса с двумя карточками."""
        layout = QVBoxLayout(self)
        layout.setContentsMargins(0, 0, 0, 0)
        layout.setSpacing(16)

        # 1. Список файлов
        self._file_list = FileListWidget(
            allowed_extensions=[".mkv", ".mp4"],
            context_name="Демуксинг",
            parent=self
        )
        self._file_list.filesChanged.connect(self._on_files_changed)
        layout.addWidget(self._file_list, stretch=1)

        # 2. Кнопка загрузки дорожек
        self._load_tracks_btn = PrimaryPushButton(
            FluentIcon.SYNC, "Загрузить дорожки", self
        )
        self._load_tracks_btn.clicked.connect(self._on_load_tracks_clicked)
        layout.addWidget(self._load_tracks_btn)

        # --- Карточка 1: Фильтры ---
        self._filters_card = CardWidget(self)
        self._filters_card.setVisible(False)
        filters_layout = QVBoxLayout(self._filters_card)
        filters_layout.setContentsMargins(16, 16, 16, 16)
        filters_layout.setSpacing(12)
        
        # Segmented Widget (Вкладки)
        self._segmented_widget = SegmentedWidget(self._filters_card)
        self._segmented_widget.addItem("video", "Видео", self._on_tab_changed)
        self._segmented_widget.addItem("audio", "Аудио", self._on_tab_changed)
        self._segmented_widget.addItem("subtitles", "Субтитры", self._on_tab_changed)
        filters_layout.addWidget(self._segmented_widget)
        
        # Stacked Widget для динамических панелей
        self._rules_stack = QStackedWidget(self._filters_card)
        
        # Пустые контейнеры для динамических правил
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
        layout.addWidget(self._filters_card, stretch=0) # Фильтры не тянутся

        # --- Карточка 2: Дерево дорожек ---
        self._tree_card = CardWidget(self)
        self._tree_card.setVisible(False)
        tree_layout = QVBoxLayout(self._tree_card)
        tree_layout.setContentsMargins(16, 16, 16, 16)
        
        self._tree = TreeWidget(self._tree_card)
        self._tree.setHeaderHidden(True)
        self._tree.setBorderVisible(False)
        self._tree.itemChanged.connect(self._on_tree_item_changed)
        tree_layout.addWidget(self._tree)
        
        layout.addWidget(self._tree_card, stretch=2) # Дерево тянется

    def _on_tab_changed(self) -> None:
        """Обработка переключения вкладки в SegmentedWidget."""
        current_route = self._segmented_widget.currentRouteKey()
        if current_route in self._rules_panels:
            self._rules_stack.setCurrentWidget(self._rules_panels[current_route])

    def _collect_dynamic_options(self) -> None:
        """Собрать уникальные свойства дорожек для генерации чекбоксов."""
        # Очистка старых данных
        for t in self._dynamic_options:
            for k in self._dynamic_options[t]:
                self._dynamic_options[t][k].clear()
                self._active_rules[t][k].clear()
                
        for tracks in self._file_tracks.values():
            for t in tracks:
                ttype = t.track_type
                if ttype not in self._dynamic_options:
                    continue
                    
                flags = self._dynamic_options[ttype]
                
                lang = t.language if (t.language and t.language != "und") else "Неизвестный"
                
                # Добавляем в список доступных опций по языкам
                flags["language"].add(lang)
                
                if t.codec:
                    flags["codec"].add(t.codec)
                    
                if t.name:
                    flags["name"].add(t.name)
                    
                if ttype == "video" and t.resolution:
                    flags["resolution"].add(t.resolution)
                    
                if ttype == "audio" and getattr(t, 'channels', 0):
                    ch = str(t.channels)
                    flags["channels"].add(ch)

    def _build_dynamic_ui(self) -> None:
        """Перестроить чекбоксы в StackedWidget на основе собранных опций."""
        labels_map = {
            "language": "Язык",
            "codec": "Кодек",
            "resolution": "Разрешение",
            "channels": "Каналы",
            "name": "Название"
        }
        
        for ttype, layout in self._rules_layouts.items():
            # Очистить layout
            while layout.count():
                item = layout.takeAt(0)
                if item.widget():
                    item.widget().deleteLater()
                elif item.layout():
                    # Рекурсивное удаление если нужно
                    pass
                    
            options = self._dynamic_options.get(ttype, {})
            has_any = False
            
            # Глобальный чекбокс "Выбрать все" для всей вкладки
            global_cb_all = CheckBox("Выбрать все", layout.parentWidget())
            layout.addWidget(global_cb_all)
            
            # Все индивидуальные чекбоксы этой вкладки
            all_tab_checkboxes: List[CheckBox] = []
            
            for prop_key, prop_label in labels_map.items():
                if prop_key not in options:
                    continue
                    
                values = sorted(list(options[prop_key]))
                if not values:
                    continue
                    
                has_any = True
                
                # Группа с названием свойства
                group_widget = QWidget()
                group_layout = QHBoxLayout(group_widget)
                group_layout.setContentsMargins(0, 0, 0, 0)
                group_layout.setAlignment(Qt.AlignmentFlag.AlignLeft)
                
                lbl = StrongBodyLabel(f"{prop_label}:", group_widget)
                lbl.setFixedWidth(100)
                group_layout.addWidget(lbl)
                
                # Контейнер для чекбоксов с переносом строк
                flow_widget = QWidget()
                flow_layout = FlowLayout(flow_widget, needAni=False)
                flow_layout.setContentsMargins(0, 0, 0, 0)
                # Устанавливаем меньшие отступы, чтобы элементы были плотнее
                flow_layout.setHorizontalSpacing(10)
                flow_layout.setVerticalSpacing(10)
                
                for val in values:
                    cb = CheckBox(val, flow_widget)
                    cb.stateChanged.connect(
                        lambda state, tt=ttype, pk=prop_key, pv=val, gcb=global_cb_all: self._on_rule_changed(tt, pk, pv, state, gcb)
                    )
                    flow_layout.addWidget(cb)
                    all_tab_checkboxes.append(cb)
                    
                group_layout.addWidget(flow_widget, stretch=1)
                layout.addWidget(group_widget)
                
            if has_any:
                # Логика глобального чекбокса (если есть опции)
                global_cb_all.stateChanged.connect(
                    lambda state, tt=ttype, cbs=all_tab_checkboxes: self._on_select_all_changed(state, tt, cbs)
                )
                # Отделяем верхний чекбокс от остального контента (с помощью margin контейнера)
                layout.addSpacing(8)
            else:
                global_cb_all.setVisible(False)
                layout.addWidget(StrongBodyLabel("Нет доступных фильтров для данного типа", None))
                
            layout.addStretch(1)

    def _on_select_all_changed(self, state: int, track_type: str, checkboxes: List[CheckBox]) -> None:
        """Обработка нажатия на 'Выбрать все' (выделяет дорожки нужного типа в дереве напрямую)."""
        is_checked = state == Qt.CheckState.Checked.value
        
        # Если ставим галочку "Выбрать все", сначала визуально и логически очищаем все фильтры 
        # (чтобы они потом не перебили наш ручной выбор потоков при следующем вызове _apply_rules)
        if is_checked:
            for cb in checkboxes:
                cb.blockSignals(True)
                cb.setChecked(False)
                cb.blockSignals(False)
            
            # Очищаем активные правила для этого типа
            if track_type in self._active_rules:
                for k in self._active_rules[track_type]:
                    self._active_rules[track_type][k].clear()
                    
        # Теперь проходим по дереву и принудительно ставим состояния именно для дорожек этого типа
        self._tree.blockSignals(True)
        try:
            root = self._tree.invisibleRootItem()
            for i in range(root.childCount()):
                file_node = root.child(i)
                for j in range(file_node.childCount()):
                    track_node = file_node.child(j)
                    t_type = track_node.data(0, self.ROLE_TRACK_TYPE)
                    if t_type == track_type:
                        track_node.setCheckState(0, Qt.CheckState.Checked if is_checked else Qt.CheckState.Unchecked)
                        
            # Обновляем состояние родительских чекбоксов файлов (файлов)
            for i in range(root.childCount()):
                file_node = root.child(i)
                checked_count = 0
                for j in range(file_node.childCount()):
                    if file_node.child(j).checkState(0) == Qt.CheckState.Checked:
                        checked_count += 1
                        
                if checked_count == 0:
                    file_node.setCheckState(0, Qt.CheckState.Unchecked)
                elif checked_count == file_node.childCount():
                    file_node.setCheckState(0, Qt.CheckState.Checked)
                else:
                    file_node.setCheckState(0, Qt.CheckState.PartiallyChecked)
        finally:
            self._tree.blockSignals(False)
            
        self._update_badges()

    def _on_rule_changed(self, track_type: str, prop_key: str, prop_val: str, state: int, global_cb: CheckBox = None) -> None:
        """Обработка изменения динамического правила."""
        is_checked = state == Qt.CheckState.Checked.value
        rules = self._active_rules[track_type][prop_key]
        
        if is_checked:
            rules.add(prop_val)
        else:
            rules.discard(prop_val)
            
        # Если фильтр изменен пользователем вручную - сбрасываем галочку "Выбрать все" (без вызова _on_select_all_changed)
        if global_cb and global_cb.isChecked():
            global_cb.blockSignals(True)
            global_cb.setChecked(False)
            global_cb.blockSignals(False)
            
        logger.info("Изменено правило [%s] %s: %s -> %s", track_type, prop_key, prop_val, is_checked)
        self._apply_rules()

    def _apply_rules(self) -> None:
        """Применить все умные правила к дереву (логика 'ИЛИ' внутри, 'И' между группами? 
        Пользователь просил: 'если выбран язык RUS и ENG, включатся все русские и английские'.
        Обычно фильтры работают так: если в группе выбрано хоть что-то, проверяем совпадение.
        Если группа пуста - она не влияет на фильтрацию (игнорируется).
        """
        self._tree.blockSignals(True)
        try:
            root = self._tree.invisibleRootItem()
            for i in range(root.childCount()):
                file_node = root.child(i)
                for j in range(file_node.childCount()):
                    track_node = file_node.child(j)
                    t_type = track_node.data(0, self.ROLE_TRACK_TYPE)
                    t_data = track_node.data(0, self.ROLE_TRACK_DATA)
                    
                    if t_type is None or not t_data:
                        continue
                        
                    rules = self._active_rules.get(t_type, {})
                    
                    # Проверяем совпадение
                    # Дорожка должна удовлетворять ВСЕМ АКТИВНЫМ ГРУППАМ
                    # (например, если выбран Кодек=AAC и Язык=RUS, то только (AAC И RUS) будут включены)
                    # Если в группе нет выбранных опций, она считается пройдённой.
                    
                    should_check = False
                    has_any_active_rules = any(len(v) > 0 for v in rules.values())
                    
                    if has_any_active_rules:
                        match_all_groups = True
                        for prop_key, selected_vals in rules.items():
                            if not selected_vals:
                                continue # группа пуста, игнорируем
                                
                            track_val = t_data.get(prop_key, "")
                            if track_val not in selected_vals:
                                match_all_groups = False
                                break
                                
                        if match_all_groups:
                            should_check = True
                            
                    track_node.setCheckState(
                        0, 
                        Qt.CheckState.Checked if should_check else Qt.CheckState.Unchecked
                    )
        finally:
            self._tree.blockSignals(False)
            
        self._update_badges()

    def _on_tree_item_changed(self, item: QTreeWidgetItem, column: int) -> None:
        """Обработка ручного изменения чекбокса в дереве."""
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
                        
        titles = {
            "video": "Видео",
            "audio": "Аудио",
            "subtitles": "Субтитры"
        }
        
        if hasattr(self._segmented_widget, "pivot"):
            pivot = self._segmented_widget.pivot
            for route, title in titles.items():
                count = counts[route]
                display_text = f"{title} 🔴 {count}" if count > 0 else title
                item = pivot.item(route)
                if item:
                    item.setText(display_text)
                    self._manage_info_badge(item, route, count)

    def _manage_info_badge(self, target_item: QWidget, route: str, count: int) -> None:
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
                    position=InfoBadgePosition.NAVIGATION_ITEM
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
            "video": {"language": set(), "codec": set(), "resolution": set(), "name": set()},
            "audio": {"language": set(), "codec": set(), "channels": set(), "name": set()},
            "subtitles": {"language": set(), "codec": set(), "name": set()}
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

        if not tracks:
            no_track = QTreeWidgetItem(file_item)
            no_track.setText(0, "⚠ Дорожки не обнаружены")
            no_track.setFlags(no_track.flags() & ~Qt.ItemFlag.ItemIsSelectable)
            return

        for track in tracks:
            track_item = QTreeWidgetItem(file_item)

            parts = [track.type_label]
            if track.codec:
                parts.append(track.codec)
                
            lang = track.language if (track.language and track.language != "und") else "Неизвестный"
            if lang != "Неизвестный":
                parts.append(lang)
                
            if getattr(track, 'resolution', ""):
                parts.append(track.resolution)
                
            if getattr(track, 'channels', 0):
                parts.append(f"{track.channels} ch")
                
            if track.name:
                parts.append(f'"{track.name}"')
                
            parts.append(f"ID: {track.track_id}")

            track_item.setText(0, "  ·  ".join(parts))
            track_item.setFlags(track_item.flags() | Qt.ItemFlag.ItemIsUserCheckable)
            track_item.setCheckState(0, Qt.CheckState.Unchecked)

            # Сохраняем данные для фильтрации
            track_data = {
                "language": lang,
                "codec": track.codec,
                "name": track.name,
                "resolution": getattr(track, 'resolution', ""),
                "channels": str(getattr(track, 'channels', 0)) if getattr(track, 'channels', 0) else ""
            }

            track_item.setData(0, self.ROLE_TRACK_ID, track.track_id)
            track_item.setData(0, self.ROLE_FILE_PATH, str(file_path))
            track_item.setData(0, self.ROLE_TRACK_TYPE, track.track_type)
            track_item.setData(0, self.ROLE_TRACK_DATA, track_data)

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
                if track_id is not None and track_node.checkState(0) == Qt.CheckState.Checked:
                    selected_ids.append(track_id)

            if file_path:
                result[file_path] = selected_ids

        return result
    
    def get_file_paths(self) -> List[Path]:
        """Возвращает список добавленных файлов (совместимость)."""
        return self._file_list.get_file_paths()
