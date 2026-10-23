@echo off
chcp 65001 >nul
rem Установка RAS v. 2.0 для Navisworks 2023 на этот компьютер. Запускать при закрытом Navisworks; права администратора не нужны.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-navisworks.ps1"
pause
