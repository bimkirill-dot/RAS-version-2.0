# Установка RAS v. 2.0 на компьютер: загрузчик и манифест — в профиль пользователя, без прав администратора
# (PRINCIPLES.md, раздел 2), для каждой версии Revit, которая есть на компьютере. Ядро и модули не ставятся:
# их приносит загрузчик с сервера. Убирает прежние отдельные установки RAS-Model History (бывший SpyPlugin) —
# иначе журнал изменений писали бы два плагина.
# Пользователь запускает install.cmd рядом (на сервере — папка Setup, её выкладывает revit.cmd, пункт 8).
# revit.cmd (пункт 1) ставит загрузчик разработчику этим же скриптом (-Dev).
# Файл в UTF-8 с BOM: иначе Windows PowerShell 5.1 искажает русский текст.
param(
    # где лежат загрузчики: <Source>\<год Revit>\<Inner>
    [string]$Source = $PSScriptRoot,
    [string]$Inner = '',
    # компьютер разработчика: сообщать только об изменениях
    [switch]$Dev
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$Years = @(2023, 2026)
$LegacyNames = @('RasModelHistory', 'SpyPlugin')
$Retry = if ($Dev) { 'запустите revit.cmd ещё раз' } else { 'запустите установку ещё раз' }
$found = $false
$failed = $false

foreach ($year in $Years) {
    $addins = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$year"

    # прежние отдельные установки: сначала манифест — без него Revit плагин уже не загрузит, даже если папку держит Revit
    foreach ($name in $LegacyNames) {
        $addin = Join-Path $addins "$name.addin"
        if (Test-Path $addin) {
            Remove-Item $addin -Force
            Write-Host "Revit $year — убрана прежняя установка $name."
        }
        $dir = Join-Path $addins $name
        if (Test-Path $dir) {
            try { Remove-Item $dir -Recurse -Force }
            catch { Write-Warning "Папку $dir держит открытый Revit $year — она удалится при следующем запуске установки (прежний плагин уже не загрузится)." }
        }
    }

    if (-not (Test-Path "$env:ProgramFiles\Autodesk\Revit $year\Revit.exe")) { continue }
    $found = $true
    $from = Join-Path (Join-Path $Source $year) $Inner
    if (-not (Test-Path (Join-Path $from 'RASv2.addin'))) {
        Write-Warning "Revit $year — нет загрузчика в $from."
        $failed = $true
        continue
    }
    $target = Join-Path $addins 'RASv2'
    try {
        New-Item -ItemType Directory -Path $target -Force | Out-Null
        $changed = $false
        # копируется только изменившееся: загрузчик занят, пока открыт Revit, — неизменный не мешает
        foreach ($file in Get-ChildItem $from -File | Where-Object { $_.Name -ne 'RASv2.addin' }) {
            $dest = Join-Path $target $file.Name
            if ((Test-Path $dest) -and (Get-FileHash $dest).Hash -eq (Get-FileHash $file.FullName).Hash) { continue }
            Copy-Item $file.FullName $dest -Force
            $changed = $true
        }
        Copy-Item (Join-Path $from 'RASv2.addin') (Join-Path $addins 'RASv2.addin') -Force
        if ($changed) { Write-Host "Revit $year — загрузчик установлен: $addins" }
        elseif (-not $Dev) { Write-Host "Revit $year — уже установлено, изменений нет." }
    }
    catch {
        Write-Warning "Revit $year — загрузчик не установлен: он занят открытым Revit $year. Закройте Revit $year и $Retry."
        $failed = $true
    }
}

# записи «Установка и удаление программ» прежнего установщика
foreach ($key in $LegacyNames) {
    $path = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$key"
    if (Test-Path $path) { Remove-Item $path -Recurse -Force }
}

if ($Dev) { exit 0 }
Write-Host ''
if (-not $found) {
    Write-Warning 'Не найден ни Revit 2023, ни Revit 2026 — устанавливать некуда.'
    exit 1
}
if ($failed) { exit 1 }
Write-Host 'Готово. Запустите Revit — на ленте появится вкладка «RAS v. 2.0».'
Write-Host 'Обновления приходят сами: при каждом запуске Revit плагин берёт актуальную версию с сервера.'
exit 0
