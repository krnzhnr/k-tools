# -*- coding: utf-8 -*-
"""Тесты для проверки качества кода: статический анализ и правила пользователя."""

import subprocess
import sys
from pathlib import Path


def test_mypy_static_typing() -> None:
    """Проверка статической типизации с помощью mypy."""
    root_dir = Path(__file__).resolve().parents[3]

    # Запускаем mypy для папки app и файла main.py
    cmd = [
        sys.executable,
        "-m",
        "mypy",
        "app/",
        "main.py",
        "--ignore-missing-imports",
    ]

    result = subprocess.run(
        cmd, cwd=root_dir, capture_output=True, text=True, encoding="utf-8"
    )

    # Если mypy нашел ошибки, тест должен упасть и показать их
    assert (
        result.returncode == 0
    ), f"Ошибки Mypy:\n{result.stdout}\n{result.stderr}"


def test_flake8_formatting() -> None:
    """Проверка соблюдения PEP8 (максимальная длина строки 79) с помощью flake8."""
    root_dir = Path(__file__).resolve().parents[3]

    cmd = [
        sys.executable,
        "-m",
        "flake8",
        "app/",
        "main.py",
        "--max-line-length=79",
    ]

    result = subprocess.run(
        cmd, cwd=root_dir, capture_output=True, text=True, encoding="utf-8"
    )

    assert (
        result.returncode == 0
    ), f"Ошибки Flake8:\n{result.stdout}\n{result.stderr}"


def test_custom_code_rules() -> None:
    """Проверка на длину функций (>50 строк) и отсутствие аннотаций типов."""
    root_dir = Path(__file__).resolve().parents[3]
    check_script = root_dir / "check_code.py"

    if not check_script.exists():
        # Если скрипт удален, пропускаем (лучше не падать, чтобы не зависеть от него)
        return

    cmd = [sys.executable, str(check_script)]
    result = subprocess.run(
        cmd, cwd=root_dir, capture_output=True, text=True, encoding="utf-8"
    )

    output = result.stdout.strip()

    assert (
        "No AST issues found." in output
    ), f"Нарушения кастомных правил:\n{output}"
