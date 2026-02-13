import pytest
import sys
from pathlib import Path

# Добавляем корень проекта в sys.path для импорта модулей приложения
sys.path.insert(0, str(Path(__file__).parent.parent))

@pytest.fixture
def temp_dir(tmp_path):
    """Фикстура для создания временной директории."""
    return tmp_path

@pytest.fixture
def mock_path_exists(mocker):
    """Фикстура для мока Path.exists."""
    return mocker.patch("pathlib.Path.exists", return_value=True)
