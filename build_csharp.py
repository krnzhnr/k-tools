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
RELEASE_NOTES_FILE = SRC_DIR / "KTools.App" / "Assets" / "WhatsNew" / "release-notes.md"
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


def extract_release_notes_section() -> str:
    """Извлечь верхнюю секцию CHANGELOG.md (заголовок версии + тело до следующего '# ')."""
    content = CHANGELOG_FILE.read_text(encoding="utf-8")
    match = re.search(r"^#\s*([\d\.\-\w]+)", content, re.MULTILINE)
    if not match:
        raise ValueError("Не удалось найти версию в CHANGELOG.md")

    header = match.group(0)
    start = content.find(header) + len(header)
    rest = content[start:]
    next_match = re.search(r"^#\s+[^#]", rest, re.MULTILINE)
    section = rest[:next_match.start()] if next_match else rest
    return (header + "\n" + section).strip() + "\n"


def is_preview_version(version: str) -> bool:
    """Является ли версия пререлизной (вид 2.2.0-preview.171)."""
    return bool(re.search(r"-preview\.\d+", version or ""))


def normalize_note_item(line: str) -> str:
    """Нормализовать пункт заметок для сравнения: срезать нумерацию и маркеры списка."""
    text = line.strip()
    text = re.sub(r"^\d+[.)]\s*", "", text)
    text = re.sub(r"^[-*+]\s*", "", text)
    return text.strip()


def split_notes_body(body: str) -> list:
    """Разбить тело заметок на подсекции: список пар (заголовок, строки пунктов)."""
    sections = []
    current_title = None
    current_items: list = []
    for raw_line in (body or "").splitlines():
        line = raw_line.strip()
        if not line:
            continue
        if line.startswith("### "):
            if current_title is not None and current_items:
                sections.append((current_title, list(current_items)))
            current_title = line[4:].strip()
            current_items = []
            continue
        if line.startswith("#"):
            continue
        if current_title is None:
            current_title = "Добавлено"
        current_items.append(raw_line.rstrip())
    if current_title is not None and current_items:
        sections.append((current_title, list(current_items)))
    return sections


def compute_preview_delta(current_body: str, prev_body: str) -> str:
    """Вычислить дельту пунктов текущего пререлиза относительно прошлого.

    Сравнение идёт по тексту пунктов без ведущих номеров, поэтому вставка
    в середину со сдвигом нумерации ложных срабатываний не даёт.
    Переформулированные и сжатые пункты попадают в дельту как новые.
    Пустой результат означает отсутствие изменений для окна «Что нового».
    """
    prev_known: dict = {}
    for title, items in split_notes_body(prev_body):
        prev_known.setdefault(title, set()).update(
            normalize_note_item(i) for i in items)
    delta_parts: list = []
    for title, items in split_notes_body(current_body):
        known = prev_known.get(title, set())
        new_items = [i for i in items if normalize_note_item(i) not in known]
        if new_items:
            delta_parts.append("### " + title)
            delta_parts.extend(new_items)
    return ("\n".join(delta_parts).strip() + "\n") if delta_parts else ""


