# Инструменты замеров производительности

Готовый набор, чтобы каждый PERF-блок (`PERF-0*.md` в корне) проверять одинаково.
Все пути — относительно корня репозитория. Python запускать через `py -3`
(алиас `python3` на этой машине сломан). PowerShell-скрипты — через
`powershell -ExecutionPolicy Bypass -File ...`.

## Что уже установлено на этой машине

- .NET SDK 10.0.401.
- `dotnet-counters` 8.x и `dotnet-trace` 8.x (глобальные тулзы, поставлены
  при создании этого набора). Проверка: `dotnet-counters --help`.
- WPR / `xperf.exe` / `tracerpt.exe` (Windows Performance Toolkit).
- НЕ работают: `Get-Counter` (счетчики сломаны — ошибка `CounterApiError`),
  PerfView / ProcMon / WPA (не установлены). Вместо них — WPR + `tracerpt`.

## Скрипты

| Скрипт | Назначение |
|---|---|
| `Measure-Startup.ps1` | Теплый старт N раз: время до первого окна (`MainWindowHandle`), маркер в логе, пик памяти. Пишет CSV+JSON в `tools/perf/results`. |
| `Analyze-StartupLog.py` | Фазы старта из сессионного `ktools_*.log` + сводка `process.started/exit` по утилитам. |
| `Trace-Startup.ps1` | ETW-трейс старта (только от администратора): WPR → ETL → XML → сводка. |
| `Summarize-Etl.py` | Дерево запусков процессов, файловые события по процессам, топ файлов. |

## Стандартный протокол блока (до и после правки, одинаковые условия)

1. `powershell -ExecutionPolicy Bypass -File tools/perf/Measure-Startup.ps1 -Runs 5`
   записать медиану до окна. Холодный старт «как после перезагрузки» недоступен
   (машину не перезагружаем) — сравниваем теплые старты и считаем IO.
2. `py -3 tools/perf/Analyze-StartupLog.py <свежий ktools_*.log>` — фазы и число
   запусков процессов.
3. От администратора: `Trace-Startup.ps1` — истина по файловым операциям.
4. `dotnet test KTools.sln -c Debug -p:Platform=x64` с trx — соседние тесты не
   деградировали по времени.
5. В отчет блока: цифры до/после + файлы из `tools/perf/results`.

## Точечные рецепты

```powershell
# Живые счетчики GC/CPU/JIT процесса (узнать PID из Диспетчера):
dotnet-counters monitor -p <pid> System.Runtime Microsoft-AspNetCore-Server-Kestrel
# Короткий runtime-трейс с CPU-семплами:
dotnet-trace collect -p <pid> --duration 00:00:20 -o tools/perf/results/ui.nettrace
```

## Юнит-замеры

Хелпер `KTools.App.Tests/TestHelpers/PerfTiming.cs`: `Measure` / `MeasureAsync` /
`CompleteWithin` (мягкий порог-триггер, не бенчмарк). Бюджеты брать с запасом —
машина общая, тайминги плавают. Пример есть в комментариях хелпера.
