@echo off
chcp 65001 >nul
rem Что сейчас у каналов «тест» и «все»: какая версия, когда выпущена, что нового. Ничего не меняет.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Release.ps1" -Action status
pause
