# Сборка и выпуск RAS v. 2.0 (PRINCIPLES.md, раздел 10).
# Запускается из build.cmd, publish.cmd, promote.cmd, rollback.cmd, publish-setup.cmd в корне.
#   build    — собрать под Revit 2023 и 2026, проверить, положить сборку в out\Releases и установить загрузчик себе
#   publish  — то же + выпуск на сервер в канал «тест» или «все»
#   promote  — перевести сборку канала «тест» на «всех» (без пересборки)
#   rollback — вернуть каналу одну из прежних сборок
#   setup    — собрать, проверить и выложить установку (install.cmd + загрузчик) в папку Setup на сервере
# Файл в UTF-8 с BOM: иначе Windows PowerShell 5.1 искажает русский текст.
param(
    [ValidateSet('build', 'publish', 'promote', 'rollback', 'setup')]
    [string]$Action = 'build',
    [ValidateSet('test', 'all')]
    [string]$Channel = 'test'
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$Root = Split-Path -Parent $PSScriptRoot
$Years = @(2023, 2026)
$ChannelNames = @{ test = 'тест'; all = 'все'; dev = 'разработка' }
$DevReleases = Join-Path $Root 'out\Releases'

# адрес сервера — из src\Shared\RasPaths.cs: одно место для кода и скрипта
$pathsFile = Join-Path $Root 'src\Shared\RasPaths.cs'
$serverMatch = Select-String -Path $pathsFile -Pattern 'ServerRoot\s*=\s*@"([^"]+)"' -Encoding UTF8 | Select-Object -First 1
if (-not $serverMatch) { throw "Не найден ServerRoot в $pathsFile" }
$ServerRoot = $serverMatch.Matches[0].Groups[1].Value
$ServerReleases = Join-Path $ServerRoot 'Releases'

function Invoke-Build {
    $projects = @()
    $projects += Get-ChildItem (Join-Path $Root 'src\Loader') -Filter *.csproj
    $projects += Get-ChildItem (Join-Path $Root 'src\Core') -Filter *.csproj
    $projects += Get-ChildItem (Join-Path $Root 'src\Modules') -Filter *.csproj -Recurse
    $projects += Get-ChildItem (Join-Path $Root 'tests') -Filter *.csproj -Recurse
    foreach ($year in $Years) {
        if (-not (Test-Path "C:\Program Files\Autodesk\Revit $year\RevitAPI.dll")) {
            throw "Не найден Revit $year (C:\Program Files\Autodesk\Revit $year): без него сборку под эту версию не собрать."
        }
        Write-Host "Сборка для Revit $year..."
        # папка частей собирается заново: в выпуск не должно попасть то, что осталось от удалённого модуля
        $parts = Join-Path $Root "out\bin\$year\parts"
        if (Test-Path $parts) { Remove-Item $parts -Recurse -Force }
        foreach ($project in $projects) {
            & dotnet build $project.FullName -c Release "-p:RevitVersion=$year" -nologo -v:q
            if ($LASTEXITCODE -ne 0) { throw "Сборка не удалась: $($project.Name) для Revit $year." }
        }
        Write-Host "Проверки для Revit $year..."
        foreach ($exe in Get-ChildItem (Join-Path $Root "out\bin\$year\tests") -Filter '*.Tests.exe') {
            & $exe.FullName
            if ($LASTEXITCODE -ne 0) { throw "Проверки не прошли ($($exe.Name), Revit $year) — сборка не выпускается." }
        }
    }
}

function New-BuildId { (Get-Date).ToString('yyyy.MM.dd-HHmmss') }

# Вызов git: его сообщения в stderr Windows PowerShell 5.1 при ErrorActionPreference=Stop считает ошибкой
function Invoke-Git {
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $out = & git -C $Root @args 2>$null
        if ($LASTEXITCODE -ne 0) { return $null }
        return $out
    }
    catch { return $null }
    finally { $ErrorActionPreference = $saved }
}

function Get-Commit {
    $commit = Invoke-Git rev-parse --short HEAD
    if (-not $commit) { return 'нет коммита' }
    if (Invoke-Git status --porcelain) { return "$commit + незафиксированные изменения" }
    return $commit
}

