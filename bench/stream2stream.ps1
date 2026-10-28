# Сквозная задержка «экран -> свой транспорт -> декодер».
#
# Цепь: окно-часы -> ddagrab (D3D11-текстура, кроп на GPU) -> hevc_amf
#       -> live-send (наш UDP, пакеты <=1200) -> live-recv -> декод -> PNG
#
# Мерило: время записи PNG минус время, нарисованное на кадре.
#
#   .\bench\stream2stream.ps1                          # 1440x1440, 60 к/с, 20 с
#   .\bench\stream2stream.ps1 -Seconds 60 -Size 1920x1080 -HwDecode:$false
param(
    [string]$Size = '1440x1440',
    [int]$Seconds = 20,
    [int]$Fps = 60,
    [int]$BitrateM = 25,
    [int]$Port = 5003,
    [int]$SampleFps = 8,
    [string]$Encoder = 'hevc_amf',
    [bool]$HwDecode = $true
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'src\QuestCastStream.Cli\bin\Release\net10.0\questcast-stream.exe'
$ffmpeg = (Get-Command ffmpeg).Source
$python = 'C:\Users\lenov\.dsh\dsh-runtimes\dsh-primary-runtime\dependencies\python\python.exe'
$run = Join-Path $root ("results\run\s2s-{0}-{1}" -f $Encoder, $Size)
Remove-Item $run -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $run | Out-Null

$w, $h = $Size.Split('x') | ForEach-Object { [int]$_ }

$dec = if ($HwDecode) {
    '-probesize 500k -analyzeduration 0 -hwaccel d3d11va -f hevc -i pipe:0 -vf "fps=' + $SampleFps + '"'
} else {
    '-probesize 500k -analyzeduration 0 -f hevc -i pipe:0 -vf "fps=' + $SampleFps + '"'
}

$src = "ddagrab=output_idx=0:framerate=${Fps}:video_size=${w}x${h}:offset_x=0:offset_y=0"

$recvCmd = @"
@echo off
"$exe" live-recv --port $Port --seconds $($Seconds + 3) 2> "$run\recv.json" | "$ffmpeg" -hide_banner -loglevel error $dec -f image2 "$run\f%%05d.png" 2> "$run\dec.log"
echo done> "$run\recv.done"
"@

$sendCmd = @"
@echo off
"$ffmpeg" -hide_banner -loglevel error -nostdin -init_hw_device d3d11va=amd -filter_hw_device amd -f lavfi -i "$src" -c:v $Encoder -usage lowlatency -quality speed -b:v ${BitrateM}M -g $($Fps*2) -bf 0 -f hevc - 2> "$run\enc.log" | "$exe" live-send --stdin --port $Port --fps 0 --seconds $Seconds 2> "$run\send.json"
echo done> "$run\send.done"
"@

Set-Content "$run\recv.cmd" -Value $recvCmd -Encoding ASCII
Set-Content "$run\send.cmd" -Value $sendCmd -Encoding ASCII

$clock = Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',"$PSScriptRoot\clock-window.ps1",'-X','20','-Y','20','-W','700','-H','130') -PassThru
Start-Sleep -Seconds 2
$recv = Start-Process -FilePath "$run\recv.cmd" -PassThru -WindowStyle Hidden
Start-Sleep -Seconds 1
$t0 = Get-Date
$send = Start-Process -FilePath "$run\send.cmd" -PassThru -WindowStyle Hidden -Wait
$elapsed = ((Get-Date) - $t0).TotalSeconds
Start-Sleep -Seconds 4
if (-not $clock.HasExited) { $clock.Kill() }

$rows = @()
foreach ($png in Get-ChildItem "$run\f*.png" | Sort-Object Name) {
    $ocr = & $python "$PSScriptRoot\ocr_clock.py" $png.FullName --video ${w}x${h} --clock 20,20,700,130 2>$null
    if (-not $ocr) { continue }
    try { $j = ($ocr | Out-String).Trim() | ConvertFrom-Json } catch { continue }
    if (-not $j.ok -or -not $j.drawn) { continue }
    try { $drawn = [datetime]::ParseExact($j.drawn, 'HH:mm:ss.fff', $null) } catch { continue }
    $lat = ($png.LastWriteTime - $drawn).TotalMilliseconds
    if ($lat -lt 0 -or $lat -gt 5000) { continue }
    $rows += [pscustomobject]@{ file = $png.Name; drawn = $j.drawn; mtime = $png.LastWriteTime; ms = [math]::Round($lat, 1) }
}

$csv = Join-Path $root ("results\stream2stream-{0}-{1}.csv" -f $Encoder, $Size)
$rows | Export-Csv -Path $csv -NoTypeInformation -Encoding UTF8
$vals = $rows.ms | Sort-Object
$summary = [pscustomobject]@{
    encoder = $Encoder
    size = $Size
    fps = $Fps
    bitrate_m = $BitrateM
    hw_decode = $HwDecode
    samples = $vals.Count
    p50 = if ($vals.Count) { [math]::Round($vals[[int]($vals.Count * 0.5)], 1) } else { $null }
    p95 = if ($vals.Count) { [math]::Round($vals[[math]::Min($vals.Count - 1, [int]($vals.Count * 0.95))], 1) } else { $null }
    min = if ($vals.Count) { $vals[0] } else { $null }
    max = if ($vals.Count) { $vals[-1] } else { $null }
    wall_seconds = [math]::Round($elapsed, 2)
    send = (Get-Content "$run\send.json" -ErrorAction SilentlyContinue | Out-String).Trim()
    recv = (Get-Content "$run\recv.json" -ErrorAction SilentlyContinue | Out-String).Trim()
}
$summary | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $root ("results\stream2stream-{0}-{1}.summary.json" -f $Encoder, $Size)) -Encoding UTF8
$summary | ConvertTo-Json -Depth 4
