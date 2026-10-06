@echo off
chcp 65001 >nul
rem Выложить установку на сервер (папка Setup): install.cmd и загрузчик для Revit 2023 и 2026.
rem Нужно, когда изменился загрузчик или скрипт установки. Ядро и модули выпускает publish.cmd.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Release.ps1" -Action setup
pause
