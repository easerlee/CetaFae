param([string]$Source = '.artifacts/art-motion/20261031-art-refinement')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$outputRoot = Join-Path $PSScriptRoot '../src/CetaFae.Probe/Art/Whale'
Copy-Item -LiteralPath (Join-Path $Source 'samples-v2/layers/B02-water-rear-rest.png') -Destination (Join-Path $outputRoot 'rest-water.png')
Copy-Item -LiteralPath (Join-Path $Source 'samples-v2/masters/B02-water-rear-rest.svg') -Destination (Join-Path $outputRoot 'rest-water.svg')
$rows = @(Import-Csv (Join-Path $Source 'spout-action-v1/motion.csv'))
for ($page = 0; $page -lt 2; $page++) {
    $atlas = [System.Drawing.Bitmap]::new(768,384,[System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($atlas)
    try {
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        for ($cell = 0; $cell -lt 32; $cell++) {
            $frame = $page * 32 + $cell
            if ($frame -ge 35) { break }
            $path = Join-Path $Source ('spout-action-v1/whale/frame-{0:000}.png' -f $frame)
            $image = [System.Drawing.Bitmap]::new($path)
            try { $graphics.DrawImage($image, [System.Drawing.Rectangle]::new(($cell % 8) * 96, [int][Math]::Floor($cell / 8) * 96,96,96)) }
            finally { $image.Dispose() }
        }
        $atlas.Save((Join-Path $outputRoot ('drop-whale-{0:00}.png' -f $page)), [System.Drawing.Imaging.ImageFormat]::Png)
    } finally { $graphics.Dispose(); $atlas.Dispose() }
}
$motion = @(0..34 | ForEach-Object {
    [ordered]@{ x = [double]$rows[$_].spoutOffsetX; y = [double]$rows[$_].spoutOffsetY }
})
[ordered]@{source='spout-fill-v4/window-wash-v1';fps=25;frames=35;cell=96;whale=$motion} |
    ConvertTo-Json -Depth 5 -Compress | Set-Content (Join-Path $outputRoot 'drop-motion.json') -Encoding utf8NoBOM
Write-Output 'Imported unchanged whale action atlas, refined resting water and registered motion; no live application changed.'
