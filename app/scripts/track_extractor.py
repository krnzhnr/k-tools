# -*- coding: utf-8 -*-
"""Скрипт массового извлечения дорожек с умными правилами.

Осуществляет извлечение выбранных потоков с использованием
одного прохода FFmpeg (One-Pass Extraction) для скорости,
с умным формированием имен выходных файлов.
"""

import logging
from pathlib import Path
from typing import Any, Dict, List

from app.core.abstract_script import (
    AbstractScript,
    ProgressCallback,
    SettingField,
    SettingType,
)
from app.core.settings_manager import SettingsManager
from app.core.output_resolver import OutputResolver
from app.infrastructure.ffmpeg_runner import FFmpegRunner
from app.infrastructure.mkvprobe_runner import MKVProbeRunner, TrackInfo
from app.core.constants import VIDEO_EXTENSIONS

logger = logging.getLogger(__name__)

# Расширения для извлекаемых форматов на основе поля codec из mkvmerge
RAW_EXTENSIONS: Dict[str, str] = {
    "AC-3": ".ac3",
    "E-AC-3": ".eac3",
    "DTS": ".dts",
    "DTS-HD Master Audio": ".dts",
    "AAC": ".aac",
    "Opus": ".opus",
    "FLAC": ".flac",
    "Vorbis": ".ogg",
    "MP3": ".mp3",
    "TrueHD": ".thd",
    "PCM": ".wav",
    "MPEG Audio": ".mp3",
    "SubRip/SRT": ".srt",
    "SubStationAlpha": ".ass",
    "HDMV PGS": ".sup",
    "VobSub": ".idx",
    # Видеокодеки
    "AVC/H.264/MPEG-4p10": ".h264",
    "H.264": ".h264",
    "HEVC/H.265/MPEG-H": ".h265",
    "H.265": ".h265",
    "MPEG-1/2 Video": ".m2v",
    "MPEG-2": ".m2v",
    "VC-1": ".vc1",
    "VP8": ".ivf",
    "VP9": ".ivf",
    "AV1": ".ivf"
}


