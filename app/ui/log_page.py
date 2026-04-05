# -*- coding: utf-8 -*-
"""Виджет для отображения логов приложения в реальном времени."""

import logging
from PyQt6.QtCore import Qt, pyqtSignal, QObject, QUrl
from PyQt6.QtGui import QTextCursor, QColor, QTextCharFormat, QDesktopServices
from PyQt6.QtWidgets import (
    QWidget,
    QVBoxLayout,
    QHBoxLayout,
    QPlainTextEdit,
)
from qfluentwidgets import (
    CardWidget,
    PushButton,
    FluentIcon,
    StrongBodyLabel,
    PrimaryPushButton,
)

logger = logging.getLogger(__name__)


class LogSignalEmitter(QObject):
    """Эмиттер сигналов для логирования, обеспечивающий потокобезопасность."""
    log_received = pyqtSignal(str, int)


class QtLogHandler(logging.Handler):
    """Обработчик логов Python, перенаправляющий сообщения в Qt-сигналы."""

    def __init__(self, emitter: LogSignalEmitter) -> None:
        super().__init__()
        self.emitter = emitter
        # Используем тот же формат, что и в основном файле логов
        self.setFormatter(logging.Formatter(
            "%(asctime)s | %(levelname)-8s | %(name)s | %(message)s",
            datefmt="%H:%M:%S"
        ))

    def emit(self, record: logging.LogRecord) -> None:
        """Перехват записи лога и испускание сигнала."""
        msg = self.format(record)
        self.emitter.log_received.emit(msg, record.levelno)


class LogPage(QWidget):
    """Страница отображения логов в реальном времени."""

    def __init__(self, parent: QWidget | None = None) -> None:
        super().__init__(parent=parent)
        self.setObjectName("logPage")
        self._max_lines = 2000

        # Цвета для уровней логирования в UI
        self._level_colors = {
            logging.DEBUG: QColor("#808080"),    # Серый
            logging.INFO: QColor("#FFFFFF"),     # Белый
            logging.WARNING: QColor("#FFB800"),  # Оранжево-желтый
            logging.ERROR: QColor("#FF4D4D"),    # Светло-красный
            logging.CRITICAL: QColor("#FF0000"), # Ярко-красный
        }

        self._signal_emitter = LogSignalEmitter()
        self._signal_emitter.log_received.connect(self._append_log)

        # Регистрация обработчика в корневом логгере
        self._handler = QtLogHandler(self._signal_emitter)
        logging.getLogger().addHandler(self._handler)

        self._init_ui()
        logger.info("Страница логов инициализирована и подключена к logging")

    def _init_ui(self) -> None:
        """Настройка пользовательского интерфейса."""
        layout = QVBoxLayout(self)
        layout.setContentsMargins(36, 10, 36, 30)
        layout.setSpacing(16)

        # Заголовок и кнопки управления
        header_layout = QHBoxLayout()
        header_layout.addWidget(StrongBodyLabel("Логи приложения (Real-time)"))
        header_layout.addStretch(1)

        self._copy_btn = PushButton(FluentIcon.COPY, "Копировать все", self)
        self._copy_btn.clicked.connect(self._copy_all)
        header_layout.addWidget(self._copy_btn)

        self._folder_btn = PushButton(FluentIcon.FOLDER, "Открыть папку", self)
        self._folder_btn.clicked.connect(self._open_log_folder)
        header_layout.addWidget(self._folder_btn)

        self._clear_btn = PrimaryPushButton(FluentIcon.DELETE, "Очистить", self)
        self._clear_btn.clicked.connect(self._clear_logs)
        header_layout.addWidget(self._clear_btn)

        layout.addLayout(header_layout)

        # Поле вывода логов
        self._card = CardWidget(self)
        card_layout = QVBoxLayout(self._card)
        card_layout.setContentsMargins(2, 2, 2, 2)

        self._log_view = QPlainTextEdit(self._card)
        self._log_view.setReadOnly(True)
        self._log_view.setUndoRedoEnabled(False)
        self._log_view.setMaximumBlockCount(self._max_lines)
        self._log_view.setStyleSheet("""
            QPlainTextEdit {
                background-color: transparent;
                border: none;
                font-family: 'Consolas', 'Monaco', 'Courier New', monospace;
                font-size: 13px;
                color: rgba(255, 255, 255, 0.8);
            }
        """)
        card_layout.addWidget(self._log_view)
        layout.addWidget(self._card)

    def _append_log(self, text: str, level: int) -> None:
        """Добавление строки лога в текстовое поле с цветовой индикацией."""
        color = self._level_colors.get(level, self._level_colors[logging.INFO])

        # Используем QTextCursor для вставки форматированного текста
        cursor = self._log_view.textCursor()
        cursor.movePosition(QTextCursor.MoveOperation.End)

        fmt = QTextCharFormat()
        fmt.setForeground(color)
        cursor.setCharFormat(fmt)

        cursor.insertText(text + "\n")

        # Автопрокрутка вниз
        self._log_view.moveCursor(QTextCursor.MoveOperation.End)

    def _open_log_folder(self) -> None:
        """Открыть директорию с логами в проводнике."""
        import os
        from pathlib import Path
        log_dir = Path("logs").absolute()
        if not log_dir.exists():
            log_dir.mkdir(parents=True, exist_ok=True)

        QDesktopServices.openUrl(QUrl.fromLocalFile(str(log_dir)))
        logger.info("Открыта папка логов: %s", log_dir)

    def _clear_logs(self) -> None:
        """Очистка окна логов."""
        self._log_view.clear()
        logger.info("Окно логов очищено пользователем")

    def _copy_all(self) -> None:
        """Копирование всех логов в буфер обмена."""
        from PyQt6.QtWidgets import QApplication
        QApplication.clipboard().setText(self._log_view.toPlainText())
        logger.info("Логи скопированы в буфер обмена")

    def cleanup(self) -> None:
        """Отключение обработчика логов и очистка ресурсов."""
        logging.getLogger().removeHandler(self._handler)
        logger.info("Обработчик логов QtLogHandler удален")

    def closeEvent(self, event) -> None:
        """Удаление обработчика при закрытии виджета."""
        self.cleanup()
        super().closeEvent(event)
