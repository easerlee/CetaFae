param(
    [string]$Source = (Join-Path $PSScriptRoot '../.artifacts/art-motion/20261030-sequence-v2'),
    [string]$Destination = (Join-Path $PSScriptRoot '../src/CetaFae.Probe/Art/Whale')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$sourceRoot = [IO.Path]::GetFullPath($Source)
$outputRoot = [IO.Path]::GetFullPath($Destination)
[IO.Directory]::CreateDirectory($outputRoot) | Out-Null
$references = @([System.Drawing.Bitmap].Assembly.Location, [System.Drawing.Rectangle].Assembly.Location,
    (Join-Path $PSHOME 'System.Runtime.dll'), (Join-Path $PSHOME 'System.Runtime.InteropServices.dll'),
    (Join-Path $PSHOME 'System.Private.Windows.Core.dll'), (Join-Path $PSHOME 'System.Private.Windows.GdiPlus.dll'))
Add-Type -ReferencedAssemblies $references -TypeDefinition @'
using System;
using System.IO;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
public static class WhaleArtExport {
    public static void Run(string source, string destination) {
        string[] layers = { "water-rear", "whale-visible", "water-front", "spout" };
        string[] motion = File.ReadAllLines(Path.Combine(source, "source-motion", "timing-check.csv"));
        foreach (string layer in layers) {
            int count = layer == "spout" ? 80 : 320;
            for (int page = 0; page * 32 < count; page++)
            using (var atlas = new Bitmap(768,384,PixelFormat.Format32bppArgb)) {
                using (var g = Graphics.FromImage(atlas)) {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    for (int cell = 0; cell < 32 && page * 32 + cell < count; cell++) {
                        int frame = page * 32 + cell;
                        int local = layer == "spout" ? frame + 240 : frame;
                        string path = layer == "spout"
                            ? Path.Combine(source,"windowed","spout",String.Format("frame-{0:000}.png",local))
                            : Path.Combine(source,"assets",layer,String.Format("frame-{0:000}.png",frame));
                        using (var original = new Bitmap(path)) {
                            Rectangle crop = layer == "spout" ? new Rectangle(340,105,136,136) : new Rectangle(0,0,400,400);
                            using (var image = original.Clone(crop,PixelFormat.Format32bppArgb)) {
                                int facing = local < 110 ? 1 : int.Parse(motion[Math.Min(local-110,209)+1].Split(',')[7]);
                                if (facing < 0) image.RotateFlip(RotateFlipType.RotateNoneFlipX);
                                g.DrawImage(image,new Rectangle(cell%8*96,cell/8*96,96,96),0,0,image.Width,image.Height,GraphicsUnit.Pixel);
                            }
                        }
                    }
                }
                atlas.Save(Path.Combine(destination,String.Format("{0}-{1:00}.png",layer,page)),ImageFormat.Png);
            }
        }
    }
}
'@
[WhaleArtExport]::Run($sourceRoot, $outputRoot)
$rows = Import-Csv (Join-Path $sourceRoot 'windowed/timeline.csv')
$motion = Import-Csv (Join-Path $sourceRoot 'source-motion/timing-check.csv')
$contact = @(0..319 | ForEach-Object { $_ -ge 110 -and [double]$motion[[Math]::Min([Math]::Max($_ - 110, 0), 209)].immersedColumnPixels -gt 0 })
$dock = @(240..319 | ForEach-Object {
    $row = $rows[$_]
    [ordered]@{
        y = ([double]$row.y - [double]$row.waterY) / 136
        scaleX = [double]$row.scaleX / 0.34
        scaleY = [double]$row.scaleY / 0.34
        waterScale = [double]$row.waterScale / 0.34
        angle = [double]$row.angle
        alpha = [double]$row.whaleWaterAlpha
        progress = [double]$row.morphProgress
    }
})
[ordered]@{ source = 'sequence-v2'; cell = 96; columns = 8; rows = 4; fps = 25; frames = 320; contact = $contact; dock = $dock } |
    ConvertTo-Json -Depth 5 -Compress | Set-Content -LiteralPath (Join-Path $outputRoot 'motion.json') -Encoding utf8NoBOM
Get-ChildItem -LiteralPath $outputRoot -File | Measure-Object Length -Sum | Select-Object Count,Sum