def bake_release_notes(version: str | None = None, prev_body: str | None = None) -> None:
    """Запечь снапшот изменений текущей версии для окна «Что нового».

    В заголовок подставляется полная версия сборки (включая -preview.N),
    чтобы окно показывалось один раз на каждый пререлиз.
    Для пререлиза запекается только дельта относительно прошлого
    опубликованного пререлиза; для стабильного релиза — полная секция.
    Пустая дельта означает пропуск автопоказа через гейт версий.
    Файл кладётся рядом с GIF-сценами и пакуется в установщик как Content.
    Ошибка не роняет сборку: без файла окно просто не показывается.
    """
    try:
        section = extract_release_notes_section()
        header, _, body = section.partition("\n")
        if not version:
            version = header.lstrip("#").strip()
        if is_preview_version(version) and (prev_body or "").strip():
            body = compute_preview_delta(body, prev_body or "")
        text = f"# {version}\n\n{body.strip()}\n" if body.strip() else f"# {version}\n"
        RELEASE_NOTES_FILE.parent.mkdir(parents=True, exist_ok=True)
        RELEASE_NOTES_FILE.write_text(text, encoding="utf-8")
        print(f"[✓] Снапшот изменений версии записан: {RELEASE_NOTES_FILE.name}")
    except Exception as e:
        print(f"[!] Не удалось запечь изменения версии: {e}")


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
        # PERF-07: явное true — релизный шип ставится пользователю с ReadyToRun,
        # как декларирует csproj для Release (PublishReadyToRun=True); молчаливое
        # расхождение устранено. Замер A/B на этом SSD-стенде (Measure-Startup,
        # Runs 3): тёплый старт в шуме, регресса нет; цена — publish +~54 МБ
        # (+24%), установщик +~11 МБ (+18%), что приемлемо. Холод на HDD-стенде
        # здесь неизмерим (дисков HDD нет, перезагрузка недоступна): добрать на
        # целевом железе; если там регресс — вернуть false, решение держать тут.
        # Триминг/single-file осознанно выключены (MSIX/COM), не трогать.
        "-p:PublishReadyToRun=true",
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

    iss_content = f"""; === АВТОМАТИЧЕСКИ СГЕНЕРИРОВАННЫЙ СКРИПТ INNO SETUP ===
[Setup]
; AppId зафиксирован навсегда: смена AppId плодит параллельные установки.
; Миграция старых идентификаторов выполняется кодом ниже (PrepareToInstall).
AppId=krnzhnr.ktools
AppName=KTools
AppVersion={version}
DefaultDirName={{autopf}}\\KTools
UsePreviousAppDir=yes
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

[Tasks]
Name: "desktopicon"; Description: "{{cm:CreateDesktopIcon}}"; \\
GroupDescription: "{{cm:AdditionalIcons}}"; Flags: unchecked

[Files]
; Основная папка публикации .NET self-contained
Source: "{publish_dir}\\*"; DestDir: "{{app}}"; \\
Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{{group}}\\KTools"; \\
Filename: "{{app}}\\KTools.App.exe"
Name: "{{autodesktop}}\\KTools"; \\
Filename: "{{app}}\\KTools.App.exe"; \\
Tasks: desktopicon; Check: ShouldCreateDesktopIcon

[Run]
Filename: "{{app}}\\KTools.App.exe"; \\
Description: "{{cm:LaunchProgram,KTools}}"; \\
Flags: nowait postinstall skipifsilent runasoriginaluser
Filename: "{{app}}\\KTools.App.exe"; \\
Flags: nowait runasoriginaluser; Check: IsSilentUpdate

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

function IsAppInstalled: Boolean;
begin
  Result := (GetUninstallString('krnzhnr.ktools') <> '') or (GetInstallDir('krnzhnr.ktools') <> '');
end;

function DesktopIconExists: Boolean;
begin
  Result := FileExists(ExpandConstant('{{autodesktop}}\\KTools.lnk')) or
            FileExists(ExpandConstant('{{userdesktop}}\\KTools.lnk')) or
            FileExists(ExpandConstant('{{commondesktop}}\\KTools.lnk'));
end;

function ShouldCreateDesktopIcon: Boolean;
begin
  // Если установка выполняется в тихом режиме (автоматическое обновление из программы):
  if IsSilentUpdate and not DesktopIconExists then
  begin
    // В тихом режиме ярлык ни при каких обстоятельствах не создается, если он уже отсутствует на рабочем столе
    Result := False;
    Exit;
  end;

  Result := True;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpSelectTasks then
  begin
    // При интерактивном обновлении поверх уже установленной версии:
    // Автоматически синхронизируем выбор задачи desktopicon с реальным наличием ярлыка.
    // Если ярлык на рабочем столе отсутствует (пользователь его удалил), снимаем отметку,
    // чтобы ярлык не пересоздавался повторно при обычном нажатии "Далее".
    if IsAppInstalled then
    begin
      if DesktopIconExists then
        WizardSelectTasks('desktopicon')
      else
        WizardSelectTasks('!desktopicon');
    end;
  end;
end;

// === Миграция старых версий: на ПК допускается только одна установка KTools ===
// Старая установка per-machine (HKLM, Program Files) не может быть удалена
// из непривилегированного процесса: для неё запрашивается повышение прав (runas).
// Если удалить не удалось, PrepareToInstall блокирует установку вместо
// тихой параллельной копии.

function IsUninstallEntryPresent(const AppIdStr: String): Boolean;
begin
  Result := GetUninstallString(AppIdStr) <> '';
end;

function IsPerMachineEntryPresent(const AppIdStr: String): Boolean;
var
  sValue: String;
begin
  Result := RegQueryStringValue(HKLM,
    'Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\' + AppIdStr + '_is1',
    'UninstallString', sValue) and (sValue <> '');
end;

function NeedsElevationForMigration: Boolean;
begin
  Result := (not IsAdminInstallMode) and (
    IsPerMachineEntryPresent('krnzhnr.ktools.v1') or
    IsPerMachineEntryPresent('krnzhnr.ktools.csharp.v2') or
    IsPerMachineEntryPresent('krnzhnr.ktools.v2') or
    IsPerMachineEntryPresent('krnzhnr.ktools'));
end;

function RemoveUninstallEntry(const AppIdStr: String; RootKey: Integer): Boolean;
var
  sUnKey, sUnInstallString, sInstallDir, sLogFile, sParams, sExecState: String;
  iResultCode, iExecError: Integer;
  bExecOk: Boolean;
begin
  Result := True;
  sUnKey := 'Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\' + AppIdStr + '_is1';
  if (not RegQueryStringValue(RootKey, sUnKey, 'UninstallString', sUnInstallString))
    or (sUnInstallString = '') then
  begin
    Log('Миграция: запись ' + AppIdStr + ' отсутствует, пропуск');
    Exit;
  end;
  if (not RegQueryStringValue(RootKey, sUnKey, 'InstallLocation', sInstallDir))
    or (sInstallDir = '') then
    sInstallDir := ExtractFilePath(RemoveQuotes(sUnInstallString));
  sUnInstallString := RemoveQuotes(sUnInstallString);
  sLogFile := ExpandConstant('{{tmp}}\\uninstall-' + AppIdStr + '.log');
  sParams := '/VERYSILENT /NORESTART /SUPPRESSMSGBOXES /LOG="' + sLogFile + '"';
  if sInstallDir <> '' then
    sParams := sParams + ' /_?="' + sInstallDir + '"';
  Log('Миграция: удаление ' + AppIdStr + ', команда: ' + sUnInstallString + ' ' + sParams);
  bExecOk := Exec(sUnInstallString, sParams, '', SW_HIDE, ewWaitUntilTerminated, iResultCode);
  if bExecOk then sExecState := 'True' else sExecState := 'False';
  Log('Миграция: Exec(' + AppIdStr + ') ok=' + sExecState + ' код=' + IntToStr(iResultCode));
  if ((not bExecOk) or (iResultCode <> 0)) and (RootKey = HKLM) then
  begin
    Log('Миграция: прямой запуск не удался, повтор с повышением прав (runas)');
    if ShellExec('runas', sUnInstallString, sParams, '', SW_HIDE, ewWaitUntilTerminated, iExecError) then
      Log('Миграция: запуск с повышением выполнен, код=' + IntToStr(iExecError))
    else
      Log('Миграция: запуск с повышением отклонён, код=' + IntToStr(iExecError));
  end;
  if (sInstallDir <> '') and DirExists(sInstallDir) then
  begin
    Log('Миграция: удаление остатков каталога ' + sInstallDir);
    if DelTree(sInstallDir, True, True, True) then
      Log('Миграция: каталог удалён ' + sInstallDir)
    else
      Log('Миграция: DelTree не смог удалить ' + sInstallDir);
  end;
  if RegQueryStringValue(RootKey, sUnKey, 'UninstallString', sUnInstallString)
    and (sUnInstallString <> '') then
  begin
    Log('Миграция: запись ' + AppIdStr + ' осталась в реестре');
    Result := False;
    Exit;
  end;
  if (sInstallDir <> '') and DirExists(sInstallDir) then
  begin
    Log('Миграция: каталог ' + sInstallDir + ' остался на диске (возможно, старая версия запущена)');
    Result := False;
    Exit;
  end;
  Log('Миграция: ' + AppIdStr + ' удалён');
end;

function RemoveOldVersion(const AppIdStr: String): Boolean;
begin
  Result := True;
  if not RemoveUninstallEntry(AppIdStr, HKLM) then Result := False;
  if not RemoveUninstallEntry(AppIdStr, HKCU) then Result := False;
end;

function RemoveAppIdEverywhere(const AppIdStr: String): Boolean;
var
  i: Integer;
begin
  Result := True;
  for i := 1 to 3 do
  begin
    if not IsUninstallEntryPresent(AppIdStr) then Exit;
    if not RemoveOldVersion(AppIdStr) then Result := False;
  end;
  if IsUninstallEntryPresent(AppIdStr) then Result := False;
end;

function InitializeSetup: Boolean;
begin
  Result := True;
  if NeedsElevationForMigration and (not WizardSilent) then
    MsgBox('Обнаружена старая версия KTools, установленная для всех пользователей. '
      + 'Для её удаления будут запрошены права администратора. '
      + 'Без удаления старая версия установка новой будет заблокирована, '
      + 'чтобы не плодить две копии программы.',
      mbInformation, MB_OK);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  sLeftovers, sCurrentModeCopy: String;
begin
  Result := '';
  NeedsRestart := False;
  sCurrentModeCopy := '';
  RemoveAppIdEverywhere('krnzhnr.ktools.v1');
  RemoveAppIdEverywhere('krnzhnr.ktools.csharp.v2');
  RemoveAppIdEverywhere('krnzhnr.ktools.v2');
  if IsAdminInstallMode then
  begin
    if not RemoveUninstallEntry('krnzhnr.ktools', HKCU) then
      sCurrentModeCopy := 'копия продукта в режиме для текущего пользователя';
  end
  else
  begin
    if not RemoveUninstallEntry('krnzhnr.ktools', HKLM) then
      sCurrentModeCopy := 'копия продукта в режиме для всех пользователей';
  end;
  sLeftovers := '';
  if IsUninstallEntryPresent('krnzhnr.ktools.v1') then
    sLeftovers := sLeftovers + #13#10 + '- старая версия 1.x: ' + GetInstallDir('krnzhnr.ktools.v1');
  if IsUninstallEntryPresent('krnzhnr.ktools.csharp.v2') then
    sLeftovers := sLeftovers + #13#10 + '- промежуточная версия: ' + GetInstallDir('krnzhnr.ktools.csharp.v2');
  if IsUninstallEntryPresent('krnzhnr.ktools.v2') then
    sLeftovers := sLeftovers + #13#10 + '- промежуточная версия: ' + GetInstallDir('krnzhnr.ktools.v2');
  if sCurrentModeCopy <> '' then
    sLeftovers := sLeftovers + #13#10 + '- ' + sCurrentModeCopy;
  if sLeftovers <> '' then
  begin
    Log('Миграция: остались неудалённые версии:' + sLeftovers);
    Result := 'На компьютере осталась старая версия KTools, удалить её автоматически не удалось.'
      + ' Установка заблокирована, чтобы не плодить две копии программы.' + #13#10 + #13#10
      + 'Закройте работающую старую версию, затем запустите установщик от имени администратора '
      + 'или удалите старую версию вручную через «Программы и компоненты».'
      + ' Остатки:' + sLeftovers;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
  begin
    // Удаление старых версий выполняется в PrepareToInstall до старта установки.
    // Здесь только чистка устаревших ярлыков с дефисом.
    DeleteFile(ExpandConstant('{{autodesktop}}\\K-Tools.lnk'));
    DeleteFile(ExpandConstant('{{userdesktop}}\\K-Tools.lnk'));
  end;
end;
"""
    iss_file.write_text(iss_content, encoding="utf-8")
    print(f"[✓] Скрипт установки {iss_file.name} успешно сгенерирован.")
    return iss_file


