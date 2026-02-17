# -*- coding: utf-8 -*-
"""Рабочая панель — страница конкретного скрипта."""

import logging
from pathlib import Path
from typing import Any

from PyQt6.QtCore import (
    Qt,
    QThread,
    pyqtSignal,
)
from PyQt6.QtWidgets import (
    QWidget,
    QVBoxLayout,
    QHBoxLayout,
    QLabel,
)
from qfluentwidgets import (
    BodyLabel,
    CaptionLabel,
    CardWidget,
    CheckBox,
    ComboBox,
    LineEdit,
    PrimaryPushButton,
    ProgressBar,
    SmoothScrollArea,
    StrongBodyLabel,
    SubtitleLabel,
    FluentIcon,
    InfoBar,
    InfoBarPosition,
    TextEdit,
)

from app.core.abstract_script import (
    AbstractScript,
    SettingField,
    SettingType,
)
from app.ui.file_list_widget import FileListWidget
from app.ui.muxing_table_widget import MuxingTableWidget
from app.ui.track_list_widget import TrackListWidget
from app.ui.stream_replace_widget import (
    StreamReplaceWidget,
)

logger = logging.getLogger(__name__)


class ScriptWorker(QThread):
    """Рабочий поток для выполнения скрипта.

    Запускает скрипт в отдельном потоке, чтобы
    не блокировать UI во время обработки.

    Signals:
        progress: (текущий, всего, сообщение).
        finished: Список строк-результатов.
        error: Текст ошибки.
    """

    progress = pyqtSignal(int, int, str)
    finished = pyqtSignal(list)
    error = pyqtSignal(str)

    def __init__(
        self,
        script: AbstractScript,
        files: list[Path],
        settings: dict[str, Any],
        parent: QWidget | None = None,
    ) -> None:
        """Инициализация рабочего потока.

        Args:
            script: Скрипт для выполнения.
            files: Список файлов.
            settings: Настройки скрипта.
            parent: Родительский виджет.
        """
        super().__init__(parent)
        self._script = script
        self._files = files
        self._settings = settings

    def run(self) -> None:
        """Выполнение скрипта в рабочем потоке."""
        try:
            results = self._script.execute(
                files=self._files,
                settings=self._settings,
                progress_callback=self._on_progress,
            )
            self.finished.emit(results)
        except Exception as exc:
            logger.exception(
                "Ошибка выполнения скрипта '%s'",
                self._script.name,
            )
            self.error.emit(str(exc))

    def _on_progress(
        self,
        current: int,
        total: int,
        message: str,
    ) -> None:
        """Callback прогресса выполнения.

        Args:
            current: Текущий обработанный файл.
            total: Общее количество файлов.
            message: Сообщение о статусе.
        """
        self.progress.emit(current, total, message)


