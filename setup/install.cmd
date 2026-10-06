@echo off
chcp 65001 >nul
rem Установка плагина RAS v. 2.0 на этот компьютер. Запускать при закрытом Revit; права администратора не нужны.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
pause