def verify_publish_content(publish_dir: Path) -> None:
    """Проверить, что контент окна «Что нового» доехал до публикации.

    Ловит рассинхрон CopyToOutputDirectory/CopyToPublishDirectory:
    без этого окно молча показывает заглушки в установленном приложении.
    """
    missing = []
    notes = publish_dir / "Assets" / "WhatsNew" / "release-notes.md"
    if not notes.exists():
        missing.append("release-notes.md")
    art_dir = publish_dir / "Assets" / "WhatsNew" / "art"
    gifs = list(art_dir.glob("*.gif")) if art_dir.exists() else []
    if not gifs:
        missing.append("art/*.gif")
    if missing:
        print(f"[!] Ошибка: в публикации отсутствует контент «Что нового»: {', '.join(missing)}")
        sys.exit(1)
    print(f"[✓] Контент «Что нового» на месте: заметки + {len(gifs)} GIF.")


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

    prev_body = None
    prev_file = os.environ.get("PREV_RELEASE_NOTES_FILE", "")
    if prev_file and Path(prev_file).exists():
        prev_body = Path(prev_file).read_text(encoding="utf-8-sig")

    bake_release_notes(version, prev_body)

    publish_dir = build_csharp_app(version)

    verify_publish_content(publish_dir)

    build_inno_installer(version, publish_dir)

    print("=" * 60)
    print(f"[🎉] Сборка C# версии K-Tools v{version} успешно завершена!")
    print("=" * 60)


if __name__ == "__main__":
    main()
