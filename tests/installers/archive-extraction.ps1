$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
. (Join-Path $PSScriptRoot 'common.ps1')
$installer = Join-Path $root 'install.ps1'
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) "nightowl-archive-tests-$([System.IO.Path]::GetRandomFileName())"
New-Item -ItemType Directory -Path $tmp -Force | Out-Null

$goodZip = Join-Path $tmp 'good.zip'
$extraZip = Join-Path $tmp 'extra.zip'
$slipZip = Join-Path $tmp 'slip.zip'
$symlinkZip = Join-Path $tmp 'symlink.zip'
$absoluteZip = Join-Path $tmp 'absolute.zip'
$driveZip = Join-Path $tmp 'drive.zip'
$uncZip = Join-Path $tmp 'unc.zip'
$dirZip = Join-Path $tmp 'dir.zip'
$dotZip = Join-Path $tmp 'dot.zip'
New-ZipWithEntries -ZipPath $goodZip -Entries @{ 'nightowl.exe' = 'test executable' }
New-ZipWithEntries -ZipPath $extraZip -Entries @{
    'nightowl.exe' = 'test executable'
    'extra.txt' = 'nope'
}
New-ZipWithEntries -ZipPath $slipZip -Entries @{ '../evil.exe' = 'pwned' }
New-ZipWithEntries -ZipPath $symlinkZip -Entries @{ 'nightowl.exe' = '/etc/passwd' } -UnixMode 0xA1FF
New-ZipWithEntries -ZipPath $absoluteZip -Entries @{ '/tmp/nightowl.exe' = 'pwned' }
New-ZipWithEntries -ZipPath $driveZip -Entries @{ 'C:/nightowl.exe' = 'pwned' }
New-ZipWithEntries -ZipPath $uncZip -Entries @{ '//server/share/nightowl.exe' = 'pwned' }
New-ZipWithEntries -ZipPath $dirZip -Entries @{ 'nightowl.exe/' = '' }
New-ZipWithEntries -ZipPath $dotZip -Entries @{ './nightowl.exe' = 'test executable' }

$global:NightOwlFixtureSums = Join-Path $tmp 'SHA256SUMS.txt'
$global:NightOwlChecksumCase = 'valid'

try {
    $env:NIGHTOWL_RID = 'win-x64'
    $env:NIGHTOWL_ASSET_BASE = 'https://fixtures.invalid'
    $env:NIGHTOWL_SKIP_SMOKE = '1'
    Install-NightOwlWebRequestMock

    Invoke-InstallerCase -Case valid -Archive $extraZip -ShouldSucceed $false -ExpectedError 'exactly one member'
    Invoke-InstallerCase -Case valid -Archive $slipZip -ShouldSucceed $false -ExpectedError 'unexpected member'
    Invoke-InstallerCase -Case valid -Archive $symlinkZip -ShouldSucceed $false -ExpectedError 'is a symlink'
    Invoke-InstallerCase -Case valid -Archive $absoluteZip -ShouldSucceed $false -ExpectedError 'is not a safe path'
    Invoke-InstallerCase -Case valid -Archive $driveZip -ShouldSucceed $false -ExpectedError 'is not a safe path'
    Invoke-InstallerCase -Case valid -Archive $uncZip -ShouldSucceed $false -ExpectedError 'is not a safe path'
    Invoke-InstallerCase -Case valid -Archive $dirZip -ShouldSucceed $false -ExpectedError 'is a directory'
    Invoke-InstallerCase -Case valid -Archive $goodZip -ShouldSucceed $true
    Invoke-InstallerCase -Case valid -Archive $dotZip -ShouldSucceed $true

    Write-Host 'PowerShell installer archive-extraction tests passed.'
}
finally {
    Clear-NightOwlInstallerTestState
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
