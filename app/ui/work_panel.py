# -*- coding: utf-8 -*-
"""Рабочая панель — страница конкретного скрипта."""

import logging
from pathlib import Path
from typing import Any, cast

from PyQt6.QtCore import (
    Qt,
    QThread,
    pyqtSignal,
    QRunnable,
    QThreadPool,
    QObject,
    QMutex,
    QSize,
)
from PyQt6.QtWidgets import (
    QWidget,
    QVBoxLayout,
    QHBoxLayout,
    QStackedWidget,
    QSizePolicy,
)
from qfluentwidgets import (
    BodyLabel,
    CardWidget,
    CheckBox,
    ComboBox,
    LineEdit,
    PrimaryPushButton,
    PushButton,
    ProgressBar,
    IndeterminateProgressBar,
    SmoothScrollArea,
    StrongBodyLabel,
    SubtitleLabel,
    CaptionLabel,
    FluentIcon,
    InfoBar,
    InfoBarPosition,
    TextEdit,
    SegmentedWidget,
    SpinBox,
)

from app.core.abstract_script import (
    AbstractScript,
    SettingField,
    SettingType,
)
from app.core.settings_manager import SettingsManager
from app.ui.file_list_widget import FileListWidget
from app.ui.muxing_table_widget import MuxingTableWidget
from app.ui.track_list_widget import TrackListWidget
from app.ui.stream_replace_widget import StreamReplaceWidget
from app.ui.track_extract_widget import TrackExtractWidget
from app.ui.ass_filter_widget import AssFilterWidget
from app.ui.keyword_manager_widget import KeywordManagerWidget

logger = logging.getLogger(__name__)


class TaskSignals(QObject):
    """Сигналы для отдельной задачи в пуле воркеров."""

    finished = pyqtSignal(list, Path)


class TaskRunnable(QRunnable):
    """Задача для одного файла в пуле воркеров."""

    def __init__(
        self,
        script: AbstractScript,
        file_path: Path,
        settings: dict[str, Any],
        output_path: str | None = None,
    ) -> None:
        super().__init__()
        self.script = script
        self.file_path = file_path
        self.settings = settings
        self.output_path = output_path
        self.signals = TaskSignals()

    def run(self) -> None:
        """Выполнение задачи."""
        if getattr(self.script, "is_cancelled", False):
            msg = f"⚠ Отменено: {self.file_path.name}"
            self.signals.finished.emit([msg], self.file_path)
            return

        try:
            results = self.script.execute_single(
                self.file_path,
                self.settings,
                self.output_path,
            )
            self.signals.finished.emit(results, self.file_path)
        except Exception as exc:
            logger.exception(
                "Ошибка в TaskRunnable для файла '%s'",
                self.file_path.name,
            )
            msg = f"❌ Критическая ошибка: {self.file_path.name} ({exc})"
            self.signals.finished.emit([msg], self.file_path)


class ScriptWorker(QThread):
    """Рабочий поток для выполнения скрипта.

    Запускает скрипт в отдельном потоке, чтобы
    не блокировать UI во время обработки.

    Signals:
        progress: (текущий, всего, сообщение).
        finished: Список строк-результатов.
        error: Текст ошибки.
    """

    progress = pyqtSignal(int, int, str, float)
    finished = pyqtSignal(list)
    error = pyqtSignal(str)

    def __init__(
        self,
        script: AbstractScript,
        files: list[Path],
        settings: dict[str, Any],
        output_path: str | None = None,
        parent: QWidget | None = None,
    ) -> None:
        """Инициализация рабочего потока.

        Args:
            script: Скрипт для выполнения.
            files: Список файлов.
            settings: Настройки скрипта.
            output_path: Опциональный путь сохранения.
            parent: Родительский виджет.
        """
        super().__init__(parent)
        self._script = script
        self._files = files
        self._settings = settings
        self._output_path = output_path

        # Очищаем и инициализируем зарезервированные пути перед новым пакетом
        if hasattr(self._script, "prepare_batch"):
            self._script.prepare_batch(self._files)

    def cancel(self) -> None:
        """Отмена выполнения воркера."""
        from app.core.process_manager import ProcessManager

        if hasattr(self._script, "cancel"):
            self._script.cancel()

        ProcessManager().cancel_all()
        logger.info("Воркер получил команду на остановку работы.")

    def run(self) -> None:
        """Выполнение скрипта в рабочем потоке."""
        try:
            # Проверка настроек параллелизма
            from app.core.settings_manager import SettingsManager

            mgr = SettingsManager()
            max_parallel = mgr.max_parallel_tasks

            # Если скрипт поддерживает параллелизм и файлов больше одного
            if (
                self._script.supports_parallel
                and max_parallel > 1
                and len(self._files) > 1
            ):
                self._run_parallel(max_parallel)
            else:
                self._run_sequential()
        except Exception as exc:
            logger.exception(
                "Ошибка выполнения скрипта '%s'",
                self._script.name,
            )
            self.error.emit(str(exc))

    def _run_sequential(self) -> None:
        """Классическое последовательное выполнение."""
        results = self._script.execute(
            files=self._files,
            settings=self._settings,
            output_path=self._output_path,
            progress_callback=cast(Any, self._on_progress),
        )
        self.finished.emit(results)

    def _run_parallel(self, max_workers: int) -> None:
        """Параллельное выполнение через пул потоков."""
        total = len(self._files)
        completed = 0
        all_results: list[str] = []

        # Используем локальный пул для изоляции и чистого завершения
        pool = QThreadPool()
        pool.setMaxThreadCount(max_workers)

        logger.info(
            "Запуск параллельной обработки: воркеров=%d, файлов=%d",
            max_workers,
            total,
        )

        mutex = QMutex()

        def on_task_finished(res: list[str], fpath: Path) -> None:
            nonlocal completed
            mutex.lock()
            try:
                completed += 1
                all_results.extend(res)

                # Сообщаем о прогрессе в UI
                last_msg = res[-1] if res else ""
                self.progress.emit(completed, total, last_msg, 0.0)
            finally:
                mutex.unlock()

        for file_path in self._files:
            runnable = TaskRunnable(
                self._script, file_path, self._settings, self._output_path
            )
            # ВАЖНО: Используем DirectConnection, так как поток воркера
            # блокируется waitForDone и не имеет цикла событий.
            runnable.signals.finished.connect(
                on_task_finished,
                Qt.ConnectionType.DirectConnection,  # type: ignore[call-arg]
            )
            pool.start(runnable)

        # Ждем завершения всех задач в пуле
        pool.waitForDone()

        self.finished.emit(all_results)

    def _on_progress(
        self,
        current: int,
        total: int,
        message: str,
        intra_percent: float = 0.0,
    ) -> None:
        """Callback прогресса выполнения.

        Args:
            current: Текущий обработанный файл.
            total: Общее количество файлов.
            message: Сообщение о статусе.
            intra_percent: Процент выполнения внутри файла (0-100).
        """
        self.progress.emit(current, total, message, intra_percent)


