# Сайт RAS-Clash Analysis (PRINCIPLES.md, «Сайт модуля» и раздел 10, «Сайт»). Запускается из site.cmd в корне.
#   update    — собрать, проверить, сделать копию базы, заменить программу, запустить (по умолчанию)
#   start     — запустить, если не запущен, и открыть в браузере
#   stop      — остановить
#   rollback  — вернуть прежнюю версию программы (база не меняется)
#   autostart — запускать сайт при входе в Windows (ярлык в «Автозагрузке»; без прав администратора)
# Сайт работает на этом компьютере: программа и данные — %LocalAppData%\RAS_v_2.0\RAS-Clash Analysis\Site.
# Файл в UTF-8 с BOM: иначе Windows PowerShell 5.1 искажает русский текст.
param(
    [ValidateSet('update', 'start', 'stop', 'rollback', 'autostart')]
    [string]$Action = 'update',
    # не открывать браузер после запуска
    [switch]$NoBrowser
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$Root = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $Root 'src\Modules\RAS-Clash Analysis\Site\RASv2.ClashAnalysis.Site.csproj'
$Tests = Join-Path $Root 'tests\RASv2.Tests\RASv2.Tests.csproj'
$SiteHome = Join-Path $env:LOCALAPPDATA 'RAS_v_2.0\RAS-Clash Analysis\Site'
$App = Join-Path $SiteHome 'Программа'
$Prev = Join-Path $SiteHome 'Программа.прежняя'
$New = Join-Path $SiteHome 'Программа.новая'
$ExeName = 'RASv2.ClashAnalysis.Site'
$Exe = Join-Path $App "$ExeName.exe"

# адрес сайта — Urls из appsettings.json; свой settings.json главнее
function Get-SiteUrl {
    $url = 'http://localhost:5080'
    foreach ($file in @((Join-Path $App 'appsettings.json'), (Join-Path $SiteHome 'settings.json'))) {
        if (Test-Path $file) {
            try {
                $json = Get-Content $file -Raw -Encoding UTF8 | ConvertFrom-Json
                if ($json.Urls) { $url = ($json.Urls -split ';')[0] }
            }
            catch { Write-Host "Не прочитан $file — $($_.Exception.Message)" }
        }
    }
    return $url
}

# только живой процесс: только что остановленный ещё виден в списке, пока на него есть ссылки
function Get-SiteProcess { Get-Process -Name $ExeName -ErrorAction SilentlyContinue | Where-Object { -not $_.HasExited } }

function Stop-Site {
    $p = Get-SiteProcess
    if (-not $p) { return }
    Write-Host 'Останавливаем сайт...'
    $p | Stop-Process -Force
    $p | Wait-Process -Timeout 20 -ErrorAction SilentlyContinue
    for ($i = 0; $i -lt 20 -and (Get-SiteProcess); $i++) { Start-Sleep -Milliseconds 500 }
    if (Get-SiteProcess) { throw 'Сайт не остановился — закройте его окно и повторите.' }
}

function Start-Site {
    if (Get-SiteProcess) { Write-Host 'Сайт уже запущен.' }
    elseif (-not (Test-Path $Exe)) { throw "Сайт ещё не установлен ($Exe). Запустите site.cmd без параметров." }
    else {
        Write-Host 'Запускаем сайт...'
        # своё окно, свёрнутое: в нём журнал сайта; закрыть окно — остановить сайт.
        # Запуск — через WMI, а не Start-Process: так сайт не дочерний процесс того, кто запустил команду, и не
        # закрывается вместе с ним (Claude Code держит свои команды в «задании» Windows — 09.10 и 12.10.2026 сайт
        # закрывался вместе с ним без записи в журнале)
        $startup = New-CimInstance -ClassName Win32_ProcessStartup -ClientOnly -Property @{ ShowWindow = [uint16]7 }
        $r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{
            CommandLine = '"' + $Exe + '"'; CurrentDirectory = $App; ProcessStartupInformation = $startup }
        if ($r.ReturnValue -ne 0) { throw "Сайт не запустился: Win32_Process.Create вернул $($r.ReturnValue)." }
        Start-Sleep -Seconds 3
        if (-not (Get-SiteProcess)) { throw "Сайт не запустился — см. журнал: $(Join-Path $SiteHome 'Журнал')" }
    }
    $url = Get-SiteUrl
    Write-Host "Сайт: $url"
    if (-not $NoBrowser) { Start-Process $url | Out-Null }
}

# Копия базы перед заменой программы: сайт остановлен, файлы базы (вместе с журналом WAL) копируются вместе
function Backup-Database {
    $db = Join-Path $SiteHome 'clashes.db'
    if (-not (Test-Path $db)) { return }
    $folder = Join-Path $SiteHome 'Копии базы'
    $target = Join-Path $folder ('перед обновлением ' + (Get-Date -Format 'yyyy-MM-dd HHmmss'))
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    Get-ChildItem $SiteHome -File -Filter 'clashes.db*' | Copy-Item -Destination $target
    Write-Host "Копия базы: $target"
    # 10 последних копий перед обновлением
    Get-ChildItem $folder -Directory -Filter 'перед обновлением *' |
        Sort-Object Name -Descending | Select-Object -Skip 10 | Remove-Item -Recurse -Force
}

switch ($Action) {
    'update' {
        Write-Host 'Проверки...'
        & dotnet build $Tests -c Release -p:RevitVersion=2026 -nologo -v:q
        if ($LASTEXITCODE -ne 0) { throw 'Проверки не собрались.' }
        & (Join-Path $Root 'out\bin\2026\tests\RASv2.Tests.exe') | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Проверки не прошли — сайт не обновлён. Подробности: out\bin\2026\tests\RASv2.Tests.exe' }

        Write-Host 'Сборка сайта...'
        if (Test-Path $New) { Remove-Item $New -Recurse -Force }
        & dotnet publish $Project -c Release -o $New -nologo -v:q
        if ($LASTEXITCODE -ne 0) { throw 'Сайт не собрался — прежняя версия не тронута.' }
        $saved = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        $commit = & git -C $Root rev-parse --short HEAD 2>$null
        $ErrorActionPreference = $saved
        Set-Content -Path (Join-Path $New 'build.txt') -Encoding UTF8 -Value @(
            "Собрано: $(Get-Date -Format 'dd.MM.yyyy HH:mm')", "Коммит: $commit")

        Stop-Site
        Backup-Database
        if (Test-Path $Prev) { Remove-Item $Prev -Recurse -Force }
        if (Test-Path $App) { Rename-Item $App (Split-Path -Leaf $Prev) }
        Rename-Item $New (Split-Path -Leaf $App)
        Write-Host 'Программа сайта обновлена (вернуть прежнюю — site.cmd rollback).'
        Start-Site
    }
    'start' { Start-Site }
    'stop' { Stop-Site; Write-Host 'Сайт остановлен.' }
    'rollback' {
        if (-not (Test-Path $Prev)) { throw 'Прежней версии нет.' }
        Stop-Site
        $broken = Join-Path $SiteHome 'Программа.откаченная'
        if (Test-Path $broken) { Remove-Item $broken -Recurse -Force }
        if (Test-Path $App) { Rename-Item $App (Split-Path -Leaf $broken) }
        Rename-Item $Prev (Split-Path -Leaf $App)
        Write-Host 'Возвращена прежняя версия программы. База не менялась.'
        Start-Site
    }
    'autostart' {
        $startup = [Environment]::GetFolderPath('Startup')
        $link = Join-Path $startup 'RAS — сайт коллизий.lnk'
        $shell = New-Object -ComObject WScript.Shell
        $s = $shell.CreateShortcut($link)
        $s.TargetPath = $Exe
        $s.WorkingDirectory = $App
        $s.WindowStyle = 7
        $s.Description = 'Сайт RAS-Clash Analysis'
        $s.Save()
        Write-Host "Сайт будет запускаться при входе в Windows: $link (убрать — удалить этот ярлык)."
    }
}
