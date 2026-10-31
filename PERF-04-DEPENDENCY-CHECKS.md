# Блок 04: Дешевые фоновые проверки зависимостей

## Цель

Старт приложения не запускает веер дочерних процессов и не делает forced
сетевых запросов. Проверки идут позже, строго последовательно, с кэшем.

## Контекст

`MainViewModel.Initialize()` (`ViewModels/MainViewModel.cs:191-233`) при
каждой загрузке `MainPage` вызывает `AreRequiredDependenciesInstalled()`
(синхронно, веер `File.Exists` + `FileVersionInfo`) и fire-and-forget
`CheckAllDependencyUpdatesAsync()`. Внутри (`Core/DependencyManager.cs`):
`CheckAllDependencyUpdatesAsync` (строки ~1643+) с `force:true` дергает
`CheckAndUpdateYtDlpAsync`, `CheckAndUpdateWhisperAsync` и GitHub API `deps-v1`,
а `GetInstalledVersionAsync` (строки ~509–588) запускает процессы
(`ffmpeg -version`, `mkvmerge -V`, `eac3to -log=nul`, `qaac --check`, таймаут
2 с каждый) через `ReadFirstOutputLineAsync`. На HDD это 3–5 холодных запусков
больших exe + 2–3 HTTPS-запроса в момент загрузки UI.

## Зона правок (владение блока)

- Только `KTools.App/Core/DependencyManager.cs`.
- ЗАПРЕЩЕНО трогать: `MainViewModel.cs`, `App.xaml.cs`, `PathManager.cs`,
  `FFmpegRunner.cs`, контракты.

## Шаги

1. Конструктор оставить легким: `RefreshAllStatuses()` (строка ~102) — только
   `File.Exists`-проверки (они дешевые, оставить), убрать из цепочки ctor все
   тяжелое (проверка версии node через `FileVersionInfo` — лениво при первом
   запросе версии, а не в `IsBinaryPresent`).
2. `CheckAllDependencyUpdatesAsync`: убрать внутренние `force:true` для yt-dlp
   и whisper (строки ~1674, ~1677) — уважать 24-часовой троттлинг; `force:true`
   остается только для ручного вызова со страницы «Компоненты».
3. Версионные пробы выполнять строго последовательно (не параллельно), с общим
   бюджетом времени; уже закэшированное (`_cachedVersions`) не перезапрашивать;
   добавить персистентный кэш версий (настройки/файл — на усмотрение
   исполнителя) с инвалидацией при смене пути/размера бинарника.
4. Сетевые вызовы: короткий таймаут, тихий skip при офлайне/rate-limit, без
   ретраев-штормов (политика Polly уже на `HttpClient`, не дублировать).
5. Сематику статусов (`Installed/NotInstalled/Downloading/...`) и события
   (`StatusChanged`, `InstallFinished`) не менять.

## Инварианты

- Страница «Компоненты» с `force:true` работает как раньше, ручная проверка
  ничего не пропускает.
- Никаких новых модальных диалогов и блокировок UI.

## Приемка

- Первые 30 с после старта: ноль запусков `ffmpeg/mkvmerge/eac3to/qaac`
  ради версий и ноль HTTP к `api.github.com` (ETW-трейс + логи:
  `throttled/skipped`, а не каскад `started`).
- Повторный старт в пределах 24 ч вообще не ходит в сеть за версиями.

## Проверка

- `dotnet build KTools.sln -c Debug -p:Platform=x64`
- `dotnet test KTools.sln -c Debug -p:Platform=x64` + новые тесты:
  `CheckAllDependencyUpdates_RespectsThrottle` (см. существующие
  `DependencyManagerTests` как образец),
  `GetInstalledVersion_UsesCache_NoProcessSpawn`.
