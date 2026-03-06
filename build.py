# -*- coding: utf-8 -*-
"""Скрипт сборки K-Tools.

Выполняет сборку приложения через PyInstaller (onedir),
копирует внешние зависимости из bin/ и генерирует
крипт Inno Setup (.iss) для создания инсталлятора.
"""

import os
import shutil
import subprocess
import sys
import time
import re
import importlib.util
from pathlib import Path

# === Настройки ===
BASE_DIR = Path(__name__).parent.resolve()
VENV_DIR = BASE_DIR / "venv"
PYTHON_EXE = VENV_DIR / "Scripts" / "python.exe"
REQUIREMENTS = BASE_DIR / "requirements.txt"
SCRIPT = BASE_DIR / "main.py"
EXE_BASE_NAME = "KTools"
ICON = BASE_DIR / "assets" / "app_icon.ico"
VERSION_FILE = BASE_DIR / "version.txt"
CHANGELOG_FILE = BASE_DIR / "CHANGELOG.md"


def get_current_version() -> str:
    """Получить текущую версию из файла."""
    if VERSION_FILE.exists():
        return VERSION_FILE.read_text().strip()
    return "1.0.000"


def save_version(version: str) -> None:
    """Сохранить новую версию в файл."""
    VERSION_FILE.write_text(version, encoding="utf-8")


def extract_version_from_changelog() -> str:
    """Извлечь последнюю версию из CHANGELOG.md.

    Ищет первую строку, начинающуюся с '# '.

    Returns:
        Строка версии (например, '1.5.0').

    Raises:
        ValueError: Если файл не найден или версия не обнаружена.
    """
    if not CHANGELOG_FILE.exists():
        raise ValueError(f"Файл {CHANGELOG_FILE} не найден")

    content = CHANGELOG_FILE.read_text(encoding="utf-8")
    match = re.search(r"^#\s*([\d\.]+)", content, re.MULTILINE)

    if not match:
        raise ValueError("Не удалось найти версию в CHANGELOG.md")

    version = match.group(1).strip()
    return version


def prompt_version_update() -> str:
    """Определить версию для сборки.

    Автоматически берет версию из окружения (CI) или из CHANGELOG.md (Локально).

    Returns:
        Строка версии.
    """
    ci_version = os.environ.get("CI_VERSION")
    if ci_version:
        # Убираем букву 'v' из тега (например 'v1.0.3' -> '1.0.3')
        version = ci_version.lstrip("v")
        save_version(version)
        print(f"[✓] CI/CD: Версия автоматически установлена: {version}")
        return version

    try:
        version = extract_version_from_changelog()
        save_version(version)
        print(f"[✓] Локальная сборка: Версия взята из CHANGELOG.md: {version}")
        return version
    except Exception as e:
        print(f"[!] Ошибка при получении версии из CHANGELOG.md: {e}")
        current_version = get_current_version()
        print(f"[*] Используется текущая версия из файла: {current_version}")
        return current_version


def update_app_version_py(version: str) -> None:
    """Обновить версию в коде приложения (app/core/version.py)."""
    version_py = BASE_DIR / "app" / "core" / "version.py"
    if not version_py.exists():
        print(f"[!] Файл {version_py} не найден для авто-обновления")
        return

    content = version_py.read_text(encoding="utf-8")

    # Регулярные выражения для замены версии
    content = re.sub(r'VERSION = "[^"]+"', f'VERSION = "{version}"', content)
    content = re.sub(r'return "[^"]+"', f'return "{version}"', content)

    version_py.write_text(content, encoding="utf-8")
    print(f"[✓] Версия в {version_py} синхронизирована.")


def ensure_venv() -> Path:
    """Проверка наличия виртуального окружения."""
    if not PYTHON_EXE.exists():
        print(f"[!] Виртуальное окружение {VENV_DIR} не найдено!")
        print(f"[!] Ожидаемый путь: {PYTHON_EXE}")

        current_exe = Path(sys.executable)
        if sys.prefix != sys.base_prefix:
            print(f"[*] Использую текущий интерпретатор: {current_exe}")
            return current_exe
        return current_exe
    else:
        print("[✓] venv найден")
        return PYTHON_EXE


def clean() -> None:
    """Очистка сборочных папок и артефактов."""
    for folder_name in ["build", "dist"]:
        folder = BASE_DIR / folder_name
        if folder.exists():
            print(f"[*] Удаляю {folder}...")
            shutil.rmtree(folder)

    for file in BASE_DIR.glob("*.spec"):
        print(f"[*] Удаляю {file.name}...")
        file.unlink()


