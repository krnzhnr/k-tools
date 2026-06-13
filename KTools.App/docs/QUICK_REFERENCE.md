# ⚡ Быстрый справочник команд

## 🏗️ Сборка и восстановление

### Стандартная сборка
```powershell
cd F:\Programming\Utils\k-tools\src-csharp
dotnet build
```

### Сборка Release (для MSIX)
```powershell
cd F:\Programming\Utils\k-tools\src-csharp
dotnet build -c Release
```

### Публикация Release (для MSIX)
```powershell
cd F:\Programming\Utils\k-tools\src-csharp
dotnet publish -c Release
```

### Очистка и восстановление (при проблемах)
```powershell
cd F:\Programming\Utils\k-tools\src-csharp
# Закрыть Visual Studio!
taskkill /F /IM devenv.exe 2>$null
taskkill /F /IM msbuild.exe 2>$null
taskkill /F /IM dotnet.exe 2>$null
Start-Sleep -Seconds 2

# Очистить кэши
Remove-Item -Path "KTools.App\obj" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path "KTools.App\bin" -Recurse -Force -ErrorAction SilentlyContinue

# Восстановить без кэша
dotnet restore --no-cache

# Собрать проект
dotnet build
```

---

## 📦 MSIX упаковка и установка

### Создать MSIX из Visual Studio
1. Правый клик на **KTools.App** проекта
2. **Publish** → **Create App Packages**
3. Выбрать **MSIX** в мастере
4. Следовать подсказкам

### Установить MSIX пакет
```powershell
# Способ 1: Двойной клик (просто)
# C:\path\to\KTools.App_x.x.x_x86.msix

# Способ 2: PowerShell (если нужны права администратора)
Add-AppxPackage -Path "C:\path\to\KTools.App_x.x.x_x86.msix"

# Способ 3: Через winget
# winget install --file "C:\path\to\KTools.App_x.x.x_x86.msix"
```

### Удалить установленный MSIX
```powershell
winget uninstall KTools-WinUI
# или
Remove-AppxPackage -Package KToolsWinUI_1.0.0.0_x86__[package-family-name]
```

### Список установленных приложений
```powershell
winget list | Select-String "KTools"
# или
Get-AppxPackage | Select-String "KTools"
```

---

## 🔍 Диагностика и логи

### Проверить версии
```powershell
dotnet --version                    # Версия .NET (должна быть 8.x)
dotnet --info                       # Полная информация
winget list | Select-String "WindowsAppRuntime"  # Windows App Runtime
```

### Посмотреть логи приложения
```powershell
# Логи KTools WinUI
$logPath = "$env:LOCALAPPDATA\KTools-WinUI\logs"
Get-ChildItem $logPath
Get-Content "$logPath\latest.log"

# События системы (MSIX краши)
Get-EventLog -LogName Application -Newest 20 | Where-Object {$_.Source -like "*KTools*"}
```

### Проверить WER (Windows Error Reporting)
```powershell
# Папка с дампами крашей
$werPath = "$env:LOCALAPPDATA\Microsoft\Windows\WER"
Get-ChildItem $werPath
```

### Проверить процессы
```powershell
# Найти процесс приложения
Get-Process | Select-String "KTools"

# Завершить процесс
Stop-Process -Name "KTools.App" -Force
```

---

## 🧹 Очистка и обслуживание

### Очистить NuGet кэш полностью
```powershell
Remove-Item -Path "$env:USERPROFILE\.nuget" -Recurse -Force -ErrorAction SilentlyContinue
```

### Очистить кэш Visual Studio
```powershell
Remove-Item -Path "$env:LOCALAPPDATA\Microsoft\VisualStudio" -Recurse -Force -ErrorAction SilentlyContinue
```

### Очистить временные файлы проекта
```powershell
cd F:\Programming\Utils\k-tools\src-csharp
Remove-Item -Path "KTools.App\obj" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path "KTools.App\bin" -Recurse -Force -ErrorAction SilentlyContinue
```

### Полная очистка (ядерный вариант)
```powershell
# Закрыть все процессы
taskkill /F /IM devenv.exe 2>$null
taskkill /F /IM msbuild.exe 2>$null
taskkill /F /IM dotnet.exe 2>$null

# Очистить всё
Remove-Item -Path "$env:USERPROFILE\.nuget" -Recurse -Force -EA SilentlyContinue
Remove-Item -Path "KTools.App\obj" -Recurse -Force -EA SilentlyContinue
Remove-Item -Path "KTools.App\bin" -Recurse -Force -EA SilentlyContinue

# Перезагрузить компьютер
Restart-Computer
```

