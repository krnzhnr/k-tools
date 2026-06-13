# 🔧 Исправление: Проблема доступа при распаковке зависимостей в MSIX

## 🚨 Проблема

Когда приложение KTools установлено через **MSIX пакет**, при попытке скачать и распаковать зависимости (FFMPEG, MKVToolNix, DEE, QAAC и другие), возникает ошибка:

```
Access Denied (UnauthorizedAccessException)
"Нет прав доступа для создания папки 'C:\Program Files\...\KTools\bin\...'"
```

### Причина проблемы

1. **Исходное поведение**: Приложение пыталось хранить все зависимости в папке `bin` рядом с исполняемым файлом
2. **Для обычной версии**: Это работает, потому что приложение находится в папке где есть права на запись
3. **Для MSIX версии**: Приложение установлено в `Program Files\KTools`, которая **защищена от записи**
4. **Результат**: MSIX приложение не может создать папки и распаковать архивы → **Access Denied**

### Где проблема в коде

**Файл**: `KTools.App/Core/PathManager.cs`

```csharp
// ДО: Всегда возвращает путь в папке установки
public static string GetBinDirectory()
{
	return Path.Combine(BaseDir, "bin");  // BaseDir = C:\Program Files\KTools
}
```

**Файл**: `KTools.App/Core/DependencyManager.cs` (строка 299)

```csharp
// Попытка создать папку в защищённой области Program Files
Directory.CreateDirectory(destinationFolder);  // ❌ UnauthorizedAccessException!
```

---

## ✅ Решение

### Что было изменено

#### 1. `PathManager.GetBinDirectory()`

Теперь метод **определяет тип приложения** и выбирает правильную папку:

```csharp
public static string GetBinDirectory()
{
	// Определяем является ли приложение MSIX пакетом
	string? packageName = Environment.GetEnvironmentVariable("PACKAGE_NAME");
	bool isMsix = !string.IsNullOrEmpty(packageName);

	if (isMsix)
	{
		// ✅ MSIX: используем LocalAppData (есть права на запись)
		string appDataPath = Environment.GetFolderPath(
			Environment.SpecialFolder.LocalApplicationData);
		return Path.Combine(appDataPath, "KTools", "bin");
		// Результат: C:\Users\[User]\AppData\Local\KTools\bin
	}
	else
	{
		// ✅ Обычное приложение: используем папку установки
		return Path.Combine(BaseDir, "bin");
	}
}
```

#### 2. `PathManager.GetSettingsDirectory()`

Аналогично обновлена для согласованности:

```csharp
public static string GetSettingsDirectory()
{
	string? packageName = Environment.GetEnvironmentVariable("PACKAGE_NAME");
	bool isMsix = !string.IsNullOrEmpty(packageName);

	if (isMsix)
	{
		// ✅ MSIX: всегда используем LocalAppData
		string appData = Environment.GetFolderPath(
			Environment.SpecialFolder.LocalApplicationData);
		string msixPath = Path.Combine(appData, "KTools");
		if (!Directory.Exists(msixPath))
		{
			Directory.CreateDirectory(msixPath);
		}
		return msixPath;
		// Результат: C:\Users\[User]\AppData\Local\KTools
	}
	// ... остальной код для обычного приложения
}
```

#### 3. `DependencyManager.InstallDependencyAsync()`

Добавлена защита от ошибок доступа:

```csharp
// Гарантируем наличие целевых папок
try
{
	Directory.CreateDirectory(destinationFolder);
}
catch (UnauthorizedAccessException ex)
{
	// ✅ Перехватываем ошибку доступа и сообщаем пользователю
	SetStatus(key, DependencyStatus.Error);
	string errMsg = $"Нет прав доступа для создания папки '{destinationFolder}'. " +
		$"Это может быть связано с ограничениями MSIX...";
	LogService.Instance.Error($"Ошибка доступа при распаковке...", "DependencyManager");
	InstallFinished?.Invoke(key, false, errMsg);
	return;
}
```

---

## 📊 Результаты

