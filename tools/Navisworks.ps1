# RAS v. 2.0 для Navisworks Manage 2023 — сборка и установка у разработчика (DECISIONS.md модуля RAS-Clash Analysis,
# «Navisworks: подстроить виды коллизий»). Отдельно от Revit: свой загрузчик (src\Navisworks\Loader — пакет .bundle),
# своя сборка (src\Modules\RAS-Clash Analysis\Navisworks), свои выпуски (Releases\Navisworks\2023), своя установка.
#   build — собрать, проверить, положить сборку в out\Releases\Navisworks (канал «разработка»), поставить себе пакет
#           (%AppData%\Autodesk\ApplicationPlugins\RASv2.Navisworks.bundle) и navisworks-loader.ini — Navisworks берёт сборку
#           из папки исходников. Прежний пакет RasGroup.ClashViewFixer (первая попытка, 09.2026) — убирается.
# Выпуск на сервер для коллег (publish / promote / rollback / setup) — после проверки прототипа.
# Файл — UTF-8 с BOM (Windows PowerShell 5.1).
param(
    [ValidateSet('build')]
    [string]$Action = 'build'
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$Year = 2023
$BundleName = 'RASv2.Navisworks.bundle'
$OutDir = Join-Path $Root "out\navisworks\$Year"
$DevReleases = Join-Path $Root 'out\Releases\Navisworks'
$Plugins = Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins'
$UserDir = Join-Path $env:APPDATA 'RAS_v_2.0'
$NavisworksExe = "C:\Program Files\Autodesk\Navisworks Manage $Year\Roamer.exe"

function Invoke-Build {
    if (-not (Test-Path $NavisworksExe)) { throw "Нет Navisworks Manage $Year — собирать не из чего (нужен его API)." }
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

function Get-Commit {
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $commit = & git -C $Root rev-parse --short HEAD 2>$null
    $dirty = & git -C $Root status --porcelain 2>$null
    $ErrorActionPreference = $saved
    if ($dirty) { return "$commit + незафиксированные изменения" }
    return $commit
}

# Сборка — в папку выпусков разработчика, указатель канала «dev»; хранятся 5 последних
function Copy-DevBuild {
    $id = (Get-Date).ToString('yyyy.MM.dd-HHmmss')
    $yearDir = Join-Path $DevReleases $Year
    $target = Join-Path $yearDir $id
    New-Item -ItemType Directory -Path "$target.tmp" -Force | Out-Null
    Get-ChildItem (Join-Path $OutDir 'build') -File | Where-Object { $_.Extension -in '.dll', '.pdb' } | Copy-Item -Destination "$target.tmp"
    Set-Content -Path (Join-Path "$target.tmp" 'build.txt') -Encoding UTF8 -Value @(
        "Сборка: $id", "Navisworks: $Year", "Коммит: $(Get-Commit)", "Собрал: $env:USERNAME, $(Get-Date -Format 'dd.MM.yyyy HH:mm')")
    Rename-Item "$target.tmp" (Split-Path -Leaf $target)
    [System.IO.File]::WriteAllText((Join-Path $yearDir 'dev.txt'), $id)
    Get-ChildItem $yearDir -Directory | Where-Object { $_.Name -match '^\d{4}\.\d{2}\.\d{2}-\d{6}$' } |
        Sort-Object Name -Descending | Select-Object -Skip 5 | Remove-Item -Recurse -Force
    return $id
}

# Пакет — себе, если он изменился (занят открытым Navisworks — сказать, что делать)
function Install-DevBundle {
    $source = Join-Path $OutDir $BundleName
    $target = Join-Path $Plugins $BundleName
    New-Item -ItemType Directory -Path $Plugins -Force | Out-Null
    $old = Join-Path $Plugins 'RasGroup.ClashViewFixer.bundle'
    if (Test-Path $old) {
        try { Remove-Item $old -Recurse -Force; Write-Host 'Убран прежний пакет RasGroup.ClashViewFixer (первая попытка; исходники — D:\Code\Navisworks-viewer).' }
        catch { Write-Warning 'Прежний пакет RasGroup.ClashViewFixer не убран: он занят открытым Navisworks. Закройте Navisworks и запустите navisworks.cmd ещё раз.' }
    }
    $same = (Test-Path $target) -and -not (Compare-Object `
        (Get-ChildItem $source -Recurse -File | Where-Object { $_.Extension -ne '.pdb' } | ForEach-Object { (Get-FileHash $_.FullName).Hash }) `
        (Get-ChildItem $target -Recurse -File | Where-Object { $_.Extension -ne '.pdb' } | ForEach-Object { (Get-FileHash $_.FullName).Hash }))
    if ($same) { Write-Host 'Пакет для Navisworks — тот же, что установлен.'; return }
    try {
        if (Test-Path $target) { Remove-Item $target -Recurse -Force }
        Copy-Item $source $target -Recurse
        Write-Host "Пакет для Navisworks установлен: $target"
    }
    catch {
        Write-Warning 'Пакет для Navisworks не установлен: он занят открытым Navisworks. Закройте Navisworks и запустите navisworks.cmd ещё раз.'
    }
}

function Set-DevConfig {
    New-Item -ItemType Directory -Path $UserDir -Force | Out-Null
    $ini = @(
        '# Компьютер разработчика: загрузчик для Navisworks берёт сборки из папки исходников (navisworks.cmd).',
        '# Удалите этот файл — сборки снова будут браться с сервера, как у всех.',
        "releases=$DevReleases",
        'channel=dev')
    Set-Content -Path (Join-Path $UserDir 'navisworks-loader.ini') -Value $ini -Encoding UTF8
}

switch ($Action) {
    'build' {
        Invoke-Build
        $id = Copy-DevBuild
        Install-DevBundle
        Set-DevConfig
        Write-Host ''
        Write-Host "Готово: сборка для Navisworks $id. Перезапустите Navisworks — на вкладке «RAS v. 2.0» кнопка «Подстроить виды»."
        Write-Host 'Сборка берётся из out\Releases\Navisworks (файл %AppData%\RAS_v_2.0\navisworks-loader.ini).'
    }
}
