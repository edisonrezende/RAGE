# Abre o RAGE! (sem elevar) numa rota, espera e captura a janela. Uso: snap-page.ps1 -Exe ... -Page /limpeza -Out x.png
param([Parameter(Mandatory)][string]$Exe, [Parameter(Mandatory)][string]$Page, [Parameter(Mandatory)][string]$Out, [int]$Wait = 7)
Get-Process RAGE, 'RAGE!' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $Exe } | Stop-Process -Force
Start-Sleep -Milliseconds 600
$p = Start-Process -FilePath $Exe -ArgumentList '--no-elevate', '--page', $Page -PassThru
Start-Sleep -Seconds $Wait
$p.Refresh()
& (Join-Path $PSScriptRoot 'snap-window.ps1') -ProcessId $p.Id -Out $Out
