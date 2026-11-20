# Corre o harness UiShots e copia as capturas para tools/UiShots/baselines.
# Usar quando uma mudança de UI é intencional e os baselines devem acompanhá-la.
# Corre só no Windows (o harness é WPF).
$ErrorActionPreference = "Stop"
$raiz = Split-Path -Parent $PSScriptRoot
$destino = Join-Path $raiz "out-store\ui-shots"
$baselines = Join-Path $PSScriptRoot "UiShots\baselines"

dotnet run --project (Join-Path $PSScriptRoot "UiShots\UiShots.csproj") -c Release -- $destino
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Get-ChildItem $baselines -Filter *.png | Remove-Item
Get-ChildItem $destino -Filter *.png |
    Where-Object { $_.Name -notlike "diff-*" } |
    Copy-Item -Destination $baselines
Write-Host "baselines atualizados em $baselines"
