@echo off
setlocal
set EXE=%~dp0src\PinShot.App\bin\Release\net8.0-windows10.0.19041.0\PinShot.exe
if not exist "%EXE%" set EXE=%~dp0src\PinShot.App\bin\Release\net8.0-windows\PinShot.exe
start "" "%EXE%"
endlocal