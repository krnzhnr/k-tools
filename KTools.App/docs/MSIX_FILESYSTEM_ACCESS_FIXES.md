# MSIX Filesystem Access Fixes (Исправления доступа к файловой системе для MSIX)

## 📋 Обзор проблемы

При установке приложения как MSIX, возникают ошибки доступа (`UnauthorizedAccessException` / "Access Denied") при:
1. **Скачивании и распаковке зависимостей** (FFMPEG, MKVToolNix и т.д.)
2. **Сохранении логов приложения**
3. **Сохранении конфигурационных файлов**

## 🔍 Корневая причина

MSIX-установленные приложения работают в контейнеризированной среде Windows:
- Папка установки (`Program Files\WindowsApps\...`) **доступна только для чтения** во время выполнения
- Приложение не может создавать/изменять файлы в папке установки
- Это касается всех операций с диском в `AppContext.BaseDirectory`

## ✅ Реализованные исправления

### 1. **PathManager.cs** - Редирект путей для MSIX

#### Функция: `GetBinDirectory()`
```csharp
public static string GetBinDirectory()
{
	// Проверяем является ли приложение MSIX пакетом
	string? packageName = Environment.GetEnvironmentVariable("PACKAGE_NAME");
	bool isMsix = !string.IsNullOrEmpty(packageName);

	if (isMsix)
	{
		// Для MSIX: используем LocalAppData вместо папки установки
		string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		return Path.Combine(appDataPath, "KTools", "bin");
	}
	else
	{
		// Для обычных приложений: используем папку установки
		return Path.Combine(BaseDir, "bin");
	}
}
```

**Что это означает:**
- При запуске как MSIX: все зависимости скачиваются/распаковываются в `%LocalAppData%\KTools\bin\`
- При обычном запуске: используется `<AppFolder>\bin\` (как было раньше)

#### Функция: `GetSettingsDirectory()`
```csharp
public static string GetSettingsDirectory()
{
	string? packageName = Environment.GetEnvironmentVariable("PACKAGE_NAME");
	bool isMsix = !string.IsNullOrEmpty(packageName);

	if (isMsix)
	{
		// Для MSIX: всегда используем LocalAppData
		string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		string msixPath = Path.Combine(appData, "KTools");
		if (!Directory.Exists(msixPath))
		{
			Directory.CreateDirectory(msixPath);
		}
		return msixPath;
	}

	// Для обычных приложений: попробуем использовать папку приложения, если нет прав - LocalAppData
	// ... (существующая логика fallback)
}
```

**Результат:**
- При MSIX: конфигурация сохраняется в `%LocalAppData%\KTools\`
- Логи автоматически сохраняются там же (в подпапке `logs`)

### 2. **DependencyManager.cs** - Обработка ошибок доступа при установке зависимостей

#### Функция: `InstallDependencyAsync()`

Добавлена обработка `UnauthorizedAccessException` при создании папки для распаковки:

```csharp
string destinationFolder = Path.Combine(_binDir, dep.Subfolder);
try
{
	Directory.CreateDirectory(destinationFolder);
}
catch (UnauthorizedAccessException ex)
{
	SetStatus(DependencyStatus.Error);
	LogService.Error($"Нет доступа к папке {destinationFolder}: {ex.Message}", "DependencyManager");
	InstallFinished?.Invoke(key, false, $"Ошибка: нет доступа для установки {dep.DisplayName}");
	return;
}

await ExtractArchiveAsync(tempArchivePath, destinationFolder, cancellationToken);
```

**Результат:**
- Если возникает ошибка доступа, она корректно логируется
- UI получает уведомление об ошибке через событие `InstallFinished`
- Приложение продолжает работу (не падает)

### 3. **LogService.cs** - Безопасное сохранение логов

#### Функция: `InitializeLogFile()`

Добавлена обработка ошибок при попытке использовать пользовательскую папку логов:

```csharp
try
{
	if (!Directory.Exists(logDir))
	{
		Directory.CreateDirectory(logDir);
	}
}
catch (UnauthorizedAccessException)
{
	// Если пользовательская папка недоступна, используем папку по умолчанию
	Debug.WriteLine($"[Warning] Нет доступа к папке логов {logDir}, используется папка по умолчанию");
	logDir = defaultLogDir;

	if (!Directory.Exists(logDir))
	{
		Directory.CreateDirectory(logDir);
	}
}
```

#### Функция: `Log()`

Добавлена обработка `UnauthorizedAccessException` при записи логов:

```csharp
try
{
	Directory.CreateDirectory(dir);
}
catch (UnauthorizedAccessException ex)
{
	Debug.WriteLine($"[Error] Нет доступа для создания папки логов {dir}: {ex.Message}");
	goto SkipFileWrite; // Пропускаем запись на диск, но продолжаем работу
}