class AdaptiveStackedWidget(QStackedWidget):
    """QStackedWidget, который меняет размер под текущий виджет."""

    def __init__(self, parent: QWidget | None = None) -> None:
        super().__init__(parent)
        self.currentChanged.connect(self._on_current_changed)

    def _on_current_changed(self, index: int) -> None:
        """Обработчик смены текущей страницы."""
        self.updateGeometry()

    def sizeHint(self) -> QSize:
        """Размер для текущего виджета."""
        if widget := self.currentWidget():
            return widget.sizeHint()
        return super().sizeHint()

    def minimumSizeHint(self) -> QSize:
        """Минимальный размер для текущего виджета."""
        if widget := self.currentWidget():
            return widget.minimumSizeHint()
        return super().minimumSizeHint()


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
        self._stream_replace_widget: StreamReplaceWidget | None = None
        self._track_extract_widget: TrackExtractWidget | None = None
        self._ass_filter_widget: AssFilterWidget | None = None
        self._file_list: Any = None
        self._settings_manager = SettingsManager()
        self._vproc_backup: dict[str, Any] = {}

        self._is_initialized = False

        logger.info(
            "Страница скрипта '%s' создана (без UI)",
            script.name,
        )

    def preload_ui(self) -> None:
        """Предзагрузка интерфейса в фоновом режиме через очередь событий."""
        if not self._is_initialized:
            logger.info("Сборка интерфейса UI для '%s'...", self._script.name)
            self._init_ui()
            self._update_visibility()
            self._is_initialized = True

    def showEvent(self, event: Any) -> None:
        """Событие отображения страницы."""
        super().showEvent(event)
        self.preload_ui()
        self._update_path_placeholder()

    def _init_ui(self) -> None:
        """Инициализация пользовательского интерфейса."""
        # Scroll area для прокрутки контента
        outer = QVBoxLayout(self)
        outer.setContentsMargins(0, 0, 0, 0)
        outer.setSpacing(0)

        scroll = SmoothScrollArea(self)
        scroll.setWidgetResizable(True)
        scroll.setStyleSheet(
            "QScrollArea { background: transparent; " "border: none; }"
        )

        container = QWidget()
        container.setStyleSheet("background: transparent;")
        layout = QVBoxLayout(container)
        layout.setContentsMargins(24, 24, 24, 24)
        layout.setSpacing(15)

        # Заголовок и описание
        self._add_header(layout)

        # Настройки скрипта
        if self._script.settings_schema:
            self._add_settings(layout)

        # Список файлов
        self._add_file_list(layout)
        layout.setStretchFactor(self._file_list, 1)

        # Лог выполнения
        self._add_log_area(layout)

        scroll.setWidget(container)
        outer.addWidget(scroll)

        # Компактная кнопка, путь и прогресс снизу (фиксированные)
        self._add_fixed_bottom_bar(outer)

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

        Группирует настройки по вкладкам, если их больше одной.
        """
        schema = self._script.settings_schema
        if not schema:
            return

        # Группируем поля по атрибуту group
        groups: dict[str, list[SettingField]] = {}
        for field in schema:
            groups.setdefault(field.group, []).append(field)

        # Если группа только одна — используем старый вид без вкладок
        if len(groups) <= 1:
            group_name = list(groups.keys())[0] if groups else "Настройки"
            settings_card = self._create_settings_group_card(
                group_name, groups.get(group_name, [])
            )
            layout.addWidget(settings_card)
        else:
            # Несколько групп — создаем вкладки (как в track_extract_widget)
            self._segmented_widget = SegmentedWidget(self)
            self._rules_stack = AdaptiveStackedWidget(self)

            for i, (group_name, fields) in enumerate(groups.items()):
                page = QWidget()
                page.setStyleSheet("background: transparent;")
                main_layout = QVBoxLayout(page)
                main_layout.setContentsMargins(0, 0, 0, 0)
                main_layout.setSpacing(
                    15
                )  # Фиксированный интервал между блоками
                main_layout.setAlignment(Qt.AlignmentFlag.AlignTop)

                card = self._create_settings_group_card(group_name, fields)
                main_layout.addWidget(card)

                self._rules_stack.addWidget(page)
                self._segmented_widget.addItem(
                    routeKey=group_name,
                    text=group_name,
                    onClick=lambda _, idx=i: self._rules_stack.setCurrentIndex(
                        idx
                    ),
                )

            self._segmented_widget.setCurrentItem(list(groups.keys())[0])
            layout.addWidget(self._segmented_widget)

            # Ограничиваем вертикальный размер стека настроек
            self._rules_stack.setSizePolicy(
                QSizePolicy.Policy.Preferred, QSizePolicy.Policy.Maximum
            )
            layout.addWidget(self._rules_stack)

        self._update_visibility()

    def _create_settings_group_card(
        self, title: str, fields: list[SettingField]
    ) -> CardWidget:
        """Создать карточку с группой настроек."""
        card = CardWidget(self)
        card_layout = QVBoxLayout(card)
        card_layout.setContentsMargins(16, 12, 16, 12)
        card_layout.setSpacing(10)

        card_title = StrongBodyLabel(title, card)
        card_layout.addWidget(card_title)
        for field in fields:
            if field.setting_type == SettingType.SUBTITLE:
                subtitle_label = StrongBodyLabel(field.label, card)
                subtitle_label.setStyleSheet(
                    "margin-top: 12px; margin-bottom: 2px;"
                )
                card_layout.addWidget(subtitle_label)
                self._settings_widgets[field.key] = subtitle_label
                self._settings_rows[field.key] = subtitle_label
                continue

            # Контейнер для поля (ряд + опциональный комментарий)
            field_container = QWidget(card)
            field_v_layout = QVBoxLayout(field_container)
            field_v_layout.setContentsMargins(0, 0, 0, 0)
            field_v_layout.setSpacing(2)

            row_widget = QWidget(field_container)
            row_layout = QHBoxLayout(row_widget)
            row_layout.setContentsMargins(0, 0, 0, 0)
            row_layout.setSpacing(10)

            widget = self._create_setting_widget(field, row_widget)
            self._settings_widgets[field.key] = widget

            # Установка подсказки
            if field.comment:
                widget.setToolTip(field.comment)

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
                label = BodyLabel(field.label, row_widget)
                label.setMinimumWidth(150)
                row_layout.addWidget(label)
                row_layout.addWidget(widget)

            field_v_layout.addWidget(row_widget)

            # Добавляем поясняющий текст, если он есть
            if field.comment:
                comment_label = CaptionLabel(field.comment, field_container)
                comment_label.setStyleSheet(
                    "color: #808080; margin-left: 2px;"
                )
                # Смещение для чекбокса, чтобы текст был под текстом чекбокса
                if field.setting_type == SettingType.CHECKBOX:
                    comment_label.setStyleSheet(
                        "color: #808080; margin-left: 28px;"
                    )
                field_v_layout.addWidget(comment_label)

            card_layout.addWidget(field_container)
            self._settings_rows[field.key] = field_container

        return card

    def _create_setting_widget(
        self, field: SettingField, parent: QWidget
    ) -> QWidget:
        """Создать виджет настройки по типу поля."""
        if field.setting_type == SettingType.TEXT:
            return self._create_text_setting_widget(field, parent)
        if field.setting_type == SettingType.COMBO:
            return self._create_combo_setting_widget(field, parent)
        if field.setting_type == SettingType.CHECKBOX:
            return self._create_checkbox_setting_widget(field, parent)
        if field.setting_type == SettingType.KEYWORD_LIST:
            return self._create_keyword_list_widget(field, parent)
        if field.setting_type == SettingType.INT:
            return self._create_int_setting_widget(field, parent)

        widget = LineEdit(parent)
        widget.setText(str(field.default))
        return widget

    def _create_text_setting_widget(
        self, field: SettingField, parent: QWidget
    ) -> LineEdit:
        widget = LineEdit(parent)
        saved_val = SettingsManager().get_script_setting(
            self._script.name, field.key, str(field.default), type_hint=str
        )
        widget.setText(str(saved_val))
        widget.setPlaceholderText(field.label)
        widget.textChanged.connect(
            lambda text: SettingsManager().set_script_setting(
                self._script.name, field.key, text
            )
        )
        widget.textChanged.connect(
            lambda text: logger.info(
                "[%s] Настройка '%s' (%s) изменена на: '%s'",
                self._script.name,
                field.label,
                field.key,
                text,
            )
        )
        return widget

    def _create_combo_setting_widget(
        self, field: SettingField, parent: QWidget
    ) -> ComboBox:
        widget = ComboBox(parent)
        widget.addItems(field.options)
        saved_val = SettingsManager().get_script_setting(
            self._script.name, field.key, str(field.default), type_hint=str
        )
        if saved_val in field.options:
            widget.setCurrentText(str(saved_val))
        else:
            widget.setCurrentText(str(field.default))
        widget.currentTextChanged.connect(
            lambda text: SettingsManager().set_script_setting(
                self._script.name, field.key, text
            )
        )
        widget.currentTextChanged.connect(
            lambda text: logger.info(
                "[%s] Настройка '%s' (%s) изменена на: '%s'",
                self._script.name,
                field.label,
                field.key,
                text,
            )
        )
        return widget

    def _create_checkbox_setting_widget(
        self, field: SettingField, parent: QWidget
    ) -> CheckBox:
        widget = CheckBox(field.label, parent)

        # Специальная логика для подмены оригинала
        if field.key == "overwrite_source":
            # При инициализации проверяем, не заблокирован ли он уже
            has_edit = hasattr(self, "_output_path_edit")
            if has_edit and self._output_path_edit.text():
                widget.setChecked(False)
                widget.setEnabled(False)

        def on_state_changed(state: int) -> None:
            is_checked = bool(state)
            SettingsManager().set_script_setting(
                self._script.name, field.key, is_checked
            )
            logger.info(
                "[%s] Настройка '%s' (%s) изменена на: %s",
                self._script.name,
                field.label,
                field.key,
                "ВКЛ" if is_checked else "ВЫКЛ",
            )
            # Если включаем подмену при заданном пути — сбрасываем путь
            if field.key == "overwrite_source" and is_checked:
                if (
                    hasattr(self, "_output_path_edit")
                    and self._output_path_edit.text().strip()
                ):
                    self._output_path_edit.clear()
                    InfoBar.info(
                        title="Путь сброшен",
                        content="Выходной путь сброшен к оригиналу, "
                        "так как включена подмена файла.",
                        orient=Qt.Orientation.Horizontal,
                        isClosable=True,
                        position=InfoBarPosition.TOP,
                        duration=5000,
                        parent=self,
                    )

            # --- Логика для VideoProcessor: Lossless зависимости ---
            if (
                self._script.name == "Видео-процессор"
                and field.key == "lossless"
            ):
                # Принудительно обновляем видимость
                self._update_visibility()

                relevant_keys = [
                    "nvenc_rc",
                    "v_qp",
                    "nvenc_preset",
                    "audio_codec",
                ]

                if is_checked:
                    # 1. ЗАПОМИНАЕМ текущие значения перед включением
                    for key in relevant_keys:
                        w = self._settings_widgets.get(key)
                        if w:
                            if isinstance(w, ComboBox):
                                self._vproc_backup[key] = w.currentText()
                            elif isinstance(w, SpinBox):
                                self._vproc_backup[key] = w.value()

                    # 2. ПРИМЕНЯЕМ оптимизацию
                    updates = {
                        "nvenc_rc": "constqp",
                        "v_qp": 0,
                        "nvenc_preset": "p1",
                        "audio_codec": "copy",
                    }
                    for key, val in updates.items():
                        # В конфиг
                        SettingsManager().set_script_setting(
                            self._script.name, key, val
                        )
                        # В UI
                        w = self._settings_widgets.get(key)
                        if w:
                            if isinstance(w, ComboBox):
                                w.setCurrentText(str(val))
                            elif isinstance(w, SpinBox):
                                try:
                                    w.setValue(int(str(val)))
                                except (ValueError, TypeError):
                                    pass

                    # Принудительно обновляем блокировку и видимость
                    self._update_visibility()
                else:
                    # 3. ВОЗВРАЩАЕМ старые значения
                    for key in relevant_keys:
                        w = self._settings_widgets.get(key)
                        if w:
                            # Восстанавливаем из бэкапа, если он есть
                            if key in self._vproc_backup:
                                val = self._vproc_backup[key]
                                if isinstance(w, ComboBox):
                                    w.setCurrentText(str(val))
                                elif isinstance(w, SpinBox):
                                    w.setValue(int(val))

                                SettingsManager().set_script_setting(
                                    self._script.name, key, val
                                )

                    # Принудительно обновляем блокировку и видимость
                    self._update_visibility()

        # Подключаем сигнал ДО установки начального значения. Это гарантирует
        # срабатывание логики (например, lossless) при старте UI
        widget.stateChanged.connect(on_state_changed)

        # Устанавливаем сохраненное значение
        saved_val = SettingsManager().get_script_setting(
            self._script.name, field.key, bool(field.default), type_hint=bool
        )
        widget.setChecked(saved_val)

        return widget

    def _create_int_setting_widget(
        self, field: SettingField, parent: QWidget
    ) -> SpinBox:
        widget = SpinBox(parent)
        widget.setRange(0, 1000000)
        saved_val = SettingsManager().get_script_setting(
            self._script.name, field.key, int(field.default), type_hint=int
        )
        widget.setValue(saved_val)
        widget.valueChanged.connect(
            lambda val: SettingsManager().set_script_setting(
                self._script.name, field.key, val
            )
        )
        widget.valueChanged.connect(
            lambda val: logger.info(
                "[%s] Настройка '%s' (%s) изменена на: %d",
                self._script.name,
                field.label,
                field.key,
                val,
            )
        )
        return widget

    def _create_keyword_list_widget(
        self, field: SettingField, parent: QWidget
    ) -> KeywordManagerWidget:
        """Создать виджет управления списком ключевых слов."""
        widget = KeywordManagerWidget(field.label, parent)
        saved_val = SettingsManager().get_script_setting(
            self._script.name, field.key, None
        )
        if saved_val is None:
            saved_val = list(field.default)
        widget.set_keywords(list(saved_val))

        widget.keywordsChanged.connect(
            lambda keywords: SettingsManager().set_script_setting(
                self._script.name, field.key, keywords
            )
        )
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

        # Дополнительная логика блокировки (enabled/disabled)
        if self._script.name == "Видео-процессор":
            is_lossless = bool(current_settings.get("lossless", False))
            relevant_keys = ["nvenc_rc", "v_qp", "nvenc_preset", "audio_codec"]
            for key in relevant_keys:
                if widget := self._settings_widgets.get(key):
                    # Если режим Lossless, блокируем выбор параметров
                    # битрейта/QP/пресета/аудио
                    widget.setEnabled(not is_lossless)

    def _add_file_list(self, layout: QVBoxLayout) -> None:
        """Добавить секцию списка файлов."""
        files_label = StrongBodyLabel("Файлы", self)
        layout.addWidget(files_label)

        script_name = str(self._script.name)
        if self._script.use_custom_widget and (
            "Подмена" in script_name or "Замена" in script_name
        ):
            self._create_stream_replace_widget(layout)
        elif self._script.use_custom_widget and (
            "Муксер" in script_name or "Муксинг" in script_name
        ):
            self._create_muxing_widget(layout)
        elif self._script.use_custom_widget and (
            "Массовое извлечение" in script_name or "Демуксинг" in script_name
        ):
            self._create_track_extract_widget(layout)
        elif self._script.use_custom_widget and "поток" in script_name.lower():
            self._create_stream_manager_widget(layout)
        elif self._script.use_custom_widget and "VTT" in script_name:
            self._create_ass_filter_widget(layout)
        else:
            self._create_generic_file_list(layout)

    def _create_stream_replace_widget(self, layout: QVBoxLayout) -> None:
        self._stream_replace_widget = StreamReplaceWidget(self)
        self._file_list = self._stream_replace_widget
        layout.addWidget(self._stream_replace_widget, stretch=1)
        self._stream_replace_widget.filesChanged.connect(
            self._update_path_placeholder
        )

    def _create_muxing_widget(self, layout: QVBoxLayout) -> None:
        self._file_list = MuxingTableWidget(self)
        self._file_list.filesChanged.connect(self._update_path_placeholder)
        layout.addWidget(self._file_list, stretch=1)

    def _create_track_extract_widget(self, layout: QVBoxLayout) -> None:
        self._track_extract_widget = TrackExtractWidget(self)
        self._file_list = self._track_extract_widget
        layout.addWidget(self._track_extract_widget, stretch=1)
        self._track_extract_widget.filesChanged.connect(
            self._update_path_placeholder
        )

    def _create_stream_manager_widget(self, layout: QVBoxLayout) -> None:
        self._file_list = FileListWidget(
            allowed_extensions=(self._script.file_extensions),
            context_name=self._script.name,
            parent=self,
        )
        self._file_list.filesChanged.connect(self._update_path_placeholder)
        layout.addWidget(self._file_list, stretch=1)

        self._load_tracks_btn = PrimaryPushButton(
            FluentIcon.SYNC, "Загрузить дорожки", self
        )
        self._load_tracks_btn.clicked.connect(self._on_load_tracks_clicked)
        layout.addWidget(self._load_tracks_btn)

        self._track_widget = TrackListWidget(self)
        layout.addWidget(self._track_widget)
        self._file_list.filesChanged.connect(self._on_files_changed)

    def _create_generic_file_list(self, layout: QVBoxLayout) -> None:
        self._file_list = FileListWidget(
            allowed_extensions=(self._script.file_extensions),
            context_name=self._script.name,
            parent=self,
        )
        self._file_list.filesChanged.connect(self._update_path_placeholder)
        layout.addWidget(self._file_list, stretch=1)

    def _create_ass_filter_widget(
        self,
        layout: QVBoxLayout,
    ) -> None:
        """Создать виджет фильтрации актёров ASS."""
        self._ass_filter_widget = AssFilterWidget(self)
        self._file_list = self._ass_filter_widget
        layout.addWidget(self._ass_filter_widget, stretch=1)
        self._ass_filter_widget.filesChanged.connect(
            self._update_path_placeholder
        )

        # Если есть настройка удаления тегов, связываем её с предпросмотром
        if "strip_formatting" in self._settings_widgets:
            cb = self._settings_widgets["strip_formatting"]
            if isinstance(cb, CheckBox):
                # Инициализируем начальное состояние
                self._ass_filter_widget.set_strip_formatting(cb.isChecked())
                # Подключаем сигнал для живого обновления предпросмотра
                cb.checkStateChanged.connect(
                    lambda: self._ass_filter_widget.set_strip_formatting(
                        cb.isChecked()
                    )
                )

        if "strip_caps" in self._settings_widgets:
            cb = self._settings_widgets["strip_caps"]
            if isinstance(cb, CheckBox):
                self._ass_filter_widget._strip_caps_cb.setChecked(
                    cb.isChecked()
                )
                cb.checkStateChanged.connect(
                    lambda: self._ass_filter_widget._strip_caps_cb.setChecked(
                        cb.isChecked()
                    )
                )
                self._ass_filter_widget._strip_caps_cb.checkStateChanged.connect(
                    lambda: cb.setChecked(
                        self._ass_filter_widget.get_strip_caps()
                    )
                )

    def _on_files_changed(self) -> None:
        """Обработчик изменения списка файлов."""
        if self._file_list is not None and self._track_widget is not None:
            # Синхронизируем дерево дорожек (удаляем ушедшие файлы)
            files = []
            if hasattr(self._file_list, "files"):
                files = self._file_list.files
            elif hasattr(self._file_list, "get_file_paths"):
                files = self._file_list.get_file_paths()

            self._track_widget.sync_with_files(files)

    def _on_load_tracks_clicked(self) -> None:
        """Обработчик кнопки «Загрузить дорожки»."""
        if self._track_widget is None:
            return
        paths = self._file_list.get_file_paths()
        if not paths:
            InfoBar.warning(
                title="Нет файлов",
                content=("Сначала добавьте MKV-файлы"),
                parent=self,
                position=InfoBarPosition.TOP,
                duration=3000,
            )
            return
        logger.info(
            "[Управление потоками] Загрузка дорожек для %d файлов", len(paths)
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

    def _add_fixed_bottom_bar(self, layout: QVBoxLayout) -> None:
        """Добавить компактную фиксированную нижнюю панель."""
        self.bottom_widget = QWidget(self)
        self.bottom_widget.setObjectName("fixedBottomBar")
        self.bottom_widget.setFixedHeight(106)
        self.bottom_widget.setStyleSheet(
            """
            QWidget#fixedBottomBar {
                background-color: transparent;
                border-top: 1px solid rgba(255, 255, 255, 0.08);
            }
            """
        )

        # Главный макет панели
        panel_layout = QVBoxLayout(self.bottom_widget)
        panel_layout.setContentsMargins(24, 6, 24, 16)
        panel_layout.setSpacing(12)

        # 1. Прогресс-бар (теперь выровнен по отступам 24px)
        progress_container = QWidget(self.bottom_widget)
        progress_layout = QVBoxLayout(progress_container)
        progress_layout.setContentsMargins(0, 6, 0, 0)
        progress_layout.setSpacing(12)

        self._progress = ProgressBar(progress_container)
        self._progress.setFixedHeight(4)
        self._progress.setVisible(True)

        self._status_label = CaptionLabel(progress_container)
        self._status_label.setText("Ожидание запуска...")

        progress_layout.addWidget(self._status_label)
        progress_layout.addWidget(self._progress)

        try:
            self._indeterminate_progress = IndeterminateProgressBar(
                progress_container
            )
            self._indeterminate_progress.setVisible(False)
            self._indeterminate_progress.setFixedHeight(4)
            progress_layout.addWidget(self._indeterminate_progress)
        except (NameError, ImportError):
            self._indeterminate_progress = None

        panel_layout.addWidget(progress_container)

        # 2. Кнопки и путь в одну строку
        btns_layout = QHBoxLayout()
        btns_layout.setSpacing(12)

        self._output_path_edit = LineEdit(self.bottom_widget)
        self._update_path_placeholder()
        self._output_path_edit.setFixedHeight(36)
        self._output_path_edit.textChanged.connect(
            self._on_output_path_changed
        )
        btns_layout.addWidget(self._output_path_edit, stretch=1)

        self._browse_output_btn = PushButton(
            FluentIcon.FOLDER, "Обзор", self.bottom_widget
        )
        self._browse_output_btn.setFixedWidth(100)
        self._browse_output_btn.setFixedHeight(36)
        self._browse_output_btn.clicked.connect(self._on_browse_output_clicked)
        btns_layout.addWidget(self._browse_output_btn)

        self._execute_btn = PrimaryPushButton(
            FluentIcon.PLAY, "Выполнить", self.bottom_widget
        )
        self._execute_btn.setFixedWidth(140)
        self._execute_btn.setFixedHeight(36)
        self._execute_btn.clicked.connect(self._on_execute_clicked)
        btns_layout.addWidget(self._execute_btn)

        panel_layout.addLayout(btns_layout)

        layout.addWidget(self.bottom_widget)

    def _on_browse_output_clicked(self) -> None:
        """Обработчик нажатия кнопки выбора папки."""
        from PyQt6.QtWidgets import QFileDialog

        folder = QFileDialog.getExistingDirectory(
            self,
            "Выберите папку для сохранения",
            str(Path.home()),
        )
        if folder:
            self._output_path_edit.setText(folder)
            logger.info(
                "[%s] Пользователь выбрал папку: %s", self._script.name, folder
            )

    def _update_path_placeholder(self) -> None:
        """Обновить плейсхолдер пути сохранения на базе глобальных настроек."""
        if not hasattr(self, "_output_path_edit"):
            return

        files = self._file_list.get_file_paths() if self._file_list else []

        # 1. Определяем базовую директорию
        if files:
            # Берем родительскую папку первого файла
            base_dir = files[0].parent.absolute()
        else:
            base_dir = Path(".").absolute()

        # 2. Определяем целевую папку
        if self._settings_manager.use_auto_subfolder:
            subfolder = self._settings_manager.default_output_subfolder
            target_dir = base_dir / subfolder
        else:
            target_dir = base_dir

        # 3. Формируем текст (абсолютный путь)
        placeholder = str(target_dir)
        self._output_path_edit.setPlaceholderText(placeholder)

    def _on_output_path_changed(self, text: str) -> None:
        """Обработчик изменения текста в поле выходного пути."""
        logger.info(
            "[%s] Ручной путь сохранения изменен на: '%s'",
            self._script.name,
            text,
        )

        has_custom_path = bool(text.strip())
        # Если задан кастомный путь — отключаем чекбокс подмены оригинала
        if "overwrite_source" in self._settings_widgets:
            widget = self._settings_widgets["overwrite_source"]
            if isinstance(widget, CheckBox):
                if has_custom_path:
                    # Сначала выключаем, потом блокируем, чтобы не вызвать
                    # лишних триггеров логики сброса в UI
                    if widget.isChecked():
                        widget.blockSignals(True)
                        widget.setChecked(False)
                        SettingsManager().set_script_setting(
                            self._script.name, "overwrite_source", False
                        )
                        widget.blockSignals(False)
                    widget.setEnabled(False)
                else:
                    widget.setEnabled(True)

    def get_settings(self) -> dict[str, Any]:
        """Получить текущие значения настроек со страницы.

        Returns:
            Словарь {ключ: значение} настроек.
        """
        self.preload_ui()
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
            elif isinstance(widget, SpinBox):
                settings[key] = widget.value()
            elif isinstance(widget, KeywordManagerWidget):
                settings[key] = widget.get_keywords()
            else:
                settings[key] = ""

        return settings

    def _on_execute_clicked(self) -> None:
        """Обработчик нажатия кнопки «Выполнить/Остановить»."""
        self.preload_ui()
        if self._worker is not None and self._worker.isRunning():
            logger.info("Пользователь нажал 'Остановить'")
            self._worker.cancel()
            self._execute_btn.setEnabled(False)
            return

        # Сбросим флаг отмены у скрипта перед новым запуском
        if hasattr(self._script, "_is_cancelled"):
            self._script._is_cancelled = False

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

        self._prepare_execution_ui()
        settings = self._get_current_settings()
        self._inject_script_settings(settings)

        logger.info(
            "Пользователь нажал 'Выполнить' для скрипта '%s'. "
            "Файлов: %d. Настройки: %s",
            self._script.name,
            len(files),
            settings,
        )

        self._worker = ScriptWorker(
            script=self._script,
            files=files,
            settings=settings,
            output_path=self._output_path_edit.text(),
            parent=self,
        )
        self._worker.progress.connect(self._on_progress_received)
        self._worker.finished.connect(self._on_finished)
        self._worker.error.connect(self._on_error)
        self._worker.start()

    def _on_progress_received(
        self,
        current: int,
        total: int,
        message: str,
        intra_percent: float | None = 0.0,
    ) -> None:
        """Обработка сигнала прогресса."""
        if (
            self._indeterminate_progress
            and self._indeterminate_progress.isVisible()
        ):
            self._indeterminate_progress.stop()
            self._indeterminate_progress.setVisible(False)
            self._progress.setVisible(True)

        if self._progress.maximum() != 1000:
            self._progress.setRange(0, 1000)  # Используем 1000 для плавности

        # Расчет общего процента (0-1000)
        total_float = float(total) if total > 0 else 1.0
        # Базовый прогресс по файлам + прогресс внутри текущего файла
        intra_val = intra_percent if intra_percent is not None else 0.0
        real_current = float(current) + (intra_val / 100.0)
        overall_percent = (real_current / total_float) * 1000.0

        self._progress.setValue(int(overall_percent))

        # Формируем текст статуса
        if total > 1:
            status_text = f"Обработано: {current}/{total}"
            if message:
                status_text += f" - {message}"
        else:
            # Для одного файла не пишем 0/1 или 1/1, показываем
            # только сообщение
            status_text = message or f"Обработка: {current}/{total}"

        self._status_label.setText(status_text)

    def _prepare_execution_ui(self) -> None:
        self._log_area.clear()
        self._execute_btn.setText("Остановить")
        self._execute_btn.setIcon(FluentIcon.CLOSE)

        # Сброс прогресс-бара в неопределенное состояние перед запуском
        self._progress.setRange(0, 0)
        self._progress.setValue(0)

        if getattr(self, "_indeterminate_progress", None):
            self._progress.setVisible(False)
            self._indeterminate_progress.setVisible(True)
            self._indeterminate_progress.start()
        else:
            self._progress.setVisible(True)
            self._progress.setRange(0, 0)

    def _inject_script_settings(self, settings: dict[str, Any]) -> None:
        if self._track_widget is not None:
            per_file = self._track_widget.get_selected_tracks_per_file()
            settings["selected_tracks_per_file"] = per_file
            logger.info(
                "[Управление потоками] Выбранные дорожки: %s", per_file
            )

        if self._track_extract_widget is not None:
            per_file = (
                self._track_extract_widget.get_selected_tracks_per_file()
            )
            settings["selected_tracks_per_file"] = per_file
            logger.info("[Демуксинг] Выбранные дорожки: %s", per_file)

        if self._stream_replace_widget is not None:
            container = self._stream_replace_widget.get_container_path()
            replacements = self._stream_replace_widget.get_replacements()
            if container:
                settings["container_path"] = str(container)
            settings["replacements"] = {
                str(k): {"path": str(v["path"]), "src_id": v["src_id"]}
                for k, v in replacements.items()
            }
            logger.info(
                "[Подмена потоков] Контейнер: '%s', замен: %d",
                container.name if container else "None",
                len(replacements),
            )

        if self._ass_filter_widget is not None:
            excluded_actors = self._ass_filter_widget.get_excluded_actors()
            settings["excluded_actors"] = excluded_actors
            excluded_styles = self._ass_filter_widget.get_excluded_styles()
            settings["excluded_styles"] = excluded_styles
            excluded_effects = self._ass_filter_widget.get_excluded_effects()
            settings["excluded_effects"] = excluded_effects

            # Добавляем капс и ручные исключения конкретных строк
            settings["strip_caps"] = self._ass_filter_widget.get_strip_caps()
            manual_excl = self._ass_filter_widget.get_manual_exclusions()
            settings["manual_exclusions"] = manual_excl

            total_manual = sum(
                len(indices) for indices in manual_excl.values()
            )
            if (
                total_manual > 0
                or excluded_actors
                or excluded_styles
                or excluded_effects
            ):
                logger.info(
                    "[ASS → VTT] Исключено: актёров: %d, стилей: %d, "
                    "эффектов: %d, строк вручную: %d",
                    len(excluded_actors),
                    len(excluded_styles),
                    len(excluded_effects),
                    total_manual,
                )

    def _on_progress(
        self,
        current: int,
        total: int,
        message: str,
        intra_percent: float | None = None,
    ) -> None:
        """Обработка прогресса выполнения."""
        self.preload_ui()
        if total <= 0:
            return

        percent = int((current / total) * 100)

        # Переключение из режима ожидания (indeterminate) в режим прогресса
        # Делаем это только когда обработан хотя бы один файл (current > 0)
        # Если файлов всего один, оставляем бегущую полоску до самого конца
        if (
            self._indeterminate_progress
            and self._indeterminate_progress.isVisible()
            and current > 0
        ):
            self._indeterminate_progress.stop()
            self._indeterminate_progress.setVisible(False)
            self._progress.setVisible(True)

        if self._progress.maximum() == 0:
            self._progress.setRange(0, 100)

        self._progress.setValue(percent)
        self._status_label.setText(
            f"Обработано: {current}/{total} - {message}"
        )

    def _on_finished(self, results: list[str]) -> None:
        """Обработка завершения выполнения."""
        self.preload_ui()
        self._execute_btn.setText("Выполнить")
        self._execute_btn.setIcon(FluentIcon.PLAY)
        self._execute_btn.setEnabled(True)
        if getattr(self, "_indeterminate_progress", None):
            self._indeterminate_progress.setVisible(False)
            self._indeterminate_progress.stop()

        self._progress.setRange(0, 100)
        self._progress.setVisible(True)
        self._progress.setValue(100)
        self._log_area.setPlainText("\n".join(results))

        success = sum(1 for r in results if r.startswith("✅"))
        skipped = sum(1 for r in results if r.startswith("⏭"))
        errors = sum(1 for r in results if r.startswith("❌"))

        self._show_finished_notification(success, skipped, errors)

    def _show_finished_notification(
        self, success: int, skipped: int, errors: int
    ) -> None:
        stats = []
        if success:
            stats.append(f"Успешно: {success}")
        if skipped:
            stats.append(f"Пропущено: {skipped}")
        if errors:
            stats.append(f"Ошибок: {errors}")

        content = ", ".join(stats) if stats else "Результатов нет"

        if self._script.is_cancelled:
            show_info, title = InfoBar.warning, "Операция прервана"
        elif errors > 0:
            show_info, title = InfoBar.error, "Завершено с ошибками"
        elif success == 0 and skipped > 0:
            show_info, title = InfoBar.warning, "Обработка пропущена"
        elif skipped > 0:
            show_info, title = InfoBar.warning, "Выполнено частично"
        else:
            show_info, title = InfoBar.success, "Выполнено"

        show_info(
            title=title,
            content=content,
            parent=self,
            position=InfoBarPosition.TOP,
            duration=5000,
        )
        logger.info(
            "Скрипт '%s' завершён: %d успехов, %d пропусков, %d ошибок",
            self._script.name,
            success,
            skipped,
            errors,
        )

    def _on_error(self, error_text: str) -> None:
        """Обработка ошибки выполнения.

        Args:
            error_text: Текст ошибки.
        """
        self.preload_ui()
        self._execute_btn.setText("Выполнить")
        self._execute_btn.setIcon(FluentIcon.PLAY)
        self._execute_btn.setEnabled(True)
        if getattr(self, "_indeterminate_progress", None):
            self._indeterminate_progress.setVisible(False)
            self._indeterminate_progress.stop()

        self._progress.setRange(0, 100)
        self._progress.setVisible(True)

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
