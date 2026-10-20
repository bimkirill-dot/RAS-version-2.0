@echo off
chcp 65001 >nul
rem Сайт RAS-Clash Analysis на этом компьютере (tools\Site.ps1).
rem   site.cmd            — собрать, проверить, обновить и запустить
rem   site.cmd start      — запустить и открыть в браузере
rem   site.cmd stop       — остановить
rem   site.cmd rollback   — вернуть прежнюю версию программы
rem   site.cmd autostart  — запускать при входе в Windows
rem   site.cmd firewall   — открыть порт сайта коллегам (один раз, спросит права администратора)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Site.ps1" %*
pause
