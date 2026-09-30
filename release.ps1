# 发布打包：构建 → 组装便携包 → zip + SHA256（可选 -Installer 生成单文件 Setup.exe）
#   powershell -ExecutionPolicy Bypass -File release.ps1
#   powershell -ExecutionPolicy Bypass -File release.ps1 -Installer
param(
    [switch]$Installer,
    [switch]$SkipBuild
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

if (-not $SkipBuild) {
    & (Join-Path $root "build.ps1")
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$exe = Join-Path $root "MiniClip.exe"
if (-not (Test-Path $exe)) { Write-Host "[x] 没有找到 MiniClip.exe" -ForegroundColor Red; exit 1 }

# 版本号从编译好的 exe 文件属性里读，顺带验证注入确实生效（单一来源 + 自检）
$info = (Get-Item $exe).VersionInfo
$ver = $info.ProductVersion
if ([string]::IsNullOrWhiteSpace($ver)) { Write-Host "[x] 文件属性里没有版本号，先修 build.ps1" -ForegroundColor Red; exit 1 }
Write-Host ("[i] 发布版本: MiniClip " + $ver)

$rel = Join-Path $root "release"
$name = "MiniClip-$ver-win-x64"
$dir = Join-Path $rel $name
if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $dir | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $dir "docs") | Out-Null

# 运行期需要的东西：程序 + 文档 + 双击入口（源码走 git 标签，不塞进二进制包）
$files = @(
    "MiniClip.exe", "README.md", "LICENSE", "CHANGELOG.md", "CONTRIBUTING.md",
    "start-clipboard.cmd", "install.cmd", "install.ps1", "uninstall.cmd", "uninstall.ps1",
    "build.cmd", "build.ps1"
)
foreach ($f in $files) {
    $src = Join-Path $root $f
    if (Test-Path $src) { Copy-Item $src (Join-Path $dir $f) -Force }
    else { Write-Host ("[!] 缺少 " + $f) -ForegroundColor Yellow }
}
foreach ($d in @("src", "tools", "tests")) {
    $from = Join-Path $root $d
    if (Test-Path $from) { Copy-Item $from (Join-Path $dir $d) -Recurse -Force }
}
Copy-Item (Join-Path $root "docs\*") (Join-Path $dir "docs") -Force -ErrorAction SilentlyContinue

$zip = Join-Path $rel ($name + ".zip")
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $dir "*") -DestinationPath $zip -CompressionLevel Optimal

$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
$zi = Get-Item $zip
Write-Host ""
Write-Host ("[OK] 目录  " + $dir)
Write-Host ("[OK] 压缩包 " + $zip + "   {0:N1} MB" -f ($zi.Length / 1MB))
Write-Host ("     SHA256 " + $hash)
$hashFile = Join-Path $rel ($name + ".sha256.txt")
[System.IO.File]::WriteAllText($hashFile, $hash + "  " + $zi.Name + "`r`n", (New-Object System.Text.UTF8Encoding($false)))

