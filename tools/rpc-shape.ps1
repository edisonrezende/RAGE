# Roda funções somente-leitura via CLI e mostra o formato (chaves + 1º item) — para alinhar a interface.
param([Parameter(Mandatory)][string]$Exe, [Parameter(Mandatory)][string[]]$Methods, [string]$Params = '{}', [int]$Max = 600)
$pf = Join-Path $env:TEMP 'rage-shape-params.json'
$Params | Set-Content $pf -Encoding utf8
foreach ($m in $Methods) {
  $out = Join-Path $env:TEMP "rage-shape-$m.json"
  Start-Process $Exe -ArgumentList '--cli', $m, '--params-file', $pf, '--out', $out -Wait -WindowStyle Hidden
  $j = Get-Content $out -Raw | ConvertFrom-Json
  $r = $j.result
  if ($r -is [array]) { $desc = "array[$($r.Count)] first=" + (($r | Select-Object -First 1) | ConvertTo-Json -Depth 3 -Compress) }
  elseif ($r -eq $null) { $desc = 'null' }
  else {
    $desc = ($r.PSObject.Properties | ForEach-Object {
      $v = $_.Value
      if ($v -is [array]) { "$($_.Name)=array[$($v.Count)]:" + (($v | Select-Object -First 1) | ConvertTo-Json -Depth 2 -Compress) }
      else { "$($_.Name)=" + ($v | ConvertTo-Json -Depth 2 -Compress) }
    }) -join ' | '
  }
  if ($desc.Length -gt $Max) { $desc = $desc.Substring(0, $Max) + '…' }
  "### $m`n$desc`n"
}
