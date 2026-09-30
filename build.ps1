# MiniClip 构建脚本
# 只使用 Windows 自带的 C# 编译器（.NET Framework 4.x 里的 csc.exe），不需要 SDK / NuGet / 网络。
#   powershell -ExecutionPolicy Bypass -File build.ps1            # 构建 -> .\MiniClip.exe
#   powershell -ExecutionPolicy Bypass -File build.ps1 -NoIcon    # 跳过图标（图标生成失败时排障用）
param(
    [switch]$NoIcon,
    [switch]$NoRes,
    [string]$Out = "MiniClip.exe"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

# ---------- 0. 停掉正在运行的实例，否则 exe 被占用无法覆盖 ----------
$run = Get-Process -Name MiniClip -ErrorAction SilentlyContinue
if ($run) {
    Write-Host ("[i] 停止正在运行的 MiniClip（" + ($run.Id -join ',') + "）")
    Stop-Process -Name MiniClip -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
}

# ---------- 1. 找系统自带的 csc.exe ----------
$cscCandidates = @(
    (Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"),
    (Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe")
)
$csc = $null
foreach ($c in $cscCandidates) { if (Test-Path $c) { $csc = $c; break } }
if (-not $csc) { Write-Host "[x] 未找到 .NET Framework 4.x 的 csc.exe" -ForegroundColor Red; exit 1 }
Write-Host ("[i] 编译器: " + $csc)

# ---------- 2. 版本号只认 src/Version.cs 这一处 ----------
$verSrc = [System.IO.File]::ReadAllText((Join-Path $root "src\Version.cs"), [System.Text.Encoding]::UTF8)
function Grab([string]$name, [string]$def) {
    if ($verSrc -match ('public const string ' + $name + ' = "([^"]*)"')) { return $Matches[1] }
    return $def
}
$ver = @{
    Name         = Grab "Name" "MiniClip"
    Number       = (Grab "V1" "1") + "." + (Grab "V2" "0") + "." + (Grab "V3" "0")
    Quad         = (Grab "V1" "1") + "." + (Grab "V2" "0") + "." + (Grab "V3" "0") + "." + (Grab "V4" "0")
    Description  = Grab "Description" "Clipboard history"
    Company      = Grab "Company" "MiniClip"
    Copyright    = Grab "Copyright" ""
    License      = Grab "License" "MIT"
}
Write-Host ("[i] 版本: " + $ver.Name + " " + $ver.Quad)

# ---------- 3. 源文件统一 UTF-8 BOM（GBK 环境下 csc 需要 BOM 才认中文） ----------
New-Item -ItemType Directory -Force -Path (Join-Path $root "build") | Out-Null
$bom = New-Object System.Text.UTF8Encoding($true)
Get-ChildItem (Join-Path $root "src") -Filter *.cs | ForEach-Object {
    $text = [System.IO.File]::ReadAllText($_.FullName, [System.Text.Encoding]::UTF8)
    [System.IO.File]::WriteAllText($_.FullName, $text, $bom)
}

# ---------- 4. 应用清单：DPI 感知 + asInvoker（不需要管理员） ----------
$manifest = @'
<?xml version="1.0" encoding="utf-8"?>
<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
  <assemblyIdentity version="1.0.0.0" name="MiniClip.app" type="win32"/>
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3">
    <security>
      <requestedPrivileges>
        <requestedExecutionLevel level="asInvoker" uiAccess="false"/>
      </requestedPrivileges>
    </security>
  </trustInfo>
  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings>
      <dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true</dpiAware>
    </windowsSettings>
  </application>
  <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1">
    <application>
      <supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}"/>
    </application>
  </compatibility>
</assembly>
'@
$manifestPath = Join-Path $root "build\app.manifest"
[System.IO.File]::WriteAllText($manifestPath, $manifest, (New-Object System.Text.UTF8Encoding($false)))

# ---------- 5. 图标：用 GDI+ 现场画，仓库里不放二进制资源 ----------
$iconArgs = @()
$icoPath = Join-Path $root "build\MiniClip.ico"
if (-not $NoIcon) {
    try {
        Add-Type -AssemblyName System.Drawing
        $bmp = New-Object System.Drawing.Bitmap(32, 32, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.Clear([System.Drawing.Color]::Transparent)
        $bg = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(232, 236, 242))
        $g.FillRectangle($bg, 3, 2, 26, 28)
        $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(90, 110, 140), 1.6)
        $g.DrawRectangle($pen, 3, 2, 26, 28)
        $blue = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(40, 110, 200))
        $g.FillRectangle($blue, 11, 0, 10, 6)
        $g.FillRectangle($blue, 12, 8, 8, 3)
        $line = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(70, 90, 120), 1.8)
        $g.DrawLine($line, 8, 15, 24, 15)
        $g.DrawLine($line, 8, 20, 24, 20)
        $g.DrawLine($line, 8, 25, 18, 25)
        $g.Dispose()
        $ico = [System.Drawing.Icon]::FromHandle($bmp.GetHicon())
        $fs = [System.IO.File]::Create($icoPath)
        $ico.Save($fs); $fs.Close()
        $bmp.Dispose()
        $iconArgs = @("-win32icon:$icoPath")
        Write-Host "[i] 图标已生成"
    } catch {
        Write-Host ("[!] 图标生成失败，继续构建: " + $_.Exception.Message) -ForegroundColor Yellow
    }
}

