# -*- coding: utf-8 -*-
"""Скрипт управления потоками MKV.

Позволяет удалять выбранные дорожки или сохранять
только выбранные из MKV-контейнера.
"""

import logging
from pathlib import Path
from typing import Any

from app.core.abstract_script import (
    AbstractScript,
    ProgressCallback,
    SettingField,
    SettingType,
)
from app.core.settings_manager import SettingsManager
from app.core.output_resolver import OutputResolver
from app.infrastructure.mkvmerge_runner import (
    MKVMergeRunner,
)
from app.infrastructure.mkvprobe_runner import (
    MKVProbeRunner,
    TrackInfo,
)

logger = logging.getLogger(__name__)

# Режимы работы скрипта.
MODE_REMOVE = "Удалить выбранные"
MODE_KEEP = "Сохранить только выбранные"


class StreamManagerScript(AbstractScript):
    """Скрипт для управления потоками в MKV."""

    def __init__(self) -> None:
        """Инициализация скрипта."""
        self._runner = MKVMergeRunner()
        self._probe = MKVProbeRunner()
        self._resolver = OutputResolver()
        logger.info(
            "Скрипт управления потоками MKV создан"
        )

    @property
    def name(self) -> str:
        """Отображаемое имя скрипта."""
        return "Управление потоками MKV"

    @property
    def description(self) -> str:
        """Описание скрипта."""
        return (
            "Удаление или сохранение выбранных "
            "дорожек (видео, аудио, субтитры) "
            "в MKV-файлах."
        )

    @property
    def icon_name(self) -> str:
        """Имя иконки FluentIcon."""
        return "EDIT"

    @property
    def file_extensions(self) -> list[str]:
        """Допустимые расширения файлов."""
        return [".mkv"]

    @property
    def settings_schema(self) -> list[SettingField]:
        """Схема настроек скрипта."""
        return [
            SettingField(
                key="mode",
                label="Режим работы",
                setting_type=SettingType.COMBO,
                default=MODE_REMOVE,
                options=[MODE_REMOVE, MODE_KEEP],
            ),
        ]

    @property
    def use_custom_widget(self) -> bool:
        """Скрипт использует кастомный виджет."""
        return True

    def execute(
        self,
        files: list[Path],
        settings: dict[str, Any],
        output_path: str | None = None,
        progress_callback: (
            ProgressCallback | None
        ) = None,
    ) -> list[str]:
        """Выполнить обработку файлов.

        Args:
            files: Список путей к MKV-файлам.
            settings: Настройки скрипта.
            progress_callback: Callback прогресса.

        Returns:
            Список строк-результатов.
        """
        results: list[str] = []
        mode = settings.get("mode", MODE_REMOVE)
        per_file: dict[str, list[int]] = settings.get(
            "selected_tracks_per_file", {}
        )
        total = len(files)
        completed = 0

        logger.info(
            "Запуск скрипта управления потоками. "
            "Режим: '%s'. Файлов: %d",
            mode,
            total,
        )

        # Проверяем, есть ли хоть один выбранный трек
        has_any = any(
            bool(ids) for ids in per_file.values()
        )
        if not has_any:
            msg = "⚠ Не выбраны дорожки для обработки"
            logger.warning(msg)
            results.append(msg)
            return results

        for file_path in files:
            completed += 1
            file_key = str(file_path)
            selected_ids = per_file.get(file_key, [])

            if not selected_ids:
                msg = (
                    f"⏭ Пропущен (нет выбранных "
                    f"дорожек): {file_path.name}"
                )
                logger.info(
                    "Пропуск файла '%s': "
                    "дорожки не выбраны",
                    file_path.name,
                )
                results.append(msg)
                if progress_callback:
                    progress_callback(
                        completed, total, msg
                    )
                continue

            logger.info(
                "Обработка файла [%d/%d]: '%s'. "
                "Выбрано дорожек: %d",
                completed,
                total,
                file_path.name,
                len(selected_ids),
            )

            try:
                all_tracks = self._probe.get_tracks(
                    file_path
                )
            except Exception:
                msg = (
                    f"❌ Ошибка анализа: "
                    f"{file_path.name}"
                )
                logger.exception(
                    "Ошибка анализа дорожек "
                    "файла '%s'",
                    file_path.name,
                )
                results.append(msg)
                if progress_callback:
                    progress_callback(
                        completed, total, msg
                    )
                continue

            # Формируем аргументы mkvmerge
            mkvmerge_args = self._build_track_args(
                all_tracks=all_tracks,
                selected_ids=selected_ids,
                mode=mode,
            )

            if not mkvmerge_args:
                logger.info(
                    "Файл '%s': все дорожки "
                    "сохраняются, ремуксинг "
                    "без фильтрации",
                    file_path.name,
                )

            # Определяем расширение по типам
            kept_types = self._get_kept_types(
                all_tracks=all_tracks,
                selected_ids=selected_ids,
                mode=mode,
            )
            if kept_types == {"audio"}:
                ext = ".mka"
            else:
                ext = file_path.suffix

            out_name = file_path.stem + ext

            # Выходной путь через резолвер
            target_dir = self._resolver.resolve(
                file_path, output_path
            )
            
            output_file_path = target_dir / out_name
            logger.info(
                "Выходной файл: '%s' "
                "(типы: %s, расширение: '%s')",
                out_name,
                kept_types,
                ext,
            )

            if (
                output_file_path.exists()
                and not SettingsManager()
                .overwrite_existing
            ):
                msg = (
                    f"⏭ Пропущен (файл существует): "
                    f"{output_file_path.name}"
                )
                logger.info(
                    "Пропуск: выходной файл '%s' "
                    "уже существует",
                    output_file_path.name,
                )
                results.append(msg)
                if progress_callback:
                    progress_callback(
                        completed, total, msg
                    )
                continue

            logger.debug(
                "Аргументы фильтрации: %s",
                mkvmerge_args,
            )

            # Запуск mkvmerge
            inputs = [
                {
                    "path": file_path,
                    "args": mkvmerge_args,
                }
            ]

            success = self._runner.run(
                output_path=output_file_path,
                inputs=inputs,
            )

            if success:
                msg = (
                    f"✅ Обработано: "
                    f"{output_file_path.name}"
                )
                logger.info(
                    "Файл успешно обработан: '%s'",
                    output_file_path.name,
                )
            else:
                msg = (
                    f"❌ Ошибка обработки: "
                    f"{file_path.name}"
                )
                logger.error(
                    "Ошибка mkvmerge при обработке "
                    "файла '%s'",
                    file_path.name,
                )

            results.append(msg)
            if progress_callback:
                progress_callback(
                    completed, total, msg
                )

        success_count = len(
            [r for r in results if r.startswith("✅")]
        )
        logger.info(
            "Управление потоками завершено. "
            "Итог: %d успешно из %d",
            success_count,
            total,
        )
        return results

    @staticmethod
    def _compute_keep_ids(
        all_tracks: list[TrackInfo],
        selected_ids: list[int],
        mode: str,
    ) -> set[int]:
        """Вычислить ID дорожек, которые остаются.

        Args:
            all_tracks: Все дорожки файла.
            selected_ids: ID выбранных дорожек.
            mode: Режим работы.

        Returns:
            Набор ID дорожек для сохранения.
        """
        if mode == MODE_KEEP:
            return set(selected_ids)
        all_ids = {t.track_id for t in all_tracks}
        return all_ids - set(selected_ids)

    @staticmethod
    def _get_kept_types(
        all_tracks: list[TrackInfo],
        selected_ids: list[int],
        mode: str,
    ) -> set[str]:
        """Определить типы оставшихся дорожек.

        Args:
            all_tracks: Все дорожки файла.
            selected_ids: ID выбранных дорожек.
            mode: Режим работы.

        Returns:
            Набор типов (video, audio, subtitles).
        """
        keep_ids = StreamManagerScript._compute_keep_ids(
            all_tracks, selected_ids, mode
        )
        return {
            t.track_type
            for t in all_tracks
            if t.track_id in keep_ids
        }

    @staticmethod
    def _build_track_args(
        all_tracks: list[TrackInfo],
        selected_ids: list[int],
        mode: str,
    ) -> list[str]:
        """Построить аргументы mkvmerge для фильтрации.

        Args:
            all_tracks: Все дорожки файла.
            selected_ids: ID выбранных дорожек.
            mode: Режим работы.

        Returns:
            Список аргументов mkvmerge.
        """
        # Группировка всех дорожек по типу
        type_map: dict[str, list[int]] = {
            "video": [],
            "audio": [],
            "subtitles": [],
        }
        for track in all_tracks:
            if track.track_type in type_map:
                type_map[track.track_type].append(
                    track.track_id
                )

        keep_ids = StreamManagerScript._compute_keep_ids(
            all_tracks, selected_ids, mode
        )

        # Для каждого типа формируем аргументы
        args: list[str] = []
        flag_map = {
            "video": "--video-tracks",
            "audio": "--audio-tracks",
            "subtitles": "--subtitle-tracks",
        }
        no_flag_map = {
            "video": "--no-video",
            "audio": "--no-audio",
            "subtitles": "--no-subtitles",
        }

        for track_type, all_type_ids in type_map.items():
            if not all_type_ids:
                continue

            # ID этого типа, которые нужно оставить
            kept = [
                tid
                for tid in all_type_ids
                if tid in keep_ids
            ]

            if len(kept) == len(all_type_ids):
                # Все дорожки этого типа остаются
                continue
            elif not kept:
                # Ни одна дорожка этого типа не нужна
                args.append(no_flag_map[track_type])
            else:
                # Оставить только конкретные ID
                ids_str = ",".join(
                    str(tid) for tid in kept
                )
                args.extend(
                    [flag_map[track_type], ids_str]
                )

        logger.debug(
            "Построены аргументы фильтрации: "
            "режим='%s', выбрано=%s, результат=%s",
            mode,
            selected_ids,
            args,
        )
        return args
