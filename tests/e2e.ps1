param(
    [int]$Debounce = 90
)
# MiniClip 端到端验证。只用可确定的 API（自建带标题的窗口 + 程序自带 IPC 指令），
# 不注入全局按键，避免打扰用户当前的输入。
$ErrorActionPreference = "Stop"
# 同一机器上只允许一个 e2e 在跑：并发会互相污染副作用与指标
$mx = New-Object System.Threading.Mutex($false, "Local\MiniClip.E2E.Single")
if (-not $mx.WaitOne(0)) { Write-Host "已有 e2e 在运行，退出"; exit 99 }
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "MiniClip.exe"
$dataDir = Join-Path (Split-Path -Parent $exe) "data"
Stop-Process -Name MiniClip -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 600
# 测试用独立数据目录，不污染用户真实历史
$dataDir = Join-Path $dataDir "test-run"
if (Test-Path $dataDir) { Remove-Item $dataDir -Recurse -Force -ErrorAction SilentlyContinue }
$env:MINICLIP_DATA = $dataDir

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$fails = New-Object System.Collections.ArrayList
$report = Join-Path $PSScriptRoot "last-report.txt"
[System.IO.File]::WriteAllText($report, "", (New-Object System.Text.UTF8Encoding($false)))
function Say([string]$m) {
    [System.IO.File]::AppendAllText($report, $m + "`r`n", (New-Object System.Text.UTF8Encoding($false)))
    Write-Host $m
}
function Check([string]$name, [bool]$ok, [string]$detail) {
    if ($ok) { Say ("PASS  " + $name + "  " + $detail) }
    else { Say ("FAIL  " + $name + "  " + $detail); [void]$fails.Add($name) }
}

function Invoke-Cli([string]$argline) {
    $out = [System.IO.Path]::GetTempFileName()
    $errf = [System.IO.Path]::GetTempFileName()
    Start-Process -FilePath $exe -ArgumentList $argline -PassThru -Wait -NoNewWindow `
        -RedirectStandardOutput $out -RedirectStandardError $errf | Out-Null
    $txt = [System.IO.File]::ReadAllText($out, [System.Text.Encoding]::ASCII)
    $err = [System.IO.File]::ReadAllText($errf, [System.Text.Encoding]::ASCII)
    Remove-Item $out, $errf -Force -ErrorAction SilentlyContinue
    return @{ Out = $txt; Err = $err }
}

function Parse-Dump([string]$txt) {
    $rows = @()
    foreach ($line in ($txt -split "`n")) {
        if ($line.Trim().Length -eq 0) { continue }
        $h = @{}
        foreach ($kv in ($line -split "`t")) {
            $i = $kv.IndexOf('=')
            if ($i -gt 0) { $h[$kv.Substring(0, $i)] = $kv.Substring($i + 1) }
        }
        if ($h.Count -gt 3) { $rows += [pscustomobject]$h }
    }
    return $rows
}

function Get-Rows { return @(Parse-Dump (Invoke-Cli "--dump 60").Out) }

function Unescape([string]$s) {
    $rx = [regex]'\\u([0-9a-fA-F]{4})'
    return $rx.Replace($s, { param($m) [char][Convert]::ToInt32($m.Groups[1].Value, 16) })
}

function Read-SharedFile([string]$p) {
    # 用 FileShare.ReadWrite 读日志；不用 Get-Content -Tail（GBK 环境下遇到中文会卡住）
    if (-not (Test-Path $p)) { return "" }
    $fs = [System.IO.File]::Open($p, 'Open', 'Read', 'ReadWrite')
    $sr = New-Object System.IO.StreamReader($fs, [System.Text.Encoding]::UTF8)
    $t = $sr.ReadToEnd(); $sr.Close(); $fs.Close()
    return $t
}

