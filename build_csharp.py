# -*- coding: utf-8 -*-
"""Скрипт сборки C#-версии K-Tools.

Выполняет компиляцию приложения на C# (.NET 8 / WinUI 3) в режиме self-contained
и генерирует скрипт Inno Setup для создания автономного установщика.
"""

import os
import shutil
import subprocess
import sys
import time
import re
from pathlib import Path

# Принудительная установка UTF-8 для консоли и процессов
os.environ["PYTHONIOENCODING"] = "utf-8"

# Попытка реконфигурации стандартных потоков для поддержки UTF-8 (Python 3.7+)
if hasattr(sys.stdout, "reconfigure"):
    try:
        getattr(sys.stdout, "reconfigure")(encoding="utf-8", errors="replace")
        getattr(sys.stderr, "reconfigure")(encoding="utf-8", errors="replace")
    except Exception:
        pass

# === Настройки ===
BASE_DIR = Path(__file__).parent.resolve()
SRC_DIR = BASE_DIR / "src-csharp"
PROJECT_FILE = SRC_DIR / "KTools.App" / "KTools.App.csproj"
VERSION_FILE = BASE_DIR / "version.txt"
CHANGELOG_FILE = BASE_DIR / "CHANGELOG.md"
ICON_SRC = SRC_DIR / "KTools.App" / "Assets" / "AppIcon.ico"
EXE_BASE_NAME = "KTools.App"  # Имя оригинального exe-файла после dotnet publish


def get_current_version() -> str:
    """Получить текущую версию из файла."""
    if VERSION_FILE.exists():
        return VERSION_FILE.read_text().strip()
    return "2.0.0"


def save_version(version: str) -> None:
    """Сохранить новую версию в файл."""
    VERSION_FILE.write_text(version, encoding="utf-8")


def extract_version_from_changelog() -> str:
    """Извлечь последнюю версию из CHANGELOG.md."""
    if not CHANGELOG_FILE.exists():
        raise ValueError(f"Файл {CHANGELOG_FILE} не найден")

    content = CHANGELOG_FILE.read_text(encoding="utf-8")
    match = re.search(r"^#\s*([\d\.\-\w]+)", content, re.MULTILINE)

    if not match:
        raise ValueError("Не удалось найти версию в CHANGELOG.md")

    version = match.group(1).strip()
    return version


def prompt_version_update() -> str:
    """Определить версию для сборки."""
    try:
        version = extract_version_from_changelog()
        save_version(version)
        print(f"[✓] Версия успешно определена из CHANGELOG.md: {version}")
        return version
    except Exception as e:
        print(f"[!] Ошибка при получении версии из CHANGELOG.md: {e}")
        current_version = get_current_version()
        print(f"[*] Используется текущая версия из файла: {current_version}")
        return current_version


def find_publish_folder() -> Path:
    """Динамически находит папку публикации publish."""
    # Возможные пути сборки (с x64 и без в зависимости от параметров MSBuild)
    candidates = [
        SRC_DIR / "KTools.App" / "bin" / "Release" / "net8.0-windows10.0.26100.0" / "win-x64" / "publish",
        SRC_DIR / "KTools.App" / "bin" / "x64" / "Release" / "net8.0-windows10.0.26100.0" / "win-x64" / "publish"
    ]
    for path in candidates:
        if (path / f"{EXE_BASE_NAME}.exe").exists():
            return path
    
    # Возвращаем путь по умолчанию, если ничего не найдено
    return candidates[0]


def create_inno_setup_script(
    publish_dir: Path,
    version_str: str,
) -> Path:
    """Генерация скрипта для Inno Setup."""
    cwd = str(BASE_DIR).replace("\\", "\\\\")
    publish_p = str(publish_dir).replace("\\", "\\\\")
    icon_p = str(ICON_SRC).replace("\\", "\\\\")

    from datetime import datetime
    datetime_str = datetime.now().strftime("%d%m%Y-%H%M")
    output_filename = f"K-Tools_v{version_str}_setup_{datetime_str}"

    iss_content = f"""
[Setup]
AppId=krnzhnr.ktools.csharp.v2
AppName=K-Tools
AppVersion={version_str}
DefaultDirName={{autopf}}\\K-Tools
DefaultGroupName=K-Tools
OutputDir={cwd}\\setup_output
OutputBaseFilename={output_filename}
SetupIconFile={icon_p}
Compression=lzma2/ultra64
SolidCompression=yes
LZMADictionarySize=65536
ArchitecturesInstallIn64BitMode=x64compatible

[Components]
Name: "main"; Description: "Основные файлы K-Tools"; Types: full compact custom; Flags: fixed
Name: "decoders"; Description: "Декодеры eac3to (требуются для работы с AAC/DTS/AC3)"; Types: full

[Tasks]
Name: "desktopicon"; Description: "{{cm:CreateDesktopIcon}}"; \\
GroupDescription: "{{cm:AdditionalIcons}}"; Flags: unchecked

[Files]
; Основная папка публикации .NET self-contained
Source: "{publish_p}\\*"; DestDir: "{{app}}"; \\
Flags: ignoreversion recursesubdirs createallsubdirs
; Установщик декодеров eac3to
Source: "{cwd}\\bin\\eac3to_decoders\\eac3to Decoder Pack 1.4.exe"; DestDir: "{{tmp}}"; \\
Flags: deleteafterinstall; Components: decoders

[Icons]
Name: "{{group}}\\K-Tools"; \\
Filename: "{{app}}\\{EXE_BASE_NAME}.exe"; \\
IconFilename: "{{app}}\\AppIcon.ico"
Name: "{{commondesktop}}\\K-Tools"; \\
Filename: "{{app}}\\{EXE_BASE_NAME}.exe"; \\
IconFilename: "{{app}}\\AppIcon.ico"; \\
Tasks: desktopicon

[Run]
Filename: "{{app}}\\{EXE_BASE_NAME}.exe"; \\
Description: "{{cm:LaunchProgram,K-Tools}}"; \\
Flags: nowait postinstall skipifsilent
Filename: "{{tmp}}\\eac3to Decoder Pack 1.4.exe"; \\
Parameters: "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART"; \\
StatusMsg: "Установка декодеров для eac3to..."; \\
Flags: runascurrentuser; Components: decoders
"""
    iss_path = SRC_DIR / "KTools_CSharp.iss"
    iss_path.write_text(iss_content, encoding="utf-8")
    print(f"[✓] Создан скрипт инсталлятора Inno Setup: {iss_path}")
    return iss_path


