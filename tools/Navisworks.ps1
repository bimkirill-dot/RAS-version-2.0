# RAS v. 2.0 для Navisworks Manage 2023 — сборка, выпуск и установка (src\Navisworks\README.md). Отдельно от Revit:
# свой загрузчик-пакет (src\Navisworks\Loader), своя сборка (src\Modules\RAS-Clash Analysis\Navisworks), свои выпуски
# (Releases\Navisworks\2023 на сервере: каналы «тест» и «все», тестовая группа — та же, Config\test-users.txt) и своя установка
# (Setup\Navisworks: install-navisworks.cmd + пакет).
#   build    — собрать, проверить, сборка — в out\Releases\Navisworks (канал «разработка»), пакет — себе (setup\install-navisworks.ps1 -Dev)
#   publish  — то же + выпуск на сервер в канал -Channel test | all (all переводит и «тест»); спрашивает «что нового»
#   promote  — сборку «теста» — всем
#   rollback — вернуть каналу -Channel прежнюю сборку
#   status   — что сейчас у каналов (ничего не меняет)
#   setup    — выложить установку в Setup\Navisworks (только когда изменился пакет: кнопки, иконки)
# Файл — UTF-8 с BOM (Windows PowerShell 5.1).
param(
    [ValidateSet('build', 'publish', 'promote', 'rollback', 'status', 'setup')]
    [string]$Action = 'build',
    [ValidateSet('test', 'all')]
    [string]$Channel = 'test'
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$Root = Split-Path -Parent $PSScriptRoot
$Year = 2023
$BundleName = 'RASv2.Navisworks.bundle'
$OutDir = Join-Path $Root "out\navisworks\$Year"
$DevReleases = Join-Path $Root 'out\Releases\Navisworks'
$UserDir = Join-Path $env:APPDATA 'RAS_v_2.0'
$ChannelNames = @{ test = 'тест'; all = 'все'; dev = 'разработка' }

# адрес сервера — из src\Shared\RasPaths.cs: одно место для кода и скриптов (как в tools\Release.ps1)
$pathsFile = Join-Path $Root 'src\Shared\RasPaths.cs'
$serverMatch = Select-String -Path $pathsFile -Pattern 'ServerRoot\s*=\s*@"([^"]+)"' -Encoding UTF8 | Select-Object -First 1
if (-not $serverMatch) { throw "Не найден ServerRoot в $pathsFile" }
$ServerRoot = $serverMatch.Matches[0].Groups[1].Value
$ServerYear = Join-Path $ServerRoot "Releases\Navisworks\$Year"
$ServerSetup = Join-Path $ServerRoot 'Setup\Navisworks'

function Assert-Server { if (-not (Test-Path $ServerRoot)) { throw "Нет доступа к папке на сервере: $ServerRoot" } }

function Confirm([string]$question) {
    $answer = Read-Host "$question (да/нет)"
    return $answer -match '^\s*(да|д|yes|y)\s*$'
}

function Invoke-Git {
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { return & git -C $Root @args 2>$null }
    finally { $ErrorActionPreference = $saved }
}

function Get-Commit {
    $commit = Invoke-Git rev-parse --short HEAD
    if (Invoke-Git status --porcelain) { return "$commit + незафиксированные изменения" }
    return $commit
}

function Invoke-Build {
    if (-not (Test-Path "C:\Program Files\Autodesk\Navisworks Manage $Year\Roamer.exe")) { throw "Нет Navisworks Manage $Year — собирать не из чего (нужен его API)." }
    $projects = @(
        (Join-Path $Root 'src\Navisworks\Loader\RASv2.Navisworks.Loader.csproj'),
        (Join-Path $Root 'src\Modules\RAS-Clash Analysis\Navisworks\RASv2.ClashAnalysis.Navisworks.csproj'))
    foreach ($p in $projects) {
        & dotnet build $p -c Release -nologo -v:q
        if ($LASTEXITCODE -ne 0) { throw "Не собрался $(Split-Path -Leaf $p)." }
    }
    # проверки логики — общие (tests\RASv2.Tests), сборка .NET Framework 4.8, как у Navisworks
    & dotnet build (Join-Path $Root 'tests\RASv2.Tests\RASv2.Tests.csproj') -c Release -p:RevitVersion=2023 -nologo -v:q
    if ($LASTEXITCODE -ne 0) { throw 'Проверки не собрались.' }
    & (Join-Path $Root 'out\bin\2023\tests\RASv2.Tests.exe') | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Проверки не прошли — подробности: out\bin\2023\tests\RASv2.Tests.exe' }
}

function New-BuildId { (Get-Date).ToString('yyyy.MM.dd-HHmmss') }

# Сборка → папка выпусков: сначала во временную папку, потом переименование — недокопированная сборка не видна
function Copy-Build([string]$yearDir, [string]$id, [string]$notes) {
    $target = Join-Path $yearDir $id
    $temp = "$target.tmp"
    if (Test-Path $temp) { Remove-Item $temp -Recurse -Force }
    New-Item -ItemType Directory -Path $temp -Force | Out-Null
    Get-ChildItem (Join-Path $OutDir 'build') -File | Where-Object { $_.Extension -in '.dll', '.pdb' } | Copy-Item -Destination $temp
    Set-Content -Path (Join-Path $temp 'build.txt') -Encoding UTF8 -Value @(
        "Сборка: $id", "Navisworks: $Year", "Коммит: $(Get-Commit)", "Собрал: $env:USERNAME, $(Get-Date -Format 'dd.MM.yyyy HH:mm')")
    if ($notes) { Set-Content -Path (Join-Path $temp 'notes.txt') -Value $notes -Encoding UTF8 }
    Rename-Item -Path $temp -NewName $id
}

# Указатель канала — одной заменой файла; история — в history.txt рядом
function Set-Pointer([string]$yearDir, [string]$channel, [string]$id) {
    $file = Join-Path $yearDir "$channel.txt"
    [System.IO.File]::WriteAllText("$file.tmp", $id)
    Move-Item -Path "$file.tmp" -Destination $file -Force
    Add-Content -Path (Join-Path $yearDir 'history.txt') -Value "$(Get-Date -Format 'dd.MM.yyyy HH:mm') $channel $id $env:USERNAME" -Encoding UTF8
}

function Get-Pointer([string]$yearDir, [string]$channel) {
    $file = Join-Path $yearDir "$channel.txt"
    if (-not (Test-Path $file)) { return $null }
    return (Get-Content $file -Encoding UTF8 | Where-Object { $_.Trim() } | Select-Object -First 1).Trim()
}

function Get-BuildText([string]$yearDir, [string]$id) {
    if (-not $id) { return $null }
    $when = $id
    $date = [datetime]::MinValue
    if ([datetime]::TryParseExact($id, 'yyyy.MM.dd-HHmmss', $null, 'None', [ref]$date)) { $when = $date.ToString('dd.MM.yyyy HH:mm') }
    $dir = Join-Path $yearDir $id
    if (-not (Test-Path $dir)) { return "$id — сборки нет на сервере!" }
    $notesFile = Join-Path $dir 'notes.txt'
    $notes = if (Test-Path $notesFile) { ((Get-Content $notesFile -Encoding UTF8) -join ' ').Trim() } else { '' }
    if (-not $notes) { $notes = '(без описания)' }
    return "$when — $notes   [$id]"
}

function Remove-OldBuilds([string]$yearDir, [int]$keep) {
    Get-ChildItem $yearDir -Directory -Filter '*.tmp' | ForEach-Object { Remove-Item $_.FullName -Recurse -Force }
    $pointed = @('dev', 'test', 'all') | ForEach-Object { Get-Pointer $yearDir $_ }
    Get-ChildItem $yearDir -Directory | Where-Object { $_.Name -match '^\d{4}\.\d{2}\.\d{2}-\d{6}$' } | Sort-Object Name -Descending |
        Select-Object -Skip $keep | Where-Object { $pointed -notcontains $_.Name } | ForEach-Object { Remove-Item $_.FullName -Recurse -Force }
}

# Пакет себе — тем же скриптом, что ставит его коллегам (одна логика установки)
function Install-DevBundle { & (Join-Path $Root 'setup\install-navisworks.ps1') -Source $OutDir -Dev }

function Set-DevConfig {
    New-Item -ItemType Directory -Path $UserDir -Force | Out-Null
    Set-Content -Path (Join-Path $UserDir 'navisworks-loader.ini') -Encoding UTF8 -Value @(
        '# Компьютер разработчика: загрузчик для Navisworks берёт сборки из папки исходников (navisworks.cmd).',
        '# Удалите этот файл — сборки снова будут браться с сервера, как у всех.',
        "releases=$DevReleases",
        'channel=dev')
}

try {
    switch ($Action) {
        'build' {
            Invoke-Build
            $yearDir = Join-Path $DevReleases $Year
            New-Item -ItemType Directory -Path $yearDir -Force | Out-Null
            $id = New-BuildId
            Copy-Build $yearDir $id $null
            [System.IO.File]::WriteAllText((Join-Path $yearDir 'dev.txt'), $id)
            Remove-OldBuilds $yearDir 5
            Install-DevBundle
            Set-DevConfig
            Write-Host ''
            Write-Host "Готово: сборка для Navisworks $id. Перезапустите Navisworks — на вкладке «RAS v. 2.0» кнопка «Подстроить виды»."
            Write-Host 'Сборка берётся из out\Releases\Navisworks (файл %AppData%\RAS_v_2.0\navisworks-loader.ini).'
        }
        'publish' {
            Assert-Server
            Invoke-Build
            if (Invoke-Git status --porcelain) {
                Write-Warning 'Есть незафиксированные изменения: по сборке нельзя будет понять, какой в ней код.'
                if (-not (Confirm 'Всё равно выпустить?')) { Write-Host 'Выпуск отменён.'; exit 1 }
            }
            if (-not (Test-Path (Join-Path $ServerSetup "$BundleName\PackageContents.xml"))) {
                Write-Warning 'На сервере ещё нет установки для Navisworks — коллегам нечем поставить пакет. Выложите её: navisworks.cmd → «установка».'
            }
            $notes = Read-Host 'Что нового — коротко, для людей'
            New-Item -ItemType Directory -Path $ServerYear -Force | Out-Null
            $id = New-BuildId
            Copy-Build $ServerYear $id $notes
            Set-Pointer $ServerYear $Channel $id
            # выпуск сразу всем — и тестовой группе: она берёт «тест», и старая сборка там оставила бы её позади всех
            if ($Channel -eq 'all') { Set-Pointer $ServerYear 'test' $id }
            Remove-OldBuilds $ServerYear 20
            Write-Host ''
            Write-Host "Выпущено: сборка для Navisworks $id, канал «$($ChannelNames[$Channel])»."
            if ($Channel -eq 'all') { Write-Host 'Канал «тест» переведён на ту же сборку.' }
            Write-Host 'Пользователям достаточно перезапустить Navisworks.'
            if ($Channel -eq 'test') { Write-Host 'Проверили — navisworks.cmd → «тест — всем».' }
        }
        'promote' {
            Assert-Server
            $id = Get-Pointer $ServerYear 'test'
            if (-not $id) { throw 'В канале «тест» нет сборки.' }
            if (-not (Confirm "Перевести сборку для Navisworks $id на всех?")) { Write-Host 'Отменено.'; exit 1 }
            Set-Pointer $ServerYear 'all' $id
            Write-Host "Сборка $id — у всех. Пользователям достаточно перезапустить Navisworks."
        }
        'rollback' {
            Assert-Server
            $current = Get-Pointer $ServerYear $Channel
            $builds = @(Get-ChildItem $ServerYear -Directory | Where-Object { $_.Name -notlike '*.tmp' } | Sort-Object Name -Descending | Select-Object -First 10)
            if ($builds.Count -eq 0) { throw 'На сервере нет сборок для Navisworks.' }
            Write-Host "Канал «$($ChannelNames[$Channel])», сейчас: $current"
            for ($i = 0; $i -lt $builds.Count; $i++) {
                $notesFile = Join-Path $builds[$i].FullName 'notes.txt'
                $notes = if (Test-Path $notesFile) { (Get-Content $notesFile -Encoding UTF8) -join ' ' } else { '' }
                $mark = if ($builds[$i].Name -eq $current) { ' ← сейчас' } else { '' }
                Write-Host ("  {0}. {1}  {2}{3}" -f ($i + 1), $builds[$i].Name, $notes, $mark)
            }
            $choice = Read-Host 'Номер сборки, которую вернуть'
            $index = 0
            if (-not [int]::TryParse($choice, [ref]$index) -or $index -lt 1 -or $index -gt $builds.Count) { Write-Host 'Отменено.'; exit 1 }
            $id = $builds[$index - 1].Name
            Set-Pointer $ServerYear $Channel $id
            Write-Host "Канал «$($ChannelNames[$Channel])» — сборка $id. Пользователям достаточно перезапустить Navisworks."
        }
        'status' {
            Assert-Server
            Write-Host ''
            Write-Host "Navisworks $Year" -ForegroundColor Cyan
            $all = Get-Pointer $ServerYear 'all'
            $test = Get-Pointer $ServerYear 'test'
            $allText = Get-BuildText $ServerYear $all
            Write-Host ('  «все»  : ' + $(if ($allText) { $allText } else { 'ничего не выпущено' }))
            if (-not $test) { Write-Host '  «тест» : своей версии нет — тестовая группа берёт ту же, что у всех' }
            elseif ($test -eq $all) { Write-Host '  «тест» : та же версия, что у всех' }
            else { Write-Host ('  «тест» : ' + (Get-BuildText $ServerYear $test) + '   ← отличается от «все»') -ForegroundColor Yellow }
            $history = Join-Path $ServerYear 'history.txt'
            if (Test-Path $history) {
                Write-Host '  последние выпуски:'
                Get-Content $history -Encoding UTF8 | Select-Object -Last 5 | ForEach-Object {
                    Write-Host ('    ' + ($_ -replace ' test ', ' «тест» ' -replace ' all ', ' «все» '))
                }
            }
            $setupInfo = Join-Path $ServerSetup 'build.txt'
            Write-Host ('  установка: ' + $(if (Test-Path $setupInfo) { ((Get-Content $setupInfo -Encoding UTF8) -join '; ') } else { 'не выложена' }))
            if (Test-Path (Join-Path $UserDir 'navisworks-loader.ini')) {
                Write-Host 'Ваш Navisworks берёт сборку не с сервера, а из папки проекта (navisworks-loader.ini, режим «разработка»).'
            }
        }
        'setup' {
            Assert-Server
            Invoke-Build
            if (Invoke-Git status --porcelain) {
                Write-Warning 'Есть незафиксированные изменения: по установке нельзя будет понять, какой в ней код.'
                if (-not (Confirm 'Всё равно выложить?')) { Write-Host 'Отменено.'; exit 1 }
            }
            $source = Join-Path $OutDir $BundleName
            $old = Join-Path $ServerSetup $BundleName
            $same = (Test-Path $old) -and -not (Compare-Object `
                @(Get-ChildItem $source -Recurse -File | Where-Object { $_.Extension -ne '.pdb' } | ForEach-Object { (Get-FileHash $_.FullName).Hash }) `
                @(Get-ChildItem $old -Recurse -File | Where-Object { $_.Extension -ne '.pdb' } | ForEach-Object { (Get-FileHash $_.FullName).Hash }))
            if ($same) { Write-Host 'Пакет тот же, что на сервере: обновятся только скрипты установки.' }
            else {
                Write-Warning ('Пакет для Navisworks новый или изменился. Ошибка в загрузчике ломает вкладку у всех, кто его поставит: ' +
                    'сначала проверьте его у себя (navisworks.cmd ставит его вам). У тех, кто уже установил, новый пакет — только после ' +
                    'повторного install-navisworks.cmd.')
            }
            if (-not (Confirm "Выложить установку в ${ServerSetup}?")) { Write-Host 'Отменено.'; exit 1 }
            # сначала во временную папку, потом замена: недокопированная установка не видна
            $temp = "$ServerSetup.tmp"
            if (Test-Path $temp) { Remove-Item $temp -Recurse -Force }
            New-Item -ItemType Directory -Path $temp -Force | Out-Null
            Copy-Item (Join-Path $Root 'setup\install-navisworks.cmd'), (Join-Path $Root 'setup\install-navisworks.ps1') -Destination $temp
            Copy-Item $source (Join-Path $temp $BundleName) -Recurse
            Get-ChildItem (Join-Path $temp $BundleName) -Recurse -Filter '*.pdb' | Remove-Item -Force
            Set-Content -Path (Join-Path $temp 'build.txt') -Encoding UTF8 -Value @("Коммит: $(Get-Commit)", "Выложил: $env:USERNAME, $(Get-Date -Format 'dd.MM.yyyy HH:mm')")
            if (Test-Path $ServerSetup) { Remove-Item $ServerSetup -Recurse -Force }
            Rename-Item -Path $temp -NewName (Split-Path -Leaf $ServerSetup)
            Write-Host ''
            Write-Host "Установка выложена: $ServerSetup\install-navisworks.cmd"
            Write-Host 'Коллеге: закрыть Navisworks, запустить install-navisworks.cmd, открыть Navisworks. Сборку пакет возьмёт из выпусков (navisworks.cmd → «выпустить»).'
        }
    }
}
catch {
    Write-Host ''
    Write-Host ('Ошибка: ' + $_.Exception.Message) -ForegroundColor Red
    exit 1
}
