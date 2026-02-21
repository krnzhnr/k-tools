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

from app.infrastructure.mkvprobe_runner import (
    MKVProbeRunner,
    TrackInfo,
)
from app.ui.file_list_widget import FileListWidget

logger = logging.getLogger(__name__)

# Расширения файлов по типу дорожки.
_VIDEO_EXTS = {
    ".mkv", ".mp4", ".avi", ".mov",
    ".webm", ".hevc", ".h264", ".h265",
    ".264", ".265", ".vc1", ".m2v",
}
_AUDIO_EXTS = {
    ".aac", ".ac3", ".eac3", ".dts",
    ".flac", ".wav", ".mp3", ".m4a",
    ".ogg", ".mka", ".opus", ".wv",
    ".thd", ".truehd", ".mlp", ".dtshd",
    ".pcm", ".mp2", ".m2a",
}
_SUBTITLE_EXTS = {
    ".srt", ".ass", ".ssa", ".sub",
    ".vtt", ".idx", ".sup",
}

# Все допустимые расширения для файлов-замен.
_ALL_REPLACEMENT_EXTS = sorted(
    _VIDEO_EXTS | _AUDIO_EXTS | _SUBTITLE_EXTS
)

# Плейсхолдер для ComboBox «не заменять».
_NO_REPLACE = "— Не заменять —"


class ReplacementCard(CardWidget):
    """Карточка назначений замен.

    Содержит строку с ComboBox для каждой
    дорожки контейнера.
    """

    replacementsChanged = pyqtSignal()

    def __init__(
        self, parent: QWidget | None = None
    ) -> None:
        """Инициализация карточки.

        Args:
            parent: Родительский виджет.
        """
        super().__init__(parent)
        self._layout = QVBoxLayout(self)
        self._layout.setContentsMargins(
            16, 12, 16, 16
        )
        self._layout.setSpacing(8)

        title = StrongBodyLabel(
            "Назначение замен", self
        )
        self._layout.addWidget(title)

        self._hint = CaptionLabel(
            "Загрузите дорожки контейнера, "
            "чтобы назначить замены",
            self,
        )
        self._hint.setStyleSheet(
            "color: rgba(255, 255, 255, 0.5);"
        )
        self._hint.setAlignment(
            Qt.AlignmentFlag.AlignCenter
        )
        self._layout.addWidget(self._hint)

        # Контейнер для строк назначений
        self._rows_widget = QWidget(self)
        self._rows_layout = QVBoxLayout(
            self._rows_widget
        )
        self._rows_layout.setContentsMargins(
            0, 0, 0, 0
        )
        self._rows_layout.setSpacing(6)
        self._rows_widget.setVisible(False)
        self._layout.addWidget(self._rows_widget)

        # Хранение: track_id → ComboBox
        self._combos: dict[int, ComboBox] = {}
        # Хранение: track_id → TrackInfo
        self._tracks: dict[int, TrackInfo] = {}

        logger.info(
            "Карточка назначений замен "
            "инициализирована"
        )

    def set_tracks(
        self, tracks: list[TrackInfo]
    ) -> None:
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
        """Обновить варианты файлов-замен.

        Фильтрует файлы по типу каждой дорожки. Если файл является
        контейнером (mkv, mp4, mka), анализирует его потоки.

        Args:
            files: Список файлов-замен.
            probe: Утилита для анализа контейнеров.
        """
        # Кэш для результатов анализа (чтобы не запускать mkvmerge постоянно)
        if not hasattr(self, "_probe_cache"):
            self._probe_cache: dict[Path, list[TrackInfo]] = {}

        # Очищаем кэш от удаленных файлов
        current_paths = set(files)
        self._probe_cache = {
            p: t for p, t in self._probe_cache.items()
            if p in current_paths
        }

        for track_id, combo in self._combos.items():
            target_track = self._tracks.get(track_id)
            if target_track is None:
                continue

            # Текущее значение для восстановления
            current_data = combo.currentData()

            # Фильтрация по типу
            exts = self._get_exts_for_type(target_track.track_type)
            
            combo.blockSignals(True)
            combo.clear()
            combo.addItem(_NO_REPLACE)

            for f in files:
                if f.suffix.lower() not in exts:
                    continue

                # Если это контейнер, пробуем достать дорожки подходящего типа
                if f.suffix.lower() in {".mkv", ".mp4", ".mka", ".m4a", ".mov"}:
                    if f not in self._probe_cache:
                        try:
                            self._probe_cache[f] = probe.get_tracks(f)
                        except Exception:
                            logger.error("Ошибка анализа замены '%s'", f.name)
                            self._probe_cache[f] = []

                    src_tracks = self._probe_cache[f]
                    # Ищем дорожки того же типа внутри файла-замены
                    relevant_tracks = [
                        t for t in src_tracks 
                        if t.track_type == target_track.track_type
                    ]

                    if relevant_tracks:
                        for st in relevant_tracks:
                            label = f"{f.name} (ID {st.track_id}: {st.codec}"
                            if st.language and st.language != "und":
                                label += f", {st.language}"
                            if st.name:
                                label += f", {st.name}"
                            label += ")"
                            
                            data = json.dumps({"path": str(f), "src_id": st.track_id})
                            combo.addItem(label, userData=data)
                    else:
                        # Если в контейнере нет таких дорожек, просто добавляем файл как есть (на авось)
                        combo.addItem(f.name, userData=json.dumps({"path": str(f), "src_id": 0}))
                else:
                    # Обычный файл (аудио-стрим, субтитры)
                    combo.addItem(f.name, userData=json.dumps({"path": str(f), "src_id": 0}))

            # Восстановить выбор
            if current_data:
                idx = combo.findData(current_data)
                if idx >= 0:
                    combo.setCurrentIndex(idx)
                else:
                    combo.setCurrentIndex(0)
            else:
                combo.setCurrentIndex(0)
            
            combo.blockSignals(False)

        self.replacementsChanged.emit()
        logger.debug(
            "ComboBox обновлены, файлов-замен: %d",
            len(files),
        )

    def get_replacements(
        self,
    ) -> dict[int, dict[str, Any]]:
        """Получить назначенные замены.

        Returns:
            Словарь {track_id: {"path": Path, "src_id": int}}.
        """
        result: dict[int, dict[str, Any]] = {}
        for track_id, combo in (
            self._combos.items()
        ):
            if combo.currentText() == _NO_REPLACE:
                continue
            data_str = combo.currentData()
            if data_str:
                try:
                    data = json.loads(data_str)
                    result[track_id] = {
                        "path": Path(data["path"]),
                        "src_id": int(data.get("src_id", 0))
                    }
                except (json.JSONDecodeError, KeyError, TypeError):
                    logger.error("Ошибка парсинга данных замены для дорожки %d", track_id)
        return result

    def clear(self) -> None:
        """Очистить все назначения."""
        self._clear_rows()
        self._tracks.clear()
        self._combos.clear()
        self._rows_widget.setVisible(False)
        self._hint.setVisible(True)

    def _create_row(
        self, track: TrackInfo
    ) -> QWidget:
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
        if (
            track.language
            and track.language != "und"
        ):
            parts.append(track.language)
        if track.name:
            parts.append(f'"{track.name}"')
        parts.append(f"ID: {track.track_id}")
        label_text = "  ·  ".join(parts)

        label = CaptionLabel(label_text, row)
        label.setStyleSheet(
            "color: rgba(255, 255, 255, 0.7);"
        )
        layout.addWidget(label)

        # ComboBox
        combo = ComboBox(row)
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
        containers = {".mkv", ".mp4", ".mka"}
        if track_type == "video":
            return _VIDEO_EXTS | containers
        if track_type == "audio":
            return _AUDIO_EXTS | containers
        if track_type == "subtitles":
            return _SUBTITLE_EXTS | containers
        return containers


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

    def __init__(
        self, parent: QWidget | None = None
    ) -> None:
        """Инициализация виджета.

        Args:
            parent: Родительский виджет.
        """
        super().__init__(parent)
        self._probe = MKVProbeRunner()
        self._tracks: list[TrackInfo] = []
        self._init_ui()
        logger.info(
            "Виджет подмены потоков "
            "инициализирован"
        )

    def _init_ui(self) -> None:
        """Настройка пользовательского интерфейса."""
        layout = QVBoxLayout(self)
        layout.setContentsMargins(0, 0, 0, 0)
        layout.setSpacing(8)

        # --- Секция исходного файла ---
        container_label = StrongBodyLabel(
            "Исходный файл (MKV / MP4)", self
        )
        layout.addWidget(container_label)

        self._container_list = FileListWidget(
            allowed_extensions=[".mkv", ".mp4"],
            context_name="Подмена потоков (исходник)",
            parent=self,
        )
        layout.addWidget(
            self._container_list, stretch=0
        )

        # Кнопка загрузки дорожек
        self._load_btn = PrimaryPushButton(
            FluentIcon.SYNC,
            "Загрузить дорожки",
            self,
        )
        self._load_btn.clicked.connect(
            self._on_load_tracks
        )
        layout.addWidget(self._load_btn)

        # --- Дерево дорожек ---
        self._track_card = CardWidget(self)
        track_layout = QVBoxLayout(self._track_card)
        track_layout.setContentsMargins(
            16, 4, 16, 16
        )
        track_layout.setSpacing(8)

        track_title = StrongBodyLabel(
            "Текущие дорожки исходника",
            self._track_card,
        )
        track_layout.addWidget(track_title)

        self._track_hint = CaptionLabel(
            "Добавьте файл и нажмите "
            "«Загрузить дорожки»",
            self._track_card,
        )
        self._track_hint.setStyleSheet(
            "color: rgba(255, 255, 255, 0.5);"
        )
        self._track_hint.setAlignment(
            Qt.AlignmentFlag.AlignCenter
        )
        track_layout.addWidget(self._track_hint)

        self._tree = TreeWidget(self._track_card)
        self._tree.setHeaderHidden(True)
        self._tree.setBorderVisible(False)
        self._tree.setVisible(False)
        track_layout.addWidget(self._tree)

        layout.addWidget(self._track_card)

        # --- Секция файлов-замен ---
        repl_label = StrongBodyLabel(
            "Файлы для подмены дорожек", self
        )
        layout.addWidget(repl_label)

        self._replacement_list = FileListWidget(
            allowed_extensions=_ALL_REPLACEMENT_EXTS,
            context_name="Подмена потоков (замены)",
            parent=self,
        )
        self._replacement_list.filesChanged.connect(
            self._on_replacements_changed
        )
        layout.addWidget(
            self._replacement_list, stretch=1
        )

        # --- Карточка назначений ---
        self._replacement_card = ReplacementCard(
            self
        )
        layout.addWidget(self._replacement_card)

        # При изменении контейнера сбрасываем
        self._container_list.filesChanged.connect(
            self._on_container_changed
        )
        self._container_list.filesChanged.connect(
            self.filesChanged.emit
        )

    def _on_load_tracks(self) -> None:
        """Обработчик кнопки «Загрузить дорожки»."""
        files = self._container_list.files
        if not files:
            logger.warning(
                "Нет контейнера для анализа"
            )
            return

        container = files[0]
        logger.info(
            "Загрузка дорожек контейнера: '%s'",
            container.name,
        )

        self._tree.clear()
        self._tracks.clear()

        try:
            self._tracks = (
                self._probe.get_tracks(container)
            )
        except Exception:
            logger.exception(
                "Ошибка анализа контейнера '%s'",
                container.name,
            )
            self._tracks = []

        if not self._tracks:
            self._track_hint.setText(
                "Дорожки не обнаружены"
            )
            self._track_hint.setVisible(True)
            self._tree.setVisible(False)
            self._replacement_card.clear()
            return

        self._track_hint.setVisible(False)
        self._tree.setVisible(True)

        # Строим дерево
        file_item = QTreeWidgetItem(self._tree)
        file_item.setText(0, container.name)
        file_item.setIcon(
            0, FluentIcon.MOVIE.icon()
        )
        file_item.setData(
            0,
            self.ROLE_FILE_PATH,
            str(container),
        )

        for track in self._tracks:
            track_item = QTreeWidgetItem(file_item)
            parts = [
                track.type_label,
                track.codec,
            ]
            if (
                track.language
                and track.language != "und"
            ):
                parts.append(track.language)
            if track.name:
                parts.append(f'"{track.name}"')
            parts.append(
                f"ID: {track.track_id}"
            )
            label = "  ·  ".join(parts)
            track_item.setText(0, label)

        self._tree.expandAll()

        # Обновляем карточку назначений
        self._replacement_card.set_tracks(
            self._tracks
        )
        # Обновляем ComboBox файлами-заменами
        self._on_replacements_changed()

        logger.info(
            "Загружено дорожек: %d",
            len(self._tracks),
        )

    def _on_container_changed(self) -> None:
        """Обработчик изменения контейнера."""
        if not self._container_list.files:
            self._tree.clear()
            self._tracks.clear()
            self._track_hint.setText(
                "Добавьте файл и нажмите "
                "«Загрузить дорожки»"
            )
            self._track_hint.setVisible(True)
            self._tree.setVisible(False)
            self._replacement_card.clear()
            logger.info(
                "Контейнер очищен, "
                "дерево дорожек сброшено"
            )

    def _on_replacements_changed(self) -> None:
        """Обновить ComboBox при изменении."""
        files = self._replacement_list.files
        self._replacement_card.update_replacement_files(
            files, self._probe
        )

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
        return (
            self._replacement_card
            .get_replacements()
        )
