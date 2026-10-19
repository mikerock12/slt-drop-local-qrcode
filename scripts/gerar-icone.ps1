<#
.SYNOPSIS
  Gera assets\icon.ico (16 a 256 px) com o mesmo desenho do logo da página do celular.

.DESCRIPTION
  O ícone já está versionado; rode este script só se mudar o desenho. Cada tamanho
  é desenhado separadamente (não reduzido de um grande), para ficar nítido em 16 px.
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$raiz = Split-Path -Parent $PSScriptRoot
$destino = Join-Path $raiz 'assets\icon.ico'
New-Item -ItemType Directory -Force (Split-Path $destino) | Out-Null

function Cor($hex) { [System.Drawing.ColorTranslator]::FromHtml($hex) }

function Retangulo-Arredondado($g, $x, $y, $w, $h, $r, $pincel, $caneta) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    if ($pincel) { $g.FillPath($pincel, $p) }
    if ($caneta) { $g.DrawPath($caneta, $p) }
    $p.Dispose()
}

function Desenhar([int]$t) {
    $bmp = New-Object System.Drawing.Bitmap $t, $t, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $t / 64.0

    $fundo = New-Object System.Drawing.SolidBrush (Cor '#1C1D21')
    $borda = New-Object System.Drawing.Pen (Cor '#FF7A1A'), ([Math]::Max(1, 2.5 * $s))
    Retangulo-Arredondado $g (2*$s) (2*$s) (60*$s) (60*$s) (14*$s) $fundo $borda

    # Os três "olhos" do QR Code.
    $branco = New-Object System.Drawing.Pen (Cor '#F2F2F3'), ([Math]::Max(1.2, 3.6 * $s))
    foreach ($pos in @(@(13, 13), @(37, 13), @(13, 37))) {
        Retangulo-Arredondado $g ($pos[0]*$s) ($pos[1]*$s) (14*$s) (14*$s) (3*$s) $null $branco
    }

    # A seta de "receber" no quarto canto.
    $seta = New-Object System.Drawing.Pen (Cor '#FF7A1A'), ([Math]::Max(1.5, 4.2 * $s))
    $seta.StartCap = 'Round'; $seta.EndCap = 'Round'; $seta.LineJoin = 'Round'
    $g.DrawLine($seta, 44*$s, 34*$s, 44*$s, 50*$s)
    $g.DrawLines($seta, [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF (37*$s), (43*$s)),
        (New-Object System.Drawing.PointF (44*$s), (50*$s)),
        (New-Object System.Drawing.PointF (51*$s), (43*$s))))

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return ,$ms.ToArray()
}

$tamanhos = 16, 20, 24, 32, 40, 48, 64, 128, 256
$imagens = foreach ($t in $tamanhos) { ,(Desenhar $t) }

# Formato ICO: cabeçalho, uma entrada de 16 bytes por imagem, depois os PNGs.
$saida = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $saida
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$tamanhos.Count)
$deslocamento = 6 + 16 * $tamanhos.Count
for ($i = 0; $i -lt $tamanhos.Count; $i++) {
    $t = $tamanhos[$i]
    $lado = if ($t -ge 256) { 0 } else { $t }
    $w.Write([byte]$lado); $w.Write([byte]$lado); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$imagens[$i].Length); $w.Write([UInt32]$deslocamento)
    $deslocamento += $imagens[$i].Length
}
foreach ($img in $imagens) { $w.Write($img) }
$w.Flush()
[System.IO.File]::WriteAllBytes($destino, $saida.ToArray())
Write-Host "Ícone gerado: $destino"

# Uma prévia em PNG para o README.
[System.IO.File]::WriteAllBytes((Join-Path $raiz 'assets\icon-256.png'), $imagens[-1])
