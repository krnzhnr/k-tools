# Исправление краша MSIX пакета KTools

## Проблема
При запуске приложения из MSIX пакета появляется чёрное окно, которое вылетает через пару секунд с ошибкой:
```
Код исключения: 0xc000027b
Модуль: Microsoft.UI.Xaml.dll
```

## Причины
1. **PublishTrimmed = True** - Trimming удаляет код, необходимый для COM Interop и P/Invoke (SetWindowSubclass)
2. **Отсутствие защиты от ошибок** - Ошибки инициализации окна приводят к краху без логирования
3. **Проблемы с зависимостями Windows App Runtime**

## Применённые исправления

### 1. Отключение Trimming в KTools.App.csproj
```xml
<PropertyGroup>
  <PublishTrimmed>False</PublishTrimmed>
</PropertyGroup>
```
**Причина:** Trimming удаляет типы и методы, необходимые для P/Invoke вызовов (SetWindowSubclass).

### 2. Добавление защиты от ошибок инициализации
Все критические операции в MainWindow.xaml.cs теперь обёрнуты в try-catch:
- Установка иконки приложения
- Инициализация SetWindowSubclass
- Инициализация главного окна

**Причина:** Если одна из этих операций падает без логирования, приложение полностью краша.

### 3. Исправление методов логирования
Использование `LogService.Warn()` вместо несуществующего `LogService.Warning()`.

## Как пересобрать MSIX пакет

### Вариант 1: Из Visual Studio
1. Правый клик на проект **KTools.App**
2. **Publish** → **Package and Publish**
3. Выбрать **Package MSIX**

### Вариант 2: Из командной строки
```powershell
cd "F:\Programming\Utils\k-tools\src-csharp"
dotnet publish -c Release -p:Platform=x64
```

Пакет будет создан в: `KTools.App\bin\Release\net8.0-windows10.0.26100.0\win-x64\`

## Тестирование
После установки нового MSIX пакета:
1. Удалить старую версию: `Add/Remove Programs` → удалить **KTools-WinUI**
2. Установить новый пакет двойным кликом
3. Запустить приложение
4. Проверить **Event Viewer** → **Application** на наличие ошибок

## Дополнительные меры для диагностики

Если проблема сохраняется:
1. Проверить файлы дампа из `C:\ProgramData\Microsoft\Windows\WER\ReportArchive\`
2. Включить отладку в Package.appxmanifest
3. Проверить логи приложения в `%LOCALAPPDATA%\KTools-WinUI\`

## Альтернативное решение
Если проблема остаётся, можно попробовать:
1. Установить `PublishReadyToRun = False` (замедляет запуск, но повышает совместимость)
2. Явно указать зависимость Windows App Runtime 2.1.3 в Package.appxmanifest
3. Тестировать с включённым отладчиком для получения точного стека вызовов
