@echo off
chcp 65001 >nul
rem Иконки кнопок из SVG: PNG 32 и 16, светлые и тёмные, рядом с SVG + лист просмотра out\icons-preview.png.
rem Стиль и палитра — tools\IconBuilder\СТИЛЬ.md.
dotnet run --project "%~dp0tools\IconBuilder" -c Release
if not errorlevel 1 start "" "%~dp0out\icons-preview.png"
pause