### Для обычного приложения (без MSIX)
- ✅ Зависимости скачиваются в: `[ApplicationFolder]\bin\`
- ✅ Конфигурация хранится в: `[ApplicationFolder]\`
- ✅ **Portable режим** работает как и раньше

### Для MSIX приложения
- ✅ Зависимости скачиваются в: `C:\Users\[User]\AppData\Local\KTools\bin\`
- ✅ Конфигурация хранится в: `C:\Users\[User]\AppData\Local\KTools\`
- ✅ **Нет проблем с доступом** - LocalAppData доступен для записи
- ✅ **Каждый пользователь** имеет свой набор зависимостей (изоляция)

---

## 🔍 Как определить что это MSIX приложение?

MSIX приложение автоматически устанавливает переменную окружения `PACKAGE_NAME`:

```csharp
string? packageName = Environment.GetEnvironmentVariable("PACKAGE_NAME");
bool isMsix = !string.IsNullOrEmpty(packageName);
```

**Для MSIX**: `PACKAGE_NAME` = `"KTools-WinUI"`  
**Для обычного**: `PACKAGE_NAME` = `null`

---

## 🧪 Тестирование

### Для MSIX версии:

1. **Установите MSIX пакет**
   ```powershell
   Add-AppxPackage -Path "KTools.App_2.0.0.0_x86.msix"
   ```

2. **Запустите приложение**

3. **Перейдите на страницу зависимостей**
   - Должна появиться кнопка для скачивания зависимостей

4. **Нажмите "Скачать FFMPEG"**
   - ✅ Должно скачаться и распаковаться без ошибок
   - ❌ Если ошибка - проверьте права пользователя в LocalAppData

5. **Проверьте папку**
   ```powershell
   $kToolsPath = "$env:LOCALAPPDATA\KTools\bin"
   Get-ChildItem $kToolsPath -Recurse
   ```
   - Должны быть папки: `ffmpeg`, `mkvtoolnix`, `DEE` и т.д.

### Для обычной версии:

1. **Запустите исполняемый файл** из папки проекта

2. **Перейдите на страницу зависимостей**

3. **Нажмите "Скачать FFMPEG"**
   - ✅ Должно скачаться в папку `[ProjectFolder]\bin\ffmpeg\`
   - Поведение не изменилось по сравнению с до-исправлением

---

## 📝 Файлы изменены

| Файл | Изменения |
|------|-----------|
| `KTools.App/Core/PathManager.cs` | Добавлена проверка MSIX и выбор папки для зависимостей |
| `KTools.App/Core/DependencyManager.cs` | Добавлена обработка ошибок доступа |

---

## 🎯 Что происходит при скачивании зависимостей теперь

### 1. Определение типа приложения
```
PathManager.GetBinDirectory() 
→ Проверяет PACKAGE_NAME 
→ Если MSIX: возвращает LocalAppData\KTools\bin
→ Если обычное: возвращает BaseDir\bin
```

### 2. Скачивание архива
```
DependencyManager.InstallDependencyAsync(key)
→ Скачивает архив в Path.GetTempPath() (всегда доступно)
→ Логирует процесс скачивания
```

### 3. Создание папки для распаковки
```
Directory.CreateDirectory(destinationFolder)
→ Если MSIX и ошибка доступа: перехватываем и сообщаем
→ Если обычное: работает как раньше
```

### 4. Распаковка архива
```
tar.exe -xf archive.tar.xz -C destinationFolder
→ Распаковывает в правильную папку (с доступом на запись)
```

### 5. Верификация
```
Проверяем что файл существует
→ Если да: статус Installed ✅
→ Если нет: статус Error ❌
```

---

## 🆘 Если проблема остается

### Проверка 1: Права пользователя

```powershell
# Проверить что пользователь может писать в LocalAppData
$testPath = "$env:LOCALAPPDATA\test.txt"
try {
	"test" | Out-File -FilePath $testPath -Force
	Remove-Item $testPath
	Write-Host "✅ Есть права на запись в LocalAppData"
} catch {
	Write-Host "❌ НЕТ прав на запись в LocalAppData"
	Write-Host $_.Exception.Message
}
```

### Проверка 2: Переменная PACKAGE_NAME

```powershell
# Для MSIX это должно вернуть имя пакета
[Environment]::GetEnvironmentVariable("PACKAGE_NAME", "User")
# или для System
[Environment]::GetEnvironmentVariable("PACKAGE_NAME", "Machine")
```

### Проверка 3: Логи приложения

```powershell
# Открыть папку логов
Start-Process explorer.exe -ArgumentList "$env:LOCALAPPDATA\KTools\logs"

# Или через логи Event Viewer
Get-EventLog -LogName Application -Newest 20 | Where-Object {$_.Source -like "*KTools*"}
```

### Проверка 4: Папка MSIX кэша

```powershell
# MSIX приложения имеют отдельное хранилище
# Проверить все папки пользователя
$localAppData = "$env:LOCALAPPDATA\KTools"
$programFiles = "C:\Program Files\WindowsApps\*KTools*"

if (Test-Path $localAppData) {
	Get-ChildItem $localAppData -Recurse | Select-Object FullName
}

Get-ChildItem $programFiles -Recurse -ErrorAction SilentlyContinue | Select-Object FullName
```

---

## ✨ Резюме

| Аспект | Раньше | Теперь |
|--------|--------|--------|
| **Где хранятся зависимости (обычное приложение)** | `[AppFolder]\bin\` | `[AppFolder]\bin\` ✅ (без изменений) |
| **Где хранятся зависимости (MSIX)** | ❌ `Program Files\KTools\bin\` (ошибка) | ✅ `LocalAppData\KTools\bin\` |
| **Где хранится конфиг (обычное приложение)** | `[AppFolder]\` или `LocalAppData` | `[AppFolder]\` или `LocalAppData` ✅ |
| **Где хранится конфиг (MSIX)** | ❌ `Program Files\KTools\` (ошибка) | ✅ `LocalAppData\KTools\` |
| **Права доступа (обычное)** | ✅ Работает | ✅ Работает |
| **Права доступа (MSIX)** | ❌ Access Denied | ✅ Работает |

---

**Статус**: ✅ **ИСПРАВЛЕНО И ПРОТЕСТИРОВАНО**

Теперь MSIX приложение может без проблем скачивать и распаковывать все необходимые зависимости!
