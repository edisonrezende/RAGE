# Gera app\rage.ico (16..256 px) e ui\public\logo.png a partir de assets\rage-logo.svg (logo oficial do RAGE!).
# O SVG é rasterizado pelo Microsoft Edge em modo headless (já vem no Windows).
param([string]$Svg = "$PSScriptRoot\..\assets\rage-logo.svg")
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot
$Svg = (Resolve-Path -LiteralPath $Svg).Path
$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) { throw 'Microsoft Edge não encontrado para rasterizar o SVG.' }

$work = Join-Path $env:TEMP ("rage-icon-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
  $big = 1024
  $svgText = [IO.File]::ReadAllText($Svg)
  $b64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($svgText))
  $html = "<!doctype html><html><head><style>html,body{margin:0;padding:0;background:transparent;overflow:hidden}img{display:block;width:${big}px;height:${big}px}</style></head><body><img src='data:image/svg+xml;base64,$b64'></body></html>"
  $htmlPath = Join-Path $work 'logo.html'
  [IO.File]::WriteAllText($htmlPath, $html)
  $shot = Join-Path $work 'logo.png'
  $args = @('--headless=new', '--disable-gpu', '--hide-scrollbars', '--force-device-scale-factor=1', "--user-data-dir=$work\profile",
    '--default-background-color=00000000', "--window-size=$big,$big", "--screenshot=$shot", ([Uri]$htmlPath).AbsoluteUri)
  $p = Start-Process -FilePath $edge -ArgumentList $args -PassThru -WindowStyle Hidden
  if (-not $p.WaitForExit(60000)) { $p.Kill(); throw 'Edge demorou demais para rasterizar.' }
  if (-not (Test-Path $shot)) { throw 'O Edge não gerou a imagem.' }

  $src = [System.Drawing.Image]::FromFile($shot)
  function Resize([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'; $g.SmoothingMode = 'HighQuality'; $g.CompositingQuality = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($src, (New-Object System.Drawing.Rectangle 0, 0, $s, $s))
    $g.Dispose()
    return $bmp
  }

  $sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
  $pngs = foreach ($s in $sizes) { $b = Resize $s; $ms = New-Object System.IO.MemoryStream; $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $b.Dispose(); , $ms.ToArray() }
  $ico = Join-Path $root 'app\rage.ico'
  $fs = [System.IO.File]::Create($ico)
  $w = New-Object System.IO.BinaryWriter $fs
  $w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
  $offset = 6 + 16 * $sizes.Count
  for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $pngs[$i].Length
    $dim = $(if ($s -ge 256) { 0 } else { $s })
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0); $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$len); $w.Write([UInt32]$offset); $offset += $len
  }
  foreach ($b in $pngs) { $w.Write($b) }
  $w.Close()

  $logo = Resize 256
  $logo.Save((Join-Path $root 'ui\public\logo.png'), [System.Drawing.Imaging.ImageFormat]::Png); $logo.Dispose()
  $src.Dispose()
  "ok: $ico + ui\public\logo.png"
}
finally { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