# Сборка → папка выпуска. Сначала во временную папку, потом переименование: недокопированная сборка не видна.
function Copy-Build([int]$year, [string]$target, [string]$id, [string]$notes) {
    $parts = Join-Path $Root "out\bin\$year\parts"
    $temp = "$target.tmp"
    if (Test-Path $temp) { Remove-Item $temp -Recurse -Force }
    New-Item -ItemType Directory -Path $temp -Force | Out-Null
    Get-ChildItem $parts -File | Where-Object { $_.Extension -in '.dll', '.pdb', '.json' -or $_.Name -eq 'ribbon.txt' } | Copy-Item -Destination $temp
    $info = @("Сборка: $id", "Revit: $year", "Коммит: $(Get-Commit)", "Собрал: $env:USERNAME, $(Get-Date -Format 'dd.MM.yyyy HH:mm')")
    Set-Content -Path (Join-Path $temp 'build.txt') -Value $info -Encoding UTF8
    if ($notes) { Set-Content -Path (Join-Path $temp 'notes.txt') -Value $notes -Encoding UTF8 }
    Rename-Item -Path $temp -NewName (Split-Path -Leaf $target)
}

# Указатель канала меняется одной заменой файла; история — в history.txt рядом.
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

function Remove-OldBuilds([string]$yearDir, [int]$keep) {
    # недокопированные остатки прерванных сборок
    Get-ChildItem $yearDir -Directory -Filter '*.tmp' | ForEach-Object { Remove-Item $_.FullName -Recurse -Force }
    $pointed = @('dev', 'test', 'all') | ForEach-Object { Get-Pointer $yearDir $_ }
    Get-ChildItem $yearDir -Directory | Sort-Object Name -Descending | Select-Object -Skip $keep |
        Where-Object { $pointed -notcontains $_.Name } | ForEach-Object { Remove-Item $_.FullName -Recurse -Force }
}

# Загрузчик себе (компьютер разработчика) и уборка прежних установок — тем же скриптом, что ставит плагин
# пользователям: одна логика установки. Загрузчик меняется редко: копируется, только если изменился.
function Install-DevLoader {
    & (Join-Path $Root 'setup\install.ps1') -Source (Join-Path $Root 'out\bin') -Inner 'loader' -Dev
}

function Set-DevConfig {
    $dir = Join-Path $env:APPDATA 'RAS_v_2.0'
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    $ini = @(
        '# Компьютер разработчика: загрузчик берёт сборки из папки исходников (build.cmd).',
        '# Удалите этот файл — сборки снова будут браться с сервера, как у всех.',
        "releases=$DevReleases",
        'channel=dev')
    Set-Content -Path (Join-Path $dir 'loader.ini') -Value $ini -Encoding UTF8
}

function Confirm([string]$question) {
    $answer = Read-Host "$question (да/нет)"
    return $answer -match '^\s*(да|д|yes|y)\s*$'
}

function Assert-Server {
    if (-not (Test-Path $ServerRoot)) { throw "Нет доступа к папке на сервере: $ServerRoot" }
}

