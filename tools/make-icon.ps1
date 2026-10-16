<#
    make-icon.ps1  ——  图标资源代理的产出物
    ------------------------------------------------------------------
    生成 LanMouseShare 的应用程序图标（多尺寸 ICO：16 / 32 / 48），
    并可选导出一张 128x128 的 PNG 预览图用于人工核对。

    设计约定：
      * 16x16 用于系统托盘通知区
      * 32x32 用于任务栏与 Alt+Tab
      * 48x48 用于资源管理器大图标
    只依赖 .NET Framework 自带的 System.Drawing，可在 Windows 7 的
    Windows PowerShell 5.1 下直接运行（不使用任何 PS7 专有语法）。
#>
[CmdletBinding()]
param(
    [string]$OutFile    = (Join-Path $PSScriptRoot '..\src\LanMouseShare.App\Assets\app.ico'),
    [string]$PreviewPng = (Join-Path $PSScriptRoot '_preview\app-icon-preview.png')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-IconBitmap {
    param([int]$Size)

    $f = [single]$Size
    $bmp = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    # ---- 背景：圆角方形，蓝色渐变 ----
    $rad  = [single]($f * 0.16)
    $dia  = [single]($rad * 2.0)
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc(0, 0, $dia, $dia, 180, 90)
    $path.AddArc([single]($f - $dia), 0, $dia, $dia, 270, 90)
    $path.AddArc([single]($f - $dia), [single]($f - $dia), $dia, $dia, 0, 90)
    $path.AddArc(0, [single]($f - $dia), $dia, $dia, 90, 90)
    $path.CloseFigure()

    $rect  = [System.Drawing.Rectangle]::new(0, 0, $Size, $Size)
    $brush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
                $rect,
                [System.Drawing.Color]::FromArgb(255, 59, 130, 246),
                [System.Drawing.Color]::FromArgb(255, 29, 78, 216),
                90.0)
    $g.FillPath($brush, $path)
    $brush.Dispose()
    $path.Dispose()

    # ---- 前景：鼠标指针箭头（白色实心 + 深蓝描边）----
    $pts = [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new([single]($f * 0.30), [single]($f * 0.12)),
        [System.Drawing.PointF]::new([single]($f * 0.30), [single]($f * 0.85)),
        [System.Drawing.PointF]::new([single]($f * 0.445), [single]($f * 0.685)),
        [System.Drawing.PointF]::new([single]($f * 0.55), [single]($f * 0.95)),
        [System.Drawing.PointF]::new([single]($f * 0.665), [single]($f * 0.885)),
        [System.Drawing.PointF]::new([single]($f * 0.56), [single]($f * 0.635)),
        [System.Drawing.PointF]::new([single]($f * 0.79), [single]($f * 0.60))
    )

    $fill = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 255, 255, 255))
    $g.FillPolygon($fill, $pts)
    $fill.Dispose()

    $strokeWidth = [single][Math]::Max(1.0, $f / 18.0)
    $pen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(235, 15, 45, 110), $strokeWidth)
    $g.DrawPolygon($pen, $pts)
    $pen.Dispose()

    $g.Dispose()
    return $bmp
}

function Get-BgraBuffer {
    param([System.Drawing.Bitmap]$Bmp)
    $rect = [System.Drawing.Rectangle]::new(0, 0, $Bmp.Width, $Bmp.Height)
    $data = $Bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $buf = [byte[]]::new($data.Stride * $Bmp.Height)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $buf, 0, $buf.Length)
    $Bmp.UnlockBits($data)
    return , $buf
}

function Get-IconImageBytes {
    param([System.Drawing.Bitmap]$Bmp)

    $w = $Bmp.Width
    $h = $Bmp.Height
    $stride = $w * 4
    $bgra = Get-BgraBuffer -Bmp $Bmp

    $ms = [System.IO.MemoryStream]::new()
    $bw = [System.IO.BinaryWriter]::new($ms)

    # BITMAPINFOHEADER：biHeight 写成 2 倍高度（XOR 图 + AND 掩码）
    $bw.Write([int]40)
    $bw.Write([int]$w)
    $bw.Write([int]($h * 2))
    $bw.Write([int16]1)
    $bw.Write([int16]32)
    $bw.Write([int]0)
    $bw.Write([int]($w * $h * 4))
    $bw.Write([int]0)
    $bw.Write([int]0)
    $bw.Write([int]0)
    $bw.Write([int]0)

    # XOR 位图数据：BGRA，自下而上
    for ($y = $h - 1; $y -ge 0; $y--) {
        $bw.Write($bgra, $y * $stride, $stride)
    }

    # AND 掩码：32bpp 带 alpha 时全 0 即可，每行按 4 字节对齐
    $maskRow = [int][Math]::Ceiling($w / 8.0)
    while ($maskRow % 4 -ne 0) { $maskRow = $maskRow + 1 }
    $mask = [byte[]]::new($maskRow * $h)
    for ($y = 0; $y -lt $h; $y++) {
        $bw.Write($mask, 0, $maskRow)
    }

    $bw.Flush()
    $result = $ms.ToArray()
    $bw.Dispose()
    $ms.Dispose()
    return , $result
}

