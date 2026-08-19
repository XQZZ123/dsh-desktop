# build.ps1 — 离线构建 DSH 桌面版（无需网络、无需 dotnet SDK）
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File build.ps1
#   powershell -ExecutionPolicy Bypass -File build.ps1 -MakeShortcut   # 同时在桌面创建快捷方式
#   powershell -ExecutionPolicy Bypass -File build.ps1 -WebView2Core "C:\path\Microsoft.Web.WebView2.Core.dll" ...
#       # 显式指定 WebView2 程序集来源（不指定则自动搜索常用目录）
#
# 产出：bin\DshDesktop.exe 及配套 WebView2 DLL，双击即可运行。
#
# 原理：
#   * 用 Windows 自带的 .NET Framework csc.exe（C# 5）编译 WinForms 程序；
#   * 从本机已安装应用（OfficePLUS / WSL / NuGet 缓存等）离线复制 WebView2 托管程序集
#     与原生加载器，因此不依赖 NuGet 网络。
#   * 若本机找不到 csc 或 WebView2 程序集，脚本会报错并给出提示。

param(
    [switch]$MakeShortcut,
    [string]$WebView2Core = '',
    [string]$WebView2WinForms = '',
    [string]$WebView2Loader = ''
)

$ErrorActionPreference = 'Stop'
$root   = Split-Path -Parent $MyInvocation.MyCommand.Path
$srcDir = Join-Path $root 'src'
$binDir = Join-Path $root 'bin'
$exeOut = Join-Path $binDir 'DshDesktop.exe'

New-Item -ItemType Directory -Force -Path $binDir | Out-Null

# ---------- 1. 定位 csc.exe（.NET Framework 编译器） ----------
$fwRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $fwRoot 'csc.exe'
if (-not (Test-Path $csc)) {
    $fwRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
    $csc = Join-Path $fwRoot 'csc.exe'
}
if (-not (Test-Path $csc)) {
    throw "未找到 csc.exe（.NET Framework 编译器）。请在安装了 .NET Framework 4.8 的 Windows 上运行本脚本。"
}
Write-Host "csc      : $csc"

# ---------- 2. 离线复制 WebView2 程序集 ----------
# 托管程序集（Core + WinForms）与原生加载器（WebView2Loader.dll）从常用安装目录
# 中搜索最新版本；也可用 -WebView2Core / -WebView2WinForms / -WebView2Loader 显式指定。
# 常见来源：OfficePLUS、WSL、NuGet 缓存（Microsoft.Web.WebView2 包）。
$webView2SearchRoots = @(
    (Join-Path $env:ProgramFiles 'Microsoft OfficePLUS'),
    (Join-Path $env:ProgramFiles 'WSL'),
    (Join-Path $env:ProgramFiles 'Microsoft Office\root\Office16\WritingAssistant'),
    (Join-Path $env:USERPROFILE '.nuget\packages\microsoft.web.webview2')
)

function Copy-Latest {
    param([string]$Override, [string[]]$Roots, [string]$Filter, [string]$OutName)
    if ($Override -ne '' -and (Test-Path $Override)) {
        Copy-Item -Path $Override -Destination (Join-Path $binDir $OutName) -Force
        Write-Host ("copy     : {0}  <-  {1}（显式指定）" -f $OutName, $Override)
        return
    }
    foreach ($r in $Roots) {
        if (-not (Test-Path $r)) { continue }
        $hits = Get-ChildItem -Path $r -Filter $Filter -Recurse -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -notmatch 'WinSxS' }
        if (-not $hits) { continue }
        $best = $hits | Sort-Object {
            try { [version]($_.Directory.Name -replace '[^0-9.]+', '') } catch { [version]'0.0' }
        } -Descending | Select-Object -First 1
        Copy-Item -Path $best.FullName -Destination (Join-Path $binDir $OutName) -Force
        Write-Host ("copy     : {0}  <-  {1}" -f $OutName, $best.FullName)
        return
    }
    throw "未找到 $Filter。请安装包含 WebView2 程序集的应用（如 Microsoft 365 / WSL），或用 -WebView2Core / -WebView2WinForms / -WebView2Loader 显式指定其路径。"
}

Copy-Latest -Override $WebView2Core     -Roots $webView2SearchRoots -Filter 'Microsoft.Web.WebView2.Core.dll'     -OutName 'Microsoft.Web.WebView2.Core.dll'
Copy-Latest -Override $WebView2WinForms -Roots $webView2SearchRoots -Filter 'Microsoft.Web.WebView2.WinForms.dll' -OutName 'Microsoft.Web.WebView2.WinForms.dll'
Copy-Latest -Override $WebView2Loader   -Roots $webView2SearchRoots -Filter 'WebView2Loader.dll'                  -OutName 'WebView2Loader.dll'

