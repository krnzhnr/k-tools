# Блок 02: Распил снепшота системы — диски и реестр GPU в фон

## Цель

Опрос дисков и реестра видеокарт никогда не блокирует UI-поток, даже если
вызван напрямую. Быстрая часть снепшота остается синхронной и дешевой.

## Контекст

`KTools.App/Services/Implementations/SystemInfoService.cs:LogDrives`
(строки ~387–476): `DriveInfo.GetDrives()` + `TotalSize/AvailableFreeSpace`
по каждому диску + `IDiskTypeDetectorService` (IOCTL) на каждый корень.
`TotalSize` на спящем HDD/оптике/сетевом диске будит устройство и висит.
`LogGraphicsAdapters` (~строки 301–385) читает ветку реестра видеоадаптеров —
дешевле, но тоже не мгновенно. Оба вызываются из `LogSystemCharacteristics()`,
который сегодня дергается синхронно на старте.

## Зона правок (владение блока)

- `KTools.App/Services/Implementations/SystemInfoService.cs` — основная.
- При нужде `KTools.App/Services/Contracts/ISystemInfoService.cs` — только
  добавлением новых методов, существующий `LogSystemCharacteristics()` сохранить
  работающим (делегирует новым).
- ЗАПРЕЩЕНО трогать: `App.xaml.cs` (владение блока 01),
  `DiskTypeDetectorService.cs` (он уже быстрый, не чинить).

## Шаги

1. Разделить снепшот:
   - `LogEssentialCharacteristics()` (синхронно, дешево): ОС, рантайм/процесс,
     CPU-имя, память, культуры. Никаких обращений к дискам.
   - `LogExtendedCharacteristicsAsync(CancellationToken)` (фон): видеоадаптеры,
     диски (+ типы носителей), остальное.
2. `LogSystemCharacteristics()` сохранить как обертку (essential + extended
   с `CancellationToken.None`) — чтобы старый вызов из `App.xaml.cs`
   компилировался и работал до/без блока 01.
3. В опросе дисков: пропускать неготовые (`IsReady` уже проверяется,
   сохранить), каждый диск опрашивать под индивидуальным try/catch (уже есть,
   сохранить), добавить общий таймаут на всю операцию; сбой одного диска не
   роняет остальные и не падает.
4. Логирование — только через `ILogService.Write`, те же `eventId`, русский
   текст сохранить (совместимость дашбордов/тестов).

## Инварианты

- Набор событий в журнале не меняется (те же `system.*`), меняется только
  поток/время их появления.
- `DiskTypeDetectorService` не дублировать и не обходить.

## Приемка

- Прямой вызов `LogSystemCharacteristics()` при спящем втором диске не вешает
  вызывающий UI-поток дольше ~100 мс (быстрая часть), полная картина
  дожимается фоном.
- В журнале присутствуют все прежние `system.*` события.

## Проверка

- `dotnet build KTools.sln -c Debug -p:Platform=x64`
- `dotnet test KTools.sln -c Debug -p:Platform=x64` + новые тесты:
  `LogEssential_DoesNotTouchDrives`, `LogExtended_SleepingDrive_DoesNotThrow`
  (моки `IDiskTypeDetectorService`, `ILogService` через Moq).
