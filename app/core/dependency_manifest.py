# -*- coding: utf-8 -*-
"""Манифест внешних зависимостей приложения.

Содержит описания всех внешних бинарных зависимостей,
их метаданные для UI, URL-ы для скачивания и маппинг
скриптов на необходимые зависимости.
"""

from dataclasses import dataclass

# Базовый URL для скачивания зависимостей из GitHub Releases.
# Зависимости хранятся как assets в релизе с тегом deps-v1.
DEPS_RELEASE_TAG = "deps-v1"
DEPS_BASE_URL = (
    "https://github.com/krnzhnr/k-tools/releases/download"
    f"/{DEPS_RELEASE_TAG}"
)

# URL для получения метаданных (checksums.json) релиза зависимостей
DEPS_CHECKSUMS_URL = f"{DEPS_BASE_URL}/checksums.json"


@dataclass(frozen=True)
class DependencyInfo:
    """Описание одной внешней зависимости.

    Attributes:
        key: Уникальный идентификатор зависимости.
        display_name: Отображаемое имя в UI.
        description: Краткое описание назначения.
        icon_name: Имя иконки из FluentIcon для карточки.
        subfolder: Подпапка внутри bin/ для распаковки.
        size_mb: Приблизительный размер после распаковки (МБ).
        archive_name: Имя tar.xz-архива в GitHub Releases.
        verify_binary: Файл для проверки наличия.
        required: True, если зависимость рекомендуемая.
    """

    key: str
    display_name: str
    description: str
    icon_name: str
    subfolder: str
    size_mb: float
    archive_name: str
    verify_binary: str
    required: bool


# Реестр всех внешних зависимостей приложения.
# Порядок определяет порядок отображения в UI.
DEPENDENCY_REGISTRY: tuple[DependencyInfo, ...] = (
    DependencyInfo(
        key="ffmpeg",
        display_name="FFmpeg + QAAC",
        description="Кодирование аудио/видео",
        icon_name="VIDEO",
        subfolder="ffmpeg",
        size_mb=471.0,
        archive_name="ffmpeg.tar.xz",
        verify_binary="kt-ffmpeg.exe",
        required=True,
    ),
    DependencyInfo(
        key="mkvtoolnix",
        display_name="MKVToolNix",
        description="Слияние и парсинг MKV",
        icon_name="SHARE",
        subfolder="mkvtoolnix",
        size_mb=21.0,
        archive_name="mkvtoolnix.tar.xz",
        verify_binary="mkvmerge.exe",
        required=True,
    ),
    DependencyInfo(
        key="eac3to",
        display_name="eac3to",
        description="Изменение скорости аудио",
        icon_name="MUSIC",
        subfolder="eac3to",
        size_mb=11.0,
        archive_name="eac3to.tar.xz",
        verify_binary="eac3to.exe",
        required=False,
    ),
    DependencyInfo(
        key="dee",
        display_name="Dolby Encoding Engine",
        description="Даунмикс аудио",
        icon_name="HEADPHONE",
        subfolder="DEE",
        size_mb=186.0,
        archive_name="dee.tar.xz",
        verify_binary="dee.exe",
        required=False,
    ),
)

# Быстрый доступ к зависимости по ключу.
DEPENDENCY_MAP: dict[str, DependencyInfo] = {
    dep.key: dep for dep in DEPENDENCY_REGISTRY
}


def get_download_url(dep: DependencyInfo) -> str:
    """Получить полный URL для скачивания архива зависимости.

    Args:
        dep: Описание зависимости.

    Returns:
        Полный URL для скачивания tar.xz-архива.
    """
    return f"{DEPS_BASE_URL}/{dep.archive_name}"