if ($Installer) {
    # IExpress 是 Windows 自带的打包器，用它做单文件 Setup.exe，不引入任何第三方安装框架
    $ix = Join-Path $env:WINDIR "System32\iexpress.exe"
    if (-not (Test-Path $ix)) { Write-Host "[!] 系统里没有 iexpress.exe，跳过安装器" -ForegroundColor Yellow; exit 0 }
    # iexpress 是老式 ANSI 程序：路径里不能有中文（本项目目录可能含中文），SED 必须用系统 ANSI 代码页写。
    # 所以先在纯 ASCII 的临时目录里打包，再把产物搬回 release\。
    if (Get-Process LockApp,LogonUI -ErrorAction SilentlyContinue) {
        Write-Host "[!] 屏幕当前是锁定的：iexpress 是 GUI 程序，锁屏下无法生成包（实测退出码 1）。解锁后重跑 -Installer。" -ForegroundColor Yellow
        Write-Host "    便携 zip 包不受影响，解压后双击 install.cmd 与安装器等效。" -ForegroundColor Yellow
        exit 0
    }
    $work = Join-Path $env:TEMP ("mcrelease-" + (Get-Random -Maximum 9999))
    $wdir = Join-Path $work $name
    New-Item -ItemType Directory -Force -Path $wdir | Out-Null
    Copy-Item (Join-Path $dir "*") $wdir -Recurse -Force
    $sed = Join-Path $work "package.sed"
    $setupTmp = Join-Path $work ($name + "-setup.exe")
    $setup = Join-Path $rel ($name + "-setup.exe")
    $pkgFiles = @(Get-ChildItem $wdir -File | ForEach-Object { $_.Name })
    $L = New-Object System.Collections.Generic.List[string]
    $L.Add("[Version]"); $L.Add("Class=IEXPRESS"); $L.Add("SEDVersion=3")
    $L.Add("[Options]")
    foreach ($o in @("PackagePurpose=InstallApp","ShowInstallProgramWindow=0","HideExtractAnimation=1",
                     "UseLongFileName=1","InsideCompressed=0","CAB_FixedSize=0","CAB_ResvCodeSigning=0",
                     "RebootMode=N","InstallPrompt=%InstallPrompt%","DisplayLicense=%DisplayLicense%",
                     "FinishMessage=%FinishMessage%","TargetName=%TargetName%","FriendlyName=%FriendlyName%",
                     "AppLaunched=%AppLaunched%","PostInstallCmd=%PostInstallCmd%",
                     "AdminQuietInstCmd=%AdminQuietInstCmd%","UserQuietInstCmd=%UserQuietInstCmd%",
                     "SourceFiles=SourceFiles")) { $L.Add($o) }
    $L.Add("[Strings]")
    $L.Add("InstallPrompt=")
    $L.Add("DisplayLicense=")
    $L.Add("FinishMessage=安装完成。看右下角托盘，按 Ctrl+Alt+V 唤出剪贴板历史。")
    $L.Add("TargetName=" + $setupTmp)
    $L.Add("FriendlyName=MiniClip " + $ver)
    $L.Add("AppLaunched=cmd /c install.cmd")
    $L.Add("PostInstallCmd=<None>")
    $L.Add("AdminQuietInstCmd=")
    $L.Add("UserQuietInstCmd=")
    for ($i = 0; $i -lt $pkgFiles.Count; $i++) { $L.Add(('File{0}="{1}"' -f $i, $pkgFiles[$i])) }
    $L.Add("[SourceFiles]")
    $L.Add("SourceFiles0=" + $wdir + "\")
    $L.Add("[SourceFiles0]")
    for ($i = 0; $i -lt $pkgFiles.Count; $i++)
    {
        $L.Add(('Path{0}={1}' -f $i, $pkgFiles[$i]))
        $L.Add(('Name{0}={1}' -f $i, $pkgFiles[$i]))
    }
    [System.IO.File]::WriteAllLines($sed, $L, [System.Text.Encoding]::Default)
    Write-Host ("[i] 生成单文件安装器（iexpress，" + $pkgFiles.Count + " 个文件）...")
    Start-Process -FilePath $ix -ArgumentList @("/N", "/Q", ('"' + $sed + '"')) -Wait -WindowStyle Hidden
    if (Test-Path $setupTmp)
    {
        Move-Item $setupTmp $setup -Force
        $si = Get-Item $setup
        Write-Host ("[OK] 安装器 " + $setup + "   {0:N1} MB" -f ($si.Length / 1MB))
        Write-Host ("     SHA256 " + (Get-FileHash $setup -Algorithm SHA256).Hash.ToLower())
        Write-Host "     它只是便携包的自解压外壳：解压后执行包内 install.cmd（建快捷方式 + 开机自启 + 启动）"
    }
    else
    {
        Write-Host "[!] iexpress 未产出安装器（常见原因：屏幕锁定 / 非交互会话）。便携 zip 包可正常使用。" -ForegroundColor Yellow
    }
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
