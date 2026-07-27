# План исправлений KTools v2.0.0 — Полный аудит

> **Цель:** Закрыть все найденные проблемы перед релизом v2.0.0
> **Файлов затронуто:** ~16
> **Задач:** 20 (~55 подзадач)
> **Оценка:** ~3–4 сессии работы

---

## 1. Удаление русской локализации при сборке

> `build_csharp.py` → `clean_unused_localizations()`
> Метод удаляет **все** папки из 2–5 символов, включая `ru` и `ru-RU`.

### Подзадачи:
- [ ] **1.1** В методе `clean_unused_localizations` добавить `ru` и `ru-RU` в whitelist исключений наряду с `bin`, `assets` и `en-US`:
  ```python
  KEEP_LOCALES = {"bin", "assets", "en-US", "ru", "ru-RU"}
  if item.name not in KEEP_LOCALES:
  ```
- [ ] **1.2** Проверить, что после сборки (`python build_csharp.py`) в папке `publish` сохраняются папки `ru` и `ru-RU`.

---

## 2. Race condition при подмене семафора параллелизма

> `MediaProbeService.cs` → `GetProbeSemaphore()`
> При изменении настроек параллелизма старый `SemaphoreSlim` уничтожается, а ожидающие его потоки падают с `ObjectDisposedException`.

### Подзадачи:
- [ ] **2.1** Убрать вызов `_probeSemaphore?.Dispose()` внутри `GetProbeSemaphore()`. Старый семафор будет собран GC самостоятельно.
- [ ] **2.2** Использовать `Interlocked.Exchange` для атомарной подмены ссылки:
  ```csharp
  var oldSemaphore = Interlocked.Exchange(ref _probeSemaphore, newSemaphore);
  // oldSemaphore НЕ диспозим — потоки ещё могут держать WaitAsync на нём
  ```
- [ ] **2.3** Убедиться что `lock (_semaphoreLock)` всё ещё защищает от повторного создания при одновременных вызовах.

---

## 3. Hardcoded путь `"ffmpeg"` в VideoEncodingScript

> Несколько мест в коде обращаются к системному `PATH` вместо использования собственных бинарников `kt-ffmpeg.exe` / `kt-ffprobe.exe` через `IPathManager`.

### 3a. `VideoEncodingScript.cs` → `GetAvailableCuvidDecoders()` (строка 1019)
> Метод `static` — запускает `FileName = "ffmpeg"` вместо `kt-ffmpeg.exe` из папки бинарников.
> Процесс не регистрируется в `ActiveProcessTracker`, чтение синхронное, пустой `catch { }`.

#### Подзадачи:
- [ ] **3.1** Убрать модификатор `static` у метода `GetAvailableCuvidDecoders()`, чтобы он имел доступ к `_pathManager` (или передать путь параметром).
- [ ] **3.2** Заменить `FileName = "ffmpeg"` на `FileName = _pathManager.GetBinaryPath("ffmpeg")` (PathManager автоматически подставит `kt-ffmpeg.exe`).
- [ ] **3.3** Обернуть `process` в `try/finally` с `ActiveProcessTracker.Register(process)` / `ActiveProcessTracker.Unregister(process)`.
- [ ] **3.4** Заменить синхронное `reader.ReadLine()` на асинхронное `ReadLineAsync()` и сделать метод `async Task<HashSet<string>>`.
- [ ] **3.5** Заменить пустой `catch { }` на `catch (Exception ex) { _logService.Warn($"Не удалось определить доступные CUVID-декодеры: {ex.Message}", "VideoEncodingScript"); }`.
- [ ] **3.6** Обновить все точки вызова `GetAvailableCuvidDecoders()` для работы с новой сигнатурой (`await`).

### 3b. `AudioWaveformService.cs` → fallback на системный `"ffmpeg"` (строки 50–52)
> При отсутствии локального `kt-ffmpeg.exe` сервис осциллограмм откатывается на `ffmpegPath = "ffmpeg"`, обращаясь к системному PATH.

#### Подзадачи:
- [ ] **3.7** Убрать fallback `ffmpegPath = "ffmpeg"`. Вместо этого — если `_pathManager.GetBinaryPath("ffmpeg")` не существует, логировать предупреждение и возвращать пустой результат (graceful degradation).
- [ ] **3.8** Добавить `_logService.Warn("kt-ffmpeg не найден, построение осциллограммы невозможно", "AudioWaveformService")` при отсутствии бинарника.

---

## 4. Пустые блоки `catch` без логирования

> Множество файлов. Нарушает правило обязательного логирования исключений на русском.

### Подзадачи:

#### App.xaml.cs (глобальные перехватчики):
- [ ] **4.1** Строки 63, 79, 91 — добавить в пустые `catch { }` запись в фоллбек-лог:
  ```csharp
  catch (Exception logEx)
  {
      System.Diagnostics.Debug.WriteLine($"[FATAL] Не удалось записать отчёт об ошибке: {logEx.Message}");
  }
  ```
  > *Примечание:* В глобальных перехватчиках использование `Debug.WriteLine` допустимо, т.к. сам `_logService` может быть недоступен (именно его сбой мог вызвать исключение).
- [ ] **4.2** Строки 170, 183 (`WriteCrashReport`) — аналогично, `Debug.WriteLine` для фоллбека записи крэш-отчёта.

#### App.xaml.cs (ProcessPendingArgsFiles):
- [ ] **4.3** Строка `catch (IOException) { }` — добавить:
  ```csharp
  catch (IOException ioEx)
  {
      System.Diagnostics.Debug.WriteLine($"[PendingArgs] Файл занят другим процессом: {ioEx.Message}");
  }
  ```
- [ ] **4.4** Строка `catch (Exception ex) { }` (внутренний) — добавить `_logService?.Warn(...)`.
- [ ] **4.5** Строка `catch (Exception ex) { }` (внешний) — добавить `_logService?.Exception(...)`.

#### DependencyManager.cs:
- [ ] **4.6** Строка 444 (`File.Delete(targetPath)`) — добавить `_logService.DebugLog(...)`.
- [ ] **4.7** Строка 536 (`File.Delete(tempArchivePath)`) — добавить `_logService.DebugLog(...)`.
- [ ] **4.8** Строка 778 (`File.Delete(tempBatPath)`) — добавить `_logService.DebugLog(...)`.
- [ ] **4.9** Строки 864, 867 (доступ к реестру) — добавить `_logService.DebugLog(...)`.

#### AbstractScript.cs:
- [ ] **4.10** Строки 533–537 (предпросмотр PowerRename) — добавить `_logService.DebugLog($"Ошибка предпросмотра шаблона переименования: {ex.Message}", ...)`.

#### QaacRunner.cs:
- [ ] **4.11** Найти и заполнить все пустые catch-блоки (строки ~202, 354, 371) вызовами `_logService.Warn(...)`.

#### AudioTransplantScript.cs:
- [ ] **4.12** Строка ~269 — добавить `_logService.Warn(...)`.

#### MediaDownloaderScript.cs:
- [ ] **4.13** Строки ~192, 255 — добавить `_logService.Warn(...)`.

#### MetadataCleanupScript.cs:
- [ ] **4.14** Строки ~172, 185 — добавить `_logService.Warn(...)`.

#### AudioWaveformService.cs:
- [ ] **4.15** Строка ~93 — добавить `_logService.Warn(...)`.

#### FocusHelper.cs и WorkPanel.xaml.cs:
- [ ] **4.16** Заменить `Debug.WriteLine(...)` на `_logService.DebugLog(...)` (там где `ILogService` доступен) или оставить `Debug.WriteLine` с пометкой (там где инжекция невозможна).

#### LogPage.xaml.cs и LogViewModel.cs:
- [ ] **4.17** Оставить `Debug.WriteLine` как есть — это намеренный фоллбек для предотвращения бесконечных циклов логирования внутри самого лог-сервиса.

---

## 5. Race condition при удалении старой версии (Inno Setup)

> `build_csharp.py` → генерация Pascal-секции `RemoveOldVersion`
> Деинсталлятор Inno Setup копирует себя во `%TEMP%`, `ewWaitUntilTerminated` не ждёт реального завершения.

### Подзадачи:
- [ ] **5.1** В `build_csharp.py`, в Pascal-коде `RemoveOldVersion`, добавить флаг `_?=` к строке запуска деинсталлятора:
  ```pascal
  sUnInstallString := sUnInstallString + ' /_?="' + sInstallDir + '"';
  ```
  Это заставляет `unins000.exe` выполняться блокирующе в текущей директории, не копируя себя.
- [ ] **5.2** Аналогично обновить `KTools_CSharp.iss` (текущая сгенерированная копия).
- [ ] **5.3** Проверить, что `DelTree` теперь отрабатывает без ошибок «Отказано в доступе».

---

## 6. Hardcoded версия SDK в скрипте сборки

> `build_csharp.py` → `find_publish_folder()`
> Строки `net10.0-windows10.0.26100.0` и `net8.0-windows10.0.26100.0` прописаны жёстко.

