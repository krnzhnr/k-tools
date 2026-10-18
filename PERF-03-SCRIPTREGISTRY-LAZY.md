# Блок 03: Ленивый реестр скриптов — старт без 18 скриптов и записи на диск

## Цель

Создание `ScriptRegistry` на старте не создает 18 скриптов и не пишет
`settings.json` на диск. Скрипты поднимаются лениво, дефолты применяются
в памяти, запись откладывается.

## Контекст

`KTools.App/Core/ScriptRegistry.cs:RegisterScripts` (строки ~138–195):
через `IServiceProvider` резолвятся все 18 `AbstractScript`, затем
`_settingsManager.InitializeDefaults(_scripts)` с `PersistNow()` — синхронная
запись `settings.json` на HDD прямо в стартовом пути (`App.xaml.cs:959`).
Сами конструкторы `AbstractScript` (`Core/AbstractScript.cs:226-259`) легкие,
тяжесть — в количестве резолвов DI и в дисковой записи дефолтов.

## Зона правок (владение блока)

- Только `KTools.App/Core/ScriptRegistry.cs`.
- ЗАПРЕЩЕНО трогать: `App.xaml.cs`, `SettingsManager.cs`, классы скриптов.

## Шаги

1. Перевести `_scripts` на ленивую инициализацию: первый доступ к `Scripts` /
   `GetScriptByName` строит список один раз (потокобезопасно, double-checked
   lock или `Lazy<>`), повторные доступы возвращают построенное.
2. `InitializeDefaults` на старте вызывать в режиме «только память»: если такой
   режим потребует изменений `SettingsManager` — НЕ делать их здесь; вместо
   этого в этом блоке отложить вызов `InitializeDefaults` до первого доступа
   к скриптам и не требовать `Persisted=true` (принять `Pending`/отложенный
   исход через существующий `RecordDefaultsOutcome`).
3. Сброс состояния скриптов (`FilesQueue.Clear()` и т.д., строки ~169–194)
   выполнять для фактически созданных скриптов, в том же ленивом проходе.
4. Сохранить поведение `GetScriptByName` (поиск без учета регистра) и
   `RecordDefaultsOutcome` без изменений.

## Инварианты

- Публичный контракт `IScriptRegistry` без изменений.
- Защита деградированной загрузки сохраняется: никакой записи поверх
  поврежденного файла вне явного `ResetToDefaults` (это гарантирует
  `SettingsManager` — не обходить).
- `MainViewModel.InitializeScripts` и `MainPage.PopulateDynamicNavItems`
  продолжают получать полный список — просто чуть позже (при первом доступе).

## Приемка

- Холодный старт: резолв `IScriptRegistry` в DI не создает ни одного скрипта
  и не пишет на диск (видно в ETW-трейсе `tools/perf/Trace-Startup.ps1`:
  ноль записей `settings.json` до
  первого открытия скрипта/настроек).
- Первый переход на любой скрипт работает как раньше, дефолты применены.

## Проверка

- `dotnet build KTools.sln -c Debug -p:Platform=x64`
- `dotnet test KTools.sln -c Debug -p:Platform=x64` + новые тесты:
  `Scripts_Lazy_NoScriptsCreatedOnResolve`,
  `GetScriptByName_FirstAccess_BuildsAllScripts`,
  `RegisterScripts_DoesNotWriteSettingsFile` (моки `IServiceProvider`,
  `ISettingsManager`, `ILogService`).
