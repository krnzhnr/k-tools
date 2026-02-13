@echo off
mkdir "%~dp0Converted"
setlocal enabledelayedexpansion

for %%f in (*.mkv) do (
    set "file=%%f"
    ffmpeg.exe -i "!file!" -c copy "%~dp0Converted\!file:~0,-4!.mp4"
)
echo All MKV files have been converted to MP4.
pause