function Write-IcoFile {
    param([string]$Path, [int[]]$Sizes)

    $images = [System.Collections.ArrayList]::new()
    foreach ($s in $Sizes) {
        $bmp = New-IconBitmap -Size $s
        [void]$images.Add((Get-IconImageBytes -Bmp $bmp))
        $bmp.Dispose()
    }

    $ms = [System.IO.MemoryStream]::new()
    $bw = [System.IO.BinaryWriter]::new($ms)
    $bw.Write([int16]0)             # reserved
    $bw.Write([int16]1)             # type = icon
    $bw.Write([int16]$Sizes.Count)  # image count

    $offset = 6 + 16 * $Sizes.Count
    for ($i = 0; $i -lt $Sizes.Count; $i++) {
        $s = $Sizes[$i]
        $len = $images[$i].Length
        $bw.Write([byte]$s)
        $bw.Write([byte]$s)
        $bw.Write([byte]0)          # color count
        $bw.Write([byte]0)          # reserved
        $bw.Write([int16]1)         # planes
        $bw.Write([int16]32)        # bit count
        $bw.Write([int]$len)
        $bw.Write([int]$offset)
        $offset = $offset + $len
    }
    foreach ($img in $images) {
        $bw.Write($img, 0, $img.Length)
    }
    $bw.Flush()
    [System.IO.File]::WriteAllBytes($Path, $ms.ToArray())
    $bw.Dispose()
    $ms.Dispose()
}

# ---------------------------- 主流程 ----------------------------

$outDir = Split-Path -Parent $OutFile
if (-not (Test-Path $outDir)) {
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
}

if ($PreviewPng) {
    $pDir = Split-Path -Parent $PreviewPng
    if ($pDir -and -not (Test-Path $pDir)) {
        New-Item -ItemType Directory -Path $pDir -Force | Out-Null
    }
    $big = New-IconBitmap -Size 128
    $big.Save($PreviewPng, [System.Drawing.Imaging.ImageFormat]::Png)
    $big.Dispose()
    Write-Output ("PNG preview: {0}" -f $PreviewPng)
}

Write-IcoFile -Path $OutFile -Sizes @(16, 32, 48)

# ---------------------------- 自检 ----------------------------
$bytes = [System.IO.File]::ReadAllBytes($OutFile)
$count = [BitConverter]::ToInt16($bytes, 4)
Write-Output ("ICO created: {0}  ({1} bytes, {2} entries)" -f $OutFile, $bytes.Length, $count)
for ($i = 0; $i -lt $count; $i++) {
    $off    = 6 + 16 * $i
    $w      = $bytes[$off]
    $h      = $bytes[$off + 1]
    $len    = [BitConverter]::ToInt32($bytes, $off + 8)
    $imgOff = [BitConverter]::ToInt32($bytes, $off + 12)
    $bihW   = [BitConverter]::ToInt32($bytes, $imgOff + 4)
    $bihH   = [BitConverter]::ToInt32($bytes, $imgOff + 8)
    $bpp    = [BitConverter]::ToInt16($bytes, $imgOff + 14)
    Write-Output ("  entry {0}: dir={1}x{2} biW={3} biH={4} bpp={5} bytes={6}" -f $i, $w, $h, $bihW, $bihH, $bpp, $len)
}
foreach ($size in @(16, 32, 48)) {
    $fs = [System.IO.File]::OpenRead($OutFile)
    $ic = [System.Drawing.Icon]::new($fs, [System.Drawing.Size]::new($size, $size))
    Write-Output ("  load at {0}: got {1}x{2}" -f $size, $ic.Width, $ic.Height)
    $ic.Dispose()
    $fs.Dispose()
}
