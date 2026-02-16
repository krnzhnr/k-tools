# -*- coding: utf-8 -*-
"""Скрипт сборки K-Tools.

Выполняет сборку приложения через PyInstaller (onedir),
копирует внешние зависимости из bin/ и генерирует
скрипт Inno Setup (.iss) для создания инсталлятора.
"""

import os
import shutil
import subprocess
import sys
import time
from pathlib import Path

# === Настройки ===
VENV_DIR = "venv"
PYTHON_EXE = os.path.join(VENV_DIR, "Scripts", "python.exe")
REQUIREMENTS = "requirements.txt"
SCRIPT = "main.py"
EXE_BASE_NAME = "KTools"
ICON = "assets/app_icon.ico"

# === Управление номером сборки ===
BUILD_NUMBER_FILE = "build_number.txt"


def get_build_number() -> int:
    """Получить текущий номер сборки из файла."""
    if os.path.exists(BUILD_NUMBER_FILE):
        with open(BUILD_NUMBER_FILE, "r") as f:
            return int(f.read().strip())
    return 0


def increment_build_number() -> int:
    """Увеличить и сохранить номер сборки."""
    build_num = get_build_number() + 1
    with open(BUILD_NUMBER_FILE, "w") as f:
        f.write(str(build_num))
    return build_num


def ensure_venv() -> str:
    """Проверить наличие виртуального окружения.

    Returns:
        Путь к интерпретатору Python.
    """
    if not os.path.exists(PYTHON_EXE):
        print(
            f"[!] Виртуальное окружение {VENV_DIR} "
            f"не найдено!"
        )
        print(
            f"[!] Ожидаемый путь: {PYTHON_EXE}"
        )
        if sys.prefix != sys.base_prefix:
            print(
                f"[*] Использую текущий интерпретатор: "
                f"{sys.executable}"
            )
            return sys.executable
        return sys.executable
    else:
        print("[✓] venv найден")
        return PYTHON_EXE


def clean() -> None:
    """Очистка сборочных папок и артефактов."""
    for folder in ["build", "dist"]:
        if os.path.exists(folder):
            print(f"[*] Удаляю {folder}...")
            shutil.rmtree(folder)

    for file in os.listdir():
        if file.endswith(".spec"):
            print(f"[*] Удаляю {file}...")
            os.remove(file)


def create_version_file(build_num_formatted: str) -> None:
    """Создать файл версии для Windows.

    Args:
        build_num_formatted: Форматированный номер сборки.
    """
    build_num = int(build_num_formatted)
    version_info = f"""# UTF-8
VSVersionInfo(
  ffi=FixedFileInfo(
    filevers=({build_num}, 0, 0, 0),
    prodvers=({build_num}, 0, 0, 0),
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
               u'K-Tools — Набор инструментов для'
               u' обработки видео/аудио'
           ),
           StringStruct(
               u'FileVersion',
               u'build {build_num_formatted}'
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
               u'1.0.{build_num}'
           )])
      ]),
    VarFileInfo(
        [VarStruct(u'Translation', [1033, 1200])]
    )
  ]
)"""
    with open(
        "file_version_info.txt", "w", encoding="utf-8"
    ) as f:
        f.write(version_info)


def copy_bin_directory(exe_name: str) -> None:
    """Копирование всей папки bin/ в сборку.

    Копирует все внешние зависимости (eac3to, ffmpeg,
    ffprobe, mkvmerge и их DLL) из исходной папки bin/
    в dist/<exe_name>/bin/.

    Args:
        exe_name: Имя папки сборки в dist/.
    """
    src_bin = Path("bin")
    dst_bin = Path("dist") / exe_name / "bin"

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
    print(
        f"[✓] Скопировано файлов из bin/: "
        f"{len(copied_files)}"
    )
    for f in sorted(copied_files):
        print(f"    • {f.name}")


