import pytest
from unittest.mock import MagicMock
from app.core.script_registry import ScriptRegistry
from app.core.abstract_script import AbstractScript, SettingField, SettingType

# --- Mock Script for Testing ---

class MockScript(AbstractScript):
    @property
    def category(self): return "Категория"
    @property
    def name(self): return "Mock Script"
    @property
    def description(self): return "Description"
    @property
    def icon_name(self): return "ICON"
    @property
    def file_extensions(self): return [".tmp"]
    @property
    def settings_schema(self):
        return [SettingField("key", "Label", SettingType.TEXT)]
    @property
    def use_custom_widget(self):
        return False
    
    def execute_single(self, file, settings, output_path=None):
        return ["Done"]

# --- ScriptRegistry Tests ---

def test_registry_empty():
    registry = ScriptRegistry()
    assert len(registry.scripts) == 0
    assert len(registry) == 0

def test_registry_registration():
    registry = ScriptRegistry()
    script = MockScript()
    registry.register(script)
    
    assert len(registry) == 1
    assert registry.scripts[0] == script
    assert registry.find_by_name(script.name) == script

def test_registry_get_nonexistent():
    registry = ScriptRegistry()
    assert registry.find_by_name("Unknown") is None

def test_registry_register_and_get():
    registry = ScriptRegistry()
    script = MockScript()
    
    registry.register(script)
    
    assert len(registry.scripts) == 1
    assert registry.get_by_index(0) == script
    assert registry.scripts[0] == script

def test_registry_len():
    registry = ScriptRegistry()
    registry.register(MockScript())
    assert len(registry) == 1

# --- AbstractScript Tests ---

def test_abstract_script_properties():
    script = MockScript()
    assert script.name == "Mock Script"
    assert script.file_extensions == [".tmp"]
    assert len(script.settings_schema) == 1

def test_abstract_script_delete_source(mocker):
    script = MockScript()
    mock_path = mocker.MagicMock()
    results = []
    
    script._delete_source(mock_path, results)
    
    mock_path.unlink.assert_called_once()
    assert any("Удалён исходник" in r for r in results)

def test_abstract_script_delete_source_error(mocker):
    script = MockScript()
    mock_path = mocker.MagicMock()
    mock_path.unlink.side_effect = OSError("Error")
    results = []
    
    script._delete_source(mock_path, results)
    
    assert any("Не удалось удалить" in r for r in results)
