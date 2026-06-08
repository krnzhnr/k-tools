@echo off
chcp 65001 > nul
title Шаг 1: Генерация сертификата разработчика K-Tools
echo Проверка и создание сертификата подписи devcert.pfx...

if exist "%~dp0devcert.pfx" (
    echo [ИНФО] Сертификат devcert.pfx уже существует в папке проекта.
) else (
    echo Генерация сертификата разработчика...
    winapp cert generate --manifest "%~dp0KTools.App\Package.appxmanifest" --install
    if %errorlevel% neq 0 (
        echo [ОШИБКА] Не удалось сгенерировать сертификат.
        color 0C
    ) else (
        echo [УСПЕХ] Сертификат успешно сгенерирован и установлен в систему.
    )
)
pause
