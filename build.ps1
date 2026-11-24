$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$devModDir = Join-Path $env:USERPROFILE "Documents\Klei\OxygenNotIncluded\mods\Dev\RemoteWarpConduit"
& dotnet build (Join-Path $projectRoot "RemoteWarpConduit.csproj") -c Release
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE" }
New-Item -ItemType Directory -Force $devModDir | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot "bin\Release\RemoteWarpConduit.dll") -Destination $devModDir -Force
Copy-Item -LiteralPath (Join-Path $projectRoot "mod.yaml") -Destination $devModDir -Force
Copy-Item -LiteralPath (Join-Path $projectRoot "mod_info.yaml") -Destination $devModDir -Force
Write-Host "Installed dev mod to $devModDir"
