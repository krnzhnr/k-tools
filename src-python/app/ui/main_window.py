# -*- coding: utf-8 -*-
"""Главное окно приложения K-Tools."""

import logging

from PyQt6.QtCore import Qt, QDateTime, QObject, QEvent
from PyQt6.QtGui import QIcon, QMouseEvent
from PyQt6.QtWidgets import QApplication, QWidget
from qfluentwidgets import (
    FluentIcon,
    NavigationItemPosition,
    FluentWindow,
)
from qfluentwidgets.components.widgets.stacked_widget import (
    DrillInTransitionStackedWidget,
)

from app.core.abstract_script import AbstractScript
from app.core.script_registry import ScriptRegistry
from app.core.constants import CATEGORY_CONFIG
from app.core.resource_utils import get_resource_path
from app.ui.work_panel import ScriptPage
from app.ui.settings_page import SettingsPage
from app.ui.home_page import HomePage
from app.ui.log_page import LogPage
from app.ui.dependency_setup_page import DependencySetupPage
from app.core.dependency_manager import DependencyManager
from app.core.settings_manager import SettingsManager

logger = logging.getLogger(__name__)

# Безопасный глобальный патч класса Flyout из библиотеки QFluentWidgets.
# В Nuitka-сборке анимация fadeOut на windowOpacity у Flyout вызывает Segfault
# при одновременной смене страниц stackedWidget.
# Мы заменяем проигрывание анимации на мгновенное скрытие и закрытие.
try:
    from qfluentwidgets.components.widgets.flyout import Flyout
    from PyQt6.QtGui import QCloseEvent

    def _safe_fade_out(self) -> None:
        """Безопасное скрытие всплывающего меню без анимации.

        Использует отложенный вызов для корректного завершения событий мыши
        в событийном цикле Qt перед скрытием виджета.
        """
        from PyQt6.QtCore import QTimer

        # Скрываем и закрываем меню с безопасной задержкой в 50 мс.
        # Это дает обработчикам событий мыши в Qt полностью завершиться.
        QTimer.singleShot(50, lambda: (self.hide(), self.close()))

    def _safe_close_event(self, e: QCloseEvent) -> None:
        """Безопасная обработка закрытия всплывающего меню.

        Исключает вызов deleteLater(), предотвращая асинхронное
        уничтожение C++ объекта во время смены страниц интерфейса,
        что полностью защищает приложение от Segfault в Nuitka.
        """
        e.accept()
        self.closed.emit()

    Flyout.fadeOut = _safe_fade_out
    Flyout.closeEvent = _safe_close_event
    logger.info(
        "Глобальный патч Flyout (fadeOut и closeEvent) успешно применен."
    )
except Exception:
    logger.exception(
        "Не удалось применить глобальный патч Flyout."
    )


