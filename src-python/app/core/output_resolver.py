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
        self, input_path: Path, manual_path: Optional[str] = None
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

        # Создаем директорию, если её нет (с retry для защиты от гонки)
        if not target_dir.exists():
            import time

            max_retries = 5
            for attempt in range(max_retries):
                try:
                    target_dir.mkdir(parents=True, exist_ok=True)
                    if attempt == 0:
                        logger.info(
                            "📁 Создана директория для вывода: %s", target_dir
                        )
                    break  # Успешно создана или уже существовала (exist_ok)
                except OSError as e:
                    # Если папка была перехвачена другим потоком
                    if target_dir.exists():
                        break

                    if attempt < max_retries - 1:
                        sleep_time = 0.1 * (attempt + 1)
                        logger.debug(
                            "Гонка при создании %s (попытка %d), "
                            "ждем %sс... (%s)",
                            target_dir.name,
                            attempt + 1,
                            sleep_time,
                            e,
                        )
                        time.sleep(sleep_time)
                    else:
                        logger.exception(
                            "❌ Не удалось создать директорию '%s' "
                            "после %d попыток",
                            target_dir,
                            max_retries,
                        )
                        # Fallback: сохраняем рядом с исходником
                        return input_path.parent

        return target_dir
