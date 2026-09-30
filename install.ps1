# 安装：桌面快捷方式 + 开机自启 + 立即启动
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $root "MiniClip.exe"
if (-not (Test-Path $exe)) {
    Write-Host "还没有 MiniClip.exe，先编译…" -ForegroundColor Yellow
    & (Join-Path $root "build.ps1")
}
if (-not (Test-Path $exe)) { Write-Host "编译失败，无法安装" -ForegroundColor Red; exit 1 }

# 桌面快捷方式
$desktop = [Environment]::GetFolderPath("Desktop")
$lnkPath = Join-Path $desktop "MiniClip.lnk"
$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut($lnkPath)
$lnk.TargetPath = $exe
$lnk.WorkingDirectory = $root
$lnk.Description = "MiniClip 剪贴板历史：托盘常驻，Ctrl+Alt+V 唤出"
$lnk.IconLocation = $exe + ",0"
$lnk.Save()
Write-Host "[OK] 桌面快捷方式: $lnkPath" -ForegroundColor Green

# 开始菜单也放一份，便于搜索
$start = Join-Path ([Environment]::GetFolderPath("Programs")) "MiniClip.lnk"
$lnk2 = $shell.CreateShortcut($start)
$lnk2.TargetPath = $exe
$lnk2.WorkingDirectory = $root
$lnk2.Description = "MiniClip 剪贴板历史"
$lnk2.IconLocation = $exe + ",0"
$lnk2.Save()
Write-Host "[OK] 开始菜单: $start" -ForegroundColor Green

# 开机自启（程序自己写 HKCU Run，不需要管理员）
& $exe --autostart on
Write-Host "[OK] 已登记开机自启（改禁用：MiniClip.exe --autostart off）" -ForegroundColor Green

# 立即启动
Start-Process -FilePath $exe -RedirectStandardOutput (Join-Path $env:TEMP "miniclip-run.out") -RedirectStandardError (Join-Path $env:TEMP "miniclip-run.err") | Out-Null
Start-Sleep -Seconds 1
Write-Host ""
Write-Host "完成。看屏幕右下角托盘，出现剪贴板图标即在运行。" -ForegroundColor Cyan
Write-Host "  唤出历史：按 Ctrl+Alt+V（或双击托盘图标）" -ForegroundColor Cyan
Write-Host "  数据存放：$(Join-Path $root 'data')  （跟着程序走，不占 C 盘）" -ForegroundColor Cyan
