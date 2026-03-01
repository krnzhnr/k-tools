# -*- coding: utf-8 -*-
"""Виджет подмены потоков MKV/MP4.

Объединяет выбор контейнера, загрузку дорожек,
добавление файлов-замен и inline-назначение замен
через ComboBox.
"""

import json
import logging
from pathlib import Path
from typing import Any

from PyQt6.QtCore import Qt, pyqtSignal
from PyQt6.QtWidgets import (
    QTreeWidgetItem,
    QVBoxLayout,
    QWidget,
)
from qfluentwidgets import (
    CardWidget,
    CaptionLabel,
    ComboBox,
    FluentIcon,
    PrimaryPushButton,
    StrongBodyLabel,
    TreeWidget,
)
from app.ui.elided_label import ElidedLabel

from app.infrastructure.mkvprobe_runner import (
    MKVProbeRunner,
    TrackInfo,
)
from app.ui.file_list_widget import FileListWidget
from app.core.constants import (
    VIDEO_EXTENSIONS,
    AUDIO_EXTENSIONS,
    SUBTITLE_EXTENSIONS,
    VIDEO_CONTAINERS,
    MEDIA_CONTAINERS,
)

logger = logging.getLogger(__name__)

# Все допустимые расширения для файлов-замен.
_ALL_REPLACEMENT_EXTS = sorted(
    VIDEO_EXTENSIONS | AUDIO_EXTENSIONS | SUBTITLE_EXTENSIONS
)

# Плейсхолдер для ComboBox «не заменять».
_NO_REPLACE = "— Не заменять —"


