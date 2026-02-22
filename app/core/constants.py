# -*- coding: utf-8 -*-
"""Централизованные константы медиа-форматов."""

# Видео контейнеры и элементарные потоки
VIDEO_EXTENSIONS = frozenset([
    ".mkv", ".mp4", ".avi", ".mov", ".webm", 
    ".hevc", ".h264", ".h265", ".264", ".265", 
    ".vc1", ".m2v", ".avc"
])

# Аудио форматы (lossless и lossy)
AUDIO_EXTENSIONS = frozenset([
    ".mp3", ".flac", ".wav", ".m4a", ".ogg", 
    ".wma", ".aiff", ".alac", ".ape", ".opus", 
    ".ac3", ".eac3", ".dts", ".wv", ".aac", 
    ".thd", ".truehd", ".mlp", ".dtshd", ".pcm", 
    ".mp2", ".m2a"
])

# Форматы субтитров
SUBTITLE_EXTENSIONS = frozenset([
    ".srt", ".ass", ".ssa", ".sub", ".vtt", 
    ".idx", ".sup"
])
