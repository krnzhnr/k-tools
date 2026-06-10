$ErrorActionPreference = "Stop"

function Invoke-BuildProcess {
    $timestamp = Get-Date -Format "yyyyMMddHHmmss"
    
    Write-Host "=== Запуск процесса полной сборки и упаковки K-Tools ===" -ForegroundColor Cyan
    Write-Host "[СОСТОЯНИЕ] Инициализация переменных завершена. Метка: $timestamp" -ForegroundColor DarkGray

    # 1. Компиляция
    Write-Host "`n[1/3] Компиляция проекта KTools.App в режиме Release (x64)..." -ForegroundColor Yellow
    Write-Host "[СОСТОЯНИЕ] Запуск дочернего процесса: dotnet build" -ForegroundColor DarkGray
    
    dotnet build KTools.App\KTools.App.csproj -c Release -p:Platform=x64
    
    Write-Host "[СОСТОЯНИЕ] Процесс dotnet build успешно завершен" -ForegroundColor DarkGray

    # 2. Проверка сертификата
    Write-Host "`n[2/3] Проверка наличия сертификата подписи..." -ForegroundColor Yellow
    Write-Host "[СОСТОЯНИЕ] Проверка файловой системы на наличие devcert.pfx" -ForegroundColor DarkGray
    
    if (-not (Test-Path "devcert.pfx")) {
        Write-Host "[СОСТОЯНИЕ] Сертификат не обнаружен. Вызов генератора winapp" -ForegroundColor DarkGray
        
        winapp cert generate `
            --manifest KTools.App\Package.appxmanifest `
            --install
            
        Write-Host "[СОСТОЯНИЕ] Генерация и установка сертификата завершены" -ForegroundColor DarkGray
    } else {
        Write-Host "Обнаружен существующий сертификат devcert.pfx." -ForegroundColor Green
        Write-Host "[СОСТОЯНИЕ] Продолжение работы с текущим сертификатом" -ForegroundColor DarkGray
    }

    # 3. Упаковка MSIX (Framework-dependent)
    Write-Host "`n[3/3] Упаковка приложения в пакет MSIX..." -ForegroundColor Yellow
    Write-Host "[СОСТОЯНИЕ] Запуск упаковщика winapp pack" -ForegroundColor DarkGray
    
    winapp pack KTools.App\bin\x64\Release\net8.0-windows10.0.26100.0\win-x64 `
        --cert devcert.pfx
        
    Write-Host "[СОСТОЯНИЕ] Процесс упаковки успешно завершен" -ForegroundColor DarkGray

    # 4. Форматирование имени файла
    Write-Host "`n=== Поиск и переименование созданного пакета ===" -ForegroundColor Cyan
    Write-Host "[СОСТОЯНИЕ] Сканирование директории на наличие *.msix файлов" -ForegroundColor DarkGray
    
    $time_limit = (Get-Date).AddMinutes(-2)
    $msix_files = Get-ChildItem -Filter "*.msix" | Where-Object { 
        $_.LastWriteTime -gt $time_limit 
    }

    if ($msix_files.Count -eq 0) {
        Write-Host "[СОСТОЯНИЕ] Критическая ошибка: файлы не найдены" -ForegroundColor Red
        Write-Error "Файл установщика MSIX не был найден в текущем каталоге!"
    }

    Write-Host "[СОСТОЯНИЕ] Найдено файлов для переименования: $($msix_files.Count)" -ForegroundColor DarkGray
    
    $msix_files | ForEach-Object {
        # Регулярное выражение заменяет системный GUID до первого подчеркивания на "K-Tools"
        $clean_basename = $_.BaseName -replace "^[^_]+", "K-Tools"
        $new_name = "{0}_{1}{2}" -f $clean_basename, $timestamp, $_.Extension
        
        Write-Host "[СОСТОЯНИЕ] Изменение имени файла с $($_.Name) на $new_name" -ForegroundColor DarkGray
        
        Rename-Item -Path $_.FullName -NewName $new_name
        
        Write-Host "Пакет успешно сохранен по пути: $new_name" -ForegroundColor Green
    }

    Write-Host "`n=== Процесс сборки успешно завершен! ===" -ForegroundColor Green
    Write-Host "[СОСТОЯНИЕ] Скрипт остановлен" -ForegroundColor DarkGray
}

Invoke-BuildProcess