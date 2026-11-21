# Gera a versão portátil do RAGE!:  dist\RAGE!\RAGE!.exe  (+ lib\, LEIA-ME.txt)  e  dist\RAGE!-portatil.zip
#   .\build.ps1            -> interface + executável + pasta portátil + zip
#   .\build.ps1 -SkipUi    -> reaproveita ui\dist
param([switch]$SkipUi, [switch]$NoZip)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$dotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

function Step($t) { Write-Host "`n==> $t" -ForegroundColor DarkYellow }

if (-not $SkipUi) {
  Step 'Interface (Vite)'
  Push-Location (Join-Path $root 'ui')
  if (-not (Test-Path node_modules)) { npm install --no-audit --no-fund | Out-Host }
  npm run build | Out-Host
  if ($LASTEXITCODE -ne 0) { Pop-Location; throw 'Falha no build da interface' }
  Pop-Location
}

Step 'Executável (.NET Framework 4.8 + WebView2)'
$obj = Join-Path $root 'app\obj'
& $dotnet build (Join-Path $root 'app\Rage.csproj') -c Release -nologo -v q | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Falha na compilação do executável' }
$bin = Join-Path $root 'app\bin\Release'

Step 'Montando a pasta portátil'
$dist = Join-Path $root 'dist'
$final = Join-Path $dist 'RAGE!'
# Monta numa pasta de preparo limpa (o zip sai daqui, nunca com a pasta data de ninguém)
$out = Join-Path $dist '.stage\RAGE!'
if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Recurse -Force }
New-Item -ItemType Directory -Force -Path (Join-Path $out 'lib') | Out-Null
Copy-Item -LiteralPath (Join-Path $bin 'RAGE.exe') -Destination (Join-Path $out 'RAGE!.exe')
Copy-Item -LiteralPath (Join-Path $bin 'RAGE.exe.config') -Destination (Join-Path $out 'RAGE!.exe.config')
Get-ChildItem -LiteralPath $bin -Filter *.dll | Where-Object { $_.Name -ne 'Microsoft.Web.WebView2.Wpf.dll' } | ForEach-Object {
  Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $out 'lib')
}
$loader = Get-ChildItem -LiteralPath $bin -Recurse -Filter 'WebView2Loader.dll' | Where-Object { $_.FullName -match 'x64|\\Release\\WebView2Loader' } | Select-Object -First 1
if ($loader) { Copy-Item -LiteralPath $loader.FullName -Destination (Join-Path $out 'lib\WebView2Loader.dll') -Force }

@"
RAGE!  —  monitor, limpeza, otimização e Modo Turbo para jogos
=============================================================

COMO USAR
  1. Copie a pasta "RAGE!" inteira para onde quiser (pendrive, outro PC, Área de Trabalho).
  2. Dê dois cliques em RAGE!.exe e aceite a permissão de administrador.
     (Sem administrador ele abre em modo limitado: só monitor e limpeza de RAM.)

PORTÁTIL
  - Não precisa instalar. Configurações, histórico, biblioteca de jogos e backups ficam em .\data
  - Para levar suas configurações para outro PC, copie a pasta junto com o .\data
  - Requisitos: Windows 10 ou 11 (64 bits). O Windows 11 já traz tudo; no Windows 10 o
    Microsoft Edge WebView2 Runtime normalmente já está instalado (o app avisa se faltar).

TEMPERATURA DA CPU
  O Windows não informa a temperatura real do processador sem um driver. Em Configurações >
  Sensores de hardware, clique em "Instalar PawnIO" (driver gratuito e assinado). Uma vez por PC.

ATALHOS
  Ctrl+Shift+F10   mostra/esconde o overlay de FPS durante o jogo (configurável)

DESINSTALAR
  Desligue "Iniciar com o Windows" em Configurações e apague a pasta.

Componentes: LibreHardwareMonitorLib (MPL-2.0), PawnIO, Microsoft WebView2, Newtonsoft.Json (MIT).
"@ | Set-Content -LiteralPath (Join-Path $out 'LEIA-ME.txt') -Encoding UTF8

$size = (Get-ChildItem -LiteralPath $out -Recurse -File | Measure-Object Length -Sum).Sum
Write-Host ("Versão montada: {0:N1} MB" -f ($size / 1MB))

if (-not $NoZip) {
  Step 'Compactando'
  $zip = Join-Path $dist 'RAGE!-portatil.zip'
  if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
  Compress-Archive -LiteralPath $out -DestinationPath $zip
  Write-Host ("Zip:   {0}  ({1:N1} MB)" -f $zip, ((Get-Item -LiteralPath $zip).Length / 1MB))
}

# Atualiza dist\RAGE! sem tocar na pasta data (configurações, jogos e backups de quem já usa o app dali)
Step 'Atualizando dist\RAGE!'
New-Item -ItemType Directory -Force -Path $final | Out-Null
try {
  Copy-Item -LiteralPath (Join-Path $out 'RAGE!.exe') -Destination (Join-Path $final 'RAGE!.exe') -Force -ErrorAction Stop
  Copy-Item -LiteralPath (Join-Path $out 'RAGE!.exe.config') -Destination $final -Force
  Copy-Item -LiteralPath (Join-Path $out 'LEIA-ME.txt') -Destination $final -Force
  if (Test-Path -LiteralPath (Join-Path $final 'lib')) { Remove-Item -LiteralPath (Join-Path $final 'lib') -Recurse -Force }
  Copy-Item -LiteralPath (Join-Path $out 'lib') -Destination $final -Recurse -Force
  Write-Host "Pasta: $final (a pasta data foi preservada)"
}
catch {
  Write-Host "O RAGE! está aberto a partir de dist\RAGE!, então o exe não pôde ser trocado agora." -ForegroundColor Yellow
  Write-Host "Feche o RAGE! (ícone na bandeja > Sair) e rode:  .\build.ps1 -SkipUi -NoZip" -ForegroundColor Yellow
  Write-Host "A versão nova já está no zip e em dist\.stage\RAGE!." -ForegroundColor Yellow
}
Write-Host "`nPronto." -ForegroundColor Green
