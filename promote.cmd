@echo off
chcp 65001 >nul
rem Сборка, проверенная тестовой группой, — всем (без пересборки).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Release.ps1" -Action promote
pause
