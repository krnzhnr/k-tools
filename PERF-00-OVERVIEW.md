# Производительность холодного старта — карта независимых блоков

Жалоба: на машине с HDD программа «запускается так себе» (долго, тормоза).
Разбор показал: проблема не одна, а цепочка из синхронных работ на UI-потоке
до первого кадра + шторм дочерних процессов сразу после старта. На SSD это
незаметно, на HDD складывается в секунды-десятки секунд.

## Блоки (каждый выполняется независимо, в любом порядке, любым агентом)

| Файл | Блок | Зона правок (владение) | Дает сам по себе |
|---|---|---|---|
| `PERF-01-STARTUP-ORCHESTRATION.md` | Оркестрация `OnLaunched` | Только `KTools.App/App.xaml.cs`: регионы `OnLaunched`, `ProcessPendingArgsFiles`, `StartArgsWatcher` (кроме строки запуска NVENC — она за блоком 05) | До `Activate()` остаются логгер, чтение настроек, окно и навигация |
| `PERF-02-SYSTEMINFO-SPLIT.md` | Распил снепшота системы | Только `Services/Implementations/SystemInfoService.cs` (+ при нужде `Services/Contracts/ISystemInfoService.cs`) | Опрос дисков/реестра GPU не блокирует UI даже при прямом вызове |
| `PERF-03-SCRIPTREGISTRY-LAZY.md` | Ленивый реестр скриптов | Только `Core/ScriptRegistry.cs` | Старт без создания 18 скриптов и без записи `settings.json` |
| `PERF-04-DEPENDENCY-CHECKS.md` | Дешевые фоновые проверки зависимостей | Только `Core/DependencyManager.cs` | Никакого веера процессов и forced-сети со старта |
| `PERF-05-NVENC-DEFER-CACHE.md` | Отложенная NVENC-проба с кэшем | `Encoders/HardwareCapabilityCache.cs` + одна строка вызова в `App.xaml.cs` (~строка 960, регион помечен) | Ноль запусков `kt-ffmpeg/nvidia-smi` в первые секунды |
| `PERF-06-PATHMANAGER-PROBE.md` | Кэширование probe-записей путей | Только `Services/Implementations/PathManager.cs` | Нет синхронных записей на диск при каждом старте |
| `PERF-07-R2R-BUILD.md` | ReadyToRun в шипе | Только `build_csharp.py` (+ комментарий в `KTools.App/KTools.App.csproj`) | Меньше JIT на холодном старте |
| `PERF-08-BACKDROP-SETTING.md` | Настройка фона окна | `MainWindow.xaml.cs`, `SettingsManager.cs` (ключи), экран настроек, `MainViewModel` при нужде | Нет блюра на слабом железе |

## Инструменты замеров (уже готовы, проверены)

Все лежат в `tools/perf/` (см. `tools/perf/README.md`), проверены запуском
на этой машине; `dotnet-counters` и `dotnet-trace` 8.x установлены глобально:

- `Measure-Startup.ps1 -Runs 5` — теплый старт: время до первого окна
  (`MainWindowHandle`), маркер готовности в логе, пик WorkingSet; CSV+JSON
  в `tools/perf/results`.
- `Analyze-StartupLog.py <ktools_*.log>` — фазы старта и сводка
  `process.started/exit` по утилитам (понимает JSONL, актуальный и legacy
  текстовые форматы).
- `Trace-Startup.ps1` (только от администратора) — WPR-трейс старта →
  ETL → XML → `Summarize-Etl.py`: дерево запусков процессов, файловые
  события по процессам, топ файлов. Замена ProcMon/PerfView.
- `KTools.App.Tests/TestHelpers/PerfTiming.cs` — `Measure`/`MeasureAsync`/
  `CompleteWithin`: мягкие пороги-триггеры регресса в юнит-тестах
  (бюджеты с 3–5-кратным запасом, это не бенчмарки).

Стандартный протокол блока: замер до → правка → замер после в тех же условиях
(Defender вкл, те же диски), цифры до/после в отчет; `dotnet test` с trx —
соседние тесты не деградировали. Холодный старт «как после перезагрузки»
недоступен (машину не перезагружаем) — сравниваем теплые старты и считаем IO.

## Общие инварианты (обязательны для всех блоков)

1. Публичные контракты (`ILogService`, `ISettingsManager`, `IScriptRegistry`,
   `IDependencyManager`, `IHardwareCapabilityCache`, `IPathManager`) не ломать:
   сигнатуры сохранять, новое — только добавлением методов.
2. Запись поверх деградированного (поврежденного) `settings.json` — только через
   явный `ResetToDefaults`. Блок 03 обязан сохранить этот инвариант.
3. Логирование только через `_logService.Write(...)`, сообщения строго на русском
   (см. `LOGGING.md`, `RULES.md` §11).
4. Никаких `Thread.Sleep`, `.Result`, `.Wait()` на UI-потоке (RULES §5.1).
5. Каждый блок привозит свои юнит-тесты (MSTest, именование
   `MethodName_StateUnderTest_ExpectedBehavior`) и проходит:
   `dotnet build KTools.sln -c Debug -p:Platform=x64` и
   `dotnet test KTools.sln -c Debug -p:Platform=x64`.
6. Замеры до/после каждого блока по протоколу из раздела «Инструменты замеров»
   выше: теплые старты (`Measure-Startup.ps1`) + подсчет запусков дочерних
   процессов и IO в первые 30 с (`Analyze-StartupLog.py`, ETW-трейс).

## Порядок слияния при параллельной работе

Блоки не зависят друг от друга функционально и сливаются в любом порядке.
Единственная общая точка — `App.xaml.cs`: ее трогают блоки 01 и 05 в разных,
непересекающихся регионах (регионы указаны в файлах блоков). При конфликте
слияния приоритет за блоком 01, регион блока 05 (одна строка вызова NVENC)
переносится вручную.

## Что сюда не входит (сделано отдельно)

Окно «Что нового» с WebView2: сейчас открывается только по debug-кнопке,
на старт не влияет. Правки по нему переданы отдельно.
