# -*- coding: utf-8 -*-
"""Скрипт сборки C#-версии K-Tools.

Выполняет компиляцию приложения на C# (.NET 10 / WinUI 3) в режиме self-contained
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
SRC_DIR = BASE_DIR
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
    # Приоритет 1: версия из переменной окружения BUILD_VERSION (передается из GitHub Actions)
    env_version = os.environ.get("BUILD_VERSION")
    if env_version:
        save_version(env_version)
        print(f"[✓] Версия успешно получена из переменной окружения BUILD_VERSION: {env_version}")
        return env_version

    # Приоритет 2: из CHANGELOG.md
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
    bin_dir = SRC_DIR / "KTools.App" / "bin"
    if bin_dir.exists():
        matches = list(bin_dir.rglob(f"publish/{EXE_BASE_NAME}.exe"))
        # Фильтруем Release сборки
        release_matches = [m.parent for m in matches if "Release" in str(m)]
        if release_matches:
            return release_matches[0]
        if matches:
            return matches[0].parent

    candidates = [
        SRC_DIR / "KTools.App" / "bin" / "Release" / "net10.0-windows10.0.26100.0" / "win-x64" / "publish",
        SRC_DIR / "KTools.App" / "bin" / "x64" / "Release" / "net10.0-windows10.0.26100.0" / "win-x64" / "publish"
    ]
    for path in candidates:
        if (path / f"{EXE_BASE_NAME}.exe").exists():
            return path
    
    return candidates[0]


def build_csharp_app(version: str) -> Path:
    """Сборка C# приложения через dotnet publish."""
    print("=" * 60)
    print(f"1. Компиляция C# приложения (dotnet publish) v{version}...")
    print("=" * 60)

    clean_version = version.split("-")[0]
    if clean_version.count(".") == 2:
        clean_version += ".0"

    cmd = [
        "dotnet", "publish",
        str(PROJECT_FILE),
        "-c", "Release",
        "-r", "win-x64",
        "--self-contained", "true",
        "-p:PublishSingleFile=false",
        "-p:PublishReadyToRun=false",
        "-p:Platform=x64",
        f"-p:Version={version}",
        f"-p:InformationalVersion={version}",
        f"-p:FileVersion={clean_version}",
        f"-p:AssemblyVersion={clean_version}"
    ]

    print(f"Выполнение команды: {' '.join(cmd)}")
    result = subprocess.run(cmd, cwd=str(BASE_DIR))

    if result.returncode != 0:
        print("[!] Ошибка при компиляции C# приложения!")
        sys.exit(1)

    publish_dir = find_publish_folder()
    if not publish_dir.exists():
        print(f"[!] Ошибка: Папка публикации не найдена по пути: {publish_dir}")
        sys.exit(1)

    print(f"[✓] C# приложение успешно скомпилировано в: {publish_dir}")
    
    # Очистка локализаций WinUI (удаляем все языковые папки кроме нужных если есть)
    clean_unused_localizations(publish_dir)

    return publish_dir


def clean_unused_localizations(publish_dir: Path):
    """Удаление ненужных языковых папок для уменьшения размера дистрибутива."""
    print("[*] Очистка неиспользуемых папок локализации...")
    cleaned_count = 0
    keep_locales = {"bin", "assets", "en-US", "ru", "ru-RU"}
    for item in publish_dir.iterdir():
        if item.is_dir() and item.name not in keep_locales:
            # В WinUI3 dotnet publish создаёт десятки языковых папок (de, fr, es, zh-Hans...)
            if len(item.name) in (2, 5) and ("-" in item.name or item.name.isalpha()):
                try:
                    shutil.rmtree(item)
                    cleaned_count += 1
                except Exception as e:
                    print(f"[!] Не удалось удалить папку локализации {item.name}: {e}")
    print(f"[✓] Удалено папок локализации: {cleaned_count}")


