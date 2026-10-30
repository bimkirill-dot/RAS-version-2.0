# Окно WPF модуля — вне Revit: создать, показать, снять PNG. Подключать из своего скрипта (scratchpad, файл с BOM):
#   powershell -NoProfile -STA -ExecutionPolicy Bypass -File мой-снимок.ps1
# В нём:
#   . '<репозиторий>\.claude\skills\new-button\window_shot.ps1'
#   $asm = Use-Parts 'RASv2.ClashAnalysis.dll'            # сборка модуля из out\bin\2023\parts, ссылки — оттуда и из Revit 2023
#   $w = New-Window $asm 'RASv2.ClashAnalysis.UI.ElementIdWindow' @(,$items)  # список — с запятой: @($items) PowerShell развернёт в элементы
#   Save-WindowPng $w 'C:\...\scratchpad\id.png'
# Падение в XAML (совпавший ключ стиля, нет ресурса) видно здесь сразу — в Revit оно роняет окно или весь Revit.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework

# сборка — из out репозитория, где лежит этот файл (у проверки ветки — из её рабочей папки); Revit не в стандартной папке —
# задать $env:RAS_REVIT2023 до подключения
$script:Parts = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..\out\bin\2023\parts'))
$script:RevitDir = if ($env:RAS_REVIT2023) { $env:RAS_REVIT2023 } else { 'C:\Program Files\Autodesk\Revit 2023' }

function Use-Parts([string]$dll) {
    [AppDomain]::CurrentDomain.add_AssemblyResolve({
        param($s, $e)
        $name = (New-Object Reflection.AssemblyName $e.Name).Name + '.dll'
        foreach ($d in @($script:Parts, $script:RevitDir)) {
            $p = Join-Path $d $name
            if (Test-Path $p) { return [Reflection.Assembly]::LoadFrom($p) }
        }
        return $null
    })
    return [Reflection.Assembly]::LoadFrom((Join-Path $script:Parts $dll))
}

# Конструктор — напрямую: PowerShell оборачивает списки в PSObject, и New-Object не находит подходящий конструктор.
# $null в строковое свойство PowerShell превращает в "" — где важно именно null, задавать в самом окне или через отражение.
function New-Window($asm, [string]$typeName, [object[]]$ctorArgs = @()) {
    $type = $asm.GetType($typeName, $true)
    $plain = [object[]]::new($ctorArgs.Count)
    for ($i = 0; $i -lt $ctorArgs.Count; $i++) {
        # без if-выражения: его вывод идёт через конвейер, и список развернулся бы в object[]
        $a = $ctorArgs[$i]
        if ($null -ne $a) { $a = $a.psobject.BaseObject }
        $plain[$i] = $a
    }
    $ctor = $type.GetConstructors() | Where-Object { $_.GetParameters().Count -eq $plain.Count } | Select-Object -First 1
    if (-not $ctor) { throw "У $typeName нет конструктора на $($plain.Count) аргумент(а)" }
    try { return $ctor.Invoke($plain) }
    catch { throw "Окно $typeName не создалось: " + $_.Exception.GetBaseException().Message }
}

function Save-WindowPng($window, [string]$path) {
    $window.Show()
    $window.UpdateLayout()
    [System.Windows.Threading.Dispatcher]::CurrentDispatcher.Invoke([Action]{}, [System.Windows.Threading.DispatcherPriority]::ApplicationIdle)
    $c = $window.Content
    # через VisualBrush: прямой Render($c) сдвигает картинку на отступ (Margin) содержимого и обрезает правый край
    $w = [int]$c.ActualWidth; $h = [int]$c.ActualHeight
    $dv = New-Object System.Windows.Media.DrawingVisual
    $dc = $dv.RenderOpen()
    $dc.DrawRectangle($window.Background, $null, (New-Object System.Windows.Rect 0, 0, $w, $h))
    $dc.DrawRectangle((New-Object System.Windows.Media.VisualBrush $c), $null, (New-Object System.Windows.Rect 0, 0, $w, $h))
    $dc.Close()
    $bmp = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $w, $h, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($dv)
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $fs = [IO.File]::Create($path)
    try { $enc.Save($fs) } finally { $fs.Close() }
    $window.Close()
    "снимок: $path"
}
