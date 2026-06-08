# -*- coding: utf-8 -*-
# Скрипт автоматической сборки, генерации сертификата и упаковки K-Tools в MSIX.
# Все комментарии и вывод выполнены на русском языке согласно правилам.

$ErrorActionPreference = "Stop"

# Получаем текущую дату и время для штампа версии сборки
$timestamp = Get-Date -Format "yyyyMMddHHmmss"

Write-Host "=== Запуск процесса полной сборки и упаковки K-Tools ===" -ForegroundColor Cyan

# Шаг 1. Сборка проекта в конфигурации Release под x64
Write-Host "`n[1/3] Компиляция проекта KTools.App в режиме Release (x64)..." -ForegroundColor Yellow
dotnet build KTools.App\KTools.App.csproj -c Release -p:Platform=x64

# Шаг 2. Проверка и генерация сертификата подписи
Write-Host "`n[2/3] Проверка наличия сертификата подписи..." -ForegroundColor Yellow
if (-not (Test-Path "devcert.pfx")) {
    Write-Host "Сертификат devcert.pfx не найден. Генерация нового сертификата..." -ForegroundColor Cyan
    winapp cert generate --manifest KTools.App\Package.appxmanifest --install
    Write-Host "Сертификат успешно создан и импортирован в систему." -ForegroundColor Green
} else {
    Write-Host "Обнаружен существующий сертификат devcert.pfx." -ForegroundColor Green
}

# Шаг 3. Упаковка в MSIX (self-contained)
Write-Host "`n[3/3] Упаковка приложения в пакет MSIX (self-contained)..." -ForegroundColor Yellow
# Путь к скомпилированным бинарникам
$binPath = "KTools.App\bin\x64\Release\net8.0-windows10.0.26100.0\win-x64"
winapp pack $binPath --cert devcert.pfx --self-contained

# Поиск созданного пакета MSIX и его переименование с добавлением штампа времени
Write-Host "`n=== Поиск и переименование созданного пакета ===" -ForegroundColor Cyan
$msixFiles = Get-ChildItem -Filter "*.msix" | Where-Object { 
    $_.LastWriteTime -gt (Get-Date).AddMinutes(-2) 
}

if ($msixFiles.Count -eq 0) {
    Write-Error "Файл установщика MSIX не был найден в текущем каталоге!"
}

foreach ($file in $msixFiles) {
    $newName = "{0}_{1}{2}" -f $file.BaseName, $timestamp, $file.Extension
    Rename-Item -Path $file.FullName -NewName $newName
    Write-Host "Пакет успешно подготовлен и сохранен по пути:" -ForegroundColor Green
    Write-Host $newName -ForegroundColor Green
}

Write-Host "`n=== Процесс сборки успешно завершен! ===" -ForegroundColor Green
