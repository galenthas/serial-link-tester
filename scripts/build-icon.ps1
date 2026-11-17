$ErrorActionPreference = 'Stop'
$build = & (Join-Path $PSScriptRoot 'build-config.ps1')
$verification = $build.Verification
New-Item -ItemType Directory -Force -Path $verification | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$generator = Join-Path $verification 'IconArtwork.exe'
& $compiler /nologo /target:exe /optimize+ /warnaserror /reference:System.Drawing.dll "/out:$generator" (Join-Path $PSScriptRoot 'artwork\IconArtwork.cs')
if ($LASTEXITCODE -ne 0) { throw 'Icon artwork compilation failed.' }
& $generator (Join-Path $PSScriptRoot 'assets')
if ($LASTEXITCODE -ne 0) { throw 'Icon generation failed.' }
Write-Output 'Icon generated: 16, 24, 32, 48, 64, 128 and 256 pixels.'
