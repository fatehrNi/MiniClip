# 验证：音频内容保存、图片文件快照、详情区换行、窗口不再置顶
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "MiniClip.exe"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$report = Join-Path $PSScriptRoot "last-media.txt"
$fails = New-Object System.Collections.ArrayList
[System.IO.File]::WriteAllText($report, "", (New-Object System.Text.UTF8Encoding($false)))
function Say([string]$m) {
    [System.IO.File]::AppendAllText($report, $m + "`r`n", (New-Object System.Text.UTF8Encoding($false)))
    Write-Host $m
}
function Check([string]$n, [bool]$ok, [string]$d) {
    if ($ok) { Say ("PASS  " + $n + "  " + $d) } else { Say ("FAIL  " + $n + "  " + $d); [void]$fails.Add($n) }
}
function Run-Cli([string]$a) {
    $o = [System.IO.Path]::GetTempFileName()
    Start-Process -FilePath $exe -ArgumentList $a -Wait -NoNewWindow -RedirectStandardOutput $o -RedirectStandardError "$o.e" | Out-Null
    $t = [System.IO.File]::ReadAllText($o, [System.Text.Encoding]::ASCII)
    Remove-Item $o, "$o.e" -Force -EA SilentlyContinue
    return $t
}
function Rows([string]$t) {
    $out = @()
    foreach ($line in ($t -split "`n")) {
        if ($line.Trim().Length -eq 0) { continue }
        $h = @{}
        foreach ($kv in ($line -split "`t")) { $i = $kv.IndexOf('='); if ($i -gt 0) { $h[$kv.Substring(0,$i)] = $kv.Substring($i+1) } }
        if ($h.Count -gt 3) { $out += [pscustomobject]$h }
    }
    return $out
}

Stop-Process -Name MiniClip -Force -EA SilentlyContinue
Start-Sleep -Milliseconds 700
$env:MINICLIP_DATA = Join-Path $root "data\media-test"
if (Test-Path $env:MINICLIP_DATA) { Remove-Item $env:MINICLIP_DATA -Recurse -Force }
New-Item -ItemType Directory -Force -Path $env:MINICLIP_DATA | Out-Null
Stop-Process -Name MiniClip -Force -EA SilentlyContinue
Start-Sleep -Milliseconds 600
Start-Process -FilePath $exe -ArgumentList "--trace","--silent" -RedirectStandardOutput (Join-Path $env:MINICLIP_DATA "o.log") -RedirectStandardError (Join-Path $env:MINICLIP_DATA "e.log") | Out-Null
Start-Sleep -Seconds 2