# 校验原生加载器架构：x64 (0x8664) 最理想；x86 (0x14c) 会给出警告（应用会回退到 Edge）。
$loaderPath = Join-Path $binDir 'WebView2Loader.dll'
$bytes = [System.IO.File]::ReadAllBytes($loaderPath)
$pe   = [BitConverter]::ToInt32($bytes, 0x3C)
$mach = [BitConverter]::ToUInt16($bytes, $pe + 4)
switch ($mach) {
    0x8664 { Write-Host 'loader   : x64' }
    0x14c  { Write-Host 'loader   : x86 (警告：WebView2 可能无法初始化，应用会自动回退 Edge 桌面模式)' }
    default { Write-Host ("loader   : 未知架构 0x{0:X4}" -f $mach) }
}

# ---------- 2b. 生成鲸鱼图标（assets\app.ico），编译时内嵌为 EXE 图标 ----------
$iconArgs = @()
try {
    & (Join-Path $root 'make-icon.ps1') -OutDir (Join-Path $root 'assets')
    $icoPath = Join-Path $root 'assets\app.ico'
    if (Test-Path $icoPath) {
        $iconArgs += "/win32icon:`"$icoPath`""
        Write-Host "icon     : 已内嵌鲸鱼图标 (app.ico)"
    }
}
catch {
    Write-Warning ('图标生成失败，将使用默认图标: ' + $_.Exception.Message)
}

# ---------- 3. 编译 ----------
$refs = @(
    (Join-Path $fwRoot 'System.dll'),
    (Join-Path $fwRoot 'System.Core.dll'),
    (Join-Path $fwRoot 'System.Drawing.dll'),
    (Join-Path $fwRoot 'System.Windows.Forms.dll'),
    (Join-Path $fwRoot 'System.Web.Extensions.dll'),
    (Join-Path $binDir 'Microsoft.Web.WebView2.Core.dll'),
    (Join-Path $binDir 'Microsoft.Web.WebView2.WinForms.dll')
)
$refArgs = $refs | ForEach-Object { "/r:`"$_`"" }
$csFiles = Get-ChildItem -Path $srcDir -Filter '*.cs' | ForEach-Object { "`"$($_.FullName)`"" }

# 内嵌 Win32 manifest（DPI 感知 PerMonitorV2 + asInvoker + Common-Controls v6）
$manifestArgs = @()
$manifest = Join-Path $root 'app.manifest'
if (Test-Path $manifest) {
    $manifestArgs += "/win32manifest:`"$manifest`""
    Write-Host "manifest : 已内嵌 DPI 感知 manifest (app.manifest)"
}
else {
    Write-Warning '未找到 app.manifest，DPI 感知将只依赖运行时 P/Invoke。'
}

$argList = @(
    '/nologo',
    '/target:winexe',
    "/out:`"$exeOut`"",
    '/platform:anycpu',
    '/optimize+',
    '/codepage:65001'
) + $manifestArgs + $iconArgs + $refArgs + $csFiles

& $csc $argList
if ($LASTEXITCODE -ne 0) { throw "编译失败 (csc exit $LASTEXITCODE)。" }
Write-Host "built    : $exeOut"

# ---------- 4. 复制配套文件 ----------
$cfgSample = Join-Path $root 'dsh-desktop.config.json'
if (Test-Path $cfgSample) { Copy-Item $cfgSample (Join-Path $binDir 'dsh-desktop.config.json') -Force }
$readme = Join-Path $root 'README.md'
if (Test-Path $readme) { Copy-Item $readme (Join-Path $binDir 'README.md') -Force }

# ---------- 5. （可选）桌面快捷方式 ----------
if ($MakeShortcut) {
    $ws = New-Object -ComObject WScript.Shell
    $desktop = [Environment]::GetFolderPath('Desktop')
    $sc = $ws.CreateShortcut((Join-Path $desktop 'DSH 桌面版.lnk'))
    $sc.TargetPath = $exeOut
    $sc.WorkingDirectory = $binDir
    $sc.IconLocation = $exeOut
    $sc.Description = 'DeepSeek Harness 桌面版：一键启动后端并打开桌面窗口'
    $sc.Save()
    Write-Host "shortcut : 已创建桌面快捷方式"
}

Write-Host ''
Write-Host '构建完成。双击 bin\DshDesktop.exe 即可。'
Write-Host '可先运行：  .\bin\DshDesktop.exe --check   做一次自检。'
