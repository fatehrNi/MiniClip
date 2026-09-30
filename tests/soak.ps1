# 压力/泄漏验证：大量复制 + 反复开合面板，观察线程、句柄、内存与压缩正确性
$ErrorActionPreference = "Stop"
$mx = New-Object System.Threading.Mutex($false, "Local\MiniClip.Soak.Single")
if (-not $mx.WaitOne(0)) { Write-Host "已有 soak 在运行"; exit 99 }
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "MiniClip.exe"
$dataDir = Join-Path (Split-Path -Parent $exe) "data"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$fails = New-Object System.Collections.ArrayList
$report = Join-Path $PSScriptRoot "last-soak.txt"
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
    Start-Process -FilePath $exe -ArgumentList $argline -PassThru -Wait -NoNewWindow `
        -RedirectStandardOutput $out -RedirectStandardError "$out.err" | Out-Null
    $txt = [System.IO.File]::ReadAllText($out, [System.Text.Encoding]::ASCII)
    Remove-Item $out, "$out.err" -Force -ErrorAction SilentlyContinue
    return $txt
}
function Stat-Kv([string]$txt) {
    $h = @{}
    foreach ($l in ($txt -split "`n")) { $i = $l.IndexOf('='); if ($i -gt 0) { $h[$l.Substring(0, $i)] = $l.Substring($i + 1).Trim() } }
    return $h
}

Stop-Process -Name MiniClip -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 600
$env:MINICLIP_DIAG = "1"
$env:MINICLIP_DATA = Join-Path $dataDir "soak-run"
if (Test-Path $env:MINICLIP_DATA) { Remove-Item $env:MINICLIP_DATA -Recurse -Force }
New-Item -ItemType Directory -Force -Path $env:MINICLIP_DATA | Out-Null
# 小容量配置：让淘汰与压缩在几十条内就能触发，便于验证
[System.IO.File]::WriteAllText((Join-Path $env:MINICLIP_DATA "config.txt"), "max_items=10`r`nstore_mb=1`r`ndebounce_ms=90`r`ntrace=1`r`n", (New-Object System.Text.UTF8Encoding($false)))