def create_version_file(version_str: str) -> None:
    """Создать файл версии Windows."""
    # Пытаемся извлечь номер RC (например, 1 из -rc1)
    rc_match = re.search(r"-rc(\d+)", version_str)
    rc_num = int(rc_match.group(1)) if rc_match else 0

    # Извлекаем основные цифры версии (напр. 1.5.2 из 1.5.2-rc1)
    numeric_match = re.match(r"^([\d\.]+)", version_str)
    if numeric_match:
        # Берем только первые три части версии
        parts = numeric_match.group(1).split(".")
        v_parts = [int(p) for p in parts][:3]
    else:
        v_parts = [1, 0, 0]

    # Дополняем до 3 элементов, если их меньше (напр. 1.5 -> 1.5.0)
    while len(v_parts) < 3:
        v_parts.append(0)

    # Технический кортеж всегда должен иметь 4 числа для Windows
    v_tuple = (v_parts[0], v_parts[1], v_parts[2], rc_num)

    version_info = f"""# UTF-8
VSVersionInfo(
  ffi=FixedFileInfo(
    filevers={v_tuple},
    prodvers={v_tuple},
    mask=0x3f,
    flags=0x0,
    OS=0x40004,
    fileType=0x1,
    subtype=0x0,
    date=(0, 0)
  ),
  kids=[
    StringFileInfo(
      [
        StringTable(
          u'040904B0',
          [StringStruct(u'CompanyName', u''),
           StringStruct(
               u'FileDescription',
               u'K-Tools'
           ),
           StringStruct(
               u'FileVersion',
               u'{version_str}'
           ),
           StringStruct(
               u'InternalName', u'{EXE_BASE_NAME}'
           ),
           StringStruct(u'LegalCopyright', u''),
           StringStruct(
               u'OriginalFilename',
               u'{EXE_BASE_NAME}.exe'
           ),
           StringStruct(
               u'ProductName', u'{EXE_BASE_NAME}'
           ),
           StringStruct(
               u'ProductVersion',
               u'{version_str}'
           )])
      ]),
    VarFileInfo(
        [VarStruct(u'Translation', [1033, 1200])]
    )
  ]
)"""
    (BASE_DIR / "file_version_info.txt").write_text(
        version_info, encoding="utf-8"
    )


def copy_bin_directory(exe_name: str) -> None:
    """Перенос внешних утилит (eac3to, ffmpeg и др.) в каталог сборки."""
    src_bin = BASE_DIR / "bin"
    dst_bin = BASE_DIR / "dist" / exe_name / "bin"

    if not src_bin.exists():
        print(
            "[!] Папка bin/ не найдена! "
            "Внешние зависимости не будут скопированы."
        )
        return

    if dst_bin.exists():
        shutil.rmtree(dst_bin)

    print(f"[*] Копирование bin/ → dist/{exe_name}/bin/")
    shutil.copytree(src_bin, dst_bin)

    copied_files = list(dst_bin.iterdir())
    print(f"[✓] Скопировано файлов из bin/: {len(copied_files)}")
    for f in sorted(copied_files):
        print(f"    • {f.name}")


def create_inno_setup_script(
    exe_name: str,
    version_str: str,
) -> None:
    """Генерация скрипта для Inno Setup.

    Args:
        exe_name: Имя исполняемого файла (без .exe).
        version_str: Строка версии.
    """
    cwd = str(BASE_DIR).replace("\\", "\\\\")
    icon_p = str(ICON).replace("\\", "\\\\")

    # Определяем имя выходного файла инсталлятора
    ci_version = os.environ.get("CI_VERSION", "")
    if "-rc" in ci_version:
        output_filename = f"{EXE_BASE_NAME}_PreRelease_Setup"
        print(
            f"[*] CI/CD (Pre-release): Фиксированное имя файла: {output_filename}"
        )
    else:
        output_filename = f"{EXE_BASE_NAME}_v{version_str}_setup"

    iss_content = f"""
[Setup]
AppId=krnzhnr.ktools.v1
AppName={EXE_BASE_NAME}
AppVersion={version_str}
DefaultDirName={{autopf}}\\{EXE_BASE_NAME}
DefaultGroupName={EXE_BASE_NAME}
OutputDir={cwd}\\setup_output
OutputBaseFilename={output_filename}
SetupIconFile={icon_p}
Compression=lzma2/ultra64
SolidCompression=yes
LZMADictionarySize=65536
ArchitecturesInstallIn64BitMode=x64

[Tasks]
Name: "desktopicon"; Description: "{{cm:CreateDesktopIcon}}"; \\
GroupDescription: "{{cm:AdditionalIcons}}"; Flags: unchecked

[Files]
; Основная папка сборки (onedir) + bin/
Source: "{cwd}\\dist\\{exe_name}\\*"; DestDir: "{{app}}"; \\
Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{{group}}\\{EXE_BASE_NAME}"; \\
Filename: "{{app}}\\{EXE_BASE_NAME}.exe"; \\
IconFilename: "{{app}}\\app_icon.ico"
Name: "{{commondesktop}}\\{EXE_BASE_NAME}"; \\
Filename: "{{app}}\\{EXE_BASE_NAME}.exe"; \\
IconFilename: "{{app}}\\app_icon.ico"; \\
Tasks: desktopicon

[Run]
Filename: "{{app}}\\{EXE_BASE_NAME}.exe"; \\
Description: "{{cm:LaunchProgram,{EXE_BASE_NAME}}}"; \\
Flags: nowait postinstall skipifsilent
"""
    iss_path = BASE_DIR / f"{EXE_BASE_NAME}.iss"
    iss_path.write_text(iss_content, encoding="utf-8")
    print(f"[✓] Создан скрипт инсталлятора: {iss_path}")


