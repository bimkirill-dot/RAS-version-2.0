# Сайт RAS-Clash Analysis (PRINCIPLES.md, «Сайт модуля» и раздел 10, «Сайт»). Запускается из site.cmd в корне.
#   update    — собрать, проверить, сделать копию базы, заменить программу, запустить (по умолчанию)
#   start     — запустить, если не запущен, и открыть в браузере
#   stop      — остановить
#   rollback  — вернуть прежнюю версию программы (база не меняется)
#   firewall  — открыть порт сайта коллегам в брандмауэре Windows (один раз; спросит права администратора)
#   autostart — запускать сайт при входе в Windows (ярлык в «Автозагрузке»; без прав администратора)
# Сайт работает на этом компьютере: программа и данные — %LocalAppData%\RAS_v_2.0\RAS-Clash Analysis\Site.
# Файл в UTF-8 с BOM: иначе Windows PowerShell 5.1 искажает русский текст.
param(
    [ValidateSet('update', 'start', 'stop', 'rollback', 'autostart', 'firewall')]
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
    # «доступен по сети» (страница «Настройки» сайта) — http://*:порт: браузер открываем на этом же компьютере
    return ($url -replace '://(\*|\+|0\.0\.0\.0):', '://localhost:')
}

# только живой процесс: только что остановленный ещё виден в списке, пока на него есть ссылки
function Get-SiteProcess { Get-Process -Name $ExeName -ErrorAction SilentlyContinue | Where-Object { -not $_.HasExited } }

# Строка в журнал сайта: остановка через site.cmd — «снаружи», сам сайт о ней не напишет (ExitWatch.cs)
function Write-SiteLog([string]$text) {
    try {
        $folder = Join-Path $SiteHome 'Журнал'
        New-Item -ItemType Directory -Force $folder | Out-Null
        $line = (Get-Date).ToString('dd.MM.yyyy HH:mm:ss') + '        Site.ps1: ' + $text + "`r`n"
        [IO.File]::AppendAllText((Join-Path $folder ('site-' + (Get-Date).ToString('yyyy-MM') + '.log')), $line, (New-Object Text.UTF8Encoding($false)))
    } catch { }
}

function Stop-Site {
    $p = Get-SiteProcess
    if (-not $p) { return }
    Write-Host 'Останавливаем сайт...'
    Write-SiteLog "site.cmd ($Action): сайт остановлен (процесс $($p.Id -join ', '))"
    $p | Stop-Process -Force
    $p | Wait-Process -Timeout 20 -ErrorAction SilentlyContinue
    for ($i = 0; $i -lt 20 -and (Get-SiteProcess); $i++) { Start-Sleep -Milliseconds 500 }
    if (Get-SiteProcess) { throw 'Сайт не остановился — повторите; не поможет — завершите RASv2.ClashAnalysis.Site в диспетчере задач.' }
}

function Start-Site {
    if (Get-SiteProcess) { Write-Host 'Сайт уже запущен.' }
    elseif (-not (Test-Path $Exe)) { throw "Сайт ещё не установлен ($Exe). Запустите site.cmd без параметров." }
    else {
        Write-Host 'Запускаем сайт...'
        # без окна: свёрнутое окно сайта принимали за забытую командную строку и закрывали — сайт останавливался
        # (12.10.2026, ExitWatch: «окно закрыто»). Журнал — в файле; остановить — site.cmd stop.
        # Запуск — через WMI, а не Start-Process: так сайт не дочерний процесс того, кто запустил команду, и не
        # закрывается вместе с ним (Claude Code держит свои команды в «задании» Windows: запущенный оттуда сайт
        # закрылся бы при перезапуске Claude Code)
        $startup = New-CimInstance -ClassName Win32_ProcessStartup -ClientOnly -Property @{ ShowWindow = [uint16]0 }
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

# Копия базы перед заменой программы: сайт остановлен, база (вместе с журналом WAL) — архивом в «Копии базы\Перед
# изменениями», туда же, где копии перед переносом базы и удалением проекта; хранятся последние 10 (DECISIONS.md модуля,
# «Копии базы — раз в день, лесенкой»). Только сама база: до 21.10.2026 сюда попадали и все прежние копии рядом с ней
function Backup-Database {
    $db = Join-Path $SiteHome 'clashes.db'
    if (-not (Test-Path $db)) { return }
    $folder = Join-Path $SiteHome 'Копии базы\Перед изменениями'
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    $zip = Join-Path $folder ((Get-Date -Format 'yyyy-MM-dd HHmmss') + ' перед обновлением.zip')
    $files = Get-ChildItem $SiteHome -File | Where-Object { @('clashes.db', 'clashes.db-wal', 'clashes.db-shm') -contains $_.Name }
    Compress-Archive -Path ($files | ForEach-Object { $_.FullName }) -DestinationPath $zip -CompressionLevel Optimal
    Write-Host "Копия базы: $zip"
    Get-ChildItem $folder -File -Filter '*.zip' | Sort-Object Name -Descending | Select-Object -Skip 10 | Remove-Item -Force
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
    'firewall' {
        # коллегам по сети сайт открывается, только если брандмауэр пропускает его порт: правило — один раз, с правами
        # администратора (Windows спросит подтверждение); профили «Домен» и «Частная» — не «Публичная» (чужие сети)
        $port = ([Uri](Get-SiteUrl)).Port
        $name = "RAS — сайт коллизий ($port)"
        $command = "if (-not (Get-NetFirewallRule -DisplayName '$name' -ErrorAction SilentlyContinue)) { New-NetFirewallRule -DisplayName '$name' -Direction Inbound -Protocol TCP -LocalPort $port -Action Allow -Profile Domain,Private | Out-Null }"
        try {
            $p = Start-Process powershell -Verb RunAs -Wait -PassThru -ArgumentList '-NoProfile', '-Command', $command
            if ($p.ExitCode -eq 0) { Write-Host "Порт $port открыт для коллег (правило «$name» в брандмауэре Windows)." }
            else { Write-Warning "Правило не добавлено (код $($p.ExitCode))." }
        }
        catch { Write-Warning 'Правило не добавлено: нужны права администратора (подтверждение Windows отменено).' }
    }
    'autostart' {
        $startup = [Environment]::GetFolderPath('Startup')
        $link = Join-Path $startup 'RAS — сайт коллизий.lnk'
        $shell = New-Object -ComObject WScript.Shell
        $s = $shell.CreateShortcut($link)
        # тоже без окна (см. Start-Site): PowerShell на миг, он запускает сайт скрытым
        $s.TargetPath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $s.Arguments = "-NoProfile -WindowStyle Hidden -Command ""Start-Process -FilePath '$Exe' -WorkingDirectory '$App' -WindowStyle Hidden"""
        $s.WorkingDirectory = $App
        $s.WindowStyle = 7
        $s.Description = 'Сайт RAS-Clash Analysis'
        $s.Save()
        Write-Host "Сайт будет запускаться при входе в Windows: $link (убрать — удалить этот ярлык)."
    }
}