### Подзадачи:
- [ ] **6.1** Заменить список кандидатов на динамический поиск через `Path.rglob()`:
  ```python
  def find_publish_folder() -> Path:
      matches = list((SRC_DIR / "KTools.App" / "bin").rglob(f"publish/{EXE_BASE_NAME}.exe"))
      if matches:
          return matches[0].parent
      raise FileNotFoundError("Папка publish не найдена")
  ```
- [ ] **6.2** Добавить фильтр по `Release` в пути (исключить `Debug`), чтобы не подхватить отладочную сборку.

---

## 7. Добавить `dotnet test` в CI/CD pipeline

> `.github/workflows/csharp-release.yml`
> Тесты не запускаются при сборке релиза.

### Подзадачи:
- [ ] **7.1** Добавить шаг перед `Build C# App and Installer`:
  ```yaml
  - name: Run Unit Tests
    run: dotnet test KTools.App.Tests/KTools.App.Tests.csproj -c Release -p:Platform=x64 --no-restore
  ```
- [ ] **7.2** Убедиться, что при провале тестов сборка прекращается (поведение по умолчанию в GitHub Actions).

---

## 8. Retry-логика чтения PendingArgs

> `App.xaml.cs` → обработчик `_argsWatcher.Created` и `ProcessPendingArgsFiles()`
> `Thread.Sleep(50)` + одноразовое чтение = потеря файлов при блокировке.

### Подзадачи:
- [ ] **8.1** Заменить `Thread.Sleep(50)` в обработчике `_argsWatcher.Created` на `await Task.Delay(100)` (обернуть в `async`).
- [ ] **8.2** В `ProcessPendingArgsFiles` внутри `catch (IOException)` реализовать retry (3 попытки, задержка 100/200/500 мс):
  ```csharp
  for (int attempt = 0; attempt < 3; attempt++)
  {
      try { /* чтение файла */ break; }
      catch (IOException) when (attempt < 2) { await Task.Delay((attempt + 1) * 150); }
  }
  ```
- [ ] **8.3** Добавить `_argsWatcher?.Dispose()` при завершении приложения (в обработчике `Exit` или `Closed`).

---

## 9. Перевод ручных процессов на AbstractProcessRunner

> `MediaDownloaderScript.cs`, `MetadataCleanupScript.cs`
> Ручной `Process.Start()`, `ReadLineAsync()` без CancellationToken, `WaitForExitAsync()` без токена.

### Подзадачи:
- [ ] **9.1** В `MediaDownloaderScript.cs` — добавить `CancellationToken` в `WaitForExitAsync()`:
  ```csharp
  await process.WaitForExitAsync(cancellationToken);
  ```
  Для создания токена использовать локальный `CancellationTokenSource`, отменяемый при `IsCancelled`.
- [ ] **9.2** В `MetadataCleanupScript.cs` — аналогично добавить `CancellationToken` в `WaitForExitAsync()`.
- [ ] **9.3** В обоих скриптах добавить таймаут на `WaitForExitAsync` через `CancellationTokenSource.CreateLinkedTokenSource` с `CancelAfter`.
- [ ] **9.4** Убедиться, что `ActiveProcessTracker.Register(process)` уже на месте (подтверждено аудитом — да, есть).

---

## 10. Метод `EnrichTrackNamesAsync` — пристроить к делу

> `MediaProbeService.cs`
> Метод обогащает дорожки значениями `tags.title` из ffprobe (которых нет в выводе mkvmerge --identify).
> Раньше вызывался при параллельном зондировании mkvmerge + ffprobe, но после перехода на чистый mkvmerge вызов был потерян.

### Подзадачи:
- [ ] **10.1** Добавить вызов `await EnrichTrackNamesAsync(structure);` в конец `ProbeMkvAsync()`, перед `return structure`.
- [ ] **10.2** Убедиться, что метод `EnrichTrackNamesAsync` корректно обрабатывает отсутствие `kt-ffprobe` (graceful degradation — если утилита не установлена, просто пропустить обогащение).
- [ ] **10.3** Добавить логирование: `_logService.DebugLog("Обогащение названий дорожек из ffprobe завершено", "MediaProbeService")`.

---

## 11. Кнопка «Обновить» для yt-dlp

> `DependencyVM.cs` / `DependencyManager.cs`
> Сейчас при наличии обновления yt-dlp пользователю показывается только кнопка «Удалить». Обновление происходит фоново и невидимо.
> Нужно: если для yt-dlp доступно обновление — показывать кнопку «Обновить» вместо «Удалить».

