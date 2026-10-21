Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$assets = Join-Path $root 'assets'
New-Item -ItemType Directory -Force -Path $assets | Out-Null

# 直接按 48x48 设计网格绘制，不依赖复合变换
function Draw-Glyph([System.Drawing.Graphics]$g, [double]$s, [System.Drawing.Pen]$pen, [System.Drawing.Brush]$fill) {
  $g.DrawArc($pen, [float](6 * $s), [float](3.05 * $s), [float](36 * $s), [float](36 * $s), 124, 292)
  $g.DrawArc($pen, [float](13 * $s), [float](11.68 * $s), [float](22 * $s), [float](22 * $s), 124, 292)
  $tri = @(
    (New-Object System.Drawing.PointF([float](24 * $s), [float](28 * $s))),
    (New-Object System.Drawing.PointF([float](33.5 * $s), [float](42 * $s))),
    (New-Object System.Drawing.PointF([float](14.5 * $s), [float](42 * $s)))
  )
  $g.FillPolygon($fill, $tri)
}

# ---------- 96x96 母版字形 ----------
$master = New-Object System.Drawing.Bitmap(96, 96, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$gm = [System.Drawing.Graphics]::FromImage($master)
$gm.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$gm.Clear([System.Drawing.Color]::Transparent)
$pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, 6)
$pen.StartCap = 'Round'; $pen.EndCap = 'Round'
Draw-Glyph $gm 2.0 $pen ([System.Drawing.Brushes]::White)
$gm.Dispose()

# ---------- 托盘 32/64：母版缩小 ----------
foreach ($size in 32, 64) {
  $b = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($b)
  $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
  $g.Clear([System.Drawing.Color]::Transparent)
  $g.DrawImage($master, (New-Object System.Drawing.Rectangle(0, 0, $size, $size)))
  if ($size -eq 64) { $b.Save((Join-Path $assets 'tray.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
  $g.Dispose(); $b.Dispose()
}

# ---------- 256 应用图标：粉红圆角方块 + 白色字形 ----------
$size = 256
$b = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($b)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.Clear([System.Drawing.Color]::Transparent)

$radius = 56.0
$d = $radius * 2
$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$path.AddArc(0, 0, $d, $d, 180, 90)
$path.AddArc($size - $d, 0, $d, $d, 270, 90)
$path.AddArc($size - $d, $size - $d, $d, $d, 0, 90)
$path.AddArc(0, $size - $d, $d, $d, 90, 90)
$path.CloseFigure()

$bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
  (New-Object System.Drawing.RectangleF(0, 0, $size, $size)),
  [System.Drawing.Color]::FromArgb(255, 255, 128, 158),
  [System.Drawing.Color]::FromArgb(255, 210, 18, 60),
  45.0)
$g.FillPath($bg, $path)

$sheenRect = New-Object System.Drawing.RectangleF(0, 0, $size, 150)
$sheen = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
  $sheenRect,
  [System.Drawing.Color]::FromArgb(78, 255, 255, 255),
  [System.Drawing.Color]::FromArgb(0, 255, 255, 255),
  90.0)
$g.SetClip($path)
$g.FillRectangle($sheen, 0, 0, $size, 150)
$g.ResetClip()

$glyphSize = 188
$gx = [int](($size - $glyphSize) / 2)
$gy = [int](($size - $glyphSize) / 2 + 2)
$dst = New-Object System.Drawing.Rectangle -ArgumentList @($gx, $gy, $glyphSize, $glyphSize)
$g.DrawImage($master, $dst)

$png = Join-Path $assets 'icon.png'
$b.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)

# 托盘也用彩色图标，浅色/深色任务栏都看得见（Electron 会自动缩放）
Copy-Item $png (Join-Path $assets 'tray.png') -Force

$g.Dispose(); $b.Dispose()
$master.Dispose()

# ---------- PNG 包成 ICO（256x256） ----------
$pngBytes = [System.IO.File]::ReadAllBytes($png)
$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]1)
$bw.Write([Byte]0); $bw.Write([Byte]0); $bw.Write([Byte]0); $bw.Write([Byte]0)
$bw.Write([UInt16]1); $bw.Write([UInt16]32)
$bw.Write([UInt32]$pngBytes.Length); $bw.Write([UInt32]22)
$bw.Write($pngBytes)
$bw.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $assets 'icon.ico'), $ms.ToArray())
$bw.Dispose(); $ms.Dispose()

Write-Output "icons written to $assets"

# ---------- Tauri 图标集 ----------
$tauriIcons = Join-Path $root 'src-tauri\icons'
New-Item -ItemType Directory -Force -Path $tauriIcons | Out-Null
Copy-Item (Join-Path $assets 'icon.ico') (Join-Path $tauriIcons 'icon.ico') -Force
Copy-Item (Join-Path $assets 'icon.png') (Join-Path $tauriIcons 'icon.png') -Force
$src = [System.Drawing.Image]::FromFile((Join-Path $assets 'icon.png'))
foreach ($s in 32, 128, 256) {
  $bmp = New-Object System.Drawing.Bitmap $s, $s
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
  $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $g.DrawImage($src, 0, 0, $s, $s)
  $name = if ($s -eq 32) { '32x32.png' } elseif ($s -eq 128) { '128x128.png' } else { '128x128@2x.png' }
  $bmp.Save((Join-Path $tauriIcons $name), [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
}
$src.Dispose()
Write-Output "tauri icons written to $tauriIcons"
