# Исправление ошибки "Access to the path" при восстановлении зависимостей

## Проблема
При выполнении `dotnet restore` возникает ошибка:
```
Access to the path 'C:\Program Files\Windows...' is denied
```

Это обычно связано с:
1. **Блокировкой файлов** - процессы (MSBuild, dotnet, Visual Studio) блокируют файлы Windows App SDK
2. **Кэшированными повреждёнными пакетами** - старые/неправильные копии в NuGet кэше
3. **Файлами в процессе использования** - Windows App SDK DLL могут быть загружены в памяти

## Решение (проверенный метод)

### Шаг 1: Закрыть все блокирующие процессы
```powershell
taskkill /F /IM msbuild.exe
taskkill /F /IM dotnet.exe
taskkill /F /IM devenv.exe  # Если открыта Visual Studio
```

**Важно:** Закройте Visual Studio полностью, иначе она будет держать файлы блокированными!

### Шаг 2: Очистить локальный кэш проекта
```powershell
cd "F:\Programming\Utils\k-tools\src-csharp"
Remove-Item -Path "KTools.App\obj" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path "KTools.App\bin" -Recurse -Force -ErrorAction SilentlyContinue
```

### Шаг 3: Очистить NuGet кэш (опционально, но рекомендуется)
```powershell
$nugetPath = "C:\Users\$env:USERNAME\.nuget\packages"
Remove-Item -Path "$nugetPath\microsoft.windowsappsdk*" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path "$nugetPath\microsoft.windows.sdk.buildtools*" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path "$nugetPath\microsoft.windows.appruntime*" -Recurse -Force -ErrorAction SilentlyContinue
```

### Шаг 4: Восстановить зависимости без кэша
```powershell
cd "F:\Programming\Utils\k-tools\src-csharp"
dotnet restore --no-cache
```

### Шаг 5: Собрать проект
```powershell
dotnet build
```

## Если проблема всё ещё остаётся

### Проверка прав доступа
```powershell
# Запустить командную строку от администратора
icacls "C:\Program Files\WindowsApps" /T /grant "Everyone:(OI)(CI)F"
```

### Отключить антивирус временно
Некоторые антивирусы (особенно Windows Defender) могут блокировать доступ к файлам Windows App SDK. Попробуйте временно отключить.

### Переустановить Windows App Runtime
```powershell
# Удалить
winget uninstall Microsoft.WindowsAppRuntime.2

# Переустановить
winget install Microsoft.WindowsAppRuntime.2 --version 2.1.3
```

### Полная очистка (ядерный вариант)
```powershell
# 1. Закрыть Visual Studio и все процессы
# 2. Удалить кэш Visual Studio
Remove-Item -Path "$env:LOCALAPPDATA\Microsoft\VisualStudio" -Recurse -Force -ErrorAction SilentlyContinue

# 3. Удалить временные файлы MSBuild
Remove-Item -Path "$env:LOCALAPPDATA\Microsoft\MSBuild" -Recurse -Force -ErrorAction SilentlyContinue

# 4. Очистить весь NuGet кэш
Remove-Item -Path "$env:USERPROFILE\.nuget" -Recurse -Force -ErrorAction SilentlyContinue

# 5. Перезагрузить компьютер
Restart-Computer
```

## Рекомендуемые настройки проекта

Убедитесь, что в `KTools.App.csproj` установлены оптимальные настройки:

```xml
<PropertyGroup>
  <!-- Отключить Trimming (может вызвать проблемы) -->
  <PublishTrimmed>False</PublishTrimmed>

  <!-- Отключить ReadyToRun в Debug (ускоряет восстановление) -->
  <PublishReadyToRun Condition="'$(Configuration)' == 'Debug'">False</PublishReadyToRun>

  <!-- Убедиться что указаны все платформы -->
  <Platforms>x86;x64;ARM64</Platforms>
</PropertyGroup>
```

## Информация о версиях

Проверьте, что используются совместимые версии:

```powershell
dotnet --version          # Должна быть .NET 8.x
dotnet --info             # Полная информация

# Проверить установленные Windows App SDK
winget list | Select-String "WindowsAppRuntime"
```

## Скрипт для автоматизации

Сохраните как `restore-clean.ps1`:

```powershell
#!/usr/bin/env pwsh

Write-Host "Закрытие процессов..." -ForegroundColor Green
taskkill /F /IM msbuild.exe 2>$null
taskkill /F /IM dotnet.exe 2>$null
taskkill /F /IM devenv.exe 2>$null
Start-Sleep -Seconds 2

Write-Host "Очистка кэшей..." -ForegroundColor Green
Remove-Item -Path "KTools.App\obj" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path "KTools.App\bin" -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "Восстановление зависимостей..." -ForegroundColor Green
dotnet restore --no-cache

Write-Host "Сборка проекта..." -ForegroundColor Green
dotnet build

Write-Host "Готово!" -ForegroundColor Green
```

Использование:
```powershell
.\restore-clean.ps1
```

## Диагностика

Если ошибка с точным путём файла:
```
Access to the path 'C:\Program Files\WindowsApps\Microsoft.WindowsAppRuntime.2_2.1.3.0_x64__8wekyb3d8bbwe\...'
```

Это значит, что этот файл используется процессом. Проверьте:
```powershell
# Windows 11/10
Get-Process | Where-Object {$_.Modules -match "WindowsAppRuntime"}
```

## Успешное восстановление

Признаки успешного восстановления:
- ✅ Нет ошибок "Access denied"
- ✅ Проект успешно собирается
- ✅ В папке `obj/` видны восстановленные пакеты
- ✅ Нет предупреждений о версиях

