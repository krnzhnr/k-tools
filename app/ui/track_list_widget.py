# -*- coding: utf-8 -*-
"""Виджет-дерево дорожек MKV с чекбоксами.

Отображает все загруженные файлы как корневые узлы,
а их дорожки — как дочерние элементы с чекбоксами.
Использует нативный TreeWidget из qfluentwidgets.
"""

import logging
from pathlib import Path

from PyQt6.QtCore import Qt
from PyQt6.QtWidgets import (
    QHBoxLayout,
    QTreeWidgetItem,
    QVBoxLayout,
    QWidget,
)
from qfluentwidgets import (
    CardWidget,
    CaptionLabel,
    FluentIcon,
    PushButton,
    StrongBodyLabel,
    TreeWidget,
)

from app.infrastructure.mkvprobe_runner import (
    MKVProbeRunner,
    TrackInfo,
)

logger = logging.getLogger(__name__)


class TrackListWidget(CardWidget):
    """Виджет-дерево для отображения и выбора дорожек.

    Корневые узлы — файлы, дочерние — дорожки
    с чекбоксами. Все файлы видны одновременно.
    """

    # Роли для хранения данных в QTreeWidgetItem.
    ROLE_TRACK_ID = Qt.ItemDataRole.UserRole
    ROLE_FILE_PATH = Qt.ItemDataRole.UserRole + 1

    def __init__(
        self, parent: QWidget | None = None
    ) -> None:
        """Инициализация виджета-дерева дорожек.

        Args:
            parent: Родительский виджет.
        """
        super().__init__(parent)
        self._probe = MKVProbeRunner()
        self._file_tracks: dict[
            Path, list[TrackInfo]
        ] = {}
        self._init_ui()
        logger.info(
            "Виджет-дерево дорожек инициализирован"
        )

    def _init_ui(self) -> None:
        """Настройка пользовательского интерфейса."""
        layout = QVBoxLayout(self)
        layout.setContentsMargins(16, 4, 16, 16)
        layout.setSpacing(8)

        # Заголовок
        title = StrongBodyLabel("Дорожки", self)
        layout.addWidget(title)

        # Подсказка
        self._hint_label = CaptionLabel(
            "Добавьте файлы и нажмите "
            "«Загрузить дорожки»",
            self,
        )
        self._hint_label.setStyleSheet(
            "color: rgba(255, 255, 255, 0.5);"
        )
        self._hint_label.setAlignment(
            Qt.AlignmentFlag.AlignCenter
        )
        layout.addWidget(self._hint_label)

        # Нативное дерево qfluentwidgets
        self._tree = TreeWidget(self)
        self._tree.setHeaderHidden(True)
        self._tree.setBorderVisible(False)
        self._tree.setVisible(False)
        layout.addWidget(self._tree)

        # Кнопки управления
        btn_layout = QHBoxLayout()
        btn_layout.setSpacing(8)

        self._select_all_btn = PushButton(
            "Выбрать все", self, FluentIcon.CHECKBOX
        )
        self._select_all_btn.clicked.connect(
            self._select_all
        )

        self._deselect_all_btn = PushButton(
            "Снять все", self, FluentIcon.CANCEL
        )
        self._deselect_all_btn.clicked.connect(
            self._deselect_all
        )

        btn_layout.addWidget(self._select_all_btn)
        btn_layout.addWidget(self._deselect_all_btn)
        btn_layout.addStretch(1)
        self._btn_widget = QWidget(self)
        self._btn_widget.setLayout(btn_layout)
        self._btn_widget.setVisible(False)
        layout.addWidget(self._btn_widget)

    def load_files(
        self, file_paths: list[Path]
    ) -> None:
        """Загрузить дорожки для списка файлов.

        Анализирует каждый файл через mkvmerge -J
        и строит дерево: файл → дорожки.

        Args:
            file_paths: Список путей к MKV-файлам.
        """
        self._tree.clear()
        self._file_tracks.clear()

        if not file_paths:
            self._tree.setVisible(False)
            self._btn_widget.setVisible(False)
            self._hint_label.setText(
                "Добавьте файлы и нажмите "
                "«Загрузить дорожки»"
            )
            self._hint_label.setVisible(True)
            return

        self._hint_label.setVisible(False)
        self._tree.setVisible(True)
        self._btn_widget.setVisible(True)

        for file_path in file_paths:
            try:
                tracks = self._probe.get_tracks(
                    file_path
                )
            except Exception:
                logger.exception(
                    "Ошибка анализа файла '%s'",
                    file_path.name,
                )
                tracks = []

            self._file_tracks[file_path] = tracks
            self._add_file_node(file_path, tracks)

        self._tree.expandAll()
        logger.info(
            "Загружено файлов: %d, "
            "общее количество дорожек: %d",
            len(file_paths),
            sum(
                len(t)
                for t in self._file_tracks.values()
            ),
        )

    def _add_file_node(
        self,
        file_path: Path,
        tracks: list[TrackInfo],
    ) -> None:
        """Добавить узел файла с дочерними дорожками.

        Args:
            file_path: Путь к файлу.
            tracks: Список дорожек файла.
        """
        file_item = QTreeWidgetItem(self._tree)
        file_item.setText(0, file_path.name)
        file_item.setIcon(
            0, FluentIcon.MOVIE.icon()
        )
        file_item.setData(
            0, self.ROLE_FILE_PATH, str(file_path)
        )

        if not tracks:
            no_track = QTreeWidgetItem(file_item)
            no_track.setText(
                0, "⚠ Дорожки не обнаружены"
            )
            no_track.setFlags(
                no_track.flags()
                & ~Qt.ItemFlag.ItemIsSelectable
            )
            return

        for track in tracks:
            track_item = QTreeWidgetItem(file_item)

            # Компактная строка дорожки
            parts = [track.type_label, track.codec]
            if (
                track.language
                and track.language != "und"
            ):
                parts.append(track.language)
            if track.name:
                parts.append(f'"{track.name}"')
            parts.append(f"ID: {track.track_id}")

            label = "  ·  ".join(parts)
            track_item.setText(0, label)

            # Чекбокс
            track_item.setFlags(
                track_item.flags()
                | Qt.ItemFlag.ItemIsUserCheckable
            )
            track_item.setCheckState(
                0, Qt.CheckState.Unchecked
            )

            # Метаданные
            track_item.setData(
                0,
                self.ROLE_TRACK_ID,
                track.track_id,
            )
            track_item.setData(
                0,
                self.ROLE_FILE_PATH,
                str(file_path),
            )

    def get_selected_tracks_per_file(
        self,
    ) -> dict[str, list[int]]:
        """Получить выбранные дорожки каждого файла.

        Returns:
            Словарь {путь_файла: [ID дорожек]}.
        """
        result: dict[str, list[int]] = {}

        root = self._tree.invisibleRootItem()
        for i in range(root.childCount()):
            file_node = root.child(i)
            file_path = file_node.data(
                0, self.ROLE_FILE_PATH
            )
            selected_ids: list[int] = []

            for j in range(file_node.childCount()):
                track_node = file_node.child(j)
                track_id = track_node.data(
                    0, self.ROLE_TRACK_ID
                )
                if (
                    track_id is not None
                    and track_node.checkState(0)
                    == Qt.CheckState.Checked
                ):
                    selected_ids.append(track_id)

            if file_path:
                result[file_path] = selected_ids

        logger.debug(
            "Выбранные дорожки по файлам: %s",
            result,
        )
        return result

    def get_selected_track_ids(self) -> list[int]:
        """Получить все выбранные ID (совместимость).

        Returns:
            Список ID выбранных дорожек.
        """
        all_ids: list[int] = []
        for ids in (
            self.get_selected_tracks_per_file()
            .values()
        ):
            all_ids.extend(ids)
        return all_ids

    def clear_tracks(self) -> None:
        """Очистить дерево дорожек."""
        self._tree.clear()
        self._file_tracks.clear()
        self._tree.setVisible(False)
        self._btn_widget.setVisible(False)
        self._hint_label.setText(
            "Добавьте файлы и нажмите "
            "«Загрузить дорожки»"
        )
        self._hint_label.setVisible(True)
        logger.info("Дерево дорожек очищено")

    def sync_with_files(
        self, file_paths: list[Path]
    ) -> None:
        """Синхронизировать дерево с текущим списком.

        Удаляет узлы файлов, которых нет в списке.
        Полезно при удалении файлов из FileListWidget.

        Args:
            file_paths: Текущий список путей к файлам.
        """
        paths_to_keep = set(file_paths)
        removed_any = False

        # 1. Удаляем из внутреннего словаря
        existing_paths = list(
            self._file_tracks.keys()
        )
        for path in existing_paths:
            if path not in paths_to_keep:
                del self._file_tracks[path]
                removed_any = True

        if not removed_any:
            return

        # 2. Удаляем из дерева (в обратном порядке)
        root = self._tree.invisibleRootItem()
        for i in range(root.childCount() - 1, -1, -1):
            item = root.child(i)
            item_path_str = item.data(
                0, self.ROLE_FILE_PATH
            )
            if item_path_str:
                p = Path(item_path_str)
                if p not in paths_to_keep:
                    root.removeChild(item)

        # 3. Если файлов больше нет, сбрасываем состояние
        if len(self._file_tracks) == 0:
            self.clear_tracks()
        else:
            logger.info(
                "Дерево дорожек синхронизировано "
                "(удалены отсутствующие файлы)"
            )

    def _select_all(self) -> None:
        """Выбрать все дорожки."""
        root = self._tree.invisibleRootItem()
        for i in range(root.childCount()):
            file_node = root.child(i)
            for j in range(file_node.childCount()):
                track_node = file_node.child(j)
                if track_node.data(
                    0, self.ROLE_TRACK_ID
                ) is not None:
                    track_node.setCheckState(
                        0, Qt.CheckState.Checked
                    )
        logger.info(
            "Пользователь выбрал все дорожки"
        )

    def _deselect_all(self) -> None:
        """Снять выбор со всех дорожек."""
        root = self._tree.invisibleRootItem()
        for i in range(root.childCount()):
            file_node = root.child(i)
            for j in range(file_node.childCount()):
                track_node = file_node.child(j)
                if track_node.data(
                    0, self.ROLE_TRACK_ID
                ) is not None:
                    track_node.setCheckState(
                        0, Qt.CheckState.Unchecked
                    )
        logger.info(
            "Пользователь снял выбор "
            "со всех дорожек"
        )
