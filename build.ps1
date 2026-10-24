param(
    [switch]$Test,
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$src  = Join-Path $root 'src'
$obj  = Join-Path $root 'obj'
$assets = Join-Path $root 'assets'

if ($Clean) {
    if (Test-Path $obj) { Remove-Item -Recurse -Force $obj }
    Write-Host "Cleaned build artifacts."
    return
}

New-Item -ItemType Directory -Force $obj | Out-Null
New-Item -ItemType Directory -Force $assets | Out-Null

$fw  = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$wpf = "$fw\WPF"

# ---- icon ----
$icoFile = Join-Path $assets 'app.ico'
if (-not (Test-Path $icoFile)) {
    Add-Type -AssemblyName System.Drawing
    function Png([int]$n) {
        $b = New-Object System.Drawing.Bitmap $n, $n
        $g = [System.Drawing.Graphics]::FromImage($b)
        $g.SmoothingMode = 'AntiAlias'
        $g.Clear([System.Drawing.Color]::Transparent)
        $d = $n * 0.44; $w = $n - 1
        $p = New-Object System.Drawing.Drawing2D.GraphicsPath
        $p.AddArc(0, 0, $d, $d, 180, 90); $p.AddArc($w - $d, 0, $d, $d, 270, 90)
        $p.AddArc($w - $d, $w - $d, $d, $d, 0, 90); $p.AddArc(0, $w - $d, $d, $d, 90, 90); $p.CloseFigure()
        $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 255, 106, 0))), $p)
        $pw = [Math]::Max(1.6, $n * 0.11); $m = $n * 0.25; $s = $n - 2 * $m
        $g.DrawEllipse((New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(80, 255, 255, 255)), $pw), $m, $m, $s, $s)
        $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), $pw
        $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
        $g.DrawArc($pen, $m, $m, $s, $s, -90, 250)
        $g.Dispose()
        $ms = New-Object IO.MemoryStream
        $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $b.Dispose()
        return ,$ms.ToArray()
    }
    $sizes = 16, 24, 32, 48, 256
    $imgs = @(); foreach ($sz in $sizes) { $imgs += ,(Png $sz) }
    $ms = New-Object IO.MemoryStream; $bw = New-Object IO.BinaryWriter $ms
    $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
    $off = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $sz = $sizes[$i] % 256
        $bw.Write([byte]$sz); $bw.Write([byte]$sz); $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$imgs[$i].Length); $bw.Write([uint32]$off)
        $off += $imgs[$i].Length
    }
    foreach ($im in $imgs) { $bw.Write($im) }
    [IO.File]::WriteAllBytes($icoFile, $ms.ToArray())
}
Copy-Item -Path $icoFile -Destination "$obj\app.ico" -Force

# ---- manifest ----
$level = if ($Test) { 'asInvoker' } else { 'highestAvailable' }
$manifestSource = if (Test-Path "$src\app.manifest") { "$src\app.manifest" } else { "$src\app.manifest.xml" }
(Get-Content $manifestSource -Raw).Replace('LEVEL', $level) | Set-Content "$obj\app.manifest" -Encoding UTF8

# ---- compile ----
$out = if ($Test) { "$obj\CleanerRAM_test.exe" } else { "$root\CleanerRAM.exe" }
& "$fw\csc.exe" /nologo /codepage:65001 /target:winexe /platform:x64 /optimize+ "/out:$out" `
    "/win32icon:$obj\app.ico" "/win32manifest:$obj\app.manifest" "/resource:$src\ui.xaml,ui.xaml" `
    "/r:$wpf\PresentationFramework.dll" "/r:$wpf\PresentationCore.dll" "/r:$wpf\WindowsBase.dll" `
    "/r:$fw\System.Xaml.dll" /r:System.Windows.Forms.dll /r:System.Drawing.dll "$src\App.cs" "$src\UI.cs"
if ($LASTEXITCODE) { throw "Build failed with exit code $LASTEXITCODE" }
Write-Host "Build successful: $out" -ForegroundColor Green
