param()
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$taskRepo = Split-Path -Parent $PSScriptRoot
$taskAssets = Join-Path $taskRepo 'src/FileAssistant.App/Assets'
$taskSourcePath = Join-Path $taskRepo 'docs/design-reference/2026-11-21-zhiyuan-logo/icon-pack/cobalt.png'
if (-not (Test-Path -LiteralPath $taskSourcePath)) { $taskSourcePath = Join-Path $taskAssets 'AppIcons/cobalt.png' }
$taskSource = [Drawing.Bitmap]::FromFile($taskSourcePath)

# Format conversion of the approved artwork only; preserve its colors and transparency.
function New-IconBitmap([int]$Width, [int]$Height, [int]$SymbolSize) {
    $taskBitmap = [Drawing.Bitmap]::new($Width, $Height, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $taskGraphics = [Drawing.Graphics]::FromImage($taskBitmap)
    try {
        $taskGraphics.Clear([Drawing.Color]::Transparent)
        $taskGraphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
        $taskGraphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
        $taskGraphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $taskGraphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $taskRect = [Drawing.Rectangle]::new(($Width - $SymbolSize) / 2, ($Height - $SymbolSize) / 2, $SymbolSize, $SymbolSize)
        $taskGraphics.DrawImage($taskSource, $taskRect, 0, 0, $taskSource.Width, $taskSource.Height, [Drawing.GraphicsUnit]::Pixel)
    } finally { $taskGraphics.Dispose() }
    return $taskBitmap
}

try {
    $taskPngs = @(
        @('LockScreenLogo.scale-200.png', 48, 48, 48),
        @('SplashScreen.scale-200.png', 1240, 600, 300),
        @('Square150x150Logo.scale-200.png', 300, 300, 300),
        @('Square44x44Logo.scale-200.png', 88, 88, 88),
        @('Square44x44Logo.targetsize-24_altform-unplated.png', 24, 24, 24),
        @('Square44x44Logo.targetsize-48_altform-lightunplated.png', 48, 48, 48),
        @('StoreLogo.png', 50, 50, 50),
        @('Wide310x150Logo.scale-200.png', 620, 300, 280)
    )
    foreach ($taskSpec in $taskPngs) {
        $taskBitmap = New-IconBitmap $taskSpec[1] $taskSpec[2] $taskSpec[3]
        try { $taskBitmap.Save((Join-Path $taskAssets $taskSpec[0]), [Drawing.Imaging.ImageFormat]::Png) }
        finally { $taskBitmap.Dispose() }
    }

    # Keep the nine hand-tuned frames from the approved ICO.
    Copy-Item -LiteralPath ([IO.Path]::ChangeExtension($taskSourcePath, '.ico')) -Destination (Join-Path $taskAssets 'AppIcon.ico') -Force
    Write-Output 'Exported Classic Cobalt: 9 original ICO frames and 8 Windows image assets.'
} finally { $taskSource.Dispose() }
