# -*- coding: utf-8 -*-
"""Виджет управления списком ключевых слов для поиска субтитров."""

import logging
from PyQt6.QtCore import pyqtSignal
from PyQt6.QtWidgets import (
    QWidget,
    QHBoxLayout,
)
from qfluentwidgets import (
    PushButton,
    TransparentToolButton,
    FluentIcon,
    Flyout,
    FlyoutView,
    LineEdit,
    CheckBox,
    StrongBodyLabel,
)

logger = logging.getLogger(__name__)


class KeywordManagerWidget(QWidget):
    """Виджет для управления списком ключевых слов.

    Позволяет добавлять новые слова и переключать их активность
    через выпадающее меню (MenuFlyout).
    """

    keywordsChanged = pyqtSignal(list)

    def __init__(self, label: str, parent: QWidget | None = None) -> None:
        """Инициализация виджета.

        Args:
            label: Текст на кнопке.
            parent: Родительский виджет.
        """
        super().__init__(parent)
        self._keywords: list[dict[str, bool | str]] = []
        self._label = label
        self._init_ui()

    def _init_ui(self) -> None:
        """Инициализация интерфейса."""
        self._layout = QHBoxLayout(self)
        self._layout.setContentsMargins(0, 0, 0, 0)

        self.btn = PushButton(FluentIcon.TAG, self._label, self)
        self.btn.clicked.connect(lambda: self._show_menu(set_focus=False))
        self._layout.addWidget(self.btn)

    def set_keywords(self, keywords: list[dict[str, bool | str]]) -> None:
        """Установить текущий список ключевых слов.

        Args:
            keywords: Список словарей {"word": str, "active": bool}.
        """
        self._keywords = keywords
        self._update_button_text()

    def get_keywords(self) -> list[dict[str, bool | str]]:
        """Получить текущий список ключевых слов."""
        return list(self._keywords)

    def _update_button_text(self) -> None:
        """Обновить текст на кнопке с количеством активных слов."""
        active_count = sum(1 for k in self._keywords if k.get("active"))
        self.btn.setText(
            f"{self._label} ({active_count}/{len(self._keywords)})"
        )

    def _show_menu(self, set_focus: bool = False) -> None:
        """Показать Flyout со списком слов и полем добавления.

        Args:
            set_focus: Нужно ли установить фокус на поле ввода сразу.
        """
        view = FlyoutView(title="Управление списком", content="")

        # Скрываем системную метку контента,
        # чтобы она не создавала зазор под заголовком
        if hasattr(view, "contentLabel"):
            view.contentLabel.hide()
            view.contentLabel.setFixedHeight(0)

        # Настройка встроенного макета
        if hasattr(view, "vBoxLayout"):
            view.vBoxLayout.setContentsMargins(0, 0, 0, 8)
            view.vBoxLayout.setSpacing(0)

        layout = view.layout()
        layout.setSpacing(0)
        layout.setContentsMargins(0, 0, 0, 0)
        view.setMinimumWidth(340)

        # 1. Заголовок "Добавить"
        add_title_label = StrongBodyLabel("Добавить новое", view)
        add_title_label.setStyleSheet(
            "font-size: 12px; opacity: 0.6; padding: 4px 16px 2px 16px;"
        )
        layout.addWidget(add_title_label)

        # 2. Секция добавления нового слова
        add_container = QWidget()
        add_layout = QHBoxLayout(add_container)
        add_layout.setContentsMargins(16, 4, 10, 12)

        self._new_word_edit = LineEdit()
        self._new_word_edit.setPlaceholderText("Новое слово...")
        # Убираем фиксированную ширину, чтобы LineEdit занимал всё пространство

        add_btn = TransparentToolButton(FluentIcon.ADD, add_container)
        add_btn.setFixedSize(32, 32)

        add_layout.addWidget(self._new_word_edit, stretch=1)
        add_layout.addWidget(add_btn)

        layout.addWidget(add_container)

        # 3. Список существующих слов
        if self._keywords:
            # Заголовок "Список"
            list_title_label = StrongBodyLabel("Текущий список", view)
            list_title_label.setStyleSheet(
                "font-size: 12px; opacity: 0.6; padding: 8px 16px 2px 16px;"
            )
            layout.addWidget(list_title_label)

            sep = QWidget()
            sep.setFixedHeight(1)
            sep.setStyleSheet(
                "background-color: rgba(255, 255, 255, 0.1); "
                "margin: 0px 16px 8px 16px;"
            )
            layout.addWidget(sep)

            for i, item in enumerate(self._keywords):
                word = str(item.get("word", ""))
                active = bool(item.get("active", True))

                word_container = QWidget()
                word_layout = QHBoxLayout(word_container)
                word_layout.setContentsMargins(16, 2, 10, 2)

                cb = CheckBox(word)
                cb.setChecked(active)
                cb.stateChanged.connect(
                    lambda state, idx=i: self._toggle_keyword(idx, bool(state))
                )

                del_btn = TransparentToolButton(
                    FluentIcon.DELETE, word_container
                )
                del_btn.setFixedSize(32, 32)
                del_btn.clicked.connect(
                    lambda _, idx=i: self._delete_and_refresh(idx)
                )

                word_layout.addWidget(cb, stretch=1)
                word_layout.addWidget(del_btn)

                layout.addWidget(word_container)

        self._current_flyout = Flyout.make(view, self.btn, self)
        add_btn.clicked.connect(self._add_keyword)
        self._new_word_edit.returnPressed.connect(self._add_keyword)

        self._current_flyout.show()
        if set_focus:
            self._new_word_edit.setFocus()

    def _add_keyword(self) -> None:
        """Добавить новое ключевое слово."""
        word = self._new_word_edit.text().strip()
        if not word:
            return

        if any(k["word"] == word for k in self._keywords):
            logger.warning("Ключевое слово '%s' уже есть в списке", word)
            return

        self._keywords.append({"word": word, "active": True})
        logger.info("Добавлено ключевое слово: '%s'", word)

        self._update_button_text()
        self.keywordsChanged.emit(self._keywords)

        if hasattr(self, "_current_flyout"):
            self._current_flyout.hide()
            # Переоткрываем, чтобы обновить список, сохраняя фокус
            self._show_menu(set_focus=True)

    def _delete_and_refresh(self, index: int) -> None:
        """Удалить ключевое слово и обновить меню."""
        if 0 <= index < len(self._keywords):
            item = self._keywords.pop(index)
            logger.info("Удалено ключевое слово: '%s'", item['word'])
            self._update_button_text()
            self.keywordsChanged.emit(self._keywords)

            if hasattr(self, "_current_flyout"):
                self._current_flyout.hide()
                # После удаления фокус возвращать не обязательно, но для
                # консистентности можно (хотя здесь оставим False)
                self._show_menu(set_focus=False)

    def _toggle_keyword(self, index: int, active: bool) -> None:
        """Переключить активность ключевого слова.

        Args:
            index: Индекс в списке.
            active: Новое состояние.
        """
        if 0 <= index < len(self._keywords):
            self._keywords[index]["active"] = active
            logger.info(
                "Ключевое слово '%s' %s",
                self._keywords[index]['word'],
                "активировано" if active else "деактивировано"
            )
            self._update_button_text()
            self.keywordsChanged.emit(self._keywords)
