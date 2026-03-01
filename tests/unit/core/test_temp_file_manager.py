# -*- coding: utf-8 -*-
import unittest
import os
from pathlib import Path
import tempfile
from app.core.temp_file_manager import TempFileManager


class TestTempFileManager(unittest.TestCase):
    def setUp(self):
        # Очищаем состояние синглтона для чистоты теста
        self.manager = TempFileManager()
        self.manager._tracked_paths.clear()

    def test_create_temp_dir(self):
        path = self.manager.create_temp_dir(prefix="test_dir_")
        self.assertTrue(path.exists())
        self.assertTrue(path.is_dir())
        self.assertIn("test_dir_", path.name)
        self.assertIn(path, self.manager._tracked_paths)

    def test_create_temp_file(self):
        path = self.manager.create_temp_file(prefix="test_file_")
        self.assertTrue(path.exists())
        self.assertTrue(path.is_file())
        self.assertIn("test_file_", path.name)
        self.assertIn(path, self.manager._tracked_paths)

    def test_cleanup(self):
        dir_path = self.manager.create_temp_dir()
        file_path = self.manager.create_temp_file()

        self.manager.cleanup()

        self.assertFalse(dir_path.exists())
        self.assertFalse(file_path.exists())
        self.assertEqual(len(self.manager._tracked_paths), 0)

    def test_cleanup_on_startup(self):
        # Создаем "забытый" файл вручную в системной темп-папке
        prefix = TempFileManager.PREFIX
        fd, path_str = tempfile.mkstemp(prefix=prefix)
        os.close(fd)
        forgotten_file = Path(path_str)

        self.assertTrue(forgotten_file.exists())

        # Запускаем очистку при старте
        self.manager.cleanup_on_startup()

        self.assertFalse(
            forgotten_file.exists(),
            "Файл с префиксом ktools_ должен быть удален",
        )


if __name__ == "__main__":
    unittest.main()