class MainWindow(FluentWindow):
    """Главное окно приложения K-Tools.

    Реализует интерфейс в стиле PowerToys:
    навигация слева, рабочая панель справа.
    """

    WINDOW_WIDTH = 792
    WINDOW_HEIGHT = 960

    def __init__(
        self,
        registry: ScriptRegistry,
        force_logs_tab: bool = False,
    ) -> None:
        """Инициализация главного окна.

        Args:
            registry: Реестр зарегистрированных скриптов.
            force_logs_tab: Принудительно показать
                и активировать вкладку логов.
        """
        super().__init__()
        self._registry = registry
        self._force_logs_tab = force_logs_tab
        self._script_pages: dict[str, ScriptPage] = {}
        self._shown = False
        self._settings_manager = SettingsManager()
        self._log_page: LogPage | None = None

        # Включаем DrillIn-анимацию переходов
        self._replace_stacked_view()

        self._setup_window()

        # Инициализация главной страницы
        self._home_page = HomePage(
            list(self._registry.scripts), self._resolve_icon, self
        )
        self._home_page.scriptRequested.connect(self._on_script_requested)

        # Инициализация страницы управления зависимостями
        self._dependency_page = DependencySetupPage(self)
        self._dependency_page.dependenciesChanged.connect(
            self._on_dependencies_changed
        )

        self._setup_navigation()

        logger.info(
            "Главное окно инициализируется с %d скриптами в реестре "
            "(force_logs=%s)",
            len(registry),
            force_logs_tab,
        )

        app_inst = QApplication.instance()
        if app_inst is not None:
            app_inst.installEventFilter(self)

    def _replace_stacked_view(self) -> None:
        """Замена PopUp-анимации на DrillIn."""
        old_view = self.stackedWidget.view
        old_view.hide()
        self.stackedWidget.hBoxLayout.removeWidget(old_view)
        old_view.currentChanged.disconnect(self.stackedWidget.currentChanged)
        old_view.deleteLater()

        new_view = DrillInTransitionStackedWidget(self.stackedWidget)
        self.stackedWidget.view = new_view
        self.stackedWidget.hBoxLayout.addWidget(new_view)
        new_view.currentChanged.connect(self.stackedWidget.currentChanged)

        # Патч API: DrillIn принимает (duration, isBack)
        # вместо (popOut, showNext, duration, easing)
        sw = self.stackedWidget
        duration = 250

        def _set_current_widget(
            widget: QWidget,
            popOut: bool = True,  # noqa: ARG001
        ) -> None:
            from PyQt6.QtWidgets import (
                QAbstractScrollArea,
            )

            if isinstance(widget, QAbstractScrollArea):
                bar = widget.verticalScrollBar()
                if bar is not None:
                    bar.setValue(0)

            # Отложенный вызов переключения для избежания Segfault в Nuitka.
            # Позволяет Popup-меню qfluentwidgets корректно закрыться и
            # удалиться ДО того, как DrillIn-анимация начнет менять
            # структуру виджетов.
            from PyQt6.QtCore import QTimer
            QTimer.singleShot(
                10,
                lambda: sw.view.setCurrentWidget(
                    widget, duration=duration
                ),
            )

        def _set_current_index(
            index: int,
            popOut: bool = True,  # noqa: ARG001
        ) -> None:
            _set_current_widget(sw.view.widget(index))

        sw.setCurrentWidget = _set_current_widget
        sw.setCurrentIndex = _set_current_index

        logger.info("DrillIn-анимация переходов установлена")

    def _setup_window(self) -> None:
        """Настройка параметров окна."""
        self.setWindowTitle("K-Tools")
        icon_path = get_resource_path("app_icon.ico")
        self.setWindowIcon(QIcon(icon_path))
        self.setMinimumSize(self.WINDOW_WIDTH, self.WINDOW_HEIGHT)
        self.resize(self.WINDOW_WIDTH, self.WINDOW_HEIGHT)
        logger.info(
            "Установлен минимальный размер окна: %dx%d",
            self.WINDOW_WIDTH,
            self.WINDOW_HEIGHT,
        )

        # Центрирование окна на экране
        screen = QApplication.primaryScreen()
        if screen:
            screen_geometry = screen.availableGeometry()
            x = (screen_geometry.width() - self.WINDOW_WIDTH) // 2
            y = (screen_geometry.height() - self.WINDOW_HEIGHT) // 2
            self.move(x, y)
            logger.info("Окно центрировано на экране в позиции (%d, %d)", x, y)

    def resizeEvent(self, event) -> None:
        """Логирование изменения размера окна."""
        super().resizeEvent(event)
        size = event.size()
        logger.debug(
            "Размер окна изменен пользователем: %dx%d",
            size.width(),
            size.height(),
        )

    def mousePressEvent(self, event: QMouseEvent) -> None:
        """Обработка нажатия кнопок мыши для навигации.

        Args:
            event: Событие мыши.
        """
        if event.button() in (
            Qt.MouseButton.XButton1,
            Qt.MouseButton.XButton2,
        ):
            logger.debug(
                "Нажата кнопка навигации мыши: %s",
                event.button(),
            )

        super().mousePressEvent(event)

    def mouseReleaseEvent(self, event: QMouseEvent) -> None:
        """Обработка отпускания кнопок мыши для навигации.

        Args:
            event: Событие мыши.
        """
        if event.button() == Qt.MouseButton.XButton1:
            if self.navigationInterface.panel.returnButton.isEnabled():
                logger.info(
                    "Отпущена кнопка мыши XButton1: "
                    "запуск перехода назад."
                )
                self.navigationInterface.panel.returnButton.click()
        elif event.button() == Qt.MouseButton.XButton2:
            logger.debug("Отпущена кнопка навигации вперед.")

        super().mouseReleaseEvent(event)

    def eventFilter(self, watched: QObject, event: QEvent) -> bool:
        """Глобальный фильтр событий для навигации по кнопкам мыши.

        Перехватывает нажатия и отпускания боковых кнопок мыши
        (XButton1/XButton2) во всем приложении до того, как они будут
        поглощены интерактивными виджетами (такими как таблицы
        муксинга или списки файлов).
        """
        if (
            event.type() in (
                QEvent.Type.MouseButtonPress,
                QEvent.Type.MouseButtonRelease,
            )
            and isinstance(event, QMouseEvent)
        ):
            if event.button() == Qt.MouseButton.XButton1:
                # Переход назад происходит строго при отпускании кнопки
                if event.type() == QEvent.Type.MouseButtonRelease:
                    if self.navigationInterface.panel.returnButton.isEnabled():
                        logger.info(
                            "Перехвачен глобальный переход назад "
                            "по кнопке мыши XButton1."
                        )
                        self.navigationInterface.panel.returnButton.click()
                return True
            elif event.button() == Qt.MouseButton.XButton2:
                if event.type() == QEvent.Type.MouseButtonRelease:
                    logger.debug(
                        "Перехвачено глобальное событие навигации "
                        "вперед по кнопке мыши XButton2."
                    )
                return True

        return super().eventFilter(watched, event)

    def showEvent(self, event) -> None:
        """Подгонка layout при первом показе окна.

        Args:
            event: Объект события.
        """
        super().showEvent(event)
        if not self._shown:
            self._shown = True
            self.resize(
                self.WINDOW_WIDTH,
                self.WINDOW_HEIGHT,
            )
            logger.info("Layout обновлён при первом показе")
            self._start_auto_check_updates()

            # Автоматически перенаправляем на зависимости при первом старте,
            # если чего-то не хватает
            if DependencyManager().has_any_missing():
                logger.info(
                    "Обнаружены недостающие зависимости при запуске. "
                    "Переключение на страницу установки зависимостей."
                )
                self.switchTo(self._dependency_page)

    def _on_dependencies_changed(self) -> None:
        """Обработчик изменения состояния внешних зависимостей.

        Обновляет доступность карточек на главной странице,
        навигационных пунктов в меню и баннеров в открытых страницах скриптов.
        """
        logger.info(
            "Статус зависимостей изменился. Обновление интерфейса..."
        )
        self._home_page.refresh_availability()
        self._update_navigation_availability()

        current_widget = self.stackedWidget.currentWidget()
        if hasattr(current_widget, "check_dependencies"):
            current_widget.check_dependencies()

    def _update_navigation_availability(self) -> None:
        """Обновить доступность навигационных пунктов скриптов."""
        dep_mgr = DependencyManager()

        for page in self._script_pages.values():
            script = page.script
            is_available = dep_mgr.is_script_available(
                script.required_dependencies
            )

            route_key = page.objectName()
            panel = self.navigationInterface.panel

            if route_key in panel.items:
                nav_item = panel.items[route_key]
                if nav_item.widget:
                    nav_item.widget.setEnabled(is_available)
                    if not is_available:
                        nav_item.widget.setStyleSheet(
                            "color: rgba(255, 255, 255, 0.3);"
                        )
                    else:
                        nav_item.widget.setStyleSheet("")

    def _start_auto_check_updates(self) -> None:
        """Запуск фоновой проверки обновлений при запуске."""
        if not self._settings_manager.auto_check_updates:
            return

        from app.core.update_worker import UpdateCheckerWorker

        self._auto_checker = UpdateCheckerWorker(
            self._settings_manager.include_pre_releases, self
        )
        self._auto_checker.checkFinished.connect(self._on_auto_check_finished)
        self._auto_checker.finished.connect(self._auto_checker.deleteLater)
        self._auto_checker.start()

    def _on_auto_check_finished(
        self, available: bool, version: str, changelog: str, download_url: str
    ) -> None:
        """Хэндлер завершения фоновой автопроверки обновлений."""
        now_str = QDateTime.currentDateTime().toString("dd.MM.yyyy hh:mm:ss")
        self._settings_manager.last_check_time = now_str
        logger.info(
            "Автоматическая проверка завершена и зафиксирована в: %s",
            now_str,
        )

        if not available:
            return

        # Переключаем кнопку в настройках в режим скачивания
        if hasattr(self, "_settings_page"):
            self._settings_page.set_update_available(
                version, download_url
            )

        from qfluentwidgets import InfoBar, InfoBarPosition, PushButton

        content_text = f"Доступна версия {version}."

        def on_info_bar_clicked() -> None:
            """Переход в настройки с прокруткой к кнопке скачивания."""
            if hasattr(self, "_settings_page"):
                self.switchTo(self._settings_page)
                self._settings_page.scroll_to_download_with_tip()

        bar = InfoBar.info(
            title="Доступно обновление",
            content=content_text,
            orient=Qt.Orientation.Horizontal,
            isClosable=True,
            position=InfoBarPosition.TOP,
            duration=10000,
            parent=self,
        )
        btn = PushButton(self.tr("Скачать"), bar)
        btn.setFixedWidth(100)
        btn.clicked.connect(on_info_bar_clicked)
        btn.clicked.connect(bar.close)
        bar.hBoxLayout.addWidget(btn)

    def _setup_navigation(self) -> None:
        """Настройка навигационной панели со скриптами."""
        self.addSubInterface(
            interface=self._home_page, icon=FluentIcon.HOME, text="Главная"
        )

        self.addSubInterface(
            interface=self._dependency_page,
            icon=FluentIcon.DOWNLOAD,
            text="Зависимости",
        )

        categories = self._group_scripts()

        # 1. Сначала добавим известные категории (сохраняя порядок)
        for cat_name, config in CATEGORY_CONFIG.items():
            if cat_name in categories:
                self._add_category_to_nav(
                    cat_name,
                    categories[cat_name],
                    icon=self._resolve_icon(str(config["icon"])),
                    route_key=str(config["nav_key"]),
                )

        # 2. Затем добавляем все остальные категории, если они есть
        for cat_name, scripts in categories.items():
            if cat_name not in CATEGORY_CONFIG:
                logger.warning(
                    "Обнаружена неизвестная категория: %s", cat_name
                )
                self._add_category_to_nav(
                    cat_name,
                    scripts,
                    icon=FluentIcon.FOLDER,
                    route_key=f"cat_{cat_name}",
                )

        self._settings_page = SettingsPage(self)
        self.addSubInterface(
            interface=self._settings_page,
            icon=FluentIcon.SETTING,
            text="Настройки",
            position=NavigationItemPosition.BOTTOM,
        )

        self._settings_page.showLogsChanged.connect(self._on_show_logs_changed)

        # Добавление вкладки логов, если включено или требуется принудительно
        if self._settings_manager.show_logs_tab or self._force_logs_tab:
            self._add_log_interface()
            if self._force_logs_tab:
                logger.warning(
                    "Вкладка логов добавлена в навигацию принудительно "
                    "из-за ошибок файлового логирования"
                )

        self.stackedWidget.currentChanged.connect(
            self._on_current_page_changed
        )

        fm = self.fontMetrics()
        max_width = max(
            (fm.horizontalAdvance(s.name) for s in self._registry.scripts),
            default=160,
        )
        self.navigationInterface.setExpandWidth(max_width + 120)

        self._update_navigation_availability()

        logger.info(
            "Навигационная панель успешно настроена. Всего скриптов: %d",
            len(self._script_pages),
        )

        # ФОНОВАЯ ИНИЦИАЛИЗАЦИЯ (Истинный Lazy Load):
        # Даем главному окну 500мс на 100% плавную отрисовку, а затем тихо
        # строим интерфейсы остальных вкладок по очереди с шагом 150мс.
        from PyQt6.QtCore import QTimer

        # Хранение сильных ссылок на таймеры для Nuitka
        self._preload_timers = []
        delay = 500
        for page in self._script_pages.values():
            t = QTimer(self)
            t.setSingleShot(True)
            t.timeout.connect(page.preload_ui)
            t.start(delay)
            self._preload_timers.append(t)
            delay += 150

    def _group_scripts(self) -> dict[str, list[AbstractScript]]:
        """Группировка скриптов по категориям."""
        categories: dict[str, list[AbstractScript]] = {}
        for script in self._registry.scripts:
            categories.setdefault(script.category, []).append(script)
        return categories

    def _add_category_to_nav(
        self,
        cat_name: str,
        scripts: list[AbstractScript],
        icon: FluentIcon,
        route_key: str,
    ) -> None:
        """Добавление категории и её скриптов в навигацию.

        Args:
            cat_name: Имя категории.
            scripts: Список скриптов.
            icon: Иконка категории.
            route_key: Псевдоним маршрута.
        """
        parent_item = self.navigationInterface.addItem(
            routeKey=route_key, icon=icon, text=cat_name, selectable=False
        )
        parent_item.setObjectName(route_key)

        for script in scripts:
            page = ScriptPage(script=script, parent=self)
            safe_id = "".join(
                c for c in script.__class__.__name__ if c.isalnum()
            )
            page.setObjectName(safe_id)
            self._script_pages[script.name] = page

            self.addSubInterface(
                interface=page,
                icon=self._resolve_icon(script.icon_name),
                text=script.name,
                parent=parent_item,
            )

    def _on_current_page_changed(self, index: int) -> None:
        """Логирование переключения страниц и сброс состояний навигации."""
        # Мгновенно сбрасываем залипшие эффекты наведения (hover)
        # на навигационной панели для предотвращения визуальных багов
        if hasattr(self, "navigationInterface") and self.navigationInterface:
            for widget in self.navigationInterface.findChildren(QWidget):
                if hasattr(widget, "isEnter"):
                    widget.isEnter = False
                if hasattr(widget, "isPressed"):
                    widget.isPressed = False
                if hasattr(widget, "isAboutSelected"):
                    widget.isAboutSelected = False
                widget.update()

        widget = self.stackedWidget.widget(index)
        page_name = widget.objectName() if widget else "Неизвестно"
        logger.info(
            "Пользователь переключился на страницу: %s (индекс: %d)",
            page_name,
            index,
        )

    def _on_script_requested(self, script_name: str) -> None:
        """Переключиться на страницу скрипта по названию.

        Этот метод вызывается при клике по карточке на главной странице.
        """
        if page := self._script_pages.get(script_name):
            logger.info(
                "Переход на страницу скрипта '%s' из Home", script_name
            )
            self.switchTo(page)

    def switchTo(self, interface: QWidget) -> None:
        """Переключить на страницу с безопасной задержкой.

        Позволяет всплывающим меню компактного режима полностью завершить
        анимации скрытия и удалиться из памяти до начала рендеринга
        нового интерфейса страницы, предотвращая критические ошибки
        доступа к памяти (Segfault) в скомпилированной Nuitka-версии.

        Args:
            interface: Виджет страницы, на которую выполняется переход.
        """
        from PyQt6.QtCore import QTimer

        # Мгновенно сбрасываем залипшие эффекты наведения (hover)
        # на навигационной панели для предотвращения визуальных багов
        if hasattr(self, "navigationInterface") and self.navigationInterface:
            for widget in self.navigationInterface.findChildren(QWidget):
                if hasattr(widget, "isEnter"):
                    widget.isEnter = False
                if hasattr(widget, "isPressed"):
                    widget.isPressed = False
                if hasattr(widget, "isAboutSelected"):
                    widget.isAboutSelected = False
                widget.update()

        logger.info(
            "Зарегистрирован запрос на переключение интерфейса: %s. "
            "Запуск отложенного выполнения через 80 мс для защиты "
            "памяти от Segfault в Nuitka.",
            interface.objectName() if interface else "Неизвестно",
        )

        # Получаем и сохраняем связанный метод родительского класса
        # во избежание потери контекста super() внутри lambda-замыкания
        parent_switch = super().switchTo
        QTimer.singleShot(80, lambda: parent_switch(interface))

    @staticmethod
    def _resolve_icon(icon_name: str) -> FluentIcon:
        """Преобразовать имя иконки в FluentIcon.

        Args:
            icon_name: Имя иконки (например, 'VIDEO').

        Returns:
            Соответствующая FluentIcon.
        """
        try:
            icon = FluentIcon[icon_name]
            logger.debug("Иконка '%s' успешно разрешена", icon_name)
            return icon
        except KeyError:
            logger.warning(
                "Иконка '%s' не найдена в FluentIcon, "
                "используется значение по умолчанию COMMAND_PROMPT",
                icon_name,
            )
            return FluentIcon.COMMAND_PROMPT

    def _on_show_logs_changed(self, show: bool) -> None:
        """Динамическое добавление или удаление вкладки логов."""
        if show:
            self._add_log_interface()
        else:
            self._remove_log_interface()

    def _add_log_interface(self) -> None:
        """Добавить интерфейс логов в навигацию."""
        if self._log_page:
            return

        self._log_page = LogPage(self)
        log_page = self._log_page

        # Добавляем вкладку логов в нижнюю часть навигации
        self.addSubInterface(
            interface=log_page,
            icon=FluentIcon.COMMAND_PROMPT,
            text="Логи",
            position=NavigationItemPosition.BOTTOM,
        )

        # Переставляем кнопку логов ПЕРЕД кнопкой настроек
        # в bottomLayout навигационной панели
        panel = self.navigationInterface.panel
        settings_key = self._settings_page.objectName()
        log_key = log_page.objectName()
        settings_nav = panel.items[settings_key].widget
        log_nav = panel.items[log_key].widget

        panel.bottomLayout.removeWidget(log_nav)
        settings_idx = panel.bottomLayout.indexOf(settings_nav)
        panel.bottomLayout.insertWidget(
            settings_idx,
            log_nav,
            0,
            Qt.AlignmentFlag.AlignBottom,
        )

        logger.info("Вкладка логов добавлена в навигацию над настройками")

    def _remove_log_interface(self) -> None:
        """Удалить интерфейс логов из навигации."""
        if not self._log_page:
            return

        # Важно очистить ресурсы (удалить обработчик логов)
        self._log_page.cleanup()

        route_key = self._log_page.objectName()

        # Удаление из stacked widget (до удаления из панели,
        # чтобы panel.removeWidget не удалил уже удалённый виджет)
        self.stackedWidget.removeWidget(self._log_page)

        # Полное удаление кнопки из навигационной панели.
        # panel.removeWidget(routeKey: str) удаляет элемент
        # из items dict, layout и вызывает deleteLater.
        self.navigationInterface.panel.removeWidget(route_key)

        self._log_page = None
        logger.info("Вкладка логов удалена из навигации")