try {
    Stop-Process -Name MiniClip -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 400
    Start-Process -FilePath $exe -ArgumentList "--silent" -RedirectStandardOutput (Join-Path $env:MINICLIP_DATA "o.log") -RedirectStandardError (Join-Path $env:MINICLIP_DATA "e.log") | Out-Null
    Start-Sleep -Seconds 2

    $base = Stat-Kv (Invoke-Cli "--stat")
    Say ("基线  threads=" + $base.threads + " handles=" + $base.handles + " ws=" + $base.working_set_mb + "MB cpu=" + $base.cpu_ms + "ms")

    # ---- A. 淘汰 + 压缩：30 条 40KB 大文本，写入量远超 store_mb ----
    $big = "SOAKBIG" + (Get-Random -Maximum 9999) + "-" + ("x" * 40000)
    $n = 30
    for ($i = 0; $i -lt $n; $i++) {
        [System.Windows.Forms.Clipboard]::SetText($big + "#" + $i)
        Start-Sleep -Milliseconds 260
    }
    Start-Sleep -Seconds 4
    $s1 = Stat-Kv (Invoke-Cli "--stat")
    Say ("A 后  items=" + $s1.items + " content=" + $s1.content_bytes + "B index=" + $s1.index_bytes + "B threads=" + $s1.threads + " handles=" + $s1.handles + " ws=" + $s1.working_set_mb + "MB")
    Check "eviction-to-max-items" ([int]$s1.items -le 10) ("上限 10，实际 items=" + $s1.items)
    Check "compact-reclaims-space" ([int64]$s1.content_bytes -lt 900000) ("写入约 " + ($n*40100) + "B，压缩后 content=" + $s1.content_bytes + "B")

    # ---- B. 反复开合面板：看稳态后是否继续增长（首次建句柄是合理的）----
    function Cycle([int]$times) {
        for ($i = 0; $i -lt $times; $i++) {
            Invoke-Cli "--show" | Out-Null
            Start-Sleep -Milliseconds 220
            Invoke-Cli "--hide" | Out-Null
            Start-Sleep -Milliseconds 220
        }
        Start-Sleep -Seconds 1
        return Stat-Kv (Invoke-Cli "--stat")
    }
    $s2 = Cycle 10
    $s3 = Cycle 10
    $s5 = Cycle 10
    Say ("三批开合: " + $s2.threads + "/" + $s2.handles + " -> " + $s3.threads + "/" + $s3.handles + " -> " + $s5.threads + "/" + $s5.handles + " (线程/句柄)")
    # 判据是"增长会停止"，不是"第一批到第二批零增长"：首批要建控件句柄，涨是正常的
    Check "thread-growth-stops" ([int]$s5.threads -le ([int]$s3.threads + 1)) ("后两批 " + $s3.threads + " -> " + $s5.threads)
    Check "handle-growth-stops" ([int]$s5.handles -le ([int]$s3.handles + 8)) ("后两批 " + $s3.handles + " -> " + $s5.handles)
    Check "ws-returns-after-close" ([double]$s3.working_set_mb -lt 8) ("收起后 working_set=" + $s3.working_set_mb + "MB")

    # ---- B3. 持续复制 60 次不涨句柄 ----
    for ($i = 0; $i -lt 60; $i++) {
        [System.Windows.Forms.Clipboard]::SetText("SOAKLOOP " + $i + " " + (Get-Random -Maximum 999999))
        Start-Sleep -Milliseconds 130
    }
    Start-Sleep -Seconds 2
    $s4 = Stat-Kv (Invoke-Cli "--stat")
    Say ("B3 后 threads=" + $s4.threads + " handles=" + $s4.handles + " items=" + $s4.items)
    Check "capture-loop-no-handle-growth" ([int]$s4.handles -le ([int]$s3.handles + 20)) ("句柄 " + $s3.handles + " -> " + $s4.handles)
    Check "capture-loop-items-capped" ([int]$s4.items -le 10) ("items=" + $s4.items)

    # ---- C. 空闲 CPU ----
    $c1 = [int](Stat-Kv (Invoke-Cli "--stat")).cpu_ms
    Start-Sleep -Seconds 30
    $c2 = [int](Stat-Kv (Invoke-Cli "--stat")).cpu_ms
    # 阈值 50ms：桌面机器上系统消息（主题/显示变化）与 GC 会带来偶发小峰值；
    # 实测三次为 0 / 15 / 0 ms。"没有常驻轮询"这件事由 trace 里的 0 次唤醒判定。
    Check "idle-cpu-near-zero" (($c2 - $c1) -lt 50) ("30s 空闲 CPU 增量 = $($c2 - $c1) ms")

    # ---- D. 压缩后数据仍然完整可读 ----
    $dump = Invoke-Cli "--dump 3"
    $firstId = 0
    foreach ($l in ($dump -split "`n")) { if ($l -match '^id=(\d+)') { $firstId = [int]$Matches[1]; break } }
    if ($firstId -gt 0) {
        $f = Join-Path $env:MINICLIP_DATA "back.txt"
        Invoke-Cli "--get $firstId `"$f`"" | Out-Null
        $got = [System.IO.File]::ReadAllText($f, [System.Text.Encoding]::UTF8)
        Check "content-readable-after-load" ($got.Length -gt 0) ("id=$firstId len=" + $got.Length)
    }

    # ---- E. 冷启动加载 ----
    $load = Stat-Kv (Invoke-Cli "--bench-load")
    Say ("加载  items=" + $load.items + " load_ms=" + $load.load_ms + " heap=" + $load.managed_heap_kb + "KB")
    Check "load-fast" ([int64]$load.load_ms -lt 1500) ("load_ms=" + $load.load_ms + " items=" + $load.items)
}
finally {
    try { $mx.ReleaseMutex(); $mx.Dispose() } catch { }
    Say ""
    if ($fails.Count -eq 0) { Say "SOAK ALL PASS"; exit 0 }
    Say ("SOAK FAILED: " + ($fails -join ", ")); exit 1
}
