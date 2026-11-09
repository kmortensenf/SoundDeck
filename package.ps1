# Builds a release and packages it as dist\SoundDeck (folder) and dist\SoundDeck-<version>.zip.
$version = "1.4.0"
$dist = Join-Path $PSScriptRoot "dist"
$folder = Join-Path $dist "SoundDeck"
$zip = Join-Path $dist "SoundDeck-$version.zip"

if (Test-Path $folder) { Remove-Item -Recurse -Force $folder }
if (Test-Path $zip) { Remove-Item -Force $zip }
New-Item -ItemType Directory -Force $folder | Out-Null

& "$PSScriptRoot\build.ps1" -Out (Join-Path $folder "SoundDeck.exe")
if (-not (Test-Path (Join-Path $folder "SoundDeck.exe"))) { throw "build failed" }
Copy-Item (Join-Path $PSScriptRoot "assets\Read me.txt") $folder

Compress-Archive -Path $folder -DestinationPath $zip
Get-ChildItem $folder, $zip | Select-Object FullName, Length
