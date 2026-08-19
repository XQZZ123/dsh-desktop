# make-icon.ps1 — 从 DSH 网页的 favicon.svg 生成 DeepSeek 鲸鱼应用图标
#
# 纯离线：用 .NET Framework 自带 WPF 的 Geometry.Parse 直接解析 SVG 路径并栅格化，
# 渲染成「鲸鱼 + 透明背景」（颜色可用 -WhaleColor 指定，默认黑色）的
# 16/32/48/64/128/256 多尺寸 PNG，打包为 assets\app.ico（PNG 条目），
# 并输出 assets\whale-256.png 预览。
#
# 用法：  powershell -ExecutionPolicy Bypass -File make-icon.ps1
# 可选：  -SvgPath <favicon.svg 路径>   -OutDir <输出目录>   -WhaleColor '#000000'（鲸鱼颜色）

param(
    [string]$SvgPath = '',
    [string]$OutDir = '',
    [string]$WhaleColor = '#000000'
)

$ErrorActionPreference = 'Stop'

if ($OutDir -eq '') { $OutDir = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'assets' }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# ---------- 1. 定位 favicon.svg ----------
if ($SvgPath -eq '') {
    $cands = @(
        "$env:LOCALAPPDATA\npm-cache\_npx\*\node_modules\@deepseek-ai\dsh-web-frontend\dist\favicon.svg",
        "$env:USERPROFILE\.npm\_npx\*\node_modules\@deepseek-ai\dsh-web-frontend\dist\favicon.svg",
        "$env:USERPROFILE\AppData\Roaming\npm-cache\_npx\*\node_modules\@deepseek-ai\dsh-web-frontend\dist\favicon.svg"
    )
    # 兼容自定义 npm 缓存目录（如 .npmrc 中 cache=... 指向的目录）
    $npmCache = ''
    try { $npmCache = (npm config get cache 2>$null | Select-Object -First 1) } catch { }
    if ($npmCache) {
        $cands += "$npmCache\_npx\*\node_modules\@deepseek-ai\dsh-web-frontend\dist\favicon.svg"
    }
    foreach ($c in $cands) {
        $hit = Get-Item $c -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($hit) { $SvgPath = $hit.FullName; break }
    }
}
if ($SvgPath -eq '' -or -not (Test-Path $SvgPath)) {
    Write-Warning '未找到 favicon.svg，跳过图标生成。'
    exit 0
}
Write-Host "svg      : $SvgPath"
Copy-Item -Path $SvgPath -Destination (Join-Path $OutDir 'whale-source.svg') -Force

# ---------- 2. 提取路径数据 ----------
$text = Get-Content -Raw -Encoding UTF8 $SvgPath
$i = $text.IndexOf(' d="')
if ($i -lt 0) { throw 'SVG 中未找到 d 属性。' }
$j = $text.IndexOf('"', $i + 4)
$pathData = $text.Substring($i + 4, $j - $i - 4)

# ---------- 3. WPF 渲染 ----------
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

$geometry = [System.Windows.Media.StreamGeometry]::Parse($pathData)
$bounds = $geometry.Bounds

# 鲸鱼颜色：默认黑色（透明背景）
$whaleColor = [System.Windows.Media.Color]::FromRgb(0x00, 0x00, 0x00)
if ($WhaleColor -match '^#?([0-9A-Fa-f]{6})$') {
    $hex = $Matches[1]
    $whaleColor = [System.Windows.Media.Color]::FromRgb(
        [Convert]::ToInt32($hex.Substring(0, 2), 16),
        [Convert]::ToInt32($hex.Substring(2, 2), 16),
        [Convert]::ToInt32($hex.Substring(4, 2), 16))
}
$whaleBrush = [System.Windows.Media.SolidColorBrush]::new($whaleColor)

function New-IconPng {
    param([int]$Size)
    $canvas = [double]$Size
    # 鲸鱼：宽度占画布 92%（贴近 favicon 满版效果），等比缩放后居中，透明背景
    $target = $canvas * 0.92
    $scale  = $target / $bounds.Width
    $w = $bounds.Width * $scale
    $h = $bounds.Height * $scale
    $tx = ($canvas - $w) / 2 - $bounds.X * $scale
    $ty = ($canvas - $h) / 2 - $bounds.Y * $scale
    $tg = [System.Windows.Media.TransformGroup]::new()
    $tg.Children.Add([System.Windows.Media.ScaleTransform]::new($scale, $scale))
    $tg.Children.Add([System.Windows.Media.TranslateTransform]::new($tx, $ty))

    $dv = [System.Windows.Media.DrawingVisual]::new()
    $dc = $dv.RenderOpen()
    $dc.PushTransform($tg)
    $dc.DrawGeometry($whaleBrush, $null, $geometry)
    $dc.Pop()
    $dc.Close()

    $rtb = [System.Windows.Media.Imaging.RenderTargetBitmap]::new(
        $Size, $Size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $rtb.Render($dv)
    $enc = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($rtb))
    $ms = New-Object System.IO.MemoryStream
    $enc.Save($ms)
    Write-Output -NoEnumerate $ms.ToArray()
}

# ---------- 4. 多尺寸打包成 ICO（PNG 条目） ----------
$sizes = @(256, 128, 64, 48, 32, 16)
$pngs = @()
foreach ($s in $sizes) {
    $pngs += , (New-IconPng -Size $s)
}

$icoPath = Join-Path $OutDir 'app.ico'
$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter($fs)
try {
    $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($k = 0; $k -lt $sizes.Count; $k++) {
        $s = $sizes[$k]
        $d = if ($s -ge 256) { 0 } else { $s }
        $b = $pngs[$k]
        $bw.Write([byte]$d); $bw.Write([byte]$d)
        $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([uint16]1); $bw.Write([uint16]32)
        $bw.Write([uint32]$b.Length); $bw.Write([uint32]$offset)
        $offset += $b.Length
    }
    foreach ($b in $pngs) { $bw.Write([byte[]]$b) }
}
finally { $bw.Close(); $fs.Close() }

# ---------- 5. 预览 PNG ----------
$prev = Join-Path $OutDir 'whale-256.png'
$prevFs = [System.IO.File]::Create($prev)
$prevFs.Write([byte[]]$pngs[0], 0, $pngs[0].Length)
$prevFs.Close()

Write-Host "icon     : $icoPath"
Write-Host "preview  : $prev"
