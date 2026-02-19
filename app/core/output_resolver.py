# -*- coding: utf-8 -*-
"""Модуль для вычисления выходных путей файлов."""

import logging
from pathlib import Path
from typing import Optional

from app.core.settings_manager import SettingsManager

logger = logging.getLogger(__name__)


class OutputResolver:
    """Сервис для определения места сохранения выходных файлов.
    
    Реализует логику выбора между автоматической подпапкой
    и пользовательским путем.
    """

    def __init__(self) -> None:
        """Инициализация резолвера."""
        self._settings = SettingsManager()

    def resolve(
        self, 
        input_path: Path, 
        manual_path: Optional[str] = None
    ) -> Path:
        """Вычислить выходной путь для файла.

        Args:
            input_path: Путь к исходному файлу.
            manual_path: Опциональный путь, указанный пользователем вручную.

        Returns:
            Путь к директории для сохранения файла.
        """
        # 1. Если пользователь указал путь вручную — используем его
        if manual_path and manual_path.strip():
            target_dir = Path(manual_path).absolute()
        else:
            # 2. Иначе проверяем глобальную настройку использования подпапок
            if self._settings.use_auto_subfolder:
                subfolder_name = self._settings.default_output_subfolder
                target_dir = input_path.parent / subfolder_name
            else:
                target_dir = input_path.parent

        # Создаем директорию, если её нет
        if not target_dir.exists():
            try:
                target_dir.mkdir(parents=True, exist_ok=True)
                logger.info("📁 Создана директория для вывода: %s", target_dir)
            except OSError:
                logger.exception(
                    "Не удалось создать директорию '%s'", 
                    target_dir
                )
                # Fallback: сохраняем рядом с исходником, если не удалось создать папку
                return input_path.parent

        return target_dir
