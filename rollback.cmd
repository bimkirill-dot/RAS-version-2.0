@echo off
chcp 65001 >nul
setlocal
rem Вернуть прежнюю сборку: rollback.cmd (у всех) или rollback.cmd test (у тестовой группы).
set "CH=%~1"
if "%CH%"=="" set "CH=all"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Release.ps1" -Action rollback -Channel %CH%
pause
