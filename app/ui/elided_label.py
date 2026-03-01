# -*- coding: utf-8 -*-
"""Модуль с виджетом для отображения сокращенного текста."""

from PyQt6.QtCore import Qt
from PyQt6.QtGui import QResizeEvent
from qfluentwidgets import BodyLabel, ToolTipFilter, ToolTipPosition


class ElidedLabel(BodyLabel):
    """Метка, которая сокращает текст многоточием посередине (для путей).

    Обеспечивает сохранение видимости расширения файла и начала пути,
    заменяя длинную среднюю часть многоточием.
    """

    def __init__(self, text: str = "", parent=None):
        super().__init__(parent)
        self._full_text = ""
        self._elide_mode = Qt.TextElideMode.ElideMiddle

        # Устанавливаем фильтр для красивых подсказок Fluent
        self._tooltip_filter = ToolTipFilter(
            self, showDelay=300, position=ToolTipPosition.TOP
        )
        self.installEventFilter(self._tooltip_filter)

        self.setText(text)

    def setText(self, text: str):
        """Установить полный текст и обновить отображение."""
        self._full_text = text
        self._update_elided_text()

    def setElideMode(self, mode: Qt.TextElideMode):
        """Установить режим обрезки."""
        self._elide_mode = mode
        self._update_elided_text()

    def resizeEvent(self, event: QResizeEvent):
        """Пересчитывать обрезку при изменении размера виджета."""
        super().resizeEvent(event)
        self._update_elided_text()

    def _update_elided_text(self):
        """Вычислить и установить сокращенный текст."""
        if not self._full_text:
            super().setText("")
            self.setToolTip("")
            return

        metrics = self.fontMetrics()
        # Вычитаем небольшой отступ, чтобы текст не прилипал к границам
        width = self.width() - 4

        if width <= 0:
            return

        elided = metrics.elidedText(self._full_text, self._elide_mode, width)
        super().setText(elided)

        # Показываем подсказку только если текст сокращен
        is_elided = elided != self._full_text
        self.setToolTip(self._full_text if is_elided else "")

    def fullText(self) -> str:
        """Вернуть исходный полный текст."""
        return self._full_text
