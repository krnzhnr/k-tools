# -*- coding: utf-8 -*-
"""Скрипт сборки K-Tools.

Выполняет компиляцию приложения через Nuitka (standalone),
копирует внешние зависимости из bin/ и генерирует
скрипт Inno Setup (.iss) для создания инсталлятора.
"""

import os
import shutil
import subprocess
import sys
import time
import re
import importlib.util
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
VENV_DIR = BASE_DIR / "venv"
PYTHON_EXE = VENV_DIR / "Scripts" / "python.exe"
REQUIREMENTS = BASE_DIR / "requirements.txt"
SCRIPT = BASE_DIR / "main.py"
EXE_BASE_NAME = "KTools"
ICON = BASE_DIR / "assets" / "app_icon.ico"
VERSION_FILE = BASE_DIR.parent / "version.txt"
CHANGELOG_FILE = BASE_DIR.parent / "CHANGELOG.md"


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

    Автоматически берет версию из окружения (CI)
    или из CHANGELOG.md (Локально).

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
            f"[*] CI/CD (Pre-release): Фиксированное имя файла: "
            f"{output_filename}"
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
ArchitecturesInstallIn64BitMode=x64compatible

[Tasks]
Name: "desktopicon"; Description: "{{cm:CreateDesktopIcon}}"; \\
GroupDescription: "{{cm:AdditionalIcons}}"; Flags: unchecked

[Files]
; Основная папка сборки (Nuitka) + bin/
Source: "{cwd}\\dist\\{exe_name}.dist\\*"; DestDir: "{{app}}"; \\
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


def build(include_bin: bool = False) -> None:
    """Основная процедура сборки приложения.

    Args:
        include_bin: Копировать ли папку bin/ в каталог сборки.
    """
    print("[*] Определение версии сборки...")
    version_str = prompt_version_update()

    # Синхронизируем версию в коде перед сборкой
    update_app_version_py(version_str)

    python_bin = ensure_venv()

    print("[*] Проверка импорта ядра...")
    try:
        sys.path.insert(0, str(BASE_DIR))
        if importlib.util.find_spec("app.core.script_registry") is None:
            raise ImportError("Модуль app.core.script_registry не найден")

        print("[✓] Импорт ядра доступен.")
    except ImportError as e:
        print(f"[!] Ошибка импорта: {e}")

    # Динамически находим путь к ресурсам qfluentwidgets
    qfw_spec = importlib.util.find_spec("qfluentwidgets")
    if qfw_spec and qfw_spec.origin:
        qfw_data_dir = Path(qfw_spec.origin).parent
        print(f"[✓] Путь к qfluentwidgets определен: {qfw_data_dir}")
    else:
        # Резервный вариант, если пакет не установлен
        qfw_data_dir = (
            BASE_DIR / "venv" / "Lib" / "site-packages" / "qfluentwidgets"
        )
        if not qfw_data_dir.exists():
            qfw_data_dir = (
                BASE_DIR / "venv" / "lib" / "site-packages" / "qfluentwidgets"
            )

    cmd = [
        str(python_bin),
        "-m",
        "nuitka",
        "--standalone",
        "--output-dir=dist",
        "--mingw64",
        "--assume-yes-for-downloads",
        "--plugin-enable=pyqt6",
        "--include-package=app",
        "--include-module=deew",
        "--windows-console-mode=disable",
        f"--output-filename={EXE_BASE_NAME}.exe",
        str(SCRIPT),
    ]

    if ICON.exists():
        abs_icon = ICON.resolve()
        cmd.append(f"--windows-icon-from-ico={abs_icon}")

    print("[*] Запуск Nuitka (это может занять 5-15 минут)...")
    print(f"Команда: {' '.join(cmd)}")
    subprocess.check_call(cmd)

    # Nuitka создает папку с суффиксом .dist
    dist_folder = BASE_DIR / "dist" / f"{SCRIPT.stem}.dist"
    # Переименовываем папку для InnoSetup, если необходимо
    # (оставим как есть, но InnoSetup теперь ссылается на {EXE_BASE_NAME}.dist)
    if dist_folder.exists() and dist_folder.name != f"{EXE_BASE_NAME}.dist":
        target_dist = BASE_DIR / "dist" / f"{EXE_BASE_NAME}.dist"
        if target_dist.exists():
            shutil.rmtree(target_dist)
        dist_folder.rename(target_dist)

    # Копирование иконки для ярлыков Inno Setup
    dst_icon = BASE_DIR / "dist" / f"{EXE_BASE_NAME}.dist" / "app_icon.ico"
    if ICON.exists():
        shutil.copy2(ICON, dst_icon)
        print(f"[✓] Иконка скопирована для ярлыков: {dst_icon}")

    # Копирование папки bin/ со всеми зависимостями (если передан флаг)
    if include_bin:
        copy_bin_directory(f"{EXE_BASE_NAME}.dist")
    else:
        print(
            "[*] Флаг --include-bin отсутствует. "
            "Зависимости bin/ не будут скопированы."
        )

    # Ручное копирование папки ресурсов qfluentwidgets для гарантированного
    # наличия стилей (QSS) и шрифтов в каталоге сборки.
    dst_qfw = BASE_DIR / "dist" / f"{EXE_BASE_NAME}.dist" / "qfluentwidgets"
    if dst_qfw.exists():
        shutil.rmtree(dst_qfw)

    print(f"[*] Копирование ресурсов qfluentwidgets → {dst_qfw}")
    try:
        shutil.copytree(qfw_data_dir, dst_qfw)
        print("[✓] Папка qfluentwidgets успешно скопирована.")
    except Exception as e:
        print(f"[!] Ошибка при копировании ресурсов qfluentwidgets: {e}")

    # Генерация ISS скрипта
    create_inno_setup_script(EXE_BASE_NAME, version_str)

    print(f"[✓] Сборка готова: dist/{EXE_BASE_NAME}.dist")


if __name__ == "__main__":
    is_ci = os.environ.get("CI_VERSION") is not None
    inc_bin = "--include-bin" in sys.argv
    try:
        clean()
        build(include_bin=inc_bin)
        if not is_ci:
            print("\n[*] Окно закроется через 10 секунд...")
            time.sleep(10)
    except Exception as e:
        print(f"\n[!] ОШИБКА: {e}")
        if not is_ci:
            input("Нажмите Enter чтобы выйти...")
        else:
            sys.exit(1)  # Жестко завершаем с ошибкой для пайплайна
