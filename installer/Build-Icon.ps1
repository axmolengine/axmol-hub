param(
    [string]$Source = "$PSScriptRoot/../src/AxmolHub/Assets/hub-icon.png",
    [string]$Output = "$PSScriptRoot/../src/AxmolHub/Assets/hub-icon.ico",
    # Linux 另外要**磁盘上的单尺寸 PNG**：256 给窗口图标（_NET_WM_ICON 传的是原始 ARGB32，
    # 尺寸直接决定这个 X property 的大小 —— 1254 原图约 6 MB），512 给 vpk 的 icon 参数
    # （它同时成为 .DirIcon 与 AppDir 里的 hicolor 图标）以及图标主题的 512 档。
    [int[]]$PngSizes = @(256, 512)
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$icoSizes = @(16, 20, 24, 32, 48, 64, 128, 256)
$extraSizes = @($PngSizes | Where-Object { $icoSizes -notcontains $_ })
$pngDirectory = Split-Path -Parent ([IO.Path]::GetFullPath($Output))
$sourceImage = [Drawing.Image]::FromFile((Resolve-Path -LiteralPath $Source).Path)
$frames = @()
try {
    foreach ($size in @($icoSizes) + $extraSizes) {
        $bitmap = New-Object Drawing.Bitmap $size, $size
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $stream = New-Object IO.MemoryStream
        try {
            $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.DrawImage($sourceImage, 0, 0, $size, $size)
            $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
            $bytes = $stream.ToArray()
            # ICO 只收标准八档；512 这类超出尺寸的档只做单文件 PNG，不塞进 ICO。
            if ($icoSizes -contains $size) { $frames += [pscustomobject]@{Size=$size; Bytes=$bytes} }
            if ($PngSizes -contains $size) {
                [IO.File]::WriteAllBytes((Join-Path $pngDirectory ("hub-icon-{0}.png" -f $size)), $bytes)
            }
        } finally { $stream.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
    }
    $file = [IO.File]::Create([IO.Path]::GetFullPath($Output))
    $writer = New-Object IO.BinaryWriter $file
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
        $offset = 6 + 16 * $frames.Count
        foreach ($frame in $frames) {
            $dimension = if ($frame.Size -eq 256) {0} else {$frame.Size}
            $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
            $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$frame.Bytes.Length); $writer.Write([uint32]$offset)
            $offset += $frame.Bytes.Length
        }
        foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
    } finally { $writer.Dispose(); $file.Dispose() }
} finally { $sourceImage.Dispose() }
Write-Output ("Built icon: {0} ({1} PNG frames) + {2} px PNGs in {3}" -f $Output, $frames.Count, ($PngSizes -join '/'), $pngDirectory)
