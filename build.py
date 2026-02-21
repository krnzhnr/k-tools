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

# === Управление версионированием ===
VERSION_FILE = "version.txt"


def get_current_version() -> str:
    """Получить текущую версию из файла."""
    if os.path.exists(VERSION_FILE):
        with open(VERSION_FILE, "r") as f:
            return f.read().strip()
    return "1.0.000"


def save_version(version: str) -> None:
    """Сохранить новую версию в файл."""
    with open(VERSION_FILE, "w") as f:
        f.write(version)


def prompt_version_update() -> str:
    """Интерактивный опрос для обновления версии.
    
    Returns:
        Новая строка версии.
    """
    current_version = get_current_version()
    print(f"\n[*] Текущая версия: {current_version}")
    print("[?] Выберите тип обновления:")
    print("  1. Major (Мажорное: X.0.0)")
    print("  2. Minor (Минорное: 1.X.0)")
    print("  3. Patch (Патч: 1.0.X)")
    print("  4. Без изменений")
    
    choice = input("Ваш выбор [1-4]: ").strip()
    
    if choice == "4":
        return current_version
        
    try:
        major, minor, patch = map(int, current_version.split('.'))
    except ValueError:
        major, minor, patch = 1, 0, 0
        
    if choice == "1":
        major += 1
        minor = 0
        patch = 0
    elif choice == "2":
        minor += 1
        patch = 0
    else:
        patch += 1
        
    new_version = f"{major}.{minor}.{patch}"
    save_version(new_version)
    print(f"[✓] Версия обновлена до: {new_version}")
    return new_version


def update_app_version_py(version: str) -> None:
    """Обновить версию в коде приложения (app/core/version.py)."""
    version_py = Path("app/core/version.py")
    if not version_py.exists():
        print(f"[!] Файл {version_py} не найден для авто-обновления")
        return
        
    with open(version_py, "r", encoding="utf-8") as f:
        lines = f.readlines()
        
    with open(version_py, "w", encoding="utf-8") as f:
        for line in lines:
            if line.startswith("VERSION =") or "return \"" in line and "Dev Mode" not in line:
                if "return \"" in line:
                    # Обработка жестко прописанной строки (как сейчас)
                    import re
                    new_line = re.sub(r'return "[^"]+"', f'return "{version}"', line)
                    f.write(new_line)
                else:
                    f.write(f'VERSION = "{version}"\n')
            else:
                f.write(line)
    print(f"[✓] Версия в {version_py} синхронизирована.")


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


def create_version_file(version_str: str) -> None:
    """Создать файл версии для Windows.

    Args:
        version_str: Строка версии (например, '1.2.3').
    """
    parts = version_str.split('.')
    v_parts = [int(p) for p in parts]
    while len(v_parts) < 4:
        v_parts.append(0)
    
    v_tuple = tuple(v_parts)
    
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
               u'K-Tools — Набор инструментов для'
               u' обработки видео/аудио'
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

    copied_files = list(dst_bin.iterdir())
    print(
        f"[✓] Скопировано файлов из bin/: "
        f"{len(copied_files)}"
    )
    for f in sorted(copied_files):
        print(f"    • {f.name}")


def create_inno_setup_script(
    exe_name: str,
    version_str: str,
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
AppVersion={version_str}
DefaultDirName={{autopf}}\\{EXE_BASE_NAME}
DefaultGroupName={EXE_BASE_NAME}
OutputDir={cwd}\\setup_output
OutputBaseFilename={EXE_BASE_NAME}_v{version_str}_setup
SetupIconFile={cwd}\\{ICON.replace("/", "\\")}
Compression=lzma2/ultra64
SolidCompression=yes
LZMADictionarySize=65536
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
    print("[*] Запуск интерактивного обновления версии...")
    version_str = prompt_version_update()
    
    # Синхронизируем версию в коде перед сборкой
    update_app_version_py(version_str)

    create_version_file(version_str)

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
        "--hidden-import=deew",

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
        exe_name, version_str
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