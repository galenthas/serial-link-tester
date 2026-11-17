$ErrorActionPreference = 'Stop'
$build = & (Join-Path $PSScriptRoot 'build-config.ps1')
$projectRoot = $build.ProjectRoot
& (Join-Path $PSScriptRoot 'build.ps1')
$releaseRoot = $build.ReleaseRoot
$appDir = $build.AppDir
$verification = $build.Verification
& (Join-Path $verification 'CoreTests.exe') (Join-Path $verification 'test_report') | Tee-Object -FilePath (Join-Path $verification 'core_tests.txt')
if ($LASTEXITCODE -ne 0) { throw 'Tests failed. Package was not created.' }
$fileNames = @('SerialLinkTester.exe','USER_GUIDE.html','QUICK_START.txt')
if (Test-Path -LiteralPath (Join-Path $appDir 'LICENSE')) { $fileNames += 'LICENSE' }
$entries = @($fileNames | ForEach-Object {
    $item = Get-Item -LiteralPath (Join-Path $appDir $_)
    [ordered]@{ name=$item.Name; bytes=$item.Length; sha256=(Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$manifest = [ordered]@{
    application='Serial Link Tester'; version=$build.AssemblyVersion; release_date=$build.ReleaseDate; platform='Windows'
    automated_test_result=(Get-Content -LiteralPath (Join-Path $verification 'core_tests.txt') -Tail 1)
    bidirectional_hardware_validation='Pending; automated transport tests are simulated.'
    files=$entries
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $appDir 'release_manifest.json') -Encoding UTF8
$zipFile = $build.ZipFile
$packageFiles = @($fileNames + 'release_manifest.json' | ForEach-Object { Join-Path $appDir $_ })
Compress-Archive -LiteralPath $packageFiles -DestinationPath $zipFile -Force
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($zipFile)
try {
    if ($zip.Entries.Count -ne ($fileNames.Count + 1)) { throw 'Unexpected package entries.' }
    foreach ($entry in $entries) {
        $packed = $zip.GetEntry($entry.name)
        if ($null -eq $packed -or $packed.Length -ne $entry.bytes) { throw 'Package entry mismatch.' }
        $stream = $packed.Open()
        $sha = [Security.Cryptography.SHA256]::Create()
        try {
            $actual = ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','').ToLowerInvariant()
            if ($actual -ne $entry.sha256) { throw 'Package hash mismatch.' }
        } finally { $sha.Dispose(); $stream.Dispose() }
    }
} finally { $zip.Dispose() }
Write-Output "Package verified: $zipFile"
