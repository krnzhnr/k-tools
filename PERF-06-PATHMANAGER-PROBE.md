# Блок 06: PathManager без пробных записей при каждом старте

## Цель

`GetBinDirectory()` / `GetSettingsDirectory()` не делают синхронных
`WriteAllText+Delete` проб при каждом запуске. Решение о доступности записи
принимается один раз и кэшируется между запусками.

## Контекст

`KTools.App/Services/Implementations/PathManager.cs:66-157`: оба метода при
каждом старте пишут и удаляют пробный файл в каталоге установки
(`.write_test_bin_<guid>`, `.write_test`). На HDD с Defender'ом каждая такая
пара — лишние синхронные сотни мс в стартовом пути, причем результат нигде
не сохраняется: `SettingsManager` (ctor) и `DependencyManager` (ctor) дергают
эти методы при каждом запуске заново.

## Зона правок (владение блока)

- Только `KTools.App/Services/Implementations/PathManager.cs`.
- ЗАПРЕЩЕНО трогать: `App.xaml.cs`, `SettingsManager.cs`,
  `DependencyManager.cs` (они выиграют автоматически).

## Шаги

1. Кэшировать verdict о записи в каталог установки между запусками
   (например, маркер в `%LocalAppData%\KTools` с версией/путем установки;
   формат и имя — на усмотрение исполнителя, документировать в комментарии).
2. При валидном кэше пробную запись не выполнять; при смене пути установки,
   версии или отсутствии/протухании кэша — выполнить пробу один раз и
   обновить кэш.
3. Внутрипроцессные кэши (`_binDirectory` и аналоги) сохранить; добавить
   потокобезопасность чтения настроек по образцу существующего
   `_binDirectoryLock`.
4. Поведение fallback'ов не менять: MSIX → `LocalAppData`, нет прав →
   `LocalAppData`, порядок кандидатов `GetBinaryPath` не менять.

## Инварианты

- Возвращаемые пути при тех же условиях — те же, что сейчас.
- Никаких новых диалогов/запросов прав; только тихий кэш.

## Приемка

- Второй и последующие запуски: ноль пробных файлов в каталоге установки
  (ETW-трейс `tools/perf/Trace-Startup.ps1`: нет `CreateFile .write_test*` в стартовом пути).
- Кейсы: portable (права есть), `Program Files` без прав, MSIX — пути как раньше.

## Проверка

- `dotnet build KTools.sln -c Debug -p:Platform=x64`
- `dotnet test KTools.sln -c Debug -p:Platform=x64` + новые тесты:
  `GetBinDirectory_SecondCall_NoProbeWrite`,
  `GetSettingsDirectory_CacheInvalidated_Reprobes`
  (подмена `baseDir` через ctor с `baseDir`, временные каталоги, чистка
  в `[TestCleanup]` по RULES §8.2).
