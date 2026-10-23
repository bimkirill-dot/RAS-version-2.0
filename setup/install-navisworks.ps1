# Установка RAS v. 2.0 для Navisworks Manage / Simulate 2023: пакет RASv2.Navisworks.bundle — в профиль пользователя
# (%AppData%\Autodesk\ApplicationPlugins), без прав администратора. Сборку с инструментами пакет берёт с сервера сам
# (Releases\Navisworks\2023) — переустанавливать нужно, только когда изменился пакет (новая кнопка, иконка).
# Отдельно от установки для Revit (install.cmd). Убирает прежний пакет RasGroup.ClashViewFixer (первая попытка «подстроить виды»).
# Пользователь запускает install-navisworks.cmd рядом (на сервере — Setup\Navisworks, её выкладывает navisworks.cmd → «установка»).
# navisworks.cmd ставит пакет разработчику этим же скриптом (-Dev).
# Файл в UTF-8 с BOM: иначе Windows PowerShell 5.1 искажает русский текст.
param(
    # где лежит пакет RASv2.Navisworks.bundle
    [string]$Source = $PSScriptRoot,
    # компьютер разработчика: сообщать только об изменениях
    [switch]$Dev
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$BundleName = 'RASv2.Navisworks.bundle'
$Legacy = @('RasGroup.ClashViewFixer.bundle')
$Plugins = Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins'
$Retry = if ($Dev) { 'запустите navisworks.cmd ещё раз' } else { 'запустите установку ещё раз' }

$installed = @('Manage', 'Simulate') | Where-Object { Test-Path "$env:ProgramFiles\Autodesk\Navisworks $_ 2023\Roamer.exe" }
if (-not $installed) {
    Write-Warning 'Не найден Navisworks Manage или Simulate 2023 — устанавливать некуда.'
    if (-not $Dev) { exit 1 } else { exit 0 }
}
$from = Join-Path $Source $BundleName
if (-not (Test-Path (Join-Path $from 'PackageContents.xml'))) { throw "Нет пакета: $from" }
New-Item -ItemType Directory -Path $Plugins -Force | Out-Null

foreach ($name in $Legacy) {
    $old = Join-Path $Plugins $name
    if (-not (Test-Path $old)) { continue }
    try { Remove-Item $old -Recurse -Force; Write-Host "Убран прежний пакет $name." }
    catch { Write-Warning "Прежний пакет $name занят открытым Navisworks — закройте Navisworks и $Retry." }
}

$target = Join-Path $Plugins $BundleName
# копируется только изменившееся: пакет занят, пока открыт Navisworks, — неизменный не мешает
$changed = @(Get-ChildItem $from -Recurse -File | Where-Object { $_.Extension -ne '.pdb' } | Where-Object {
    $dest = Join-Path $target $_.FullName.Substring($from.Length + 1)
    -not (Test-Path $dest) -or (Get-FileHash $dest).Hash -ne (Get-FileHash $_.FullName).Hash
})
if ($changed.Count -eq 0) {
    if (-not $Dev) { Write-Host 'Уже установлено, изменений нет.' }
    else { Write-Host 'Пакет для Navisworks — тот же, что установлен.' }
    exit 0
}
try {
    foreach ($file in $changed) {
        $dest = Join-Path $target $file.FullName.Substring($from.Length + 1)
        New-Item -ItemType Directory -Path (Split-Path $dest) -Force | Out-Null
        Copy-Item $file.FullName $dest -Force
    }
    Write-Host "Пакет для Navisworks установлен: $target"
    if (-not $Dev) { Write-Host 'Откройте Navisworks — на ленте появится вкладка «RAS v. 2.0».' }
}
catch {
    Write-Warning "Пакет для Navisworks не установлен: он занят открытым Navisworks. Закройте Navisworks и $Retry."
    exit 1
}
