Add-Type -AssemblyName System.Drawing

$assetDirectory = Join-Path $PSScriptRoot '..\Assets'
New-Item -ItemType Directory -Force -Path $assetDirectory | Out-Null
$iconPath = Join-Path $assetDirectory 'BalanceDock.ico'

$bitmap = New-Object System.Drawing.Bitmap 64, 64
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([System.Drawing.Color]::Transparent)

$bounds = New-Object System.Drawing.RectangleF 4, 4, 56, 56
$radius = 14.0
$diameter = $radius * 2
$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$path.AddArc($bounds.Left, $bounds.Top, $diameter, $diameter, 180, 90)
$path.AddArc($bounds.Right - $diameter, $bounds.Top, $diameter, $diameter, 270, 90)
$path.AddArc($bounds.Right - $diameter, $bounds.Bottom - $diameter, $diameter, $diameter, 0, 90)
$path.AddArc($bounds.Left, $bounds.Bottom - $diameter, $diameter, $diameter, 90, 90)
$path.CloseFigure()

$green = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 16, 163, 127))
$graphics.FillPath($green, $path)
$pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), 5
$pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$graphics.DrawLine($pen, 17, 25, 47, 25)
$graphics.DrawLine($pen, 17, 39, 37, 39)

$handle = $bitmap.GetHicon()
$icon = [System.Drawing.Icon]::FromHandle($handle).Clone()
$stream = [System.IO.File]::Create($iconPath)
$icon.Save($stream)
$stream.Dispose()
$icon.Dispose()
$pen.Dispose()
$green.Dispose()
$path.Dispose()
$graphics.Dispose()
$bitmap.Dispose()

Write-Output $iconPath
