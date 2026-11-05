$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
. (Join-Path $PSScriptRoot 'common.ps1')
$installer = Join-Path $root 'install.ps1'
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) "nightowl-installer-tests-$([System.IO.Path]::GetRandomFileName())"
$payload = Join-Path $tmp 'payload'
$archive = Join-Path $tmp 'nightowl-win-x64.zip'
$sums = Join-Path $tmp 'SHA256SUMS.txt'

New-Item -ItemType Directory -Path $payload -Force | Out-Null
Set-Content -LiteralPath (Join-Path $payload 'nightowl.exe') -Value 'test executable'
New-ZipWithEntries -ZipPath $archive -Entries @{ 'nightowl.exe' = 'test executable' }
$global:NightOwlFixtureArchive = $archive
$global:NightOwlFixtureSums = $sums
$assetHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $archive).Hash.ToLowerInvariant()
Set-Content -LiteralPath $sums -Value "$assetHash  nightowl-win-x64.zip"

try {
    $env:NIGHTOWL_RID = 'win-x64'
    $env:NIGHTOWL_ASSET_BASE = 'https://fixtures.invalid'
    $env:NIGHTOWL_SKIP_SMOKE = '1'
    Install-NightOwlWebRequestMock

    Invoke-InstallerCase -Case missing -ShouldSucceed $false -ExpectedError 'refusing an unverified install'
    Invoke-InstallerCase -Case mismatch -ShouldSucceed $false -ExpectedError 'Checksum mismatch'
    Invoke-InstallerCase -Case malformed -ShouldSucceed $false -ExpectedError 'has no entry'
    Invoke-InstallerCase -Case transport -ShouldSucceed $false -ExpectedError 'Could not download'
    Invoke-InstallerCase -Case valid -ShouldSucceed $true

    Write-Host 'PowerShell installer checksum tests passed.'
}
finally {
    Clear-NightOwlInstallerTestState
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