class ScriptPage(QWidget):
    """Страница отдельного скрипта.

    Содержит описание, настройки, список файлов,
    прогресс-бар и кнопку выполнения.
    """

    def __init__(
        self,
        script: AbstractScript,
        parent: QWidget | None = None,
    ) -> None:
        """Инициализация страницы скрипта.

        Args:
            script: Скрипт для отображения.
            parent: Родительский виджет.
        """
        super().__init__(parent)
        self._script = script
        self._settings_widgets: dict[str, QWidget] = {}
        self._settings_rows: dict[str, QWidget] = {}
        self._worker: ScriptWorker | None = None
        self._track_widget: TrackListWidget | None = None
        self._stream_replace_widget: (
            StreamReplaceWidget | None
        ) = None

        self._init_ui()
        logger.info(
            "Страница скрипта '%s' создана",
            script.name,
        )

    def _init_ui(self) -> None:
        """Инициализация пользовательского интерфейса."""
        # Scroll area для прокрутки контента
        outer = QVBoxLayout(self)
        outer.setContentsMargins(0, 0, 0, 0)

        scroll = SmoothScrollArea(self)
        scroll.setWidgetResizable(True)
        scroll.setStyleSheet(
            "QScrollArea { background: transparent; "
            "border: none; }"
        )

        container = QWidget()
        container.setStyleSheet(
            "background: transparent;"
        )
        layout = QVBoxLayout(container)
        layout.setContentsMargins(24, 24, 24, 24)
        layout.setSpacing(16)

        # Заголовок и описание
        self._add_header(layout)

        # Настройки скрипта
        if self._script.settings_schema:
            self._add_settings(layout)

        # Список файлов
        self._add_file_list(layout)

        # Лог выполнения
        self._add_log_area(layout)

        # Прогресс бар и кнопка
        self._add_bottom_bar(layout)

        scroll.setWidget(container)
        outer.addWidget(scroll)

    def _add_header(self, layout: QVBoxLayout) -> None:
        """Добавить заголовок и описание скрипта.

        Args:
            layout: Родительский layout.
        """
        title = SubtitleLabel(self._script.name, self)
        layout.addWidget(title)

        desc = BodyLabel(self._script.description, self)
        desc.setWordWrap(True)
        layout.addWidget(desc)

    def _add_settings(self, layout: QVBoxLayout) -> None:
        """Добавить секцию настроек скрипта.

        Args:
            layout: Родительский layout.
        """
        settings_card = CardWidget(self)
        card_layout = QVBoxLayout(settings_card)
        card_layout.setContentsMargins(16, 12, 16, 12)
        card_layout.setSpacing(10)

        settings_title = StrongBodyLabel(
            "Настройки", settings_card
        )
        card_layout.addWidget(settings_title)

        for field in self._script.settings_schema:
            row_widget = QWidget(settings_card)
            row_layout = QHBoxLayout(row_widget)
            row_layout.setContentsMargins(0, 0, 0, 0)

            widget = self._create_setting_widget(
                field, settings_card
            )
            self._settings_widgets[field.key] = widget

            # Подключение сигналов для динамической видимости
            if isinstance(widget, ComboBox):
                widget.currentTextChanged.connect(
                    lambda _: self._update_visibility()
                )
            elif isinstance(widget, CheckBox):
                widget.stateChanged.connect(
                    lambda _: self._update_visibility()
                )

            if field.setting_type == SettingType.CHECKBOX:
                row_layout.addWidget(widget)
            else:
                label = BodyLabel(
                    field.label, settings_card
                )
                label.setMinimumWidth(150)
                row_layout.addWidget(label)
                row_layout.addWidget(widget)

            card_layout.addWidget(row_widget)
            self._settings_rows[field.key] = row_widget

        self._update_visibility()
        layout.addWidget(settings_card)

    def _create_setting_widget(
        self,
        field: SettingField,
        parent: QWidget,
    ) -> QWidget:
        """Создать виджет настройки по типу поля.

        Args:
            field: Описание поля настройки.
            parent: Родительский виджет.

        Returns:
            Виджет настройки.
        """
        if field.setting_type == SettingType.TEXT:
            widget = LineEdit(parent)
            widget.setText(str(field.default))
            widget.setPlaceholderText(field.label)
            
            # Логирование изменения текстового поля
            widget.textChanged.connect(
                lambda text: logger.info(
                    "[%s] Настройка '%s' (%s) изменена пользователем: '%s'",
                    self._script.name, field.label, field.key, text
                )
            )
            return widget

        if field.setting_type == SettingType.COMBO:
            widget = ComboBox(parent)
            widget.addItems(field.options)
            if field.default in field.options:
                widget.setCurrentText(str(field.default))
            
            # Логирование изменения комбобокса
            widget.currentTextChanged.connect(
                lambda text: logger.info(
                    "[%s] Настройка '%s' (%s) изменена пользователем на: '%s'",
                    self._script.name, field.label, field.key, text
                )
            )
            return widget

        if field.setting_type == SettingType.CHECKBOX:
            widget = CheckBox(field.label, parent)
            widget.setChecked(bool(field.default))
            
            # Логирование изменения чекбокса
            widget.stateChanged.connect(
                lambda state: logger.info(
                    "[%s] Настройка '%s' (%s) изменена пользователем на: %s",
                    self._script.name, field.label, field.key, "ВКЛ" if state else "ВЫКЛ"
                )
            )
            return widget

        # Fallback для неизвестных типов
        widget = LineEdit(parent)
        widget.setText(str(field.default))
        return widget

    def _update_visibility(self) -> None:
        """Обновить видимость настроек."""
        current_settings = self._get_current_settings()

        for field in self._script.settings_schema:
            if not field.visible_if:
                continue

            is_visible = True
            for key, allowed_values in field.visible_if.items():
                current_value = current_settings.get(key)
                if current_value not in allowed_values:
                    is_visible = False
                    break

            if row := self._settings_rows.get(field.key):
                row.setVisible(is_visible)

    def _add_file_list(self, layout: QVBoxLayout) -> None:
        """Добавить секцию списка файлов.

        Args:
            layout: Родительский layout.
        """
        files_label = StrongBodyLabel("Файлы", self)
        layout.addWidget(files_label)

        if (
            self._script.use_custom_widget
            and isinstance(self._script.name, str)
            and "Подмена" in self._script.name
        ):
            # Скрипт подмены потоков MKV
            self._stream_replace_widget = (
                StreamReplaceWidget(self)
            )
            self._file_list = (
                self._stream_replace_widget
            )
            layout.addWidget(
                self._stream_replace_widget,
                stretch=1,
            )
            return
        elif (
            self._script.use_custom_widget
            and isinstance(self._script.name, str)
            and "Муксер" in self._script.name
        ):
            self._file_list = MuxingTableWidget(self)
        elif (
            self._script.use_custom_widget
            and isinstance(self._script.name, str)
            and "поток" in self._script.name.lower()
        ):
            # Скрипт управления потоками MKV
            self._file_list = FileListWidget(
                allowed_extensions=(
                    self._script.file_extensions
                ),
                context_name=self._script.name,
                parent=self,
            )
            layout.addWidget(self._file_list, stretch=1)

            # Кнопка загрузки дорожек
            self._load_tracks_btn = PrimaryPushButton(
                FluentIcon.SYNC,
                "Загрузить дорожки",
                self,
            )
            self._load_tracks_btn.clicked.connect(
                self._on_load_tracks_clicked
            )
            layout.addWidget(self._load_tracks_btn)

            # Виджет-дерево дорожек
            self._track_widget = TrackListWidget(self)
            layout.addWidget(self._track_widget)

            # При очистке списка файлов сбрасываем дерево дорожек
            self._file_list.filesChanged.connect(
                self._on_files_changed
            )
            return
        else:
            self._file_list = FileListWidget(
                allowed_extensions=(
                    self._script.file_extensions
                ),
                context_name=self._script.name,
                parent=self,
            )
        
        layout.addWidget(self._file_list, stretch=1)

    def _on_files_changed(self) -> None:
        """Обработчик изменения списка файлов."""
        if (
            self._file_list is not None
            and self._track_widget is not None
        ):
            # Синхронизируем дерево дорожек (удаляем ушедшие файлы)
            self._track_widget.sync_with_files(
                self._file_list.files
            )

    def _on_load_tracks_clicked(self) -> None:
        """Обработчик кнопки «Загрузить дорожки»."""
        if self._track_widget is None:
            return
        paths = self._file_list.get_file_paths()
        if not paths:
            InfoBar.warning(
                title="Нет файлов",
                content=(
                    "Сначала добавьте MKV-файлы"
                ),
                parent=self,
                position=InfoBarPosition.TOP,
                duration=3000,
            )
            return
        logger.info(
            "[Управление потоками] Загрузка "
            "дорожек для %d файлов",
            len(paths),
        )
        self._track_widget.load_files(paths)
    def _add_log_area(self, layout: QVBoxLayout) -> None:
        """Добавить область лога выполнения.

        Args:
            layout: Родительский layout.
        """
        log_label = StrongBodyLabel("Результат", self)
        layout.addWidget(log_label)

        self._log_area = TextEdit(self)
        self._log_area.setReadOnly(True)
        self._log_area.setMaximumHeight(100)
        self._log_area.setPlaceholderText(
            "Здесь появятся результаты выполнения..."
        )
        layout.addWidget(self._log_area)

    def _add_bottom_bar(
        self, layout: QVBoxLayout
    ) -> None:
        """Добавить прогресс-бар и кнопку выполнения.

        Args:
            layout: Родительский layout.
        """
        self._progress = ProgressBar(self)
        self._progress.setVisible(False)
        layout.addWidget(self._progress)

        self._status_label = CaptionLabel("", self)
        self._status_label.setVisible(False)
        layout.addWidget(self._status_label)

        self._execute_btn = PrimaryPushButton(
            FluentIcon.PLAY,
            "Выполнить",
            self,
        )
        self._execute_btn.setMinimumHeight(38)
        self._execute_btn.clicked.connect(
            self._on_execute_clicked
        )
        layout.addWidget(self._execute_btn)

    def get_settings(self) -> dict[str, Any]:
        """Получить текущие значения настроек со страницы.

        Returns:
            Словарь {ключ: значение} настроек.
        """
        return self._get_current_settings()

    def _get_current_settings(self) -> dict[str, Any]:
        """Собрать текущие значения настроек.

        Returns:
            Словарь {ключ: значение} настроек.
        """
        settings: dict[str, Any] = {}

        for key, widget in self._settings_widgets.items():
            if isinstance(widget, LineEdit):
                settings[key] = widget.text()
            elif isinstance(widget, ComboBox):
                settings[key] = widget.currentText()
            elif isinstance(widget, CheckBox):
                settings[key] = widget.isChecked()
            else:
                settings[key] = ""

        return settings

    def _on_execute_clicked(self) -> None:
        """Обработчик нажатия кнопки «Выполнить»."""
        files = self._file_list.get_file_paths()

        if not files:
            InfoBar.warning(
                title="Нет файлов",
                content="Добавьте файлы для обработки",
                parent=self,
                position=InfoBarPosition.TOP,
                duration=3000,
            )
            return

        self._log_area.clear()
        self._execute_btn.setEnabled(False)
        self._progress.setVisible(True)
        self._status_label.setVisible(True)
        self._progress.setValue(0)

        settings = self._get_current_settings()

        # Инъекция выбранных дорожек для скрипта потоков
        if self._track_widget is not None:
            per_file = (
                self._track_widget
                .get_selected_tracks_per_file()
            )
            settings["selected_tracks_per_file"] = (
                per_file
            )
            logger.info(
                "[Управление потоками] "
                "Выбранные дорожки по файлам: %s",
                per_file,
            )

        # Инъекция данных для скрипта подмены
        if self._stream_replace_widget is not None:
            container = (
                self._stream_replace_widget
                .get_container_path()
            )
            replacements = (
                self._stream_replace_widget
                .get_replacements()
            )
            if container:
                settings["container_path"] = (
                    str(container)
                )
            settings["replacements"] = {
                str(k): str(v)
                for k, v in replacements.items()
            }
            logger.info(
                "[Подмена потоков] "
                "Контейнер: '%s', замен: %d",
                container.name if container
                else "не указан",
                len(replacements),
            )

        logger.info(
            "Пользователь нажал кнопку 'Выполнить' для скрипта '%s'. "
            "Количество файлов в очереди: %d. Текущие настройки: %s",
            self._script.name,
            len(files),
            settings
        )

        self._worker = ScriptWorker(
            script=self._script,
            files=files,
            settings=settings,
            parent=self,
        )
        self._worker.progress.connect(
            self._on_progress
        )
        self._worker.finished.connect(
            self._on_finished
        )
        self._worker.error.connect(
            self._on_error
        )
        self._worker.start()

    def _on_progress(
        self,
        current: int,
        total: int,
        message: str,
    ) -> None:
        """Обработка прогресса выполнения.

        Args:
            current: Текущий файл.
            total: Всего файлов.
            message: Сообщение о статусе.
        """
        percent = int((current / total) * 100)
        logger.info(
            "Прогресс выполнения скрипта '%s': %d%% (%d/%d). Статус: %s",
            self._script.name,
            percent,
            current,
            total,
            message
        )
        self._progress.setValue(percent)
        self._status_label.setText(
            f"{current}/{total}: {message}"
        )

    def _on_finished(self, results: list[str]) -> None:
        """Обработка завершения выполнения.

        Args:
            results: Список строк-результатов.
        """
        self._execute_btn.setEnabled(True)
        self._progress.setValue(100)
        self._log_area.setPlainText("\n".join(results))

        success_count = len([r for r in results if r.startswith("✅")])
        skipped_count = len([r for r in results if r.startswith("⏭")])
        error_count = len([r for r in results if r.startswith("❌")])
        total = len(results)

        # Подготовка сообщения
        stats = []
        if success_count:
            stats.append(f"Успешно: {success_count}")
        if skipped_count:
            stats.append(f"Пропущено: {skipped_count}")
        if error_count:
            stats.append(f"Ошибок: {error_count}")
        
        content = ", ".join(stats) if stats else "Результатов нет"

        # Выбор типа уведомления и заголовка
        if error_count > 0:
            show_info = InfoBar.error
            title = "Завершено с ошибками"
        elif success_count == 0 and skipped_count > 0:
            show_info = InfoBar.warning
            title = "Обработка пропущена"
        elif skipped_count > 0:
            show_info = InfoBar.warning
            title = "Выполнено частично"
        else:
            show_info = InfoBar.success
            title = "Выполнено"

        show_info(
            title=title,
            content=content,
            parent=self,
            position=InfoBarPosition.TOP,
            duration=5000,
        )

        logger.info(
            "Скрипт '%s' завершён: %d успешно, %d пропущено, %d ошибок",
            self._script.name,
            success_count,
            skipped_count,
            error_count,
        )

    def _on_error(self, error_text: str) -> None:
        """Обработка ошибки выполнения.

        Args:
            error_text: Текст ошибки.
        """
        self._execute_btn.setEnabled(True)
        self._progress.setVisible(False)
        self._status_label.setVisible(False)

        InfoBar.error(
            title="Ошибка",
            content=error_text,
            parent=self,
            position=InfoBarPosition.TOP,
            duration=5000,
        )

        logger.error(
            "Ошибка выполнения скрипта '%s': %s",
            self._script.name,
            error_text,
        )