File.AppendAllText(_currentLogFile, formatted + Environment.NewLine, Encoding.UTF8);
```

**Результат:**
- Логи всегда сохраняются в доступную папку (`%LocalAppData%\KTools\logs\`)
- Если папка недоступна, приложение продолжает работу (логирует в консоль)
- Пользовательская настройка пути к логам игнорируется при MSIX автоматически

#### Функция: `ClearCurrentLog()`

```csharp
catch (UnauthorizedAccessException ex)
{
	Debug.WriteLine($"[Error] Нет доступа для очистки лог-файла: {ex.Message}");
}
```

---

## 📁 Итоговая структура папок при MSIX

```
C:\Users\<username>\AppData\Local\KTools\
├── bin/                          # Скачанные зависимости
│   ├── ffmpeg/
│   │   ├── kt-ffmpeg.exe
│   │   └── kt-ffprobe.exe
│   ├── mkvtoolnix/
│   │   └── mkvmerge.exe
│   ├── eac3to/
│   └── qaac/
├── logs/                          # Логи приложения
│   ├── ktools_20250108_143015.log
│   ├── ktools_20250108_143025.log
│   └── ...
└── settings.json                  # Конфигурация приложения
```

## 🧪 Проверка и тестирование

### Локальное тестирование (без MSIX упаковки)

1. Запустить приложение из Visual Studio в режиме Debug
2. В Application Output смотреть логи инициализации пути
3. Убедиться, что зависимости скачиваются в `<AppFolder>\bin\`

### Тестирование MSIX

1. Создать MSIX пакет: `dotnet publish -c Release`
2. Установить пакет через PowerShell:
   ```powershell
   Add-AppxPackage -Path "KTools-WinUI_<version>_x64.appxbundle"
   ```
3. Запустить установленное приложение
4. Открыть меню зависимостей и запустить установку FFMPEG
5. **Проверить результаты:**
   - ✅ Зависимость скачивается без ошибок
   - ✅ Файлы находятся в `%LocalAppData%\KTools\bin\ffmpeg\`
   - ✅ Логи сохраняются в `%LocalAppData%\KTools\logs\`
   - ✅ Лог содержит сообщение "Зависимость успешно установлена"

### Команды для проверки

**Просмотр установленного пакета:**
```powershell
Get-AppxPackage | Where-Object { $_.Name -like "*KTools*" }
```

**Удаление пакета:**
```powershell
Remove-AppxPackage -Package "KTools-WinUI_<version>_x64_<hash>_x64__<hash>"
```

**Проверка файлов зависимостей:**
```powershell
Get-ChildItem "$env:LOCALAPPDATA\KTools\bin\" -Recurse
```

**Проверка логов:**
```powershell
Get-ChildItem "$env:LOCALAPPDATA\KTools\logs\" | Sort-Object LastWriteTime -Descending | Select-Object -First 5
Get-Content "$env:LOCALAPPDATA\KTools\logs\ktools_*.log" -Tail 50
```

---

## 🔧 Что изменилось для разработчиков

### Для добавления новых операций с файлами:

1. **Используйте PathManager для путей:**
   ```csharp
   // ✅ Правильно
   string binDir = PathManager.GetBinDirectory();
   string settingsDir = PathManager.GetSettingsDirectory();

   // ❌ Неправильно
   string binDir = Path.Combine(AppContext.BaseDirectory, "bin");
   ```

2. **Обрабатывайте UnauthorizedAccessException:**
   ```csharp
   try
   {
	   Directory.CreateDirectory(folder);
	   File.WriteAllText(path, content);
   }
   catch (UnauthorizedAccessException ex)
   {
	   LogService.Instance.Error($"Нет доступа: {ex.Message}");
	   // Используйте fallback или уведомьте пользователя
   }
   ```

3. **Тестируйте в обоих режимах:**
   - Локально (обычное приложение)
   - Как MSIX (упакованное приложение)

---

## 📊 Статус реализации

| Компонент | Статус | Описание |
|-----------|--------|---------|
| PathManager.GetBinDirectory() | ✅ Завершено | Использует LocalAppData для MSIX |
| PathManager.GetSettingsDirectory() | ✅ Завершено | Использует LocalAppData для MSIX |
| DependencyManager InstallDependencyAsync() | ✅ Завершено | Обработка UnauthorizedAccessException |
| LogService.InitializeLogFile() | ✅ Завершено | Fallback на папку по умолчанию |
| LogService.Log() | ✅ Завершено | Обработка ошибок записи |
| LogService.ClearCurrentLog() | ✅ Завершено | Обработка ошибок очистки |
| SettingsManager.SaveSettings() | ✅ Встроено | Использует PathManager |
| Сборка и тестирование | ⏳ Ожидает | MSIX пакет требует переоборку |

---

## 📝 Дополнительная информация

- **MSIX Detection:** `Environment.GetEnvironmentVariable("PACKAGE_NAME")` — переменная окружения автоматически устанавливается Windows при запуске MSIX приложения
- **LocalAppData Path:** `Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)` — эквивалент `%LocalAppData%`
- **Все пути абсолютные и кроссплатформенные** благодаря использованию `Path.Combine()`

---

## 🚀 Следующие шаги

1. ✅ Пересобрать приложение
2. ⏳ Создать новый MSIX пакет с изменениями
3. ⏳ Установить MSIX на чистую машину для тестирования
4. ⏳ Проверить установку зависимостей и сохранение логов
5. ⏳ Убедиться в отсутствии ошибок доступа
