# Блок 07: ReadyToRun в релизном шипе

## Цель

Убрать расхождение сборки и замерить эффект: релизный установщик собирается
с ReadyToRun, холодный старт делает меньше JIT.

## Контекст

`KTools.App/KTools.App.csproj:101-102`: `PublishReadyToRun=True` для Release.
Но `build_csharp.py:126` публикует с `-p:PublishReadyToRun=false` — шип,
который ставится пользователю, идет без R2R. Триминг выключен осознанно
(`PublishTrimmed=False`, MSIX/COM — не включать). Single-file выключен
(оставить).

## Зона правок (владение блока)

- Только `build_csharp.py` (строка ~126) и при нужде комментарий в
  `KTools.App/KTools.App.csproj` (секция Publish Properties).
- ЗАПРЕЩЕНО трогать: C#-код, `PublishTrimmed`, `PublishSingleFile`,
  `WindowsAppSDKSelfContained`, инсталляторные секции.

## Шаги

1. Убрать `-p:PublishReadyToRun=false` из `build_csharp_app` (чтобы действовал
   `True` из csproj для Release) либо заменить на явное
   `-p:PublishReadyToRun=true` с комментарием почему.
2. Собрать шип через `python build_csharp.py`, зафиксировать: размер
   установщика и `publish/`, время холодного старта (после перезагрузки) до/после
   на HDD-стенде, время теплого старта.
3. Если выигрыш старта есть, а рост размера приемлемый — оставить; если нет —
   вернуть `false`, но с комментарием-причиной в `build_csharp.py` (чтобы
   расхождение больше не было молчаливым). Решение зафиксировать в этом файле
   (дописать итог замера).

## Инварианты

- Установщик ставится/обновляется как раньше (тихий апдейт, ярлыки, реестр —
  не трогать).
- `TreatWarningsAsErrors` и остальные строгости сборки не ослаблять.

## Приемка

- A/B-замер холодного старта на HDD приложен (цифры до/после), решение
  зафиксировано, сборка зеленая.

## Проверка

- `python build_csharp.py` (полный шип) + установка и холодный старт.
- `dotnet build KTools.sln -c Debug -p:Platform=x64`
- `dotnet test KTools.sln -c Debug -p:Platform=x64`