class ReplacementCard(CardWidget):
    """Карточка назначений замен.

    Содержит строку с ComboBox для каждой
    дорожки контейнера.
    """

    replacementsChanged = pyqtSignal()

    def __init__(self, parent: QWidget | None = None) -> None:
        """Инициализация карточки.

        Args:
            parent: Родительский виджет.
        """
        super().__init__(parent)
        self._layout = QVBoxLayout(self)
        self._layout.setContentsMargins(16, 12, 16, 16)
        self._layout.setSpacing(8)

        title = StrongBodyLabel("Назначение замен", self)
        self._layout.addWidget(title)

        self._hint = CaptionLabel(
            "Загрузите дорожки контейнера, " "чтобы назначить замены",
            self,
        )
        self._hint.setStyleSheet("color: rgba(255, 255, 255, 0.5);")
        self._hint.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self._layout.addWidget(self._hint)

        # Контейнер для строк назначений
        self._rows_widget = QWidget(self)
        self._rows_layout = QVBoxLayout(self._rows_widget)
        self._rows_layout.setContentsMargins(0, 0, 0, 0)
        self._rows_layout.setSpacing(6)
        self._rows_widget.setVisible(False)
        self._layout.addWidget(self._rows_widget)

        # Хранение: track_id → ComboBox
        self._combos: dict[int, ComboBox] = {}
        # Хранение: track_id → TrackInfo
        self._tracks: dict[int, TrackInfo] = {}

        logger.info("Карточка назначений замен " "инициализирована")

    def set_tracks(self, tracks: list[TrackInfo]) -> None:
        """Установить дорожки контейнера.

        Создаёт строку с ComboBox для каждой.

        Args:
            tracks: Список дорожек.
        """
        self._clear_rows()
        self._tracks.clear()
        self._combos.clear()

        if not tracks:
            self._rows_widget.setVisible(False)
            self._hint.setVisible(True)
            return

        self._hint.setVisible(False)
        self._rows_widget.setVisible(True)

        for track in tracks:
            self._tracks[track.track_id] = track
            row = self._create_row(track)
            self._rows_layout.addWidget(row)

        logger.info(
            "Карточка назначений: %d дорожек",
            len(tracks),
        )

    def update_replacement_files(
        self, files: list[Path], probe: MKVProbeRunner
    ) -> None:
        """Обновить варианты файлов-замен."""
        if not hasattr(self, "_probe_cache"):
            self._probe_cache: dict[Path, list[TrackInfo]] = {}

        current_paths = set(files)
        self._probe_cache = {
            p: t for p, t in self._probe_cache.items() if p in current_paths
        }

        for track_id, combo in self._combos.items():
            target_track = self._tracks.get(track_id)
            if not target_track:
                continue

            current_data = combo.currentData()
            exts = self._get_exts_for_type(target_track.track_type)

            combo.blockSignals(True)
            combo.clear()
            combo.addItem(_NO_REPLACE)

            for f in files:
                if f.suffix.lower() not in exts:
                    continue
                self._add_replacement_option(combo, f, target_track, probe)

            if current_data:
                idx = combo.findData(current_data)
                combo.setCurrentIndex(idx if idx >= 0 else 0)
            else:
                combo.setCurrentIndex(0)

            combo.blockSignals(False)

        self.replacementsChanged.emit()
        logger.debug("ComboBox обновлены, файлов-замен: %d", len(files))

    def _add_replacement_option(
        self,
        combo: ComboBox,
        f: Path,
        target_track: TrackInfo,
        probe: MKVProbeRunner,
    ) -> None:
        """Добавить опцию файла-замены в ComboBox."""
        container_exts = {".mkv", ".mp4", ".mka", ".m4a", ".mov"}
        if f.suffix.lower() in container_exts:
            self._process_replacement_container(combo, f, target_track, probe)
        else:
            combo.addItem(
                f.name, userData=json.dumps({"path": str(f), "src_id": 0})
            )

    def _process_replacement_container(
        self,
        combo: ComboBox,
        f: Path,
        target_track: TrackInfo,
        probe: MKVProbeRunner,
    ) -> None:
        """Обработать файл-контейнер как источник замен."""
        if f not in self._probe_cache:
            try:
                self._probe_cache[f] = probe.get_tracks(f)
            except Exception:
                logger.error("Ошибка анализа замены '%s'", f.name)
                self._probe_cache[f] = []

        src_tracks = self._probe_cache[f]
        relevant_tracks = [
            t for t in src_tracks if t.track_type == target_track.track_type
        ]

        if relevant_tracks:
            for st in relevant_tracks:
                label_parts = [f"{f.name} (ID {st.track_id}: {st.codec}"]
                if st.language and st.language != "und":
                    label_parts.append(f", {st.language}")
                if st.name:
                    label_parts.append(f", {st.name}")
                label_parts.append(")")

                combo.addItem(
                    "".join(label_parts),
                    userData=json.dumps(
                        {"path": str(f), "src_id": st.track_id}
                    ),
                )
        else:
            combo.addItem(
                f.name, userData=json.dumps({"path": str(f), "src_id": 0})
            )

    def get_replacements(
        self,
    ) -> dict[int, dict[str, Any]]:
        """Получить назначенные замены.

        Returns:
            Словарь {track_id: {"path": Path, "src_id": int}}.
        """
        result: dict[int, dict[str, Any]] = {}
        for track_id, combo in self._combos.items():
            if combo.currentText() == _NO_REPLACE:
                continue
            data_str = combo.currentData()
            if data_str:
                try:
                    data = json.loads(data_str)
                    result[track_id] = {
                        "path": Path(data["path"]),
                        "src_id": int(data.get("src_id", 0)),
                    }
                except (json.JSONDecodeError, KeyError, TypeError):
                    logger.error(
                        "Ошибка парсинга данных замены для дорожки %d",
                        track_id,
                    )
        return result

    def clear(self) -> None:
        """Очистить все назначения."""
        self._clear_rows()
        self._tracks.clear()
        self._combos.clear()
        self._rows_widget.setVisible(False)
        self._hint.setVisible(True)

    def _create_row(self, track: TrackInfo) -> QWidget:
        """Создать строку назначения.

        Args:
            track: Информация о дорожке.

        Returns:
            Виджет-строка.
        """
        row = QWidget(self._rows_widget)
        layout = QVBoxLayout(row)
        layout.setContentsMargins(0, 0, 0, 0)
        layout.setSpacing(2)

        # Лейбл дорожки
        parts = [track.type_label, track.codec]
        if track.language and track.language != "und":
            parts.append(track.language)
        if track.name:
            parts.append(f'"{track.name}"')
        parts.append(f"ID: {track.track_id}")
        label_text = "  ·  ".join(parts)

        label = ElidedLabel(label_text, row)
        label.setStyleSheet("color: rgba(255, 255, 255, 0.7);")
        label.setElideMode(Qt.TextElideMode.ElideMiddle)
        layout.addWidget(label)

        # ComboBox
        from PyQt6.QtWidgets import QSizePolicy

        combo = ComboBox(row)
        # Игнорируем размер содержимого при расчете ширины
        combo.setSizePolicy(
            QSizePolicy.Policy.Ignored, QSizePolicy.Policy.Fixed
        )
        combo.setMinimumWidth(200)
        combo.addItem(_NO_REPLACE)
        combo.currentIndexChanged.connect(
            lambda: self.replacementsChanged.emit()
        )
        layout.addWidget(combo)

        self._combos[track.track_id] = combo
        return row

    def _clear_rows(self) -> None:
        """Удалить все строки назначений."""
        while self._rows_layout.count():
            item = self._rows_layout.takeAt(0)
            if item is not None:
                widget = item.widget()
                if widget:
                    widget.deleteLater()

    @staticmethod
    def _get_exts_for_type(
        track_type: str,
    ) -> set[str]:
        """Расширения файлов для типа дорожки.

        Включает специфичные расширения и общие контейнеры.

        Args:
            track_type: Тип дорожки.

        Returns:
            Множество расширений.
        """
        if track_type == "video":
            return set(VIDEO_EXTENSIONS | MEDIA_CONTAINERS)
        if track_type == "audio":
            return set(AUDIO_EXTENSIONS | MEDIA_CONTAINERS)
        if track_type == "subtitles":
            return set(SUBTITLE_EXTENSIONS | MEDIA_CONTAINERS)
        return set(MEDIA_CONTAINERS)


