# 造几条真实风格的历史记录，唤出面板并截图，供人工核对排版
param([switch]$Hide)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "MiniClip.exe"
$dataDir = Join-Path $root "data"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

function Run-Cli([string]$a) {
    $o = [System.IO.Path]::GetTempFileName()
    Start-Process -FilePath $exe -ArgumentList $a -PassThru -Wait -NoNewWindow -RedirectStandardOutput $o -RedirectStandardError "$o.e" | Out-Null
    $t = [System.IO.File]::ReadAllText($o, [System.Text.Encoding]::ASCII)
    Remove-Item $o, "$o.e" -Force -ErrorAction SilentlyContinue
    return $t
}

if (-not ('MCWin32b' -as [type])) {
    Add-Type -Name MCWin32b -Namespace '' -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
'@
}

# 无消息循环的脚本里不能 Close 窗体（会卡住），全程复用同一个窗口只改内容
$script:shotForm = $null
$script:shotBox = $null
function CopyFrom([string]$title, [string]$text) {
    if (-not $script:shotForm) {
        $script:shotForm = New-Object System.Windows.Forms.Form
        $script:shotForm.Text = $title
        $script:shotForm.TopMost = $true
        $script:shotForm.Size = New-Object System.Drawing.Size(360, 140)
        $script:shotBox = New-Object System.Windows.Forms.TextBox
        $script:shotBox.Multiline = $true
        $script:shotBox.Dock = [System.Windows.Forms.DockStyle]::Fill
        $script:shotForm.Controls.Add($script:shotBox)
        $script:shotForm.Show() | Out-Null
    }
    $script:shotForm.Text = $title
    $script:shotBox.Text = $text
    [void][System.Windows.Forms.Application]::DoEvents()
    [void][MCWin32b]::SetForegroundWindow($script:shotForm.Handle)
    Start-Sleep -Milliseconds 250
    $script:shotBox.SelectAll()
    $script:shotBox.Copy()
    Start-Sleep -Milliseconds 250
}

Stop-Process -Name MiniClip -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 600
if (Test-Path $dataDir) { Remove-Item $dataDir -Recurse -Force -ErrorAction SilentlyContinue }
Start-Process -FilePath $exe -ArgumentList "--silent" -RedirectStandardOutput (Join-Path $env:TEMP "mc-shot.out") -RedirectStandardError (Join-Path $env:TEMP "mc-shot.err") | Out-Null
Start-Sleep -Seconds 2

CopyFrom "订单明细 - Google Chrome" "【收件人】李文博 138-0000-1234 上海市杨浦区政立路 481 号创智天地 3 号楼 501 室；【商品】 legion-y9000p-2022 / RTX3060 / 16G+512G x1"
CopyFrom "需求讨论 - 微信" "结论：粘贴板工具只做本地存储，不上传；快捷键 Ctrl+Alt+V 唤出，历史保留 2000 条。`n下次评审：周四 15:00"
CopyFrom "terminal - powershell" "git commit -m ""feat(clipstore): append-only index with lazy content offsets"""
CopyFrom "WPS Office - 论文初稿.md" "摘要：本文针对多源异构数据缺失问题，提出一种基于变分自编码器的插补框架，并在三个公开数据集上验证了有效性。"
CopyFrom "下载列表 - Edge" "https://github.com/openclarity/clipper/releases/download/v2.4.1/clipper-x64.msi"

# 一条图片
$bmp = New-Object System.Drawing.Bitmap(220, 120)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.Clear([System.Drawing.Color]::FromArgb(240, 60, 130, 210))
$font = New-Object System.Drawing.Font("Microsoft YaHei UI", 14)
$g.DrawString("图片示例 220x120", $font, [System.Drawing.Brushes]::White, 12, 40)
$g.Dispose()
[System.Windows.Forms.Clipboard]::SetImage($bmp)
$bmp.Dispose()
Start-Sleep -Milliseconds 700

# 一条文件列表（CF_HDROP 由资源管理器提供，这里用真实文件对象模拟）
$files = New-Object System.Collections.Specialized.StringCollection
    $files.Add((Join-Path $root "README.md")) | Out-Null
    $files.Add((Join-Path $root "MiniClip.exe")) | Out-Null
[System.Windows.Forms.Clipboard]::SetFileDropList($files)
Start-Sleep -Milliseconds 900

Run-Cli "--dump 8" | Out-Null
Run-Cli "--show" | Out-Null
Start-Sleep -Milliseconds 1200

$png = Join-Path $env:TEMP "miniclip-panel.png"
$bounds = [System.Windows.Forms.SystemInformation]::VirtualScreen
$cap = New-Object System.Drawing.Bitmap($bounds.Width, $bounds.Height)
$gc = [System.Drawing.Graphics]::FromImage($cap)
$gc.CopyFromScreen($bounds.X, $bounds.Y, 0, 0, $cap.Size)
$gc.Dispose()
$cap.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
$cap.Dispose()
Write-Host ("screenshot=" + $png)
Write-Host (Run-Cli "--stat")
if ($Hide) { Run-Cli "--hide" | Out-Null }
if ($script:shotForm) { $script:shotForm.Hide() }
