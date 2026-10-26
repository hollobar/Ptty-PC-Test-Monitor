# Builds PttyPcTestMonitor.exe and a release zip into dist/.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
$version = ([xml](Get-Content (Join-Path $root 'src/PttyPcTestMonitor.csproj'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

dotnet build (Join-Path $root 'src/PttyPcTestMonitor.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

New-Item -ItemType Directory -Force $dist | Out-Null
Copy-Item (Join-Path $root 'src/bin/Release/net48/PttyPcTestMonitor.exe') $dist -Force

# Release zip: exe, usage guide, license and third-party notices.
$stage = Join-Path $dist "PttyPcTestMonitor-v$version"
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $stage | Out-Null
Copy-Item (Join-Path $dist 'PttyPcTestMonitor.exe'), (Join-Path $root 'LICENSE'), (Join-Path $root 'THIRD-PARTY-NOTICES.md'), (Join-Path $root 'docs/사용법.txt') $stage
Copy-Item (Join-Path $root 'licenses') $stage -Recurse
$zip = "$stage.zip"
Remove-Item $zip -Force -ErrorAction SilentlyContinue
# ZipFile stores non-ASCII names as UTF-8 (Compress-Archive in Windows PowerShell mangles them).
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip)
Remove-Item $stage -Recurse -Force

Write-Host "Built $dist\PttyPcTestMonitor.exe and $zip"
