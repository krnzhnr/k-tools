@echo off
mkdir "%~dp0Converted"
setlocal enabledelayedexpansion

for %%f in (*.mp4) do (
    set "file=%%f"
    ffmpeg.exe -i "!file!" -c copy "%~dp0Converted\!file:~0,-4!.mkv"
)
echo All MP4 files have been converted to MKV.
pause