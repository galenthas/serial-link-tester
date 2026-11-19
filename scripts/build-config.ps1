$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$metadata = Get-Content -LiteralPath (Join-Path $projectRoot 'version.json') -Raw | ConvertFrom-Json
if ($metadata.version -notmatch '^\d+\.\d+\.\d+$') { throw 'version.json must contain a major.minor.patch version.' }
$parsedVersion = [version]$metadata.version
if ($parsedVersion.Major -gt 65534 -or $parsedVersion.Minor -gt 65534 -or $parsedVersion.Build -gt 65534) { throw 'Version components exceed assembly limits.' }
$releaseDate = [datetime]::ParseExact($metadata.releaseDate, 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)
$folderName = $releaseDate.ToString('yyyyMMdd') + '_serial_port_tester_v' + $metadata.version.Replace('.', '_')
$releaseRoot = Join-Path $projectRoot ('artifacts\' + $folderName)
[pscustomobject]@{
    ProjectRoot=$projectRoot
    Version=[string]$metadata.version
    AssemblyVersion=([string]$metadata.version + '.0')
    ReleaseDate=[string]$metadata.releaseDate
    ReleaseRoot=$releaseRoot
    AppDir=(Join-Path $releaseRoot 'app')
    Verification=(Join-Path $releaseRoot 'verification')
    ZipFile=(Join-Path $releaseRoot ('SerialPortTester_v' + $metadata.version + '_Windows.zip'))
}
