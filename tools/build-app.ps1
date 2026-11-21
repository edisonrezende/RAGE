# Compila só o host C# e mostra os erros de forma compacta.
#   -Artifacts <pasta>  saída/obj separados (para builds em paralelo)
#   -SkipCare / -SkipTools  compila sem app\Care ou app\Tools
param([string]$Config = "Release", [string]$Artifacts = "", [switch]$SkipCare, [switch]$SkipTools)
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$root = Split-Path $PSScriptRoot
$dotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
$args2 = @('build', (Join-Path $root 'app\Rage.csproj'), '-c', $Config, '-nologo', '-v', 'q')
if ($Artifacts) { $args2 += @('--artifacts-path', $Artifacts) }
if ($SkipCare) { $args2 += '-p:SkipCare=true' }
if ($SkipTools) { $args2 += '-p:SkipTools=true' }
$o = & $dotnet @args2 2>&1
$errs = $o | Where-Object { $_ -match ': error ' } | ForEach-Object { ($_ -replace '\s*\[C:.*\.csproj\]', '') -replace '^.*\\rage\\app\\', '' } | Select-Object -Unique
$warns = $o | Where-Object { $_ -match ': warning CS' } | ForEach-Object { ($_ -replace '\s*\[C:.*\.csproj\]', '') -replace '^.*\\rage\\app\\', '' } | Select-Object -Unique
if ($errs) { $errs; "FALHOU ($($errs.Count) erros)"; exit 1 }
if ($warns) { "avisos:"; $warns | Select-Object -First 15 }
if ($Artifacts) { "OK -> " + ((Get-ChildItem (Join-Path $Artifacts 'bin') -Recurse -Filter RAGE.exe | Select-Object -First 1).FullName) } else { "OK -> " + (Join-Path $root 'app\bin\Release\RAGE.exe') }
