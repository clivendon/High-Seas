param(
    [Parameter(Mandatory)] [string] $SourcePng,
    [Parameter(Mandatory)] [string] $DestinationIco
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# Windows selects different frames for title bars, taskbar buttons, Start, and Explorer. Supplying
# each common size prevents the shell from shrinking one detailed 256px frame into a muddy icon.
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$source = [System.Drawing.Image]::FromFile((Resolve-Path -LiteralPath $SourcePng))
$frames = [System.Collections.Generic.List[byte[]]]::new()

try
{
    foreach ($size in $sizes)
    {
        $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try
        {
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            try
            {
                $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
                $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
                $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
                $graphics.DrawImage($source, [System.Drawing.Rectangle]::new(0, 0, $size, $size))
            }
            finally { $graphics.Dispose() }

            # Store each frame as a 32-bit Windows DIB instead of a PNG-compressed ICO frame.
            # Older shell and System.Drawing paths still used by WinForms can misread PNG frames.
            $stream = [System.IO.MemoryStream]::new()
            $frameWriter = [System.IO.BinaryWriter]::new($stream)
            try
            {
                $maskStride = [int]([math]::Ceiling($size / 32.0) * 4)
                $frameWriter.Write([uint32]40)                    # BITMAPINFOHEADER size.
                $frameWriter.Write([int32]$size)
                $frameWriter.Write([int32]($size * 2))            # XOR image plus AND mask.
                $frameWriter.Write([uint16]1)
                $frameWriter.Write([uint16]32)
                $frameWriter.Write([uint32]0)                     # BI_RGB.
                $frameWriter.Write([uint32]($size * $size * 4))
                $frameWriter.Write([int32]0)
                $frameWriter.Write([int32]0)
                $frameWriter.Write([uint32]0)
                $frameWriter.Write([uint32]0)

                # ICO DIB pixels are BGRA and bottom-up.
                for ($y = $size - 1; $y -ge 0; $y--)
                {
                    for ($x = 0; $x -lt $size; $x++)
                    {
                        $pixel = $bitmap.GetPixel($x, $y)
                        $frameWriter.Write([byte]$pixel.B)
                        $frameWriter.Write([byte]$pixel.G)
                        $frameWriter.Write([byte]$pixel.R)
                        $frameWriter.Write([byte]$pixel.A)
                    }
                }

                # The alpha channel handles soft edges; the legacy mask preserves hard transparency.
                for ($y = $size - 1; $y -ge 0; $y--)
                {
                    $mask = [byte[]]::new($maskStride)
                    for ($x = 0; $x -lt $size; $x++)
                    {
                        if ($bitmap.GetPixel($x, $y).A -lt 128)
                        {
                            $byteIndex = [int]($x / 8)
                            $mask[$byteIndex] = $mask[$byteIndex] -bor (1 -shl (7 - ($x % 8)))
                        }
                    }
                    $frameWriter.Write($mask)
                }

                $frameWriter.Flush()
                $frames.Add($stream.ToArray())
            }
            finally
            {
                $frameWriter.Dispose()
                $stream.Dispose()
            }
        }
        finally { $bitmap.Dispose() }
    }
}
finally { $source.Dispose() }

$outputFolder = Split-Path -Parent $DestinationIco
if ($outputFolder) { [System.IO.Directory]::CreateDirectory($outputFolder) | Out-Null }
$file = [System.IO.File]::Open($DestinationIco, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write)
$writer = [System.IO.BinaryWriter]::new($file)

try
{
    $writer.Write([uint16]0)                  # Reserved.
    $writer.Write([uint16]1)                  # ICO image type.
    $writer.Write([uint16]$frames.Count)
    $offset = 6 + (16 * $frames.Count)

    for ($index = 0; $index -lt $frames.Count; $index++)
    {
        $size = $sizes[$index]
        $frame = $frames[$index]
        $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
        $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
        $writer.Write([byte]0)                # Palette size; PNG frames are true color.
        $writer.Write([byte]0)
        $writer.Write([uint16]1)              # Color planes.
        $writer.Write([uint16]32)             # Bits per pixel.
        $writer.Write([uint32]$frame.Length)
        $writer.Write([uint32]$offset)
        $offset += $frame.Length
    }

    foreach ($frame in $frames) { $writer.Write($frame) }
}
finally
{
    $writer.Dispose()
    $file.Dispose()
}

[pscustomobject]@{ Path = (Resolve-Path -LiteralPath $DestinationIco).Path; Frames = $sizes -join ', '; Bytes = (Get-Item -LiteralPath $DestinationIco).Length }