def generate_inno_script(version: str, publish_dir: Path) -> Path:
    """Динамически генерирует файл скрипта установки Inno Setup (KTools_CSharp.iss)."""
    iss_file = BASE_DIR / "KTools_CSharp.iss"
    output_dir = BASE_DIR / "setup_output"
    icon_file = SRC_DIR / "KTools.App" / "Assets" / "AppIcon.ico"
    decoders_exe = BASE_DIR / "bin" / "eac3to_decoders" / "eac3to Decoder Pack 1.4.exe"
    # Флаг временного включения/отключения упаковывания декодеров eac3to в инсталлятор
    include_decoders = False and decoders_exe.exists()

    components_section = """[Components]
Name: "main"; Description: "Основные файлы KTools"; Types: full compact custom; Flags: fixed"""
    if include_decoders:
        components_section += '\nName: "decoders"; Description: "Декодеры eac3to (опционально, для работы с DTS/DTS-HD)"; Types: custom; Flags: dontinheritcheck'

    files_decoders_line = f'Source: "{decoders_exe}"; DestDir: "{{tmp}}"; Flags: deleteafterinstall; Components: decoders' if include_decoders else ''

    run_decoders_line = f"""Filename: "{{tmp}}\\eac3to Decoder Pack 1.4.exe"; \\
Parameters: "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART"; \\
StatusMsg: "Установка декодеров для eac3to..."; \\
Flags: runascurrentuser; Components: decoders""" if include_decoders else ''

    iss_content = f"""; === АВТОМАТИЧЕСКИ СГЕНЕРИРОВАННЫЙ СКРИПТ INNO SETUP ===
[Setup]
AppId=krnzhnr.ktools
AppName=KTools
AppVersion={version}
DefaultDirName={{autopf}}\\KTools
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultGroupName=KTools
OutputDir={output_dir}
OutputBaseFilename=KTools_v{version}_Setup
SetupIconFile={icon_file}
Compression=lzma2/ultra64
SolidCompression=yes
LZMADictionarySize=65536
ArchitecturesInstallIn64BitMode=x64compatible

{components_section}

[Tasks]
Name: "desktopicon"; Description: "{{cm:CreateDesktopIcon}}"; \\
GroupDescription: "{{cm:AdditionalIcons}}"; Flags: unchecked

[Files]
; Основная папка публикации .NET self-contained
Source: "{publish_dir}\\*"; DestDir: "{{app}}"; \\
Flags: ignoreversion recursesubdirs createallsubdirs
{files_decoders_line}

[Icons]
Name: "{{group}}\\KTools"; \\
Filename: "{{app}}\\KTools.App.exe"; \\
IconFilename: "{{app}}\\AppIcon.ico"
Name: "{{autodesktop}}\\KTools"; \\
Filename: "{{app}}\\KTools.App.exe"; \\
IconFilename: "{{app}}\\AppIcon.ico"; \\
Tasks: desktopicon

[Run]
Filename: "{{app}}\\KTools.App.exe"; \\
Description: "{{cm:LaunchProgram,KTools}}"; \\
Flags: nowait postinstall skipifsilent
Filename: "{{app}}\\KTools.App.exe"; \\
Flags: nowait; Check: IsSilentUpdate
{run_decoders_line}

[UninstallDelete]
Type: filesandordirs; Name: "{{app}}\\bin"

[Registry]
Root: HKCU; Subkey: "Software\\Classes\\*\\shell\\KTools"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\\Classes\\Directory\\shell\\KTools"; Flags: uninsdeletekey

[Code]
function IsSilentUpdate: Boolean;
begin
  Result := WizardSilent;
end;

function GetInstallDir(const AppIdStr: String): String;
var
  sUnInstPath: String;
  sInstallDir: String;
begin
  sUnInstPath := 'Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\' + AppIdStr + '_is1';
  sInstallDir := '';
  if not RegQueryStringValue(HKLM, sUnInstPath, 'InstallLocation', sInstallDir) then
    RegQueryStringValue(HKCU, sUnInstPath, 'InstallLocation', sInstallDir);
  Result := sInstallDir;
end;

function GetUninstallString(const AppIdStr: String): String;
var
  sUnInstPath: String;
  sUnInstallString: String;
begin
  sUnInstPath := 'Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\' + AppIdStr + '_is1';
  sUnInstallString := '';
  if not RegQueryStringValue(HKLM, sUnInstPath, 'UninstallString', sUnInstallString) then
    RegQueryStringValue(HKCU, sUnInstPath, 'UninstallString', sUnInstallString);
  Result := sUnInstallString;
end;

procedure RemoveOldVersion(const AppIdStr: String);
var
  sUnInstallString: String;
  sInstallDir: String;
  iResultCode: Integer;
begin
  sInstallDir := GetInstallDir(AppIdStr);
  sUnInstallString := GetUninstallString(AppIdStr);
  if sUnInstallString <> '' then
  begin
    if sInstallDir = '' then
    begin
      sInstallDir := ExtractFilePath(RemoveQuotes(sUnInstallString));
    end;
    sUnInstallString := RemoveQuotes(sUnInstallString);
    Exec(sUnInstallString, '/VERYSILENT /NORESTART /SUPPRESSMSGBOXES /_?="' + sInstallDir + '"', '', SW_HIDE, ewWaitUntilTerminated, iResultCode);
    // Принудительно удаляем оставшуюся папку со старыми файлами Python версии
    if (sInstallDir <> '') and DirExists(sInstallDir) then
    begin
      DelTree(sInstallDir, True, True, True);
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
  begin
    // Бесшумное удаление всех старых промежуточных версий
    RemoveOldVersion('krnzhnr.ktools.v1');
    RemoveOldVersion('krnzhnr.ktools.csharp.v2');
    RemoveOldVersion('krnzhnr.ktools.v2');
    // Удаление устаревших ярлыков с дефисом
    DeleteFile(ExpandConstant('{{autodesktop}}\\K-Tools.lnk'));
    DeleteFile(ExpandConstant('{{userdesktop}}\\K-Tools.lnk'));
  end;
end;
"""
    iss_file.write_text(iss_content, encoding="utf-8")
    print(f"[✓] Скрипт установки {iss_file.name} успешно сгенерирован.")
    return iss_file


