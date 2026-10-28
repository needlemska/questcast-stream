# Часы для стенда: крупные цифры с миллисекундами, поверх всех окон.
# Их видит захват на стороне отправителя и они же приезжают в кадре получателя.
# Окно DPI-aware и с фиксированным заголовком: стенд находит его через FindWindow
# и знает точный прямоугольник, поэтому OCR не приходится угадывать координаты.
param(
    [int]$X = 40,
    [int]$Y = 40,
    [int]$W = 780,
    [int]$H = 140
)

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class QcsDpi {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
"@
[void][QcsDpi]::SetProcessDPIAware()

$form = New-Object System.Windows.Forms.Form
$form.Text = 'QCS-Clock'
$form.FormBorderStyle = 'None'
$form.StartPosition = 'Manual'
$form.Location = New-Object System.Drawing.Point($X, $Y)
$form.ClientSize = New-Object System.Drawing.Size($W, $H)
$form.TopMost = $true
$form.BackColor = [System.Drawing.Color]::Black
$form.ShowInTaskbar = $false

$label = New-Object System.Windows.Forms.Label
$label.AutoSize = $false
$label.Dock = 'Fill'
$label.TextAlign = 'MiddleCenter'
$label.Font = New-Object System.Drawing.Font('Consolas', 40, [System.Drawing.FontStyle]::Bold)
$label.ForeColor = [System.Drawing.Color]::White
$label.BackColor = [System.Drawing.Color]::Black
$label.Text = '00:00:00.000'
$form.Controls.Add($label)

$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 10
$timer.Add_Tick({ $label.Text = (Get-Date).ToString('HH:mm:ss.fff') })
$timer.Start()

[void]$form.ShowDialog()