### Подзадачи:
- [ ] **11.1** В `DependencyManager.cs` добавить публичный метод (или свойство/событие) `IsUpdateAvailable(string key)`, возвращающий `true`, если для данной зависимости обнаружена новая версия.
- [ ] **11.2** В `DependencyVM.cs` добавить свойство `UpdateAvailable` (bool) и вычисляемые свойства:
  - `UpdateButtonVisibility` — видимость кнопки «Обновить» (Visible когда `Status == Installed && UpdateAvailable`).
  - Кнопка «Удалить» скрывается, когда показана кнопка «Обновить» (или показываются обе).
- [ ] **11.3** Добавить команду `UpdateCommand` в VM, вызывающую `_dependencyManager.InstallDependencyAsync(key)` (переустановка = обновление).
- [ ] **11.4** В XAML страницы зависимостей добавить кнопку «Обновить» с привязкой к `UpdateButtonVisibility` и `UpdateCommand`.
- [ ] **11.5** После фоновой проверки обновлений yt-dlp (`CheckAndUpdateYtDlpAsync`) обновлять свойство `UpdateAvailable` у соответствующей `DependencyVM`.

---

## 12. Проверка: не перебрасывает на экран зависимостей после установки обязательных

> `MainViewModel.cs` → `AreRequiredDependenciesInstalled()`
> Логика: `_registry.Where(d => d.IsRequired).All(IsBinaryPresent)`.

### Подзадачи:
- [ ] **12.1** Проверить список `_registry` — какие зависимости помечены `IsRequired = true`. Убедиться, что опциональные (eac3to, DEE, Node.js) **не** помечены как обязательные.
- [ ] **12.2** Проверить метод `IsBinaryPresent` — что он проверяет наличие файла, а не версию или что-то ещё.
- [ ] **12.3** Ручной тест: после установки всех обязательных компонентов перезапустить приложение и убедиться, что оно открывается на главном экране, а не на странице зависимостей.

---

## 13. Кириллица в .bat файлах DependencyManager

> `DependencyManager.cs` → резервное удаление eac3to
> `.bat` файл с `chcp 65001`, но `cmd.exe` стартует в кодировке 866 и может не найти bat-файл, если путь `%TEMP%` содержит кириллицу.

### Подзадачи:
- [ ] **13.1** Изменить кодировку записи bat-файла: вместо `Encoding.GetEncoding(866)` записывать в чистом ASCII (все пути используют переменные окружения `%SystemRoot%` / `%SystemRoot%`, которые не содержат кириллицы).
- [ ] **13.2** Альтернатива: если путь к `tempBatPath` содержит кириллицу, использовать `Path.GetTempPath()` через `GetShortPathName` (8.3 формат), как уже сделано для DEE.

---

## 14. DependencyVM — перевод на CommunityToolkit.Mvvm

> `Models/DependencyVM.cs`
> Ручная реализация `INotifyPropertyChanged` вместо `[ObservableProperty]`.

### Подзадачи:
- [ ] **14.1** Наследовать `DependencyVM` от `ObservableObject`.
- [ ] **14.2** Заменить приватные поля + ручные свойства + `OnPropertyChanged()` на атрибуты `[ObservableProperty]`.
- [ ] **14.3** Убедиться что все привязки `x:Bind` в XAML корректно работают с новыми именами свойств (CommunityToolkit генерирует PascalCase из camelCase полей).
- [ ] **14.4** Проверить, что `DispatcherQueue.TryEnqueue` для обновления UI-свойств по-прежнему работает.

---

## 15. AbstractScript — внедрение CancellationTokenSource

> `AbstractScript.cs`
> Вместо `volatile bool _isCancelled` предоставить готовый `CancellationToken`.

### Подзадачи:
- [ ] **15.1** Добавить в `AbstractScript` поле `private CancellationTokenSource _cts = new();` и свойство `protected CancellationToken CancellationToken => _cts.Token;`.
- [ ] **15.2** В методе `Cancel()` вызывать `_cts.Cancel()` (в дополнение к `_isCancelled = true` для обратной совместимости).
- [ ] **15.3** В методе `ResetCancellation()` пересоздавать `_cts = new CancellationTokenSource()`.
- [ ] **15.4** Постепенно заменить `while (!IsCancelled) await Task.Delay(100)` в дочерних скриптах на использование `CancellationToken`.

---

## 16. Блокирующие Thread.Sleep в AbstractScript

> `AbstractScript.cs` → `DeleteSource`, `ReplaceSourceWithResult`
> Синхронный `Thread.Sleep(delayMs)` внутри retry-логики.

### Подзадачи:
- [ ] **16.1** Заменить `Thread.Sleep(delayMs)` на `await Task.Delay(delayMs)` в обоих методах.
- [ ] **16.2** Убедиться что методы `DeleteSource` и `ReplaceSourceWithResult` уже `async` (или сделать их `async`).

