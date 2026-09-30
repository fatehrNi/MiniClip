# 死锁自愈验证：故意让来源程序"卡住不回数据"，确认 MiniClip 会主动放弃并释放系统剪贴板
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "MiniClip.exe"
Add-Type -AssemblyName System.Windows.Forms

$report = Join-Path $PSScriptRoot "last-deadlock.txt"
$fails = New-Object System.Collections.ArrayList
[System.IO.File]::WriteAllText($report, "", (New-Object System.Text.UTF8Encoding($false)))
function Say([string]$m) {
    [System.IO.File]::AppendAllText($report, $m + "`r`n", (New-Object System.Text.UTF8Encoding($false)))
    Write-Host $m
}
function Check([string]$n, [bool]$ok, [string]$d) {
    if ($ok) { Say ("PASS  " + $n + "  " + $d) } else { Say ("FAIL  " + $n + "  " + $d); [void]$fails.Add($n) }
}
if (-not ('CBH' -as [type])) {
    Add-Type -Name CBH -Namespace '' -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool OpenClipboard(IntPtr o);
[DllImport("user32.dll")] public static extern bool CloseClipboard();
public static bool Ok() { if (OpenClipboard(IntPtr.Zero)) { CloseClipboard(); return true; } return false; }
'@
}

$env:MINICLIP_DATA = Join-Path $root "data\deadlock-test"
Stop-Process -Name MiniClip -Force -EA SilentlyContinue
Start-Sleep -Milliseconds 700
if (Test-Path $env:MINICLIP_DATA) { Remove-Item $env:MINICLIP_DATA -Recurse -Force }
New-Item -ItemType Directory -Force -Path $env:MINICLIP_DATA | Out-Null
Start-Process -FilePath $exe -ArgumentList "--trace","--silent" -RedirectStandardOutput (Join-Path $env:MINICLIP_DATA "o.log") -RedirectStandardError (Join-Path $env:MINICLIP_DATA "e.log") | Out-Null
Start-Sleep -Seconds 2

# 子进程：放一份"延迟渲染"的音频到剪贴板，然后睡 60 秒且不泵消息
# —— 我们的读线程去要数据就会被挂住，同时占着剪贴板
$clipput = Join-Path $root "build\clipput.exe"
if (-not (Test-Path $clipput)) { Say "缺少 build\clipput.exe（先跑 build.ps1）"; exit 3 }
# clipput hang：SetClipboardData(CF_UNICODETEXT, NULL) = 延迟渲染，然后睡 40 秒且不泵消息。
# 任何监听方去 GetClipboardData 就会被挂住，而它此刻正占着剪贴板 —— 正是要复现的场景。
$cp = Start-Process -FilePath $clipput -ArgumentList @("hang", "40") -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $env:MINICLIP_DATA "child.out") `
        -RedirectStandardError (Join-Path $env:MINICLIP_DATA "child.err")

# 实测结论：Windows 在等延迟渲染期间会临时释放剪贴板，所以别的程序始终能读写剪贴板；
# 真正会被卡住的是我们的读线程。因此这里判定两件事：
#   1) 期间别人的剪贴板访问一直可用（没有将全系统冻住）
#   2) 我们的看门狗把卡住的读线程救回来，之后照常记录
$usableAllAlong = $true; $samples = 0
try {
    for ($i = 0; $i -lt 20; $i++) {
        Start-Sleep -Milliseconds 1000
        $samples++
        if (-not [CBH]::Ok()) { $usableAllAlong = $false; Say ("      第 " + ($i + 1) + " 秒：剪贴板打不开") }
    }
    Start-Sleep -Seconds 2
    $trace = ""
    $tf = Join-Path $env:MINICLIP_DATA "trace.log"
    if (Test-Path $tf) {
        $fs = [System.IO.File]::Open($tf, 'Open', 'Read', 'ReadWrite')
        $sr = New-Object System.IO.StreamReader($fs, [System.Text.Encoding]::UTF8)
        $trace = $sr.ReadToEnd(); $sr.Close(); $fs.Close()
    }
    Check "clipboard-usable-during-hang" $usableAllAlong ($samples + " 次探测全部可用")
    Check "watchdog-fired" ($trace -match "CRITICAL") ("trace: " + $(if ($trace -match '(CRITICAL[^\r\n]*)') { $Matches[1] } else { "无" }))
    Check "app-still-alive" ($null -ne (Get-Process MiniClip -EA SilentlyContinue)) "实例仍在运行"
    # 恢复后还能正常记录
    [System.Windows.Forms.Clipboard]::SetText("AFTER-RECOVERY-" + (Get-Random -Maximum 999))
    Start-Sleep -Milliseconds 1200
    $o = [System.IO.Path]::GetTempFileName()
    Start-Process -FilePath $exe -ArgumentList "--dump 5" -Wait -NoNewWindow -RedirectStandardOutput $o | Out-Null
    $t = [System.IO.File]::ReadAllText($o, [System.Text.Encoding]::ASCII)
    Remove-Item $o -Force
    Check "capture-works-after-recovery" ($t -like "*AFTER-RECOVERY*") "恢复后新复制仍被记录"
}
finally {
    try { Stop-Process -Id $cp.Id -Force -EA SilentlyContinue } catch { }
    Say ""
    if ($fails.Count -eq 0) { Say "DEADLOCK ALL PASS"; exit 0 }
    Say ("DEADLOCK FAILED: " + ($fails -join ", ")); exit 1
}