class TrackExtractorScript(AbstractScript):
    """Скрипт для массового извлечения дорожек из MKV/MP4."""

    def __init__(self) -> None:
        """Инициализация скрипта."""
        self._ffmpeg = FFmpegRunner()
        self._probe = MKVProbeRunner()
        self._resolver = OutputResolver()
        logger.info("Скрипт массового извлечения потоков из контейнера создан")

    @property
    def category(self) -> str:
        """Категория скрипта."""
        return "Муксинг"

    @property
    def name(self) -> str:
        """Отображаемое имя скрипта."""
        return "Демуксинг"

    @property
    def description(self) -> str:
        """Описание скрипта."""
        return "Массовое извлечение потоков из контейнера с авто-именованием."

    @property
    def icon_name(self) -> str:
        """Имя иконки FluentIcon."""
        return "DOWNLOAD"

    @property
    def file_extensions(self) -> list[str]:
        """Допустимые расширения файлов."""
        return list(VIDEO_EXTENSIONS)

    @property
    def settings_schema(self) -> list[SettingField]:
        """Схема настроек скрипта."""
        return [
            SettingField(
                key="name_format",
                label="Формат имени файла",
                setting_type=SettingType.COMBO,
                default="{original}_{lang}_{id}",
                options=[
                    "{original}_{lang}_{id}",
                    "{original}_{id}_{lang}",
                    "{original}_{lang}",
                ],
            )
        ]

    @property
    def use_custom_widget(self) -> bool:
        """Скрипт использует кастомный виджет."""
        return True

    def _get_extension_for_track(self, track: TrackInfo) -> str:
        """Определить расширение файла по кодеку дорожки.
        
        Args:
            track: Информация о дорожке.
            
        Returns:
            Строка расширения с точкой (например, '.aac').
        """
        if track.codec in RAW_EXTENSIONS:
            return RAW_EXTENSIONS[track.codec]
            
        # Fallback на основе типа, если кодек неизвестен
        if track.track_type == "video":
            return ".mkv" # Видео сырым извлекать сложнее, лучше в контейнер
        elif track.track_type == "audio":
            return ".mka"
        elif track.track_type == "subtitles":
            return ".mks"
            
        return ".bin"

    def _format_filename(
        self, 
        original_stem: str, 
        track: TrackInfo, 
        ext: str, 
        name_format: str
    ) -> str:
        """Сформировать имя извлеченного файла по шаблону."""
        lang = track.language if track.language and track.language != "und" else ""
        t_id = f"track{track.track_id:02d}"
        
        # Замена плейсхолдеров
        name = name_format.replace("{original}", original_stem)
        name = name.replace("{id}", t_id)
        
        if "{lang}" in name:
            if lang:
                name = name.replace("{lang}", lang)
            else:
                # Если языка нет, удаляем висячие подчеркивания
                name = name.replace("_{lang}", "")
                name = name.replace("{lang}_", "")
                name = name.replace("{lang}", "")
                
        # На всякий случай зачищаем двойные подчеркивания
        name = name.replace("__", "_").rstrip("_")
        
        return f"{name}{ext}"

    def execute_single(
        self,
        file_path: Path,
        settings: dict[str, Any],
        output_path: str | None = None,
    ) -> list[str]:
        """Обработать один файл (массовое извлечение выбранных дорожек).
        
        Используется один вызов FFmpeg с несколькими -map для высокой 
        скорости работы (One-Pass Extraction).
        """
        per_file = settings.get("selected_tracks_per_file", {})
        file_key = str(file_path)
        selected_ids: List[int] = per_file.get(file_key, [])
        overwrite = SettingsManager().overwrite_existing
        name_format = settings.get("name_format", "{original}_{lang}_{id}")

        if not selected_ids:
            msg = f"⏭ Пропущен (нет выбранных дорожек): {file_path.name}"
            logger.info("[%s] %s", self.name, msg)
            return [msg]

        try:
            all_tracks = self._probe.get_tracks(file_path)
        except Exception:
            logger.exception("Ошибка анализа дорожек файла '%s'", file_path.name)
            return [f"❌ Ошибка анализа: {file_path.name}"]

        # Оставляем только те дорожки, которые были выбраны
        tracks_to_extract = [t for t in all_tracks if t.track_id in selected_ids]
        
        if not tracks_to_extract:
            return [f"⏭ Пропущен (нет валидных дорожек для извлечения): {file_path.name}"]

        target_dir = self._resolver.resolve(file_path, output_path)
        
        ffmpeg_args: List[str] = []
        extracted_files: List[Path] = []
        
        output_results: List[str] = []

        # Формируем аргументы для FFmpeg (one-pass extraction)
        for track in tracks_to_extract:
            ext = self._get_extension_for_track(track)
            out_filename = self._format_filename(file_path.stem, track, ext, name_format)
            out_filepath = self._get_safe_output_path(file_path, target_dir / out_filename)
            
            if out_filepath.exists() and not overwrite:
                logger.info("[%s] Дорожка пропущена (файл существует): %s", self.name, out_filepath.name)
                output_results.append(f"⏭ Пропущена дорожка {track.track_id}: {out_filename}")
                continue
                
            # Маппинг дорожки (в ffmpeg id субтитров и аудио могут отличаться от mkvmerge, 
            # но mkvmerge id обычно совпадает со stream_index - проверяем совместимость).
            # Внимание: для надежного -map лучше использовать индексы FFmpeg, но если мы 
            # получаем track_id из mkvmerge, он может отличаться на 1 или быть таким же.
            # Если FFprobe не использовался, предполагаем -map 0:{track.track_id}
            # Это обычно верно для большинства MKV.
            
            ffmpeg_args.extend([
                "-map", f"0:{track.track_id}",
                "-c:v" if track.track_type == "video" else "-c:a" if track.track_type == "audio" else "-c:s", 
                "copy",
                str(out_filepath)
            ])
            extracted_files.append(out_filepath)
            output_results.append(f"✅ Извлечена дорожка {track.track_id}: {out_filename}")

        if not ffmpeg_args:
            return output_results # Все дорожки были пропущены
            
        # Запускаем один процесс FFmpeg на все дорожки сразу
        logger.info(
            "[%s] Запуск FFmpeg для One-Pass извлечения %d дорожек из '%s'", 
            self.name, len(extracted_files), file_path.name
        )
        
        # Для FFmpegRunner `extra_args` вставляются ДО выходного файла, но здесь у нас 
        # много выходных файлов. FFmpegRunner.run ожидает один output_path.
        # Поскольку у нас сложная команда со множеством выходов, мы вызовем FFmpegRunner 
        # немного иначе. FFmpegRunner.run(output_path) добавляет output_path в конец.
        # Мы можем передать "основным" выходным файлом первый файл, а остальные запихнуть 
        # в extra_args. 
        # НО! Нужно быть осторожными с позицией аргументов.
        
        main_output = ffmpeg_args[-1]
        args_without_main_output = ffmpeg_args[:-1]
        
        success = self._ffmpeg.run(
            input_path=file_path,
            output_path=Path(main_output),
            extra_args=args_without_main_output,
            overwrite=overwrite
        )

        if success:
            logger.info("[%s] Успешно извлечены файлы: %s", self.name, [f.name for f in extracted_files])
            return [f"✅ Извлечено из {file_path.name}: {len(extracted_files)} файл(ов)"]
        else:
            logger.error("[%s] Ошибка при массовом извлечении из '%s'", self.name, file_path.name)
            return [f"❌ Ошибка извлечения: {file_path.name}"]
