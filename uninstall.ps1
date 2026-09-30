# 卸载：退出程序、删除快捷方式、取消开机自启（保留 data 目录里的历史）
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $root "MiniClip.exe"
if (Test-Path $exe) { & $exe --quit 2>$null | Out-Null }
Stop-Process -Name MiniClip -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 400

$desktop = [Environment]::GetFolderPath("Desktop")
foreach ($n in @("MiniClip.lnk", "剪贴板历史.lnk")) {
    Remove-Item (Join-Path $desktop $n) -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path ([Environment]::GetFolderPath("Programs")) $n) -Force -ErrorAction SilentlyContinue
}
if (Test-Path $exe) { & $exe --autostart off 2>$null | Out-Null }
Write-Host "[OK] 已退出并移除快捷方式与自启项。" -ForegroundColor Green
Write-Host "     历史数据仍保留在 $(Join-Path $root 'data')，需要彻底清除请手动删除该文件夹。" -ForegroundColor Cyan