def create_inno_setup_script(
    exe_name: str,
    build_num: str,
) -> None:
    """Генерация скрипта для Inno Setup.

    Args:
        exe_name: Имя исполняемого файла (без .exe).
        build_num: Форматированный номер сборки.
    """
    cwd = os.getcwd()
    iss_content = f"""
[Setup]
AppId=krnzhnr.ktools.v1
AppName={EXE_BASE_NAME}
AppVersion=1.0.{build_num}
DefaultDirName={{autopf}}\\{EXE_BASE_NAME}
DefaultGroupName={EXE_BASE_NAME}
OutputDir={cwd}\\setup_output
OutputBaseFilename={EXE_BASE_NAME}_v1.0.{build_num}_setup
SetupIconFile={cwd}\\{ICON.replace("/", "\\")}
Compression=lzma
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64

[Tasks]
Name: "desktopicon"; Description: "{{cm:CreateDesktopIcon}}"; \
GroupDescription: "{{cm:AdditionalIcons}}"; Flags: unchecked

[Files]
; Основная папка сборки (onedir) + bin/
Source: "{cwd}\\dist\\{exe_name}\\*"; DestDir: "{{app}}"; \
Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{{group}}\\{EXE_BASE_NAME}"; \
Filename: "{{app}}\\{EXE_BASE_NAME}.exe"; \
IconFilename: "{{app}}\\app_icon.ico"
Name: "{{commondesktop}}\\{EXE_BASE_NAME}"; \
Filename: "{{app}}\\{EXE_BASE_NAME}.exe"; \
IconFilename: "{{app}}\\app_icon.ico"; \
Tasks: desktopicon

[Run]
Filename: "{{app}}\\{EXE_BASE_NAME}.exe"; \
Description: "{{cm:LaunchProgram,{EXE_BASE_NAME}}}"; \
Flags: nowait postinstall skipifsilent
"""
    iss_path = f"{EXE_BASE_NAME}.iss"
    with open(iss_path, "w", encoding="utf-8") as f:
        f.write(iss_content)
    print(
        f"[✓] Создан скрипт инсталлятора: {iss_path}"
    )


def build() -> None:
    """Основная процедура сборки приложения."""
    print("[*] Сборка .exe (Dir Mode)...")
    build_num = increment_build_number()
    build_num_formatted = f"{build_num:03d}"
    print(f"[*] Номер сборки: {build_num_formatted}")

    create_version_file(build_num_formatted)

    python_bin = ensure_venv()

    # === Проверка импортов ===
    print(
        "[*] Проверка импорта "
        "app.core.script_registry..."
    )
    try:
        sys.path.insert(0, os.getcwd())
        import app.core.script_registry
        print("[✓] Модуль найден успешно.")
    except ImportError as e:
        print(
            f"[!] ОШИБКА: Не удалось "
            f"импортировать модуль: {e}"
        )
        print(
            "[!] Проверьте структуру папок "
            "и __init__.py"
        )

    exe_name = EXE_BASE_NAME

    cmd = [
        python_bin,
        "-m", "PyInstaller",
        "--noconfirm",
        "--clean",
        "--onedir",
        "--noconsole",
        f"--name={exe_name}",
        "--version-file=file_version_info.txt",

        # Путь для поиска модулей
        "--paths=.",

        # Hidden imports — PyQt6 / qfluentwidgets
        "--hidden-import=PyQt6",
        "--hidden-import=qfluentwidgets",

        # Hidden imports — модули приложения
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

        # Collect data
        "--collect-all=qfluentwidgets",

        # Главный скрипт
        SCRIPT,
    ]

    if ICON and os.path.exists(ICON):
        abs_icon = os.path.abspath(ICON)
        # Вставляем иконку ПЕРЕД главным скриптом
        cmd.insert(-1, f"--icon={abs_icon}")
        cmd.insert(-1, f"--add-data={abs_icon};.")

    print("[*] Запуск PyInstaller...")
    print(f"Команда: {' '.join(cmd)}")
    subprocess.check_call(cmd)

    # Копирование иконки для ярлыков Inno Setup
    dst_icon = Path("dist") / exe_name / "app_icon.ico"
    if os.path.exists(ICON):
        shutil.copy2(ICON, dst_icon)
        print(f"[✓] Иконка скопирована для ярлыков: {dst_icon}")

    # Копирование папки bin/ со всеми зависимостями
    copy_bin_directory(exe_name)

    # Генерация ISS скрипта
    create_inno_setup_script(
        exe_name, build_num_formatted
    )

    print(f"[✓] Готово! Сборка находится в dist/{exe_name}")
    print(
        f"[✓] Для создания инсталлятора откройте "
        f"{EXE_BASE_NAME}.iss в Inno Setup "
        f"и нажмите Compile"
    )


if __name__ == "__main__":
    try:
        clean()
        build()
        print("\n[*] Окно закроется через 10 секунд...")
        time.sleep(10)
    except Exception as e:
        print(f"\n[!] ОШИБКА: {e}")
        input("Нажмите Enter чтобы выйти...")