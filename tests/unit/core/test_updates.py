# -*- coding: utf-8 -*-
"""Юнит-тесты для модуля проверки обновлений."""

import json
import urllib.error
from unittest.mock import MagicMock, patch

from app.core.update_worker import parse_version, UpdateCheckerWorker


def test_parse_version() -> None:
    """Тестирование корректности разбора и сравнения версий."""
    # Базовые сравнения
    assert parse_version("1.7.0") > parse_version("1.6.0")
    assert parse_version("1.7.1") > parse_version("1.7.0")
    assert parse_version("2.0.0") > parse_version("1.9.9")

    # Сравнение с префиксами v и v.
    assert parse_version("v1.7.0") == parse_version("1.7.0")
    assert parse_version("v.1.7.0") == parse_version("1.7.0")

    # Сравнение пре-релизов со стабильными релизами
    assert parse_version("1.7.0") > parse_version("1.7.0-beta.1")
    assert parse_version("1.7.0-beta.2") > parse_version("1.7.0-beta.1")
    assert parse_version("1.7.0-alpha.1") < parse_version("1.7.0-beta.1")

    # Некорректные или укороченные строки версий
    assert parse_version("1.7") == (1, 7, 0, 1, 0, 0)
    assert parse_version("1") == (1, 0, 0, 1, 0, 0)


@patch("urllib.request.urlopen")
def test_update_checker_no_update(mock_urlopen: MagicMock) -> None:
    """Тест работы воркера, когда установлена последняя версия."""
    # Мокаем ответ от GitHub API
    mock_response = MagicMock()
    mock_response.read.return_value = json.dumps(
        [
            {
                "tag_name": "v1.5.0",
                "prerelease": False,
                "body": "Описание релиза v1.5.0",
                "zipball_url": "https://github.com/zipball/v1.5.0",
                "assets": [],
            }
        ]
    ).encode("utf-8")
    mock_urlopen.return_value.__enter__.return_value = mock_response

    worker = UpdateCheckerWorker(include_prereleases=False)

    finished_called = False

    def on_finished(available: bool, version: str, body: str, url: str) -> None:
        nonlocal finished_called
        finished_called = True
        assert not available
        assert version == "v1.5.0"
        assert body == "Описание релиза v1.5.0"
        assert url == "https://github.com/zipball/v1.5.0"

    worker.checkFinished.connect(on_finished)
    worker.run()

    assert finished_called


@patch("urllib.request.urlopen")
def test_update_checker_has_update(mock_urlopen: MagicMock) -> None:
    """Тест работы воркера при наличии нового релиза."""
    mock_response = MagicMock()
    mock_response.read.return_value = json.dumps(
        [
            {
                "tag_name": "v3.0.0",
                "prerelease": False,
                "body": "Новые фичи в v3.0.0",
                "zipball_url": "https://github.com/zipball/v3.0.0",
                "assets": [{"browser_download_url": "https://github.com/exe/v3.0.0"}],
            }
        ]
    ).encode("utf-8")
    mock_urlopen.return_value.__enter__.return_value = mock_response

    worker = UpdateCheckerWorker(include_prereleases=False)

    finished_called = False

    def on_finished(available: bool, version: str, body: str, url: str) -> None:
        nonlocal finished_called
        finished_called = True
        assert available
        assert version == "v3.0.0"
        assert body == "Новые фичи в v3.0.0"
        assert url == "https://github.com/exe/v3.0.0"

    worker.checkFinished.connect(on_finished)
    worker.run()

    assert finished_called


@patch("urllib.request.urlopen")
def test_update_checker_network_error(mock_urlopen: MagicMock) -> None:
    """Тест работы воркера при ошибке сети."""
    mock_urlopen.side_effect = urllib.error.URLError("Сбой сети")

    worker = UpdateCheckerWorker(include_prereleases=False)

    error_called = False

    def on_error(msg: str) -> None:
        nonlocal error_called
        error_called = True
        assert "Не удалось подключиться" in msg

    worker.checkError.connect(on_error)
    worker.run()

    assert error_called