def build_inno_installer(version: str, publish_dir: Path):
    """Компиляция инсталлятора через Inno Setup (iscc)."""
    print("=" * 60)
    print("2. Генерация инсталлятора через Inno Setup...")
    print("=" * 60)

    iss_file = generate_inno_script(version, publish_dir)

    # Ищем iscc.exe в PATH или в стандартных папках Program Files
    iscc_exe = shutil.which("iscc")
    if not iscc_exe:
        possible_paths = [
            Path(os.environ.get("ProgramFiles(x86)", "C:\\Program Files (x86)")) / "Inno Setup 6" / "iscc.exe",
            Path(os.environ.get("ProgramFiles", "C:\\Program Files")) / "Inno Setup 6" / "iscc.exe",
        ]
        for p in possible_paths:
            if p.exists():
                iscc_exe = str(p)
                break

    if not iscc_exe:
        print("[!] Ошибка: Inno Setup Compiler (iscc.exe) не найден!")
        print("Установите Inno Setup 6 или добавьте путь к iscc.exe в системный PATH.")
        sys.exit(1)

    output_dir = BASE_DIR / "setup_output"
    output_dir.mkdir(exist_ok=True)

    cmd = [
        iscc_exe,
        str(iss_file)
    ]

    print(f"Выполнение команды: {' '.join(cmd)}")
    result = subprocess.run(cmd, cwd=str(BASE_DIR))

    if result.returncode != 0:
        print("[!] Ошибка при компиляции инсталлятора Inno Setup!")
        sys.exit(1)

    setup_file = output_dir / f"KTools_v{version}_Setup.exe"
    print(f"[✓] Инсталлятор успешно сгенерирован: {setup_file}")


def main():
    print("=" * 60)
    print("      K-Tools (C# / WinUI 3) - Автоматическая сборка")
    print("=" * 60)

    version = prompt_version_update()
    print(f"[*] Сборка версии: {version}")

    publish_dir = build_csharp_app(version)

    build_inno_installer(version, publish_dir)

    print("=" * 60)
    print(f"[🎉] Сборка C# версии K-Tools v{version} успешно завершена!")
    print("=" * 60)


if __name__ == "__main__":
    main()
