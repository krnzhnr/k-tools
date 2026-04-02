# -*- coding: utf-8 -*-
import os
from app.core.settings_manager import SettingsManager
from app.ui.settings_page import SettingsPage


def test_settings_manager_singleton():
    s1 = SettingsManager()
    s2 = SettingsManager()
    assert s1 is s2


def test_settings_manager_save_load():
    # Удаляем старый файл для чистоты теста
    if os.path.exists("settings.ini"):
        os.remove("settings.ini")

    s = SettingsManager()
    # Сброс синглтона для чистоты (но в жизни он один)
    # Т.к. QSettings лениво пишет, сбросим
    s.overwrite_existing = True
    assert s.overwrite_existing is True

    s.overwrite_existing = False
    assert s.overwrite_existing is False

    s.clear_list_on_add = True
    assert s.clear_list_on_add is True

    s.clear_list_on_add = False
    assert s.clear_list_on_add is False

    if os.path.exists("settings.ini"):
        os.remove("settings.ini")


def test_settings_page_init(qtbot):
    page = SettingsPage()
    qtbot.addWidget(page)

    assert page.objectName() == "settingsPage"
    assert page._overwrite_card is not None
    assert page._switch_btn is not None
    assert page._clear_list_card is not None
    assert page._clear_list_switch is not None

    # Проверка начального значения из менеджера
    assert (
        page._switch_btn.isChecked() == SettingsManager().overwrite_existing
    )
    assert (
        page._clear_list_switch.isChecked()
        == SettingsManager().clear_list_on_add
    )


def test_settings_page_toggle(qtbot):
    page = SettingsPage()
    qtbot.addWidget(page)

    # Меняем через UI
    initial = page._switch_btn.isChecked()
    page._switch_btn.setChecked(not initial)

    assert SettingsManager().overwrite_existing == (not initial)
