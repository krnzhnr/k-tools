@echo off
chcp 65001 > nul
cls
setlocal EnableDelayedExpansion

echo Выберите режим:
echo.
echo 1 - Перекодировать WAV в M4A
echo 2 - Вшить M4A в MP4 (замена файлов)
echo.
set /p MODE=Введите 1 или 2: 

if "%MODE%"=="1" goto MODE1
if "%MODE%"=="2" goto MODE2

echo.
echo Неверный выбор!
pause
exit /b


:: ===============================
:: РЕЖИМ 1 — WAV -> M4A (13 потоков)
:: ===============================
:MODE1

echo.
echo === Режим 1: Конвертация WAV -> M4A (13 потоков) ===
echo.

set MAX=13

for %%F in (*.wav) do (

:wait1
    set COUNT=0

    for %%P in (qaac64.exe) do (
        tasklist | find /I "%%P" > nul && set /A COUNT+=1
    )

    if !COUNT! GEQ %MAX% (
        timeout /t 2 /nobreak > nul
        goto wait1
    )

    echo Обработка: %%F

    start "" /B cmd /C ^
    "qaac64 --ignorelength --tvbr 127 "%%F" -o "%%~nF.m4a""
)

echo.
echo Все задания запущены
pause
goto END


:: ===============================
:: РЕЖИМ 2 — M4A -> MP4 (с заменой)
:: ===============================
:MODE2

echo.
echo === Режим 2: Вшивание M4A в MP4 (замена) ===
echo.

for %%V in (*.mp4) do (

    if exist "%%~nV.m4a" (

        echo Обработка: %%V

        ffmpeg -y -loglevel error ^
        -i "%%V" ^
        -i "%%~nV.m4a" ^
        -map 0:v:0 ^
        -map 1:a:0 ^
        -map_metadata -1 ^
        -c copy ^
        "%%~nV_tmp.mp4"

        if !errorlevel! EQU 0 (

            del "%%V"
            del "%%~nV.m4a"

            ren "%%~nV_tmp.mp4" "%%V"

            echo Готово: %%V

        ) else (

            echo ОШИБКА: %%V
            if exist "%%~nV_tmp.mp4" del "%%~nV_tmp.mp4"
        )

    ) else (

        echo Нет аудио для: %%V
    )
)

echo.
echo Все файлы обновлены
pause
goto END


:: ===============================
:: КОНЕЦ
:: ===============================
:END

endlocal
exit /b