def build() -> None:
    """Основная процедура сборки приложения."""
    print("[*] Определение версии сборки...")
    version_str = prompt_version_update()

    # Синхронизируем версию в коде перед сборкой
    update_app_version_py(version_str)

    create_version_file(version_str)

    python_bin = ensure_venv()

    print("[*] Проверка импорта ядра...")
    try:
        sys.path.insert(0, str(BASE_DIR))
        if importlib.util.find_spec("app.core.script_registry") is None:
            raise ImportError("Модуль app.core.script_registry не найден")

        print("[✓] Импорт ядра доступен.")
    except ImportError as e:
        print(f"[!] Ошибка импорта: {e}")

    cmd = [
        str(python_bin),
        "-m",
        "PyInstaller",
        "--noconfirm",
        "--clean",
        "--onedir",
        "--noconsole",
        f"--name={EXE_BASE_NAME}",
        "--version-file=file_version_info.txt",
        "--paths=.",
        "--hidden-import=PyQt6",
        "--hidden-import=qfluentwidgets",
        "--hidden-import=app.core.abstract_script",
        "--hidden-import=app.core.path_utils",
        "--hidden-import=app.core.resource_utils",
        "--hidden-import=app.core.script_registry",
        "--hidden-import=app.infrastructure.eac3to_runner",
        "--hidden-import=app.infrastructure.ffmpeg_runner",
        "--hidden-import=app.infrastructure.mkvmerge_runner",
        "--hidden-import=app.scripts.audio_converter",
        "--hidden-import=app.scripts.audio_speed_changer",
        "--hidden-import=app.scripts.container_converter",
        "--hidden-import=app.scripts.metadata_cleaner",
        "--hidden-import=app.scripts.muxer",
        "--hidden-import=app.ui.main_window",
        "--hidden-import=app.ui.work_panel",
        "--hidden-import=app.ui.file_list_widget",
        "--hidden-import=app.ui.muxing_table_widget",
        "--hidden-import=deew",
        "--collect-all=qfluentwidgets",
        str(SCRIPT),
    ]

    if ICON.exists():
        abs_icon = ICON.resolve()
        cmd.insert(-1, f"--icon={abs_icon}")
        cmd.insert(-1, f"--add-data={abs_icon};.")

    print("[*] Запуск PyInstaller...")
    print(f"Команда: {' '.join(cmd)}")
    subprocess.check_call(cmd)

    # Копирование иконки для ярлыков Inno Setup
    dst_icon = BASE_DIR / "dist" / EXE_BASE_NAME / "app_icon.ico"
    if ICON.exists():
        shutil.copy2(ICON, dst_icon)
        print(f"[✓] Иконка скопирована для ярлыков: {dst_icon}")

    # Копирование папки bin/ со всеми зависимостями
    copy_bin_directory(EXE_BASE_NAME)

    # Генерация ISS скрипта
    create_inno_setup_script(EXE_BASE_NAME, version_str)

    print(f"[✓] Сборка готова: dist/{EXE_BASE_NAME}")


if __name__ == "__main__":
    is_ci = os.environ.get("CI_VERSION") is not None
    try:
        clean()
        build()
        if not is_ci:
            print("\n[*] Окно закроется через 10 секунд...")
            time.sleep(10)
    except Exception as e:
        print(f"\n[!] ОШИБКА: {e}")
        if not is_ci:
            input("Нажмите Enter чтобы выйти...")
        else:
            sys.exit(1)  # Жестко завершаем с ошибкой для пайплайна