# ---------- 0. 备份用户剪贴板 ----------
$savedText = ""
$savedImg = $null
try {
    if ([System.Windows.Forms.Clipboard]::ContainsImage()) { $savedImg = [System.Windows.Forms.Clipboard]::GetImage() }
    if ([System.Windows.Forms.Clipboard]::ContainsText()) { $savedText = [System.Windows.Forms.Clipboard]::GetText() }
} catch { }

$form = $null
try {
    # ---------- 1. 启动实例 ----------
    Stop-Process -Name MiniClip -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
    Remove-Item (Join-Path $dataDir "trace.log") -Force -ErrorAction SilentlyContinue
    $np = Join-Path $env:TEMP "miniclip-out.log"
    # --silent：测试期间不把面板盖到用户桌面上，panel-shows 这一步才是有效判定
    Start-Process -FilePath $exe -ArgumentList "--trace","--silent" -RedirectStandardOutput $np -RedirectStandardError "$np.err" | Out-Null
    Start-Sleep -Seconds 2

    $pid0 = 0
    foreach ($l in ((Invoke-Cli "--stat").Out -split "`n")) { if ($l -match '^pid=(\d+)') { $pid0 = [int]$Matches[1] } }
    Check "instance-started" ($pid0 -gt 0) "pid=$pid0"

    $trace = Read-SharedFile (Join-Path $dataDir "trace.log")
    Check "hotkey-registered" ($trace -match "hotkey registered") ("trace: " + $(if ($trace -match '(hotkey \w+[^\r\n]*)') { $Matches[1] } else { "无记录" }))
    Check "event-driven-listener" ($trace -match "mode=event listener ok=True") "监听按事件注册，没有轮询定时器"

    # ---------- 2. 来源归属：在一个真有标题栏的窗口里执行复制 ----------
    $mark = "MCATTR" + (Get-Random -Maximum 99999)
    $cjk = "" + [char]0x4E2D + [char]0x6587 + [char]0x590D + [char]0x5236
    $body = $mark + " " + $cjk + "`r`nline2-abc`r`nline3-def"

    $form = New-Object System.Windows.Forms.Form
    $form.Text = "MiniClipE2E-" + $mark
    $form.TopMost = $true
    $form.Size = New-Object System.Drawing.Size(420, 220)
    $tb = New-Object System.Windows.Forms.TextBox
    $tb.Multiline = $true
    $tb.Dock = [System.Windows.Forms.DockStyle]::Fill
    $tb.Text = $body
    $form.Controls.Add($tb)

    if (-not ('MCWin32' -as [type])) {
        Add-Type -Name MCWin32 -Namespace '' -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
'@
    }
    $form.Show() | Out-Null
    [void][System.Windows.Forms.Application]::DoEvents()
    [void][MCWin32]::SetForegroundWindow($form.Handle)
    Start-Sleep -Milliseconds 400
    [void][System.Windows.Forms.Application]::DoEvents()
    $fg = [MCWin32]::GetForegroundWindow()
    $sbTitle = New-Object System.Text.StringBuilder 200
    [void][MCWin32]::GetWindowText($fg, $sbTitle, 200)
    $fgTitle = $sbTitle.ToString()
    $fgPid = [uint32]0
    [void][MCWin32]::GetWindowThreadProcessId([MCWin32]::GetForegroundWindow(), [ref]$fgPid)
    $fgApp = (Get-Process -Id $fgPid -ErrorAction SilentlyContinue).ProcessName
    Say ("      复制瞬间真实前台: $fgApp / $fgTitle")
    $tb.Focus()
    $tb.SelectAll()
    $tb.Copy()
    Start-Sleep -Milliseconds (700 + $Debounce)
    $form.Hide()

    $hit = Get-Rows | Where-Object { $_.preview -like "*$mark*" } | Select-Object -First 1
    if ($hit) {
        # 基准 = 复制那一瞬间系统告诉我们的前台窗口；谁在前台都行，只要记录与之一致
        Check "attribution-window-title" ((Unescape $hit.title) -eq $fgTitle) ("记录=" + (Unescape $hit.title) + " | 现场=" + $fgTitle)
        Check "attribution-process-name" ($hit.app -eq ($fgApp + ".exe")) ("app=" + $hit.app + " 现场=" + $fgApp)
        Check "metadata-complete" ($hit.ts -and $hit.time -and $hit.kind -and $hit.chars -and $hit.hash -and $hit.fmt -and $hit.preview) ("chars=" + $hit.chars + " size=" + $hit.size)
        Check "format-list-captured" ($hit.fmt -match 'CF_UNICODETEXT') ("fmt=" + $hit.fmt)
    } else {
        Check "attribution-window-title" $false "没有含 $mark 的条目"
        Check "attribution-process-name" $false "n/a"
        Check "metadata-complete" $false "n/a"
        Check "format-list-captured" $false "n/a"
    }
    $id0 = 0
    if ($hit) { $id0 = [int]$hit.id }

    # ---------- 3. 正文保真（中文 + 多行 + CRLF）----------
    if ($id0 -gt 0) {
        $gotFile = Join-Path $env:TEMP "mc-e2e-got.txt"
        Invoke-Cli ("--get $id0 ""$gotFile""") | Out-Null
        $got = [System.IO.File]::ReadAllText($gotFile, [System.Text.Encoding]::UTF8)
        Check "content-fidelity" ($got -ceq $body) ("len got=" + $got.Length + " want=" + $body.Length)
        Remove-Item $gotFile -Force -ErrorAction SilentlyContinue
    }

    # ---------- 4. 去重：同内容再复制一次 ----------
    $before = @(Get-Rows).Count
    [System.Windows.Forms.Clipboard]::SetText($body)
    Start-Sleep -Milliseconds (700 + $Debounce)
    $after = @(Get-Rows).Count
    $hit2 = Get-Rows | Where-Object { $_.preview -like "*$mark*" } | Select-Object -First 1
    $hits = if ($hit2) { [int]$hit2.hits } else { 0 }
    Check "dedupe-increments-hits" (($after -eq $before) -and ($hits -ge 2)) ("rows $before->$after hits=$hits")

    # ---------- 5. 回写剪贴板（IPC --copy），且自己写回不被记成新条目 ----------
    $sentinel = "SENTINEL" + (Get-Random -Maximum 99999)
    [System.Windows.Forms.Clipboard]::SetText($sentinel)
    Start-Sleep -Milliseconds (700 + $Debounce)      # 先让这条被正常记录
    $beforeCopy = @(Get-Rows).Count
    Invoke-Cli ("--copy $id0") | Out-Null
    Start-Sleep -Milliseconds 900
    $now = ""
    try { $now = [System.Windows.Forms.Clipboard]::GetText() } catch { }
    Check "ipc-copy-restores-content" ($now -ceq $body) ("len=" + $now.Length)
    $rows3 = @(Get-Rows).Count
    Check "self-write-not-recorded" ($rows3 -eq $beforeCopy) ("rows $beforeCopy -> $rows3")

    # ---------- 6. 面板开合 ----------
    Invoke-Cli "--show" | Out-Null
    Start-Sleep -Milliseconds 900
    $title = (Get-Process -Name MiniClip -ErrorAction SilentlyContinue | Select-Object -First 1).MainWindowTitle
    Check "panel-shows" ($title -like "*MiniClip*") ("title=$title")
    Invoke-Cli "--hide" | Out-Null
    Start-Sleep -Milliseconds 600
    $title2 = (Get-Process -Name MiniClip -ErrorAction SilentlyContinue | Select-Object -First 1).MainWindowTitle
    Check "panel-hides" ([string]::IsNullOrEmpty($title2)) ("title='$title2'")

    # ---------- 7. 图片条目 ----------
    $bmp = New-Object System.Drawing.Bitmap(64, 48)
    $gg = [System.Drawing.Graphics]::FromImage($bmp)
    $gg.Clear([System.Drawing.Color]::FromArgb(220, 30, 120, 200))
    $gg.Dispose()
    [System.Windows.Forms.Clipboard]::SetImage($bmp)
    $bmp.Dispose()
    Start-Sleep -Milliseconds (900 + $Debounce)
    $img = Get-Rows | Where-Object { $_.kind -eq "2" } | Select-Object -First 1
    $pngs = @(Get-ChildItem (Join-Path $dataDir "images") -Filter *.png -ErrorAction SilentlyContinue)
    Check "image-captured-to-png" (($img -ne $null) -and ($pngs.Count -ge 1)) ("preview=" + $(if ($img) { $img.preview }) + " pngFiles=" + $pngs.Count)

    # ---------- 8. 端到端延迟 ----------
    $bench = Invoke-Cli "--bench-capture 15"
    $kv = @{}
    foreach ($l in ($bench.Out -split "`n")) { $i = $l.IndexOf('='); if ($i -gt 0) { $kv[$l.Substring(0, $i)] = $l.Substring($i + 1).Trim() } }
    $p50 = 0
    if ($kv.ContainsKey("p50_ms")) { $p50 = [int]$kv["p50_ms"] }
    Check "capture-latency-predictable" ($kv["samples"] -eq $kv["sent"] -and $p50 -ge $Debounce -and $p50 -lt ($Debounce + 220)) `
        ("p50=$p50 ms (防抖 $Debounce ms) min=$($kv.min_ms) max=$($kv.max_ms) samples=$($kv.samples)/$($kv.sent)")

    # ---------- 9. 空闲开销 ----------
    $c1 = 0
    foreach ($l in ((Invoke-Cli "--stat").Out -split "`n")) { if ($l -match '^cpu_ms=(\d+)') { $c1 = [int]$Matches[1] } }
    Say "      空闲采样 30s ..."
    Start-Sleep -Seconds 30
    $c2 = 0; $w2 = 0.0; $pr = 0.0; $th = 0; $hd = 0
    foreach ($l in ((Invoke-Cli "--stat").Out -split "`n")) {
        if ($l -match '^cpu_ms=(\d+)') { $c2 = [int]$Matches[1] }
        if ($l -match '^working_set_mb=([\d.]+)') { $w2 = [double]$Matches[1] }
        if ($l -match '^private_mb=([\d.]+)') { $pr = [double]$Matches[1] }
        if ($l -match '^threads=(\d+)') { $th = [int]$Matches[1] }
        if ($l -match '^handles=(\d+)') { $hd = [int]$Matches[1] }
    }
    $dcpu = $c2 - $c1
    Check "idle-cpu-near-zero" ($dcpu -lt 20) ("30s CPU 增量 = $dcpu ms")
    Check "idle-memory-small" ($w2 -lt 24) ("working_set=$w2 MB private=$pr MB")
    # 阈值取 soak 测到的稳态平台上限；"是否持续增长"由 soak 负责判定
    Check "steady-state-footprint" ($th -le 16 -and $hd -le 460) ("threads=$th handles=$hd (平台 12-15 / 405-408)")
}
catch {
    Say ("EXCEPTION " + $_.Exception.GetType().Name + ": " + $_.Exception.Message + " @line " + $_.InvocationInfo.ScriptLineNumber)
    [void]$fails.Add("exception")
}
finally {
    if ($form) { try { $form.Close() } catch { } }
    try { $mx.ReleaseMutex(); $mx.Dispose() } catch { }
    try {
        if ($savedImg) { [System.Windows.Forms.Clipboard]::SetImage($savedImg) }
        elseif ($savedText.Length -gt 0) { [System.Windows.Forms.Clipboard]::SetText($savedText) }
    } catch { }
    Say ""
    if ($fails.Count -eq 0) { Say "ALL PASS"; exit 0 }
    Say ("FAILED: " + ($fails -join ", ")); exit 1
}