---

## 17. Дублирование аргументов iscc.exe

> `build_csharp.py`
> Передача `/DMyAppVersion=...`, `/DPublishDir=...` при вызове `iscc.exe`, но шаблон `.iss` эти константы не использует.

### Подзадачи:
- [ ] **17.1** Удалить неиспользуемые параметры `/D...` из вызова `iscc.exe` в `build_csharp.py`.

---

## 18. FileSystemWatcher не освобождается

> `App.xaml.cs`
> `_argsWatcher` не диспозится при закрытии приложения.

### Подзадачи:
- [ ] **18.1** Добавить `_argsWatcher?.Dispose()` в обработчик завершения приложения.
  > *Примечание:* Объединить с задачей 8.3.

---

## 19. UpdateService — блокировка временного файла

> `UpdateService.cs`
> `FileStream` с `FileShare.None` блокирует повторное скачивание при аварийном завершении.

### Подзадачи:
- [ ] **19.1** Перед открытием `FileStream` попытаться удалить старый файл:
  ```csharp
  if (File.Exists(tempFilePath))
      File.Delete(tempFilePath);
  ```

---

## 20. Изоляция бинарников от системного PATH

> Программа должна использовать **исключительно** собственные скачиваемые зависимости (`kt-ffmpeg.exe`, `kt-ffprobe.exe`, `mkvmerge.exe`, `eac3to.exe` и т.д.) через `IPathManager`.
> Обращение к системным утилитам из `PATH` (кроме `cmd.exe`, `explorer.exe`, `nvidia-smi`) **запрещено**.

### Результаты проверки:

| Файл | FileName | Источник пути | Статус |
|---|---|---|---|
| `AbstractProcessRunner.cs` | `binaryPath` | `PathManager.GetBinaryPath()` | ✅ |
| `MediaDownloaderScript.cs` | `ytdlpPath` | `PathManager.GetBinaryPath("yt-dlp")` | ✅ |
| `MetadataCleanupScript.cs` | `ffmpegPath` | `PathManager.GetBinaryPath("ffmpeg")` | ✅ |
| `QaacRunner.cs` | `ffmpegPath` | `PathManager.GetBinaryPath("ffmpeg")` | ✅ |
| `QaacRunner.cs` | `tempQaacPath` | Через `PathManager` + копия | ✅ |
| `FileListControl.xaml.cs` | `ytdlpPath` | `PathManager.GetBinaryPath("yt-dlp")` | ✅ |
| `VideoEncodingScript.cs` | `"ffmpeg"` | Hardcoded строка | ❌ **→ п.3a** |
| `AudioWaveformService.cs` | `"ffmpeg"` (fallback) | Fallback на PATH | ❌ **→ п.3b** |
| `FFmpegRunner.cs` | `"nvidia-smi"` | Системная утилита NVIDIA | ⚪ Допустимо |
| `DependencyManager.cs` | `"cmd.exe"` | Системная утилита Windows | ⚪ Допустимо |
| `LogViewModel.cs` | `"explorer.exe"` | Системная утилита Windows | ⚪ Допустимо |
| `DependencySetupVM.cs` | `"explorer.exe"` | Системная утилита Windows | ⚪ Допустимо |

### Подзадачи:
- [ ] **20.1** Исправить `VideoEncodingScript.cs` (см. п.3a).
- [ ] **20.2** Исправить `AudioWaveformService.cs` (см. п.3b).
- [ ] **20.3** После исправлений выполнить grep-проверку по всему проекту: не должно быть `FileName = "ffmpeg"`, `FileName = "ffprobe"`, `FileName = "mkvmerge"`, `FileName = "eac3to"`, `FileName = "yt-dlp"` без использования `PathManager`.

---

## ✅ Проверка и валидация

- [ ] **V.1** `dotnet build KTools.sln -c Debug -p:Platform=x64` — 0 ошибок, 0 предупреждений.
- [ ] **V.2** `dotnet test KTools.App.Tests -c Debug -p:Platform=x64` — все тесты проходят.
- [ ] **V.3** Ручной тест: запуск приложения → главный экран (не экран зависимостей).
- [ ] **V.4** Ручной тест: страница зависимостей → yt-dlp показывает «Обновить» при наличии обновления.
- [ ] **V.5** Сборка инсталлятора через `python build_csharp.py` без ошибок.
- [ ] **V.6** Grep-проверка: `FileName = "ffmpeg"` / `"ffprobe"` / `"mkvmerge"` / `"eac3to"` / `"yt-dlp"` — 0 результатов.
