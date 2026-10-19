<#
.SYNOPSIS
  Roda os testes, publica o programa e gera o instalador.

.DESCRIPTION
  O resultado fica em entrega\SLT-Drop-Local-QRCode-Setup-<versão>.exe, junto
  com o SHA-256. A versão vem de Directory.Build.props: mude lá e o instalador
  acompanha, sem editar o .iss.

.EXAMPLE
  .\scripts\gerar-instalador.ps1
#>
[CmdletBinding()]
param(
    [switch]$PularTestes
)

$ErrorActionPreference = 'Stop'
$raiz = Split-Path -Parent $PSScriptRoot

function Etapa($texto) { Write-Host "`n==> $texto" -ForegroundColor Cyan }
function Falha($texto) { Write-Host "`nFALHOU: $texto`n" -ForegroundColor Red; exit 1 }

# ---------- versão ----------
$versao = ([xml](Get-Content (Join-Path $raiz 'Directory.Build.props'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $versao) { Falha 'não achei <Version> em Directory.Build.props' }
Etapa "Versão $versao"

# ---------- Inno Setup ----------
$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { Falha 'Inno Setup 6 não encontrado. Instale com: winget install --id JRSoftware.InnoSetup' }

# ---------- testes ----------
# Antes da publicação: instalador gerado de código que não passa nos testes
# parece pronto para entregar, e não está.
if (-not $PularTestes) {
    Etapa 'Rodando os testes'
    & dotnet test (Join-Path $raiz 'SltDropLocal.slnx') -c Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { Falha 'os testes não passaram; nada foi publicado' }
}

# ---------- publicação ----------
Etapa 'Publicando o executável autocontido (win-x64, arquivo único)'
$publish = Join-Path $raiz 'publish'
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
& dotnet publish (Join-Path $raiz 'src\SltDropLocal.App\SltDropLocal.App.csproj') -c Release -o $publish --nologo -v quiet
if ($LASTEXITCODE -ne 0) { Falha 'dotnet publish' }

$exe = Join-Path $publish 'SLTDropLocalQRCode.exe'
if (-not (Test-Path $exe)) { Falha "não encontrei $exe" }
$sobras = Get-ChildItem $publish -File | Where-Object { $_.Name -ne 'SLTDropLocalQRCode.exe' -and $_.Extension -ne '.pdb' }
if ($sobras) { Falha ("a publicação deveria ser um arquivo só, mas saiu também: " + ($sobras.Name -join ', ')) }
Write-Host ("    {0:N1} MB" -f ((Get-Item $exe).Length / 1MB)) -ForegroundColor DarkGray

# ---------- instalador ----------
Etapa 'Compilando o instalador'
& $iscc /Q "/DVersao=$versao" (Join-Path $raiz 'instalador\slt-drop-local-qrcode.iss')
if ($LASTEXITCODE -ne 0) { Falha 'ISCC' }

$setup = Join-Path $raiz "entrega\SLT-Drop-Local-QRCode-Setup-$versao.exe"
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $(Split-Path $setup -Leaf)" | Set-Content -Encoding ascii "$setup.sha256"

Etapa 'Pronto'
Write-Host "    $setup"
Write-Host ("    {0:N1} MB · SHA-256 {1}" -f ((Get-Item $setup).Length / 1MB), $hash)
