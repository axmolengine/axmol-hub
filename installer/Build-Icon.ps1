param(
    [string]$Source = "$PSScriptRoot/../src/AxmolHub/Assets/hub-icon.png",
    [string]$Output = "$PSScriptRoot/../src/AxmolHub/Assets/hub-icon.ico",
    # Linux additionally needs **on-disk single-size PNGs**: 256 for the window icon (_NET_WM_ICON is passed raw ARGB32,
    # so the size directly determines this X property's byte cost — the 1254 original is about 6 MB), 512 for vpk's icon argument
    # (it becomes both .DirIcon and the hicolor icon inside the AppDir) and the icon themes' 512 slot.
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
            # The ICO only takes the standard eight sizes; oversized entries like 512 are emitted as standalone PNG files, not stuffed into the ICO.
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
