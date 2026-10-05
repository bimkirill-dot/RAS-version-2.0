@echo off
chcp 65001 >nul
rem Сборка под Revit 2023 и 2026 + проверки. Сборка ложится в out\Releases, загрузчик ставится себе.
rem Выпуск для людей — publish.cmd.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Release.ps1" -Action build
pause
