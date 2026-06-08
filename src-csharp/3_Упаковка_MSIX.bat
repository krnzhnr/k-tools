@echo off
chcp 65001 > nul
title Шаг 3: Упаковка приложения K-Tools в MSIX
echo Упаковка скомпилированных файлов в MSIX с автоматической подписью...

if not exist "%~dp0devcert.pfx" (
    echo [ОШИБКА] Отсутствует сертификат devcert.pfx! Сначала запустите Шаг 1.
    color 0C
    pause
    exit /b 1
)

if not exist "%~dp0KTools.App\bin\x64\Release\net8.0-windows10.0.26100.0\win-x64" (
    echo [ОШИБКА] Отсутствует папка сборки Release! Сначала запустите Шаг 2.
    color 0C
    pause
    exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
    "$timestamp = Get-Date -Format 'yyyyMMddHHmmss';" ^
    "winapp pack KTools.App\bin\x64\Release\net8.0-windows10.0.26100.0\win-x64 --cert devcert.pfx --self-contained;" ^
    "Get-ChildItem -Filter '*.msix' | Where-Object { $_.LastWriteTime -gt (Get-Date).AddMinutes(-2) } | ForEach-Object {" ^
    "  $newName = '{0}_{1}{2}' -f $_.BaseName, $timestamp, $_.Extension;" ^
    "  Rename-Item -Path $_.FullName -NewName $newName;" ^
    "  Write-Host 'Пакет успешно сохранен как: ' $newName -ForegroundColor Green" ^
    "}"

if %errorlevel% neq 0 (
    echo [ОШИБКА] Ошибка упаковки или подписи пакета.
    color 0C
) else (
    echo [УСПЕХ] Упаковка в MSIX успешно завершена!
)
pause