def compile_installer(iss_path: Path) -> None:
    """Компиляция установщика через ISCC.exe."""
    iscc_path = "ISCC.exe"  # Пробуем запустить из PATH
    
    # Стандартные пути установки Inno Setup
    search_paths = [
        Path("C:/Program Files (x86)/Inno Setup 6/ISCC.exe"),
        Path("C:/Program Files/Inno Setup 6/ISCC.exe")
    ]
    
    for path in search_paths:
        if path.exists():
            iscc_path = str(path)
            break

    print(f"[*] Запуск компиляции установщика с помощью {iscc_path}...")
    try:
        subprocess.check_call([iscc_path, str(iss_path)])
        print(f"[✓] Установщик успешно собран в папке: {BASE_DIR / 'setup_output'}")
    except Exception as e:
        print(f"[!] Не удалось автоматически скомпилировать установщик через Inno Setup: {e}")
        print("[!] Пожалуйста, убедитесь, что Inno Setup 6 установлен и добавлен в PATH, либо запустите сгенерированный ISS скрипт вручную.")


def clean_publish_folder() -> None:
    """Очищает предыдущие сборки publish."""
    # Пытаемся найти папки публикации и очистить их
    candidates = [
        SRC_DIR / "KTools.App" / "bin" / "Release" / "net8.0-windows10.0.26100.0" / "win-x64" / "publish",
        SRC_DIR / "KTools.App" / "bin" / "x64" / "Release" / "net8.0-windows10.0.26100.0" / "win-x64" / "publish"
    ]
    for path in candidates:
        if path.exists():
            print(f"[*] Очистка папки публикации {path}...")
            shutil.rmtree(path)


def main() -> None:
    print("=== Начало сборки K-Tools C# Edition ===")
    version_str = prompt_version_update()

    # Шаг 1. Очистка старых данных публикации
    clean_publish_folder()

    # Шаг 2. Запуск dotnet publish
    print("[*] Запуск компиляции C# проекта (.NET 8 / WinUI 3)...")
    cmd = [
        "dotnet", "publish", str(PROJECT_FILE),
        "-c", "Release",
        "-r", "win-x64",
        "--self-contained", "true",
        "-p:WindowsPackageType=None"
    ]
    print(f"Команда: {' '.join(cmd)}")
    subprocess.check_call(cmd)

    # Шаг 3. Поиск папки публикации
    publish_dir = find_publish_folder()
    if not publish_dir.exists():
        raise FileNotFoundError(f"Не удалось найти папку публикации после dotnet publish по пути: {publish_dir}")
    print(f"[✓] Папка публикации найдена: {publish_dir}")

    # Шаг 5. Копирование иконки в папку публикации для ярлыка
    dst_icon = publish_dir / "AppIcon.ico"
    if ICON_SRC.exists():
        shutil.copy2(ICON_SRC, dst_icon)
        print(f"[✓] Иконка приложения скопирована в: {dst_icon}")

    # Шаг 6. Создание ISS скрипта
    iss_path = create_inno_setup_script(publish_dir, version_str)

    # Шаг 7. Компиляция установщика
    compile_installer(iss_path)

    print("\n=== Сборка K-Tools C# Edition успешно завершена! ===")


if __name__ == "__main__":
    try:
        main()
        print("\n[*] Окно закроется через 10 секунд...")
        time.sleep(10)
    except Exception as e:
        print(f"\n[!] ОШИБКА СБОРКИ: {e}")
        input("Нажмите Enter, чтобы выйти...")
