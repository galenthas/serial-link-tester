$ErrorActionPreference = 'Stop'
$build = & (Join-Path $PSScriptRoot 'build-config.ps1')
$projectRoot = $build.ProjectRoot
$release = $build.AppDir
$verification = $build.Verification
& (Join-Path $PSScriptRoot 'build-icon.ps1')
$iconPath = Join-Path $PSScriptRoot 'assets\SerialPortTester.ico'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Windows .NET Framework 4.x compiler was not found.' }
New-Item -ItemType Directory -Force -Path $release | Out-Null
New-Item -ItemType Directory -Force -Path $verification | Out-Null
$sources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })
$generatedVersion = Join-Path $verification 'BuildInfo.cs'
$versionSource = @"
using System.Reflection;
[assembly: AssemblyVersion("$($build.AssemblyVersion)")]
[assembly: AssemblyFileVersion("$($build.AssemblyVersion)")]
namespace SerialRf { public static class BuildInfo { public const string Version = "$($build.Version)"; } }
"@
[IO.File]::WriteAllText($generatedVersion, $versionSource, [Text.UTF8Encoding]::new($false))
$generatedManifest = Join-Path $verification 'app.manifest'
$manifestSource = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'app.manifest')).Replace('__ASSEMBLY_VERSION__', $build.AssemblyVersion)
[IO.File]::WriteAllText($generatedManifest, $manifestSource, [Text.UTF8Encoding]::new($false))
& $compiler /nologo /target:winexe /platform:anycpu /optimize+ /utf8output /warn:4 /warnaserror "/win32manifest:$generatedManifest" "/win32icon:$iconPath" "/resource:$iconPath,SerialPortTester.ico" "/out:$release\SerialPortTester.exe" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll $sources $generatedVersion
if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }
$core = @('Protocol.cs','NativeSerial.cs','Measurement.cs','SerialWarnings.cs','Engine.cs','Session.cs','PrecisionWaiter.cs','Export.cs','ExportSession.cs','ExportDiagnostics.cs') | ForEach-Object { Join-Path $PSScriptRoot "src\$_" }
& $compiler /nologo /target:exe /platform:anycpu /optimize+ /utf8output /warn:4 /warnaserror "/win32manifest:$generatedManifest" "/out:$verification\CoreTests.exe" /reference:System.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll $core $generatedVersion (Join-Path $projectRoot 'tests\TestPorts.cs') (Join-Path $projectRoot 'tests\CoreTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
& $compiler /nologo /target:exe /main:UiTests /platform:anycpu /optimize+ /utf8output /warn:4 /warnaserror "/win32manifest:$generatedManifest" "/resource:$iconPath,SerialPortTester.ico" "/out:$verification\UiTests.exe" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll $sources $generatedVersion (Join-Path $projectRoot 'tests\TestPorts.cs') (Join-Path $projectRoot 'tests\UiTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'UI test build failed.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\USER_GUIDE.html') -Destination (Join-Path $release 'USER_GUIDE.html')
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\QUICK_START.txt') -Destination (Join-Path $release 'QUICK_START.txt')
if (Test-Path -LiteralPath (Join-Path $projectRoot 'LICENSE')) { Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination (Join-Path $release 'LICENSE') }
Write-Output "Ready: $release\SerialPortTester.exe"
