# -*- coding: utf-8 -*-
"""Скрипт комплексной обработки видео (вшивание надписей, аудио, фильтры)."""

import logging
import os
import tempfile
import time
from pathlib import Path
from typing import Any

from app.core.abstract_script import (
    AbstractScript,
    SettingField,
    SettingType,
    ProgressCallback,
)
from app.core.constants import VIDEO_CONTAINERS, ScriptCategory, ScriptMetadata
from app.core.output_resolver import OutputResolver
from app.core.settings_manager import SettingsManager
from app.infrastructure.ffmpeg_runner import FFmpegRunner
from app.core.temp_file_manager import TempFileManager
from app.core.ffmpeg_output_parser import ProgressInfo
from app.core.ffmpeg_utils import sanitize_filename_part

logger = logging.getLogger(__name__)


class VideoProcessorScript(AbstractScript):
    """Комплексный видео-процессор: субтитры, аудио, кодирование."""

    def __init__(self) -> None:
        """Инициализация скрипта."""
        self._ffmpeg = FFmpegRunner()
        self._resolver = OutputResolver()
        self._nvenc_available = self._ffmpeg.check_nvenc_support()
        logger.info(
            "Видео-процессор создан. NVENC доступен: %s",
            self._nvenc_available,
        )

    @property
    def category(self) -> str:
        """Категория скрипта."""
        return ScriptCategory.VIDEO

    @property
    def name(self) -> str:
        """Отображаемое имя скрипта."""
        return ScriptMetadata.VIDEO_PROCESSOR_NAME

    @property
    def description(self) -> str:
        """Описание скрипта."""
        return ScriptMetadata.VIDEO_PROCESSOR_DESC

    @property
    def icon_name(self) -> str:
        """Имя иконки FluentIcon."""
        return "VIDEO"

    @property
    def file_extensions(self) -> list[str]:
        """Допустимые расширения файлов."""
        return list(VIDEO_CONTAINERS)

    @property
    def settings_schema(self) -> list[SettingField]:
        """Схема настроек скрипта с разделением по группам."""
        default_encoder = (
            "NVENC (GPU)" if self._nvenc_available else "x265 (CPU)"
        )

        return [
            # --- Основные ---
            SettingField(
                key="sub_base",
                label="Основные параметры",
                setting_type=SettingType.SUBTITLE,
                default="",
                group="Видео",
            ),
            SettingField(
                key="encoder",
                label="Энкодер",
                setting_type=SettingType.COMBO,
                default=default_encoder,
                group="Видео",
                options=["NVENC (GPU)", "x265 (CPU)"],
            ),
            # Настройки NVENC
            SettingField(
                key="sub_nv_enc",
                label="Параметры NVENC",
                setting_type=SettingType.SUBTITLE,
                default="",
                group="Видео",
                visible_if={"encoder": ["NVENC (GPU)"]},
            ),
            SettingField(
                key="nvenc_preset",
                label="Пресет NVENC",
                setting_type=SettingType.COMBO,
                default="p7",
                group="Видео",
                options=["p1", "p2", "p3", "p4", "p5", "p6", "p7"],
                visible_if={"encoder": ["NVENC (GPU)"]},
            ),
            SettingField(
                key="nvenc_rc",
                label="Режим управления битрейтом",
                setting_type=SettingType.COMBO,
                default="vbr_hq",
                group="Видео",
                options=["cbr", "vbr", "vbr_hq", "constqp"],
                visible_if={"encoder": ["NVENC (GPU)"], "lossless": [False]},
            ),
            # Настройки CPU
            SettingField(
                key="sub_cpu_enc",
                label="Параметры CPU (x265)",
                setting_type=SettingType.SUBTITLE,
                default="",
                group="Видео",
                visible_if={"encoder": ["x265 (CPU)"]},
            ),
            SettingField(
                key="cpu_preset",
                label="Пресет CPU",
                setting_type=SettingType.COMBO,
                default="medium",
                group="Видео",
                options=[
                    "ultrafast",
                    "superfast",
                    "veryfast",
                    "faster",
                    "fast",
                    "medium",
                    "slow",
                    "slower",
                    "veryslow",
                ],
                visible_if={"encoder": ["x265 (CPU)"]},
            ),
            SettingField(
                key="cpu_crf",
                label="CRF",
                setting_type=SettingType.INT,
                default=23,
                group="Видео",
                visible_if={"encoder": ["x265 (CPU)"], "lossless": [False]},
            ),
            # Общие настройки видео
            SettingField(
                key="sub_common",
                label="Общие параметры качества",
                setting_type=SettingType.SUBTITLE,
                default="",
                group="Видео",
            ),
            SettingField(
                key="lossless",
                label="Режим Lossless",
                setting_type=SettingType.CHECKBOX,
                default=False,
                group="Видео",
            ),
            SettingField(
                key="v_bitrate",
                label="Битрейт видео (кбит/с)",
                setting_type=SettingType.INT,
                default=4000,
                group="Видео",
                visible_if={
                    "nvenc_rc": ["cbr", "vbr", "vbr_hq"],
                    "lossless": [False],
                },
            ),
            SettingField(
                key="v_qp",
                label="QP/Quality",
                comment="0-51. Меньше = лучше, 0 - без потерь",
                setting_type=SettingType.INT,
                default=0,
                group="Видео",
                visible_if={"nvenc_rc": ["constqp"]},
            ),
            SettingField(
                key="force_10bit",
                label="Принудительно 10-бит (Main10)",
                setting_type=SettingType.CHECKBOX,
                default=False,
                group="Видео",
            ),
            # Расширенные NVENC
            SettingField(
                key="sub_nv_extra",
                label="Расширенные функции NVENC",
                setting_type=SettingType.SUBTITLE,
                default="",
                group="Видео",
                visible_if={"encoder": ["NVENC (GPU)"]},
            ),
            SettingField(
                key="nv_lookahead",
                label="Lookahead",
                setting_type=SettingType.COMBO,
                default="32",
                group="Видео",
                options=["Выкл", "8", "16", "24", "32"],
                visible_if={"encoder": ["NVENC (GPU)"]},
            ),
            SettingField(
                key="nv_aq",
                label="Spatial AQ",
                setting_type=SettingType.CHECKBOX,
                default=True,
                group="Видео",
                visible_if={"encoder": ["NVENC (GPU)"]},
            ),
            # Вкладка: Аудио
            SettingField(
                key="audio_codec",
                label="Кодек аудио",
                setting_type=SettingType.COMBO,
                default="copy",
                group="Аудио",
                options=["copy", "aac", "ac3", "flac"],
            ),
            SettingField(
                key="audio_bitrate",
                label="Битрейт аудио",
                setting_type=SettingType.COMBO,
                default="320k",
                group="Аудио",
                options=["128k", "192k", "256k", "320k", "448k", "640k"],
                visible_if={"audio_codec": ["aac", "ac3"]},
            ),
            SettingField(
                key="audio_channels",
                label="Каналы",
                setting_type=SettingType.COMBO,
                default="Original",
                group="Аудио",
                options=["Original", "1", "2", "6"],
                visible_if={"audio_codec": ["aac", "ac3", "flac"]},
            ),
            # Вкладка: Субтитры
            SettingField(
                key="sub_keywords",
                label="Ключевые слова для поиска надписей",
                setting_type=SettingType.KEYWORD_LIST,
                default=[{"word": "Надписи", "active": True}],
                group="Субтитры",
            ),
            SettingField(
                key="strip_keywords",
                label="Удалять строки с тегами",
                setting_type=SettingType.KEYWORD_LIST,
                default=[
                    {
                        "word": (
                            r"{\fad(500,500)\b1\an3\fnTahoma\fs50\shad3"
                            r"\bord1.3\4c&H000000&\4a&H00&}"
                        ),
                        "active": True,
                    },
                    {
                        "word": (
                            r"{\fad(500,500)\b1\an3\fnTahoma\fs16.667"
                            r"\shad1\bord0.433\4c&H000000&\4a&H00&}"
                        ),
                        "active": True,
                    },
                    {
                        "word": (
                            r"{\fad(500,500)\b1\an3\fnTahoma\fs100\shad6"
                            r"\bord2.6\4c&H000000&\4a&H00&}"
                        ),
                        "active": True,
                    },
                ],
                group="Субтитры",
            ),
            # Вкладка: Общие
            SettingField(
                key="overwrite_source",
                label="Заменить исходный файл после обработки",
                setting_type=SettingType.CHECKBOX,
                default=False,
                group="Общие",
            ),
        ]

    def execute_single(
        self,
        file_path: Path,
        settings: dict[str, Any],
        output_path: str | None = None,
        progress_callback: ProgressCallback | None = None,
        current: int = 0,
        total: int = 1,
    ) -> list[str]:
        """Оркестратор обработки одного файла.

        Разделяет процесс на аналитику, подготовку ресурсов,
        формирование аргументов и исполнение задачи FFmpeg.
        """
        results: list[str] = []
        logger.info("Видео-процессор: начало %s", file_path.name)

        # 1. Анализ файла
        info = self._ffmpeg.get_video_info(file_path)
        if not info:
            return [f"❌ ОШИБКА: Не удалось проанализировать {file_path.name}"]

        # 2. Подготовка окружения
        with tempfile.TemporaryDirectory(
            prefix=f"{TempFileManager.PREFIX}vproc_"
        ) as tmp_dir:
            tmp_path = Path(tmp_dir)

            # Извлечение шрифтов и подготовка очищенных субтитров
            sub_file, fonts_dir = self._prepare_resources(
                info,
                file_path,
                tmp_path,
                settings,
                results,
            )

            # 3. Пути и аргументы
            target_dir = self._resolver.resolve(file_path, output_path)
            output_file = self._get_safe_output_path(
                file_path,
                target_dir / file_path.with_suffix(".mp4").name,
            )

            extra_args = self._build_ffmpeg_args(
                info,
                sub_file,
                fonts_dir,
                settings,
            )

            # 3.5 Аппаратное декодирование
            input_args = self._get_hw_input_args(info, settings)
            if input_args:
                logger.info(
                    "Использование аппаратного декодера: %s",
                    " ".join(input_args),
                )

            # 4. Исполнение задачи
            duration = float(info.get("format", {}).get("duration", 0))
            logger.info(
                "Видео-процессор: длительность для прогресса = %.2f сек.",
                duration,
            )

            def on_ffmpeg_progress(p_info: ProgressInfo) -> None:
                if progress_callback:
                    # Формируем информативную строку статуса
                    b_str = f"{p_info.bitrate} | " if p_info.bitrate else ""
                    fps_str = (
                        f"FPS: {int(p_info.fps)} | " if p_info.fps else ""
                    )
                    msg = (
                        f"Обработка: {file_path.name} | "
                        f"{p_info.percent:.1f}% | "
                        f"{fps_str}"
                        f"{b_str}"
                        f"Speed: {p_info.speed or 0}x | "
                        f"ETA: {p_info.eta}"
                    )
                    progress_callback(current, total, msg, p_info.percent)

            success = self._ffmpeg.run(
                file_path,
                output_file,
                extra_args,
                input_args=input_args,
                overwrite=SettingsManager().overwrite_existing,
                total_duration=duration,
                on_progress=on_ffmpeg_progress,
            )

            # 5. Постобработка
            self._handle_result(
                success,
                file_path,
                output_file,
                settings,
                results,
            )

        return results

    def _prepare_resources(
        self,
        info: dict[str, Any],
        file_path: Path,
        tmp_path: Path,
        settings: dict[str, Any],
        results: list[str],
    ) -> tuple[Path | None, Path]:
        """Подготовка временных ресурсов (шрифты, субтитры)."""
        fonts_dir = tmp_path / "fonts"
        fonts_dir.mkdir(exist_ok=True)

        # 1. Извлечение шрифтов
        attachments = [
            s
            for s in info.get("streams", [])
            if s.get("codec_type") == "attachment"
        ]
        if attachments:
            count = self._ffmpeg.extract_fonts(
                file_path,
                attachments,
                fonts_dir,
            )
            logger.info("Извлечено шрифтов: %d", count)

        # 2. Извлечение и очистка субтитров
        sub_file = None
        keywords = settings.get("sub_keywords", [])
        sub_stream = self._find_subtitle_stream(info, keywords)

        if sub_stream:
            # Вычисляем относительный индекс среди потоков субтитров (0:s:N)
            rel_idx = self._ffmpeg.get_relative_index(
                info, sub_stream["index"], "subtitle"
            )

            # Именуем временный файл уникально (PID + Timestamp)
            raw_title = sub_stream.get("tags", {}).get("title", "subs")
            safe_title = sanitize_filename_part(raw_title, max_length=30)
            suffix = f"{os.getpid()}_{int(time.time() * 1000)}"
            sub_file_name = f"temp_{safe_title}_{suffix}.ass"
            sub_file = tmp_path / sub_file_name

            if sub_file is not None and self._ffmpeg.extract_subtitle(
                file_path,
                rel_idx,
                sub_file,
                relative=True,
            ):
                logger.info(
                    "Субтитры выбраны ('%s' #%d) и извлечены в: %s",
                    raw_title,
                    rel_idx,
                    sub_file.name,
                )
                strip_words = settings.get("strip_keywords", [])
                if strip_words:
                    self._strip_subtitle_lines(sub_file, strip_words)
            else:
                sub_file = None
                results.append(
                    f"⚠ Не удалось извлечь субтитры из {file_path.name}"
                )

        return sub_file, fonts_dir

    def _handle_result(
        self,
        success: bool,
        file_path: Path,
        output_file: Path,
        settings: dict[str, Any],
        results: list[str],
    ) -> None:
        """Обработка результата завершения FFmpeg."""
        if success:
            results.append(f"✅ Готово: {output_file.name}")
            if settings.get("overwrite_source"):
                self._replace_source_with_result(
                    file_path,
                    output_file,
                    results,
                )
        else:
            if self.is_cancelled:
                self._cleanup_if_cancelled(output_file)
                results.append(f"⚠ Отменено: {file_path.name}")
            else:
                results.append(f"❌ Ошибка обработки: {file_path.name}")

    def _find_subtitle_stream(
        self,
        info: dict[str, Any],
        keywords: list[dict[str, Any]],
    ) -> dict[str, Any] | None:
        """Поиск потока субтитров по приоритетам."""
        streams = [
            s
            for s in info.get("streams", [])
            if s.get("codec_type") == "subtitle"
        ]
        if not streams:
            return None

        # 1. Поиск по ключевым словам во всех тегах (заголовок и др.)
        active_words = [k["word"].lower() for k in keywords if k.get("active")]
        if active_words:
            for s in streams:
                tags = s.get("tags", {})
                title = tags.get("title", "").lower()
                for word in active_words:
                    if word in title:
                        logger.info(
                            "Субтитры найдены по заголовку '%s' в дорожке: %s",
                            word,
                            title,
                        )
                        return s

        # 2. Поиск по флагам по умолчанию
        for s in streams:
            disposition = s.get("disposition", {})
            if disposition.get("default") or disposition.get("forced"):
                logger.info(
                    "Субтитры выбраны по флагу default/forced (Track #%d)",
                    s["index"],
                )
                return s

        # 3. Возвращаем первый попавшийся
        logger.info(
            "Субтитры выбраны автоматически (Track #%d)",
            streams[0]["index"],
        )
        return streams[0]

    def _extract_fonts(
        self,
        info: dict[str, Any],
        file_path: Path,
        target_dir: Path,
    ) -> None:
        """Извлечение шрифтов."""
        for s in info.get("streams", []):
            if s.get("codec_type") == "attachment":
                idx = s.get("index")
                fname = s.get("tags", {}).get("filename", f"f_{idx}.ttf")
                self._ffmpeg.extract_attachment(
                    file_path, idx, target_dir / fname
                )

    def _strip_subtitle_lines(
        self,
        sub_file: Path,
        keywords: list[dict[str, Any]],
    ) -> None:
        """Удаление строк субтитров по вхождению ключевых слов."""
        active_strip = [k["word"] for k in keywords if k.get("active")]
        if not active_strip or sub_file is None:
            return

        try:
            with open(sub_file, "r", encoding="utf-8-sig") as f:
                lines = f.readlines()

            new_lines = []
            removed = 0
            for line in lines:
                if any(word in line for word in active_strip):
                    removed += 1
                    continue
                new_lines.append(line)

            if removed > 0 and sub_file is not None:
                with open(sub_file, "w", encoding="utf-8") as f:
                    f.writelines(new_lines)
                logger.info("Удалено строк из субтитров: %d", removed)
        except Exception:
            logger.exception("Ошибка при очистке субтитров")

    def _build_ffmpeg_args(
        self,
        info: dict[str, Any],
        sub_file: Path | None,
        fonts_dir: Path,
        settings: dict[str, Any],
    ) -> list[str]:
        """Оркестратор формирования аргументов кодирования."""
        args: list[str] = []

        # 1. Видео параметры
        self._append_video_args(args, settings)

        # 2. Аудио параметры
        self._append_audio_args(args, settings)

        # 3. Видеофильтры (вшивание субтитров)
        if sub_file:
            self._append_subtitle_filter_args(args, sub_file, fonts_dir)

        # 4. Маппинг, метаданные и системные флаги
        self._append_mapping_args(args)

        return args

    def _get_hw_input_args(
        self,
        info: dict[str, Any],
        settings: dict[str, Any],
    ) -> list[str]:
        """Определить параметры аппаратного декодирования (CUVID)."""
        if settings.get("encoder") != "NVENC (GPU)":
            return []

        # Получаем кодек первого видеопотока
        video_streams = [
            s
            for s in info.get("streams", [])
            if s.get("codec_type") == "video"
        ]
        if not video_streams:
            return []

        v_codec = video_streams[0].get("codec_name", "").lower()
        decoders = self._ffmpeg.get_available_cuvid_decoders()

        # Таблица маппинга популярных кодеков на CUVID
        # h264 -> h264_cuvid
        # hevc -> hevc_cuvid
        # vp8 -> vp8_cuvid
        # vp9 -> vp9_cuvid
        # av1 -> av1_cuvid (Ampere+)
        # vc1 -> vc1_cuvid
        # mpeg2video -> mpeg2_cuvid
        # mpeg4 -> mpeg4_cuvid
        mapping = {
            "h264": "h264_cuvid",
            "hevc": "hevc_cuvid",
            "vp8": "vp8_cuvid",
            "vp9": "vp9_cuvid",
            "vc1": "vc1_cuvid",
            "mpeg2video": "mpeg2_cuvid",
            "mpeg4": "mpeg4_cuvid",
        }

        # Специальная проверка для AV1 (нужен FFmpeg + GPU Ampere+)
        if v_codec == "av1":
            if self._ffmpeg.is_av1_decode_supported():
                return ["-hwaccel", "cuda", "-c:v", "av1_cuvid"]

        cuvid = mapping.get(v_codec)
        if cuvid and cuvid in decoders:
            return ["-hwaccel", "cuda", "-c:v", cuvid]

        return []

    def _append_video_args(
        self,
        args: list[str],
        settings: dict[str, Any],
    ) -> None:
        """Настройка параметров видео-энкодера."""
        encoder = settings.get("encoder", "")
        p_fmt = "yuv420p10le" if settings.get("force_10bit") else "yuv420p"

        if "NVENC" in encoder:
            self._setup_nvenc_args(args, p_fmt, settings)
        else:
            self._setup_cpu_args(args, p_fmt, settings)

    def _setup_nvenc_args(
        self,
        args: list[str],
        pix_fmt: str,
        settings: dict[str, Any],
    ) -> None:
        """Формирование аргументов специально для NVENC."""
        # Для 10-битного NVENC нативным форматом является p010le
        if pix_fmt == "yuv420p10le":
            pix_fmt = "p010le"

        args.extend(["-c:v", "hevc_nvenc", "-pix_fmt", pix_fmt])
        args.extend(["-preset", settings.get("nvenc_preset", "p7")])

        # Безопасное получение параметров QP и битрейта
        v_qp_val = settings.get("v_qp")
        v_qp = int(str(v_qp_val)) if str(v_qp_val).isdigit() else 0

        if settings.get("lossless"):
            args.extend(
                ["-rc", "constqp", "-qp", str(v_qp), "-tune", "lossless"]
            )
        else:
            rc = settings.get("nvenc_rc", "vbr_hq")
            args.extend(["-rc", rc])
            if rc == "constqp":
                # Если QP не задан для режима constqp, используем 23
                qp = v_qp if v_qp > 0 else 23
                args.extend(["-qp", str(qp)])
            else:
                v_br_val = settings.get("v_bitrate")
                v_br = (
                    int(str(v_br_val)) if str(v_br_val).isdigit() else 4000
                )
                min_br = v_br
                max_br = v_br * 2
                buf_size = max_br * 2
                args.extend(
                    [
                        "-b:v",
                        f"{v_br}k",
                        "-minrate",
                        f"{min_br}k",
                        "-maxrate",
                        f"{max_br}k",
                        "-bufsize",
                        f"{buf_size}k",
                    ]
                )

            # Расширенные флаги
            l_ahead = settings.get("nv_lookahead", "32")
            if l_ahead != "Выкл":
                args.extend(["-rc-lookahead", l_ahead])
            if settings.get("nv_aq"):
                args.extend(["-spatial-aq", "1", "-aq-strength", "15"])

    def _setup_cpu_args(
        self,
        args: list[str],
        pix_fmt: str,
        settings: dict[str, Any],
    ) -> None:
        """Формирование аргументов для x265 (CPU)."""
        args.extend(["-c:v", "libx265", "-pix_fmt", pix_fmt])
        args.extend(["-preset", settings.get("cpu_preset", "medium")])

        if settings.get("lossless"):
            args.extend(["-x265-params", "lossless=1"])
        else:
            # Безопасное получение CRF
            cpu_crf_val = settings.get("cpu_crf")
            cpu_crf = (
                int(str(cpu_crf_val)) if str(cpu_crf_val).isdigit() else 23
            )
            args.extend(["-crf", str(cpu_crf)])

            # Безопасное получение битрейта
            v_br_val = settings.get("v_bitrate")
            if v_br_val and str(v_br_val).isdigit():
                v_br = int(str(v_br_val))
                min_br = v_br
                max_br = v_br * 2
                buf_size = max_br * 2
                args.extend(
                    [
                        "-b:v",
                        f"{v_br}k",
                        "-minrate",
                        f"{min_br}k",
                        "-maxrate",
                        f"{max_br}k",
                        "-bufsize",
                        f"{buf_size}k",
                    ]
                )

    def _append_audio_args(
        self,
        args: list[str],
        settings: dict[str, Any],
    ) -> None:
        """Настройка параметров аудио-энкодера."""
        audio_codec = settings.get("audio_codec", "copy")
        args.extend(["-c:a", audio_codec])

        if audio_codec != "copy":
            args.extend(["-b:a", settings.get("audio_bitrate", "320k")])
            channels = settings.get("audio_channels", "Original")
            if channels != "Original":
                args.extend(["-ac", channels])

    def _append_subtitle_filter_args(
        self,
        args: list[str],
        sub_file: Path,
        fonts_dir: Path,
    ) -> None:
        """Настройка фильтра для вшивания субтитров."""
        # Используем надежное экранирование для фильтров
        sub_path = self._ffmpeg.escape_filter_path(sub_file)
        fonts_path = self._ffmpeg.escape_filter_path(fonts_dir)

        # Формируем строку фильтра с экранированными путями
        filter_str = f"subtitles=filename='{sub_path}':fontsdir='{fonts_path}'"
        args.extend(["-vf", filter_str])

    def _append_mapping_args(self, args: list[str]) -> None:
        """Добавление маппинга и служебных флагов."""
        # Маппинг первого видео и первого аудио (если есть)
        args.extend(["-map", "0:v:0", "-map", "0:a:0?"])

        # Совместимость с Apple/QuickTime (HEVC)
        args.extend(["-tag:v", "hvc1"])

        # Оптимизация структуры для быстрого запуска
        args.extend(["-movflags", "+faststart"])

        # Удаляем лишние метаданные (кроме базовых)
        args.extend(["-map_metadata", "-1"])
