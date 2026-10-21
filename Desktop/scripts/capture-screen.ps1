# 连拍主显示器的一块区域（物理像素），用于核对启动动画和右下角面板在真实桌面上的效果。
# 默认只截“屏幕中心偏左上 200px → 右下角”这一块，并缩小保存，尽量少拍到桌面上的其他内容。
param(
  [Parameter(Mandatory = $true)][string]$Out,
  [int]$Frames = 1,
  [int]$IntervalMs = 200,
  [string]$Region = '',
  [double]$Scale = 0.5,
  [int]$DelayMs = 0
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;
public static class HpsDpi { [DllImport("user32.dll")] public static extern bool SetProcessDPIAware(); }
'@
[HpsDpi]::SetProcessDPIAware() | Out-Null

$scr = [System.Windows.Forms.Screen]::PrimaryScreen
$b = $scr.Bounds
if ($Region) {
  $x, $y, $w, $h = $Region.Split(',') | ForEach-Object { [int]$_ }
} else {
  $x = [int]($b.X + $b.Width / 2 - 200)
  $y = [int]($b.Y + $b.Height / 2 - 200)
  $w = $b.Right - $x
  $h = $b.Bottom - $y
}
New-Item -ItemType Directory -Force -Path $Out | Out-Null
# 屏幕信息写到文件，便于脚本换算窗口坐标
"bounds=$($b.X),$($b.Y),$($b.Width),$($b.Height) work=$($scr.WorkingArea.X),$($scr.WorkingArea.Y),$($scr.WorkingArea.Width),$($scr.WorkingArea.Height) region=$x,$y,$w,$h scale=$Scale" |
  Set-Content -Encoding UTF8 (Join-Path $Out 'screen.txt')

if ($DelayMs -gt 0) { Start-Sleep -Milliseconds $DelayMs }
$ow = [int]($w * $Scale)
$oh = [int]($h * $Scale)
$start = [Environment]::TickCount
for ($i = 0; $i -lt $Frames; $i++) {
  $t0 = [Environment]::TickCount
  $bmp = New-Object System.Drawing.Bitmap $w, $h
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($x, $y, 0, 0, $bmp.Size)
  $g.Dispose()
  $small = New-Object System.Drawing.Bitmap $bmp, $ow, $oh
  $ms = [Environment]::TickCount - $start
  $small.Save((Join-Path $Out ('f{0:D2}-{1:D5}ms.png' -f $i, $ms)), [System.Drawing.Imaging.ImageFormat]::Png)
  $small.Dispose()
  $bmp.Dispose()
  $left = $IntervalMs - ([Environment]::TickCount - $t0)
  if ($left -gt 0) { Start-Sleep -Milliseconds $left }
}
"frames=$Frames region=$x,$y,$w,$h"
