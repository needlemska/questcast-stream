# Стенд сквозной задержки для этапа 1.
#
#   [окно часов] --ddagrab--> crop --hevc_amf--> mpegts/udp --> [приёмник ffmpeg]
#
# Приёмник сохраняет кадры в файлы, у каждого файла есть время записи. На кадре
# нарисованы часы отправителя, поэтому задержка = время записи файла минус
# нарисованное время. В число входят захват, кодирование, сеть, демуксинг и декод;
# не входят вывод на экран и скан-аут монитора (это ещё ~8 мс на 60 Гц).
#
# Запуск:
#   powershell -NoProfile -ExecutionPolicy Bypass -File bench\glass2glass.ps1 -Samples 20
param(
    [int]$Samples = 20,
    [int]$WarmupSec = 5,
    [int]$VideoW = 1280,
    [int]$VideoH = 720,
    [string]$Encoder = 'hevc_amf',
    [string]$Bitrate = '20M',
    [int]$Fps = 60,
    [int]$CaptureFps = 2,
    [string]$Out = ''
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here
$python = 'C:\Users\lenov\.dsh\dsh-runtimes\dsh-primary-runtime\dependencies\python\python.exe'
if (-not (Test-Path $python)) { $python = 'python' }
$ffmpeg = (Get-Command ffmpeg).Source
if (-not $Out) { $Out = Join-Path $root ("results\glass2glass-{0}-{1}x{2}.csv" -f $Encoder, $VideoW, $VideoH) }

$Every = [Math]::Max(1, [int]($Fps / [Math]::Max(1, $CaptureFps)))
$frames = Join-Path $env:TEMP 'qcs-recv'
Remove-Item $frames -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $frames | Out-Null

"стенд: $VideoW x $VideoH @ $Fps fps, $Encoder $Bitrate, снимков $Samples по $CaptureFps к/с"

$clockProc = Start-Process -FilePath 'powershell.exe' -ArgumentList @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $here 'clock-window.ps1')
) -PassThru

$receiver = $null
$sender = $null
try {
    Start-Sleep -Seconds 2

    $receiver = Start-Process -FilePath $ffmpeg -ArgumentList @(
        '-hide_banner', '-loglevel', 'error', '-nostdin',
        '-fflags', 'nobuffer', '-flags', 'low_delay', '-probesize', '32', '-analyzeduration', '0', '-threads', '1',
        '-i', 'udp://127.0.0.1:5000?buffer_size=65536&fifo_size=128&overrun_nonfatal=1',
        '-vf', "select=not(mod(n\,$Every))", '-vsync', '0', '-q:v', '3',
        '-f', 'image2', (Join-Path $frames 'f%04d.png')
    ) -PassThru -WindowStyle Hidden

    $sender = Start-Process -FilePath $ffmpeg -ArgumentList @(
        '-hide_banner', '-loglevel', 'error', '-nostdin',
        '-f', 'lavfi', '-i', "ddagrab=output_idx=0:framerate=$Fps",
        '-vf', "hwdownload,format=bgra,crop=$($VideoW):$($VideoH):0:0",
        # -muxdelay/-muxpreload по умолчанию дают mpegts ~0.7 с буфера: именно они
        # и держали «задержку» в 600 мс на loopback
        '-c:v', $Encoder, '-usage', 'lowlatency', '-b:v', $Bitrate, '-g', "$Fps", '-rc', 'cbr', '-bf', '0', '-muxdelay', '0', '-muxpreload', '0',
        '-f', 'mpegts', 'udp://127.0.0.1:5000?pkt_size=1316'
    ) -PassThru -WindowStyle Hidden

    "прогрев $WarmupSec с..."
    Start-Sleep -Seconds $WarmupSec
    $deadline = (Get-Date).AddSeconds([Math]::Max(20, $Samples * 3))
    while ((Get-Date) -lt $deadline) {
        $n = @(Get-ChildItem $frames -Filter '*.png' -ErrorAction SilentlyContinue).Count
        if ($n -ge $Samples + 2) { break }
        Start-Sleep -Milliseconds 200
    }
}
finally {
    foreach ($p in @($sender, $receiver, $clockProc)) { if ($p -and -not $p.HasExited) { try { $p.Kill() } catch {} } }
}

$rows = New-Object System.Collections.Generic.List[object]
$files = @(Get-ChildItem $frames -Filter '*.png' | Sort-Object Name)
"кадров пришло: $($files.Count)"

foreach ($f in $files) {
    $json = & $python (Join-Path $here 'ocr_clock.py') $f.FullName --auto 2>$null
    try { $o = $json | ConvertFrom-Json } catch { $o = $null }
    if (-not ($o -and $o.ok)) { continue }

    $written = [int64][Math]::Round((($f.LastWriteTime) - (Get-Date).Date).TotalMilliseconds)
    $drawn = [int64]$o.drawn_ms
    $delta = $written - $drawn
    if ($delta -lt 0) { $delta += 24 * 3600 * 1000 }

    $rows.Add([pscustomobject]@{
        file = $f.Name; drawn = $o.drawn; written = $f.LastWriteTime.ToString('HH:mm:ss.fff'); delta_ms = [int]$delta
    })
    "  {0}  нарисовано {1}  записано {2}  -> {3} мс" -f $f.Name, $o.drawn, $f.LastWriteTime.ToString('HH:mm:ss.fff'), $delta
}

$values = @($rows | ForEach-Object { $_.delta_ms } | Sort-Object)
if ($values.Count -eq 0) { throw 'ни одного замера: часы не попали в кадр или приёмник не получил поток' }

$pct = { param($p) $values[[Math]::Min($values.Count - 1, [int][Math]::Floor($values.Count * $p))] }
$stats = [pscustomobject]@{
    video = "$($VideoW)x$($VideoH)"; fps = $Fps; encoder = $Encoder; bitrate = $Bitrate
    samples = $values.Count
    p50_ms = & $pct 0.5; p95_ms = & $pct 0.95
    min_ms = $values[0]; max_ms = $values[-1]
    mean_ms = [math]::Round(($values | Measure-Object -Average).Average, 1)
}

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Out) | Out-Null
$rows | Export-Csv -Path $Out -NoTypeInformation -Encoding UTF8
$stats | ConvertTo-Json | Tee-Object -FilePath ($Out -replace '\.csv$', '.summary.json')
"CSV: $Out"