class StreamReplaceWidget(QWidget):
    """Виджет подмены потоков MKV/MP4.

    Объединяет:
    1. Выбор контейнера (MKV/MP4)
    2. Загрузку и отображение дорожек
    3. Добавление файлов-замен
    4. Inline-назначение замен через ComboBox
    """

    # Роль для хранения пути файла.
    ROLE_FILE_PATH = Qt.ItemDataRole.UserRole + 1

    filesChanged = pyqtSignal()

    def __init__(self, parent: QWidget | None = None) -> None:
        """Инициализация виджета.

        Args:
            parent: Родительский виджет.
        """
        super().__init__(parent)
        self._probe = MKVProbeRunner()
        self._tracks: list[TrackInfo] = []
        self._init_ui()
        logger.info("Виджет подмены потоков " "инициализирован")

    def _init_ui(self) -> None:
        """Настройка пользовательского интерфейса."""
        layout = QVBoxLayout(self)
        layout.setContentsMargins(0, 0, 0, 0)
        layout.setSpacing(8)

        self._init_container_section(layout)
        self._init_track_tree_section(layout)
        self._init_replacements_section(layout)

        self._container_list.filesChanged.connect(self._on_container_changed)
        self._container_list.filesChanged.connect(self.filesChanged.emit)

    def _init_container_section(self, layout: QVBoxLayout) -> None:
        """Инициализировать секцию выбора исходного контейнера."""
        layout.addWidget(
            StrongBodyLabel("Исходный файл (MKV / MP4 / MOV)", self)
        )
        self._container_list = FileListWidget(
            allowed_extensions=list(VIDEO_CONTAINERS),
            context_name="Подмена потоков (исходник)",
            parent=self,
        )
        layout.addWidget(self._container_list, stretch=0)

        self._load_btn = PrimaryPushButton(
            FluentIcon.SYNC, "Загрузить дорожки", self
        )
        self._load_btn.clicked.connect(self._on_load_tracks)
        layout.addWidget(self._load_btn)

    def _init_track_tree_section(self, layout: QVBoxLayout) -> None:
        """Инициализировать секцию дерева дорожек исходника."""
        self._track_card = CardWidget(self)
        track_layout = QVBoxLayout(self._track_card)
        track_layout.setContentsMargins(16, 4, 16, 16)
        track_layout.setSpacing(8)

        track_layout.addWidget(
            StrongBodyLabel("Текущие дорожки исходника", self._track_card)
        )

        self._track_hint = CaptionLabel(
            "Добавьте файл и нажмите «Загрузить дорожки»", self._track_card
        )
        self._track_hint.setStyleSheet("color: rgba(255, 255, 255, 0.5);")
        self._track_hint.setAlignment(Qt.AlignmentFlag.AlignCenter)
        track_layout.addWidget(self._track_hint)

        self._tree = TreeWidget(self._track_card)
        self._tree.setHeaderHidden(True)
        self._tree.setBorderVisible(False)
        self._tree.setVisible(False)
        track_layout.addWidget(self._tree)

        layout.addWidget(self._track_card)

    def _init_replacements_section(self, layout: QVBoxLayout) -> None:
        """Инициализировать секцию вариантов замен."""
        layout.addWidget(StrongBodyLabel("Файлы для подмены дорожек", self))
        self._replacement_list = FileListWidget(
            allowed_extensions=_ALL_REPLACEMENT_EXTS,
            context_name="Подмена потоков (замены)",
            parent=self,
        )
        self._replacement_list.filesChanged.connect(
            self._on_replacements_changed
        )
        layout.addWidget(self._replacement_list, stretch=1)

        self._replacement_card = ReplacementCard(self)
        layout.addWidget(self._replacement_card)

    def _on_load_tracks(self) -> None:
        """Обработчик кнопки «Загрузить дорожки»."""
        if not self._container_list.files:
            logger.warning("Нет контейнера для анализа")
            return

        container = self._container_list.files[0]
        logger.info("Загрузка дорожек контейнера: '%s'", container.name)

        self._tree.clear()
        self._tracks.clear()

        try:
            self._tracks = self._probe.get_tracks(container)
        except Exception:
            logger.exception("Ошибка анализа контейнера '%s'", container.name)
            self._tracks = []

        if not self._tracks:
            self._handle_no_tracks()
            return

        self._handle_tracks_loaded(container)

    def _handle_no_tracks(self) -> None:
        """Обработка случая, когда дорожки не найдены."""
        self._track_hint.setText("Дорожки не обнаружены")
        self._track_hint.setVisible(True)
        self._tree.setVisible(False)
        self._replacement_card.clear()

    def _handle_tracks_loaded(self, container: Path) -> None:
        """Обработка успешной загрузки дорожек."""
        self._track_hint.setVisible(False)
        self._tree.setVisible(True)

        file_item = QTreeWidgetItem(self._tree)
        file_item.setText(0, container.name)
        file_item.setIcon(0, FluentIcon.MOVIE.icon())
        file_item.setData(0, self.ROLE_FILE_PATH, str(container))

        for track in self._tracks:
            track_item = QTreeWidgetItem(file_item)
            parts = [track.type_label, track.codec]
            if track.language and track.language != "und":
                parts.append(track.language)
            if track.name:
                parts.append(f'"{track.name}"')
            parts.append(f"ID: {track.track_id}")
            track_item.setText(0, "  ·  ".join(parts))

        self._tree.expandAll()
        self._replacement_card.set_tracks(self._tracks)
        self._on_replacements_changed()
        logger.info("Загружено дорожек: %d", len(self._tracks))

    def _on_container_changed(self) -> None:
        """Обработчик изменения контейнера."""
        if not self._container_list.files:
            self._tree.clear()
            self._tracks.clear()
            self._track_hint.setText(
                "Добавьте файл и нажмите " "«Загрузить дорожки»"
            )
            self._track_hint.setVisible(True)
            self._tree.setVisible(False)
            self._replacement_card.clear()
            logger.info("Контейнер очищен, " "дерево дорожек сброшено")

    def _on_replacements_changed(self) -> None:
        """Обновить ComboBox при изменении."""
        files = self._replacement_list.files
        self._replacement_card.update_replacement_files(files, self._probe)

    # --- Публичный API для WorkPanel ---

    def get_file_paths(self) -> list[Path]:
        """Список файлов для совместимости.

        Returns:
            Список из контейнера (если есть).
        """
        return self._container_list.files

    def get_container_path(self) -> Path | None:
        """Путь к контейнеру.

        Returns:
            Path или None.
        """
        files = self._container_list.files
        return files[0] if files else None

    def get_replacements(
        self,
    ) -> dict[int, dict[str, Any]]:
        """Получить назначенные замены.

        Returns:
            Словарь {track_id: {"path": Path, "src_id": int}}.
        """
        return self._replacement_card.get_replacements()
