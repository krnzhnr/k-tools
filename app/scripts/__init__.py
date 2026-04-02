# -*- coding: utf-8 -*-
"""Реализации скриптов обработки медиафайлов."""

from . import ass_to_vtt_converter
from . import audio_converter
from . import audio_dee_downmixer
from . import audio_speed_changer
from . import audio_splitter
from . import container_converter
from . import metadata_cleaner
from . import muxer
from . import stream_manager
from . import stream_replacer
from . import track_extractor
from . import video_processor

# Список всех модулей для удобства регистрации
SCRIPT_MODULES = [
    ass_to_vtt_converter,
    audio_converter,
    audio_dee_downmixer,
    audio_speed_changer,
    audio_splitter,
    container_converter,
    metadata_cleaner,
    muxer,
    stream_manager,
    stream_replacer,
    track_extractor,
    video_processor,
]