---

## 📝 Git операции

### Проверить статус репозитория
```powershell
cd F:\Programming\Utils\k-tools
git status
```

### Закоммитить все изменения
```powershell
cd F:\Programming\Utils\k-tools
git add .
git commit -m "Fix MSIX crash and UI layout issues"
```

### Отправить изменения
```powershell
cd F:\Programming\Utils\k-tools
git push origin feature/csharp-migration
```

### Проверить логи
```powershell
cd F:\Programming\Utils\k-tools
git log --oneline -10
```

---

## 🔧 Специальные команды

### Запустить приложение в Debug режиме
```powershell
cd F:\Programming\Utils\k-tools\src-csharp
dotnet run --project KTools.App/KTools.App.csproj
```

### Выполнить тесты (если есть)
```powershell
cd F:\Programming\Utils\k-tools\src-csharp
dotnet test
```

### Просмотреть все ошибки сборки
```powershell
cd F:\Programming\Utils\k-tools\src-csharp
dotnet build --no-incremental 2>&1 | Select-String "error"
```

### Узнать размер собранного приложения
```powershell
$buildPath = "F:\Programming\Utils\k-tools\src-csharp\KTools.App\bin\x86\Release\net8.0-windows10.0.26100.0\win-x86"
$size = (Get-ChildItem $buildPath -Recurse | Measure-Object -Property Length -Sum).Sum / 1MB
Write-Host "Размер: $size MB"
```

---

## 🚨 Если что-то сломалось

### Шаг 1: Проверьте документацию
```
📄 MSIX_CRASH_FIX.md         → Если MSIX падает
📄 NUGET_ACCESS_DENIED_FIX.md → Если ошибки восстановления
📄 TROUBLESHOOTING_SUMMARY.md → Общее решение проблем
```

### Шаг 2: Выполните очистку
```powershell
# Закрыть Visual Studio и запустить из PowerShell:
taskkill /F /IM devenv.exe 2>$null
taskkill /F /IM msbuild.exe 2>$null
taskkill /F /IM dotnet.exe 2>$null
cd F:\Programming\Utils\k-tools\src-csharp
Remove-Item "KTools.App\obj", "KTools.App\bin" -Recurse -Force -EA SilentlyContinue
dotnet restore --no-cache
dotnet build
```

### Шаг 3: Если не помогло
1. Проверьте ошибку в выводе консоли
2. Найдите подходящий раздел в одном из MD файлов
3. Следуйте инструкциям для этой специфичной ошибки

---

## 📚 Ссылки на документы

Все документы находятся в папке: `F:\Programming\Utils\k-tools\src-csharp\KTools.App\`

| Документ | Назначение |
|----------|-----------|
| 📄 MSIX_CRASH_FIX.md | Решение краша MSIX |
| 📄 NUGET_ACCESS_DENIED_FIX.md | Решение "Access denied" |
| 📄 TROUBLESHOOTING_SUMMARY.md | Общий чек-лист |
| 📄 PROJECT_STATUS.md | Статус проекта и инструкции |
| 📄 CHECKLIST_COMPLETED.md | Все выполненные исправления |
| 📄 FINAL_REPORT.md | Итоговый отчёт |
| 📄 QUICK_REFERENCE.md | Этот файл |

---

## ⚡ Самые используемые команды

```powershell
# Собрать проект
dotnet build

# Собрать Release для MSIX
dotnet build -c Release

# Очистить и восстановить при проблемах
dotnet restore --no-cache

# Запустить приложение
dotnet run --project KTools.App/KTools.App.csproj

# Посмотреть логи
Get-Content "$env:LOCALAPPDATA\KTools-WinUI\logs\latest.log"

# Удалить установленное приложение
winget uninstall KTools-WinUI

# Установить MSIX пакет
Add-AppxPackage -Path "path\to\KTools.App_x.x.x_x86.msix"
```

---

**Последнее обновление**: 2024  
**Версия проекта**: 2.0.0  
**Статус**: ✅ ГОТОВО  

Сохраните эту ссылку! Она очень полезна при дальнейшей разработке. 📌
