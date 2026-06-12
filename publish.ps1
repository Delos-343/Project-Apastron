# Build a self-contained, single-file Apastron for one runtime.
# Usage: ./publish.ps1 [-Rid win-x64]
param([string]$Rid = "win-x64")

$ErrorActionPreference = "Stop"
$proj = "src/Apastron/Apastron.csproj"
$out  = "publish/$Rid"

Write-Host "Publishing Apastron for $Rid -> $out"
dotnet publish $proj `
  -c Release `
  -r $Rid `
  --self-contained true `
  -p:SelfContained=true `
  -o $out

Write-Host ""
Write-Host "Done. Executable is in: $out"
Write-Host "  Windows: $out\Apastron.exe"
