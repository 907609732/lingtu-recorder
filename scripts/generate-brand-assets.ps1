[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Source,

    [string]$AlphaMask,

    [string]$ProcessedSourceDestination
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sourcePath = [System.IO.Path]::GetFullPath($Source)
if (-not (Test-Path -LiteralPath $sourcePath)) {
    throw "Source image not found: $sourcePath"
}

$desktopAssets = Join-Path $repositoryRoot 'src\UnpackVision.App\Assets'
$androidResources = Join-Path $repositoryRoot 'mobile\UnpackVision.Android\app\src\main\res'
$readmeAssets = Join-Path $repositoryRoot 'docs\assets'
$websiteAssets = Join-Path $repositoryRoot 'website\public'
$qaAssets = Join-Path $repositoryRoot 'design\brand\qa-2.6.0'
New-Item -ItemType Directory -Force -Path $desktopAssets, $readmeAssets, $websiteAssets, $qaAssets | Out-Null

function New-TransparentMaster([string]$path) {
    $sourceBitmap = [System.Drawing.Bitmap]::new($path)
    try {
        # Generated blue/cyan brand colors must never be interpreted as a chroma key.
        # Background extraction is explicit through AlphaMask; the default preserves RGBA.
        return $sourceBitmap.Clone(
            [System.Drawing.Rectangle]::new(0, 0, $sourceBitmap.Width, $sourceBitmap.Height),
            [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    }
    finally {
        $sourceBitmap.Dispose()
    }
}

function Apply-AlphaMask([System.Drawing.Bitmap]$source, [string]$maskPath) {
    $originalMask = [System.Drawing.Bitmap]::new($maskPath)
    $mask = [System.Drawing.Bitmap]::new(
        $source.Width,
        $source.Height,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $maskGraphics = [System.Drawing.Graphics]::FromImage($mask)
        try {
            $maskGraphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $maskGraphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $maskGraphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $maskGraphics.DrawImage($originalMask, 0, 0, $source.Width, $source.Height)
        }
        finally {
            $maskGraphics.Dispose()
        }

        $result = [System.Drawing.Bitmap]::new(
            $source.Width,
            $source.Height,
            [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        for ($y = 0; $y -lt $source.Height; $y++) {
            for ($x = 0; $x -lt $source.Width; $x++) {
                $pixel = $source.GetPixel($x, $y)
                # Intersect the explicit silhouette with the source alpha so transparent
                # generated pixels never become opaque black around the outline.
                $alpha = [int][Math]::Round($mask.GetPixel($x, $y).A * $pixel.A / 255.0)
                $result.SetPixel($x, $y, [System.Drawing.Color]::FromArgb($alpha, $pixel.R, $pixel.G, $pixel.B))
            }
        }
        return $result
    }
    finally {
        $mask.Dispose()
        $originalMask.Dispose()
    }
}

function Save-ResizedPng(
    [System.Drawing.Bitmap]$source,
    [int]$size,
    [string]$destination,
    [System.Drawing.Color]$background = [System.Drawing.Color]::Transparent,
    [double]$contentScale = 1.0
) {
    $directory = Split-Path -Parent $destination
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.Clear($background)
        $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceOver
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $ratio = [Math]::Min($size / $source.Width, $size / $source.Height) * $contentScale
        $width = [int][Math]::Round($source.Width * $ratio)
        $height = [int][Math]::Round($source.Height * $ratio)
        $graphics.DrawImage($source, [int](($size - $width) / 2), [int](($size - $height) / 2), $width, $height)
        $bitmap.Save($destination, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

function Write-MultiSizeIcon([System.Drawing.Bitmap]$source, [string]$destination) {
    $sizes = @(16, 24, 32, 48, 64, 128, 256)
    $images = @()
    try {
        foreach ($size in $sizes) {
            $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $graphics.DrawImage($source, 0, 0, $size, $size)
            $stream = [System.IO.MemoryStream]::new()
            $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            $images += ,@($size, $stream.ToArray())
            $stream.Dispose()
            $graphics.Dispose()
            $bitmap.Dispose()
        }

        $file = [System.IO.File]::Create($destination)
        $writer = [System.IO.BinaryWriter]::new($file)
        try {
            $writer.Write([UInt16]0)
            $writer.Write([UInt16]1)
            $writer.Write([UInt16]$images.Count)
            $offset = 6 + (16 * $images.Count)
            foreach ($image in $images) {
                $size = [int]$image[0]
                $bytes = [byte[]]$image[1]
                $dimension = if ($size -ge 256) { 0 } else { $size }
                $writer.Write([byte]$dimension)
                $writer.Write([byte]$dimension)
                $writer.Write([byte]0)
                $writer.Write([byte]0)
                $writer.Write([UInt16]1)
                $writer.Write([UInt16]32)
                $writer.Write([UInt32]$bytes.Length)
                $writer.Write([UInt32]$offset)
                $offset += $bytes.Length
            }
            foreach ($image in $images) {
                $writer.Write([byte[]]$image[1])
            }
        }
        finally {
            $writer.Dispose()
        }
    }
    finally {
        $images = @()
    }
}

function Save-WebsiteShareImage([System.Drawing.Bitmap]$source, [string]$destination) {
    $bitmap = [System.Drawing.Bitmap]::new(1200, 630, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $background = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        [System.Drawing.Point]::new(0, 0),
        [System.Drawing.Point]::new(1200, 630),
        [System.Drawing.Color]::White,
        [System.Drawing.ColorTranslator]::FromHtml('#EBF7FF'))
    $headingBrush = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#163A63'))
    $bodyBrush = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#536B81'))
    $accentBrush = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#078BF0'))
    $fontFamily = [System.Drawing.FontFamily]::new('Microsoft YaHei UI')
    $brandFont = [System.Drawing.Font]::new($fontFamily, 24, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $titleFont = [System.Drawing.Font]::new($fontFamily, 64, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $bodyFont = [System.Drawing.Font]::new($fontFamily, 30, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    $captionFont = [System.Drawing.Font]::new($fontFamily, 23, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    try {
        $graphics.FillRectangle($background, 0, 0, 1200, 630)
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
        $graphics.DrawImage($source, 56, 115, 380, 380)
        $graphics.DrawString('灵兔 LTTLT', $brandFont, $accentBrush, 478, 168)
        $graphics.DrawString('灵兔拆包智录', $titleFont, $headingBrush, 474, 229)
        $graphics.DrawString('让每一次拆包，都有证据。', $bodyFont, $bodyBrush, 478, 334)
        $graphics.DrawString('Windows · Android · 开源拆包录像', $captionFont, $bodyBrush, 478, 407)
        $graphics.FillRectangle($accentBrush, 0, 622, 1200, 8)
        $bitmap.Save($destination, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        foreach ($resource in @($captionFont, $bodyFont, $titleFont, $brandFont, $fontFamily,
                $accentBrush, $bodyBrush, $headingBrush, $background, $graphics, $bitmap)) {
            $resource.Dispose()
        }
    }
}

function Save-AdaptiveMaskPreview([System.Drawing.Bitmap]$foreground, [string]$destination, [bool]$round) {
    $bitmap = [System.Drawing.Bitmap]::new(432, 432, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $background = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#F3F6FA'))
    try {
        $graphics.Clear([System.Drawing.Color]::White)
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        if ($round) {
            $path.AddEllipse(72, 72, 288, 288)
        }
        else {
            $path.AddArc(72, 72, 96, 96, 180, 90)
            $path.AddArc(264, 72, 96, 96, 270, 90)
            $path.AddArc(264, 264, 96, 96, 0, 90)
            $path.AddArc(72, 264, 96, 96, 90, 90)
            $path.CloseFigure()
        }
        $graphics.SetClip($path)
        $graphics.FillRectangle($background, 0, 0, 432, 432)
        $graphics.DrawImage($foreground, 0, 0, 432, 432)
        $bitmap.Save($destination, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        foreach ($resource in @($background, $path, $graphics, $bitmap)) { $resource.Dispose() }
    }
}

$master = New-TransparentMaster $sourcePath
if (-not [string]::IsNullOrWhiteSpace($AlphaMask)) {
    $alphaMaskPath = [System.IO.Path]::GetFullPath($AlphaMask)
    if (-not (Test-Path -LiteralPath $alphaMaskPath)) {
        $master.Dispose()
        throw "Alpha mask not found: $alphaMaskPath"
    }
    $maskedMaster = Apply-AlphaMask $master $alphaMaskPath
    $master.Dispose()
    $master = $maskedMaster
}
try {
    if (-not [string]::IsNullOrWhiteSpace($ProcessedSourceDestination)) {
        $processedSourcePath = [System.IO.Path]::GetFullPath($ProcessedSourceDestination)
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $processedSourcePath) | Out-Null
        $master.Save($processedSourcePath, [System.Drawing.Imaging.ImageFormat]::Png)
    }

    $masterPath = Join-Path $desktopAssets 'EcommerceUnpackRecorder-Logo.png'
    Save-ResizedPng $master 1024 $masterPath
    Save-ResizedPng $master 512 (Join-Path $readmeAssets 'logo.png')
    Write-MultiSizeIcon $master (Join-Path $desktopAssets 'EcommerceUnpackRecorder.ico')
    Save-ResizedPng $master 1024 (Join-Path $websiteAssets 'unpackvision-logo.png')
    Save-ResizedPng $master 256 (Join-Path $websiteAssets 'brand-mark.png')
    Save-ResizedPng $master 32 (Join-Path $websiteAssets 'favicon-32.png')
    Save-ResizedPng $master 180 (Join-Path $websiteAssets 'apple-touch-icon.png')
    Save-WebsiteShareImage $master (Join-Path $websiteAssets 'og.png')
    Save-ResizedPng $master 1024 (Join-Path $qaAssets 'logo-white.png') ([System.Drawing.Color]::White)

    $densitySizes = @{
        'mipmap-mdpi' = 48
        'mipmap-hdpi' = 72
        'mipmap-xhdpi' = 96
        'mipmap-xxhdpi' = 144
        'mipmap-xxxhdpi' = 192
    }
    foreach ($entry in $densitySizes.GetEnumerator()) {
        $directory = Join-Path $androidResources $entry.Key
        Save-ResizedPng $master $entry.Value (Join-Path $directory 'ic_launcher.png')
        Save-ResizedPng $master $entry.Value (Join-Path $directory 'ic_launcher_round.png')
    }
    $foregroundPath = Join-Path $androidResources 'drawable-nodpi\ic_launcher_foreground.png'
    # A 186 px square fits inside Android's central 66/108 safe circle at 432 px.
    # Keep the complete existing rounded-square outline and the corner brand badge.
    Save-ResizedPng $master 432 $foregroundPath ([System.Drawing.Color]::Transparent) (186.0 / 432.0)
    $foreground = [System.Drawing.Bitmap]::new($foregroundPath)
    try {
        Save-AdaptiveMaskPreview $foreground (Join-Path $qaAssets 'android-circle-white.png') $true
        Save-AdaptiveMaskPreview $foreground (Join-Path $qaAssets 'android-rounded-square-white.png') $false
    }
    finally {
        $foreground.Dispose()
    }
}
finally {
    $master.Dispose()
}

Write-Output "Generated desktop, Android, README, website and white-background QA assets from $sourcePath"
