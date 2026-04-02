# -*- coding: utf-8 -*-
"""Парсер вывода FFmpeg для расширенного анализа прогресса."""

import re
import logging
from dataclasses import dataclass
from typing import Optional

logger = logging.getLogger(__name__)


@dataclass(frozen=True)
class ProgressInfo:
    """Информация о текущем прогрессе обработки файла.

    Attributes:
        time_seconds: Текущее время обработки (в секундах).
        percent: Процент выполнения (0.0-100.0).
        fps: Кадры в секунду (для видео).
        bitrate: Текущий битрейт.
        speed: Скорость относительно реального времени (например, 2.5x).
        eta: Оставшееся время до конца обработки (строка).
    """

    time_seconds: float
    percent: float
    fps: Optional[float] = None
    bitrate: Optional[str] = None
    speed: Optional[float] = None
    eta: str = "н/д"


class FFmpegOutputParser:
    """Парсер строк вывода FFmpeg (из stderr)."""

    # Регулярное выражение для поиска времени
    # (time=00:01:23.45 или time=00:01:23,45)
    TIME_REGEX = re.compile(
        r'(?:^|[\s(\[])time=(\d{2}):(\d{2}):(\d{2})[\.\,](\d+)'
    )

    # Более гибкие регулярные выражения для остальных метрик
    FPS_REGEX = re.compile(r'fps=\s*([\d\.]+)')
    BITRATE_REGEX = re.compile(r'bitrate=\s*([\d\.N/A]+\s*[kmg]?bits/s|N/A)')
    SPEED_REGEX = re.compile(r'speed=\s*([\d\.]+)x')

    @classmethod
    def parse_line(
        cls, line: str, total_duration: float
    ) -> Optional[ProgressInfo]:
        """Разобрать строку FFmpeg и вычислить текущий прогресс.

        Args:
            line: Строка вывода FFmpeg.
            total_duration: Общая длительность файла в секундах.

        Returns:
            Объект ProgressInfo или None, если информация не найдена.
        """
        # Поиск времени (обязательно для любого прогресса)
        time_match = cls.TIME_REGEX.search(line)
        if not time_match:
            return None

        try:
            h, m, s, ms_str = time_match.groups()
            h, m, s = map(int, [h, m, s])
            # Учитываем дробную часть любой длины (обычно .CC или .CCC)
            ms = int(ms_str) / (10**len(ms_str))
            current_time = h * 3600 + m * 60 + s + ms
        except (ValueError, TypeError, ZeroDivisionError):
            return None

        # Расчет процента
        percent = 0.0
        if total_duration > 0:
            percent = (current_time / total_duration) * 100.0
            percent = min(max(percent, 0.0), 100.0)

        # Поиск дополнительных метрик
        fps: Optional[float] = None
        bitrate: Optional[str] = None
        speed: Optional[float] = None
        eta = "н/д"

        # FPS
        fps_match = cls.FPS_REGEX.search(line)
        if fps_match:
            try:
                fps = float(fps_match.group(1))
            except ValueError:
                pass

        # Bitrate
        bit_match = cls.BITRATE_REGEX.search(line)
        if bit_match:
            bitrate_raw = bit_match.group(1).strip()
            bitrate = None if bitrate_raw == "N/A" else bitrate_raw

        # Speed
        speed_match = cls.SPEED_REGEX.search(line)
        if speed_match:
            try:
                speed = float(speed_match.group(1))
            except ValueError:
                pass

        # Расчет ETA (Оставшееся время)
        if speed is not None and speed > 0 and total_duration > current_time:
            remaining_sec = (total_duration - current_time) / speed
            # Форматируем в ч:мм:сс
            rem_h = int(remaining_sec // 3600)
            rem_m = int((remaining_sec % 3600) // 60)
            rem_s = int(remaining_sec % 60)
            if rem_h > 0:
                eta = f"{rem_h}:{rem_m:02}:{rem_s:02}"
            else:
                eta = f"{rem_m:02}:{rem_s:02}"

        return ProgressInfo(
            time_seconds=current_time,
            percent=percent,
            fps=fps,
            bitrate=bitrate,
            speed=speed,
            eta=eta,
        )
