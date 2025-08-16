$ErrorActionPreference = "Stop"
$project = "RaOverlay.Desktop"
$rid     = "win-x64"
$out = "artifacts"
Remove-Item -Recurse -Force $out -ErrorAction Ignore
New-Item -ItemType Directory -Force -Path $out | Out-Null

dotnet publish $project -c Release -r $rid `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:PublishTrimmed=false `
  -p:SelfContained=true

$pub = Join-Path $project "bin\Release\net8.0-windows\$rid\publish"
$zip = Join-Path $out "RaOverlay-$rid.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $pub "*") -DestinationPath $zip
Write-Host "Packed: $zip"