$refs = @("-r:System.dll", "-r:System.Core.dll", "-r:System.Drawing.dll", "-r:System.Windows.Forms.dll")
$sources = @(Get-ChildItem (Join-Path $root "src") -Filter *.cs | ForEach-Object { $_.FullName })
$exePath = Join-Path $root $Out

# ---------- 6. 先编译资源注入小工具，再编译主程序 ----------
$resedit = Join-Path $root "build\resedit.exe"
$resSrc = Join-Path $root "tools\ResEdit.cs"
$needResedit = (-not (Test-Path $resedit)) -or ((Get-Item $resSrc).LastWriteTime -gt (Get-Item $resedit).LastWriteTime)
if ($needResedit) {
    & $csc -nologo -noconfig -optimize+ -target:exe "-out:$resedit" -r:System.dll $resSrc
    if ($LASTEXITCODE -ne 0) { Write-Host "[x] resedit 编译失败" -ForegroundColor Red; exit $LASTEXITCODE }
}

# 测试用的剪贴板投放小工具（原生 Win32，能放 CF_WAVE / CF_DIB / 延迟渲染）
$clipput = Join-Path $root "build\clipput.exe"
$cpSrc = Join-Path $root "tools\ClipPut.cs"
if (-not (Test-Path $clipput) -or ((Get-Item $cpSrc).LastWriteTime -gt (Get-Item $clipput).LastWriteTime)) {
    & $csc -nologo -noconfig -optimize+ -target:exe "-out:$clipput" -r:System.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll $cpSrc
    if ($LASTEXITCODE -ne 0) { Write-Host "[!] clipput 编译失败（tests/media.ps1 会受影响）" -ForegroundColor Yellow }
}

$cscArgs = @(
    "-nologo", "-noconfig", "-optimize+", "-target:winexe",
    "-platform:anycpu", "-filealign:512",
    "-win32manifest:$manifestPath"
) + $iconArgs + $refs + @("-out:$exePath") + $sources

$sw = [System.Diagnostics.Stopwatch]::StartNew()
& $csc $cscArgs
$code = $LASTEXITCODE
$sw.Stop()
if ($code -ne 0) { Write-Host "[x] 编译失败 ($code)" -ForegroundColor Red; exit $code }

# ---------- 7. 注入文件属性（右键 -> 属性 -> 详细信息） ----------
if (-not $NoRes) {
    $vi = Join-Path $root "build\version.txt"
    $body = @(
        ("version=" + $ver.Quad),
        ("fileversion=" + $ver.Quad),
        ("productversion=" + $ver.Number),
        ("description=" + $ver.Description),
        ("product=" + $ver.Name),
        ("company=" + $ver.Company),
        ("copyright=" + $ver.Copyright),
        ("original=" + $Out),
        ("internal=" + $ver.Name)
    ) -join "`r`n"
    [System.IO.File]::WriteAllText($vi, $body, (New-Object System.Text.UTF8Encoding($false)))
    & $resedit $exePath $vi
    if ($LASTEXITCODE -ne 0) { Write-Host "[!] 版本资源注入失败（不影响运行）" -ForegroundColor Yellow }
}

$fi = Get-Item $exePath
$info = $fi.VersionInfo
Write-Host ("[OK] " + $exePath)
Write-Host ("     {0:N1} KB   编译 {1} ms   版本 {2}   描述 {3}" -f ($fi.Length / 1KB), $sw.ElapsedMilliseconds, $info.FileVersion, $info.FileDescription)