try {
    # ---- 1. 音频内容（CF_WAVE，用 clipput 放真实字节并保持进程存活）----
    $clipput = Join-Path $root "build\clipput.exe"
    if (-not (Test-Path $clipput)) { Say "      缺少 build\clipput.exe，先跑一次 build.ps1"; throw "no clipput" }
    $wav = Join-Path $env:MINICLIP_DATA "test.wav"
    $ms2 = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter($ms2)
    $a = [System.Text.Encoding]::ASCII
    $samples = 8000; $bytes = $samples * 2
    $w.Write($a.GetBytes("RIFF")); $w.Write([int](36 + $bytes)); $w.Write($a.GetBytes("WAVE"))
    $w.Write($a.GetBytes("fmt ")); $w.Write([int]16); $w.Write([int16]1); $w.Write([int16]1)
    $w.Write([int]8000); $w.Write([int]16000); $w.Write([int16]2); $w.Write([int16]16)
    $w.Write($a.GetBytes("data")); $w.Write([int]$bytes)
    for ($i = 0; $i -lt $samples; $i++) { $w.Write([int16]([math]::Sin($i / 20.0) * 12000)) }
    $w.Flush(); [System.IO.File]::WriteAllBytes($wav, $ms2.ToArray()); $w.Close()
    $ap = Start-Process -FilePath $clipput -ArgumentList @("wav", "`"$wav`"", "14") -PassThru -WindowStyle Hidden `
            -RedirectStandardOutput (Join-Path $env:MINICLIP_DATA "put.out") -RedirectStandardError (Join-Path $env:MINICLIP_DATA "put.err")
    Start-Sleep -Milliseconds 1400
    $r1 = Rows (Run-Cli "--dump 10")
    $aud = $r1 | Where-Object { $_.kind -eq "4" } | Select-Object -First 1
    $mediaFiles = @(Get-ChildItem (Join-Path $env:MINICLIP_DATA "media") -EA SilentlyContinue)
    Check "audio-captured" (($aud -ne $null) -and ($mediaFiles.Count -ge 1)) ("kind=4 条目=" + $(if ($aud) { $aud.id }) + " media文件=" + $mediaFiles.Count + " 预览=" + $(if ($aud) { $aud.preview }))

    # 回写：换一条内容再取回，确认剪贴板里又是音频
    [System.Windows.Forms.Clipboard]::SetText("SENTINEL-AUDIO-" + (Get-Random -Maximum 9999))
    Start-Sleep -Milliseconds 600
    if ($aud) {
        Run-Cli ("--copy " + $aud.id) | Out-Null
        Start-Sleep -Milliseconds 800
        $hasAudio = [System.Windows.Forms.Clipboard]::ContainsAudio()
        Check "audio-paste-back" ($hasAudio) "剪贴板 ContainsAudio=$hasAudio"
    }
    try { Stop-Process -Id $ap.Id -Force -EA SilentlyContinue } catch { }

    # ---- 1b. 复制图片内容（CF_DIB）----
    $srcPng = Join-Path $env:MINICLIP_DATA "src.png"
    $bb = New-Object System.Drawing.Bitmap(90, 60)
    $gg = [System.Drawing.Graphics]::FromImage($bb)
    $gg.Clear([System.Drawing.Color]::DeepSkyBlue); $gg.Dispose()
    $bb.Save($srcPng, [System.Drawing.Imaging.ImageFormat]::Png); $bb.Dispose()
    $dp = Start-Process -FilePath $clipput -ArgumentList @("png", "`"$srcPng`"", "8") -PassThru -WindowStyle Hidden `
            -RedirectStandardOutput (Join-Path $env:MINICLIP_DATA "put2.out") -RedirectStandardError (Join-Path $env:MINICLIP_DATA "put2.err")
    Start-Sleep -Milliseconds 1400
    $r1b = Rows (Run-Cli "--dump 10")
    $dib = $r1b | Where-Object { $_.kind -eq "2" } | Select-Object -First 1
    Check "image-content-captured" ($dib -ne $null) ("图片条目=" + $(if ($dib) { $dib.id + " " + $dib.preview }))
    try { Stop-Process -Id $dp.Id -Force -EA SilentlyContinue } catch { }

    # ---- 2. 复制单个图片文件 -> 应存成图片条目并带快照 ----
    $png = Join-Path $env:TEMP ("mc-media-" + (Get-Random) + ".png")
    $b = New-Object System.Drawing.Bitmap(80, 50)
    $g = [System.Drawing.Graphics]::FromImage($b)
    $g.Clear([System.Drawing.Color]::Coral); $g.Dispose()
    $b.Save($png, [System.Drawing.Imaging.ImageFormat]::Png); $b.Dispose()
    $sc = New-Object System.Collections.Specialized.StringCollection
    $sc.Add($png) | Out-Null
    [System.Windows.Forms.Clipboard]::SetFileDropList($sc)
    Start-Sleep -Milliseconds 1200
    $r2 = Rows (Run-Cli "--dump 10")
    $img = $r2 | Where-Object { $_.kind -eq "2" } | Select-Object -First 1
    $snap = @(Get-ChildItem (Join-Path $env:MINICLIP_DATA "media") -EA SilentlyContinue)
    Check "image-file-snapshotted" (($img -ne $null) -and ($snap.Count -ge 2)) ("图片条目=" + $(if ($img) { $img.id }) + " 预览=" + $(if ($img) { $img.preview }) + " media数=" + $snap.Count)
    # 删掉源文件后仍应能取回（证明存的是本体，不是死路径）
    Remove-Item $png -Force -EA SilentlyContinue
    if ($img) {
        Run-Cli ("--copy " + $img.id) | Out-Null
        Start-Sleep -Milliseconds 700
        Check "image-survives-source-deletion" ([System.Windows.Forms.Clipboard]::ContainsImage()) "源文件已删，仍能放回图片"
    }

    # ---- 3. 窗口不再置顶 ----
    Run-Cli "--show" | Out-Null
    Start-Sleep -Milliseconds 800
    if (-not ('ZOrder' -as [type])) {
        Add-Type -Name ZOrder -Namespace '' -MemberDefinition @'
[DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int idx);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
public static bool IsTopmost(IntPtr h) { return (GetWindowLong(h, -20) & 0x8) != 0; }   // GWL_EXSTYLE / WS_EX_TOPMOST
'@
    }
    $p = Get-Process MiniClip | Select-Object -First 1
    $top = [ZOrder]::IsTopmost($p.MainWindowHandle)
    Check "panel-not-topmost" (-not $top) ("WS_EX_TOPMOST=" + $top)

    # ---- 4. 详情区换行（WordWrap=true）----
    $long = ("LONGTEXT-MARK " + ("abcdefghij" * 60))
    [System.Windows.Forms.Clipboard]::SetText($long)
    Start-Sleep -Milliseconds 1200
    $r3 = Rows (Run-Cli "--dump 6")
    $lt = $r3 | Where-Object { $_.preview -like "*LONGTEXT-MARK*" } | Select-Object -First 1
    Check "long-text-captured" ($lt -ne $null) ("chars=" + $(if ($lt) { $lt.chars }))
    if ($lt) { Run-Cli ("--copy " + $lt.id) | Out-Null; Start-Sleep -Milliseconds 600 }
    $out = Join-Path $env:TEMP "mc-media-panel.png"
    if (Test-Path $out) { Remove-Item $out -Force }
    Add-Type -AssemblyName System.Drawing
    $cap = New-Object System.Drawing.Bitmap([System.Windows.Forms.SystemInformation]::VirtualScreen.Width, [System.Windows.Forms.SystemInformation]::VirtualScreen.Height)
    $gc = [System.Drawing.Graphics]::FromImage($cap)
    $gc.CopyFromScreen(0, 0, 0, 0, $cap.Size); $gc.Dispose()
    $cap.Save($out, [System.Drawing.Imaging.ImageFormat]::Png); $cap.Dispose()
    Say ("      截图: " + $out)
}
catch {
    Say ("EXCEPTION " + $_.Exception.GetType().Name + ": " + $_.Exception.Message + " @line " + $_.InvocationInfo.ScriptLineNumber)
    [void]$fails.Add("exception")
}
finally {
    try { Run-Cli "--hide" | Out-Null } catch { }
    Say ""
    if ($fails.Count -eq 0) { Say "MEDIA ALL PASS"; exit 0 }
    Say ("MEDIA FAILED: " + ($fails -join ", ")); exit 1
}