try {
    switch ($Action) {
        'build' {
            Invoke-Build
            $id = New-BuildId
            foreach ($year in $Years) {
                $yearDir = Join-Path $DevReleases $year
                New-Item -ItemType Directory -Path $yearDir -Force | Out-Null
                Copy-Build $year (Join-Path $yearDir $id) $id $null
            }
            foreach ($year in $Years) {
                $yearDir = Join-Path $DevReleases $year
                Set-Pointer $yearDir 'dev' $id
                Remove-OldBuilds $yearDir 5
            }
            Install-DevLoader
            Set-DevConfig
            Write-Host ""
            Write-Host "Готово: сборка $id. Перезапустите Revit — подхватится она."
            Write-Host "На этом компьютере загрузчик берёт сборки из out\Releases (файл %AppData%\RAS_v_2.0\loader.ini)."
        }
        'publish' {
            Assert-Server
            Invoke-Build
            if (Invoke-Git status --porcelain) {
                Write-Warning 'Есть незафиксированные изменения: по сборке нельзя будет понять, какой в ней код.'
                if (-not (Confirm 'Всё равно выпустить?')) { Write-Host 'Выпуск отменён.'; exit 1 }
            }
            $notes = Read-Host 'Что нового — коротко, для людей'
            $id = New-BuildId
            # сначала сборки для всех версий Revit, потом указатели: выпуска «наполовину» не бывает
            foreach ($year in $Years) {
                $yearDir = Join-Path $ServerReleases $year
                New-Item -ItemType Directory -Path $yearDir -Force | Out-Null
                Copy-Build $year (Join-Path $yearDir $id) $id $notes
            }
            foreach ($year in $Years) {
                $yearDir = Join-Path $ServerReleases $year
                Set-Pointer $yearDir $Channel $id
                Remove-OldBuilds $yearDir 20
            }
            $testers = Join-Path $ServerRoot 'Config\test-users.txt'
            if (-not (Test-Path $testers)) {
                New-Item -ItemType Directory -Path (Split-Path $testers) -Force | Out-Null
                Set-Content -Path $testers -Value @('# Тестовая группа: имена пользователей Windows, по одному в строке.', $env:USERNAME) -Encoding UTF8
            }
            Write-Host ""
            Write-Host "Выпущено: сборка $id, канал «$($ChannelNames[$Channel])»."
            Write-Host 'Пользователям этого канала достаточно перезапустить Revit.'
            if ($Channel -eq 'test') { Write-Host "Тестовая группа: $testers. Проверили — promote.cmd переведёт эту сборку на всех." }
        }
        'setup' {
            Assert-Server
            Invoke-Build
            if (Invoke-Git status --porcelain) {
                Write-Warning 'Есть незафиксированные изменения: по установке нельзя будет понять, какой в ней код.'
                if (-not (Confirm 'Всё равно выложить?')) { Write-Host 'Отменено.'; exit 1 }
            }
            $setup = Join-Path $ServerRoot 'Setup'
            $changed = @($Years | Where-Object {
                $old = Join-Path $setup "$_\RASv2.Loader.dll"
                -not (Test-Path $old) -or (Get-FileHash $old).Hash -ne (Get-FileHash (Join-Path $Root "out\bin\$_\loader\RASv2.Loader.dll")).Hash
            })
            if ($changed.Count -eq 0) { Write-Host 'Загрузчик тот же, что на сервере: обновятся только скрипты установки.' }
            else {
                Write-Warning ("Загрузчик для Revit " + ($changed -join ', ') + " новый или изменился. Ошибка в загрузчике ломает плагин у всех, кто его поставит: " +
                    "сначала проверьте его у себя в каждой версии Revit (build.cmd ставит его вам). У тех, кто уже установил плагин, " +
                    "новый загрузчик появится только после повторного запуска install.cmd.")
            }
            if (-not (Confirm "Выложить установку в $setup?")) { Write-Host 'Отменено.'; exit 1 }
            # сначала во временную папку, потом замена: недокопированная установка не видна
            $temp = "$setup.tmp"
            if (Test-Path $temp) { Remove-Item $temp -Recurse -Force }
            New-Item -ItemType Directory -Path $temp -Force | Out-Null
            Copy-Item (Join-Path $Root 'setup\install.cmd'), (Join-Path $Root 'setup\install.ps1') -Destination $temp
            foreach ($year in $Years) {
                $dir = Join-Path $temp $year
                New-Item -ItemType Directory -Path $dir -Force | Out-Null
                Get-ChildItem (Join-Path $Root "out\bin\$year\loader") -File | Where-Object { $_.Extension -ne '.pdb' } | Copy-Item -Destination $dir
            }
            $info = @("Коммит: $(Get-Commit)", "Выложил: $env:USERNAME, $(Get-Date -Format 'dd.MM.yyyy HH:mm')")
            Set-Content -Path (Join-Path $temp 'build.txt') -Value $info -Encoding UTF8
            if (Test-Path $setup) { Remove-Item $setup -Recurse -Force }
            Rename-Item -Path $temp -NewName (Split-Path -Leaf $setup)
            Write-Host ""
            Write-Host "Установка выложена: $setup\install.cmd"
            Write-Host 'Коллеге: закрыть Revit, запустить install.cmd, открыть Revit. Ядро и модули загрузчик возьмёт из выпусков (publish.cmd).'
        }
        'promote' {
            Assert-Server
            $yearDir = Join-Path $ServerReleases $Years[-1]
            $id = Get-Pointer $yearDir 'test'
            if (-not $id) { throw 'В канале «тест» нет сборки.' }
            if (-not (Confirm "Перевести сборку $id на всех?")) { Write-Host 'Отменено.'; exit 1 }
            foreach ($year in $Years) {
                $dir = Join-Path $ServerReleases $year
                if (Test-Path (Join-Path $dir $id)) { Set-Pointer $dir 'all' $id }
                else { Write-Warning "Для Revit $year сборки $id нет — у этой версии всё по-прежнему." }
            }
            Write-Host "Сборка $id — у всех. Пользователям достаточно перезапустить Revit."
        }
        'rollback' {
            Assert-Server
            $yearDir = Join-Path $ServerReleases $Years[-1]
            $current = Get-Pointer $yearDir $Channel
            $builds = @(Get-ChildItem $yearDir -Directory | Where-Object { $_.Name -notlike '*.tmp' } | Sort-Object Name -Descending | Select-Object -First 10)
            if ($builds.Count -eq 0) { throw 'На сервере нет сборок.' }
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
            foreach ($year in $Years) {
                $dir = Join-Path $ServerReleases $year
                if (Test-Path (Join-Path $dir $id)) { Set-Pointer $dir $Channel $id }
            }
            Write-Host "Канал «$($ChannelNames[$Channel])» — сборка $id. Пользователям достаточно перезапустить Revit."
        }
    }
}
catch {
    Write-Host ""
    Write-Host ("Ошибка: " + $_.Exception.Message) -ForegroundColor Red
    exit 1
}
