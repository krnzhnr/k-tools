# Блок 05: Отложенная NVENC-проба с дисковым кэшем

## Цель

В первые секунды после старта — ноль запусков `kt-ffmpeg` и `nvidia-smi`.
Проба выполняется позже (idle или первое открытие видео-скрипта), результат
кэшируется на диске.

## Контекст

`App.xaml.cs:960` сразу бросает `Task.Run(InitializeAsync)`, а
`Encoders/HardwareCapabilityCache.cs:34-104` внутри запускает
`ffmpeg -encoders` + `nvidia-smi -L` + (при успехе) `ffmpeg -h encoder=hevc_nvenc`
(`Infrastructure/FFmpegRunner.cs:493-602`). Фоновость не спасает: процессы
тянут десятки МБ с того же HDD одновременно с загрузкой UI + AV-скан каждого.

## Зона правок (владение блока)

- `KTools.App/Encoders/HardwareCapabilityCache.cs` — основная.
- `KTools.App/App.xaml.cs` — ТОЛЬКО регион вызова `InitializeAsync` (~строка
  960, одна строка + комментарий): заменить прямой запуск на отложенный
  (idle-таймер N секунд после `Activate()` или ленивый запуск — на усмотрение
  исполнителя). Остальной `App.xaml.cs` — владение блока 01, не трогать.

## Шаги

1. Добавить дисковый кэш результата (`{NVENC, TemporalAQ, ffmpegVersion,
   driverVersion, timestamp}` — место и формат на усмотрение исполнителя,
   рядом с настройками, не в `settings.json` без нужды).
2. `InitializeAsync`: при валидном кэше (совпадают версия ffmpeg и драйвера)
   возвращать кэш без запуска процессов; иначе — как сейчас, затем записать
   кэш. `_isInitialized` + `_initLock` сохранить.
3. Вызов в `App.xaml.cs` отложить: не раньше idle (5–10 с после первого кадра)
   или первого обращения из видео-пайплайна; при закрытии раньше срока —
   просто не запускать.
4. Ошибка/отсутствие NVIDIA — тихий fallback (`false/false`), без диалогов;
   логи как сейчас (`encoder.capabilities.*`).

## Инварианты

- Контракт `IHardwareCapabilityCache` без изменений (свойства + `InitializeAsync`
  + `Invalidate`; `Invalidate` обязан сносить и дисковый кэш).
- Кэш никогда не врет после обновления ffmpeg/драйвера (инвалидация по версиям).

## Приемка

- ETW-трейс (`tools/perf/Trace-Startup.ps1`): первые 10 с после клика — ноль `kt-ffmpeg`/`nvidia-smi`; свойства
  `IsNvencSupported` корректны после завершения фоновой пробы и после
  рестарта берутся из кэша без процессов.
- Машина без NVIDIA: поведение как сейчас (CPU-путь), без задержек старта.

## Проверка

- `dotnet build KTools.sln -c Debug -p:Platform=x64`
- `dotnet test KTools.sln -c Debug -p:Platform=x64` + новые тесты:
  `InitializeAsync_ValidCache_NoProcessSpawn`,
  `InitializeAsync_DriverChanged_Reprobes`,
  `Invalidate_ClearsDiskCache` (раннер в моках).
