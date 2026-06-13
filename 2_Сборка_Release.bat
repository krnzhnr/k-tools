@echo off
chcp 65001 > nul
title Шаг 2: Компиляция K-Tools в режиме Release
echo Компиляция решения в конфигурации Release (x64)...

dotnet build "%~dp0KTools.App\KTools.App.csproj" -c Release -p:Platform=x64
if %errorlevel% neq 0 (
    echo [ОШИБКА] Ошибка сборки решения.
    color 0C
) else (
    echo [УСПЕХ] Сборка решения успешно завершена.
)
pause
