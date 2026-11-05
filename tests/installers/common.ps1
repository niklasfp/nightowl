$ErrorActionPreference = 'Stop'

function New-ZipWithEntries {
    param(
        [Parameter(Mandatory)][string]$ZipPath,
        [Parameter(Mandatory)][hashtable]$Entries,
        [int]$UnixMode = 0
    )

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    if (Test-Path -LiteralPath $ZipPath) { Remove-Item -LiteralPath $ZipPath }
    $zip = [System.IO.Compression.ZipFile]::Open($ZipPath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in $Entries.Keys) {
            $entry = $zip.CreateEntry($name)
            if ($UnixMode -ne 0) {
                $entry.ExternalAttributes = ($UnixMode -shl 16)
            }
            $content = [string]$Entries[$name]
            if ($content.Length -gt 0) {
                $stream = $entry.Open()
                try {
                    $bytes = [System.Text.Encoding]::UTF8.GetBytes($content)
                    $stream.Write($bytes, 0, $bytes.Length)
                }
                finally {
                    $stream.Dispose()
                }
            }
        }
    }
    finally {
        $zip.Dispose()
    }
}

function New-NightOwl404Exception {
    $ex = New-Object System.Exception '404 Not Found'
    Add-Member -InputObject $ex -NotePropertyName StatusCode -NotePropertyValue 404
    return $ex
}

function Install-NightOwlWebRequestMock {
    if (Get-Command Invoke-WebRequest -ErrorAction SilentlyContinue) {
        Remove-Item Function:\global:Invoke-WebRequest -ErrorAction SilentlyContinue
    }

    function global:Invoke-WebRequest {
        param(
            [Parameter(Mandatory)][string]$Uri,
            [Parameter(Mandatory)][string]$OutFile,
            [switch]$UseBasicParsing,
            [Parameter(ValueFromRemainingArguments)][object[]]$Rest
        )

        if ($Uri.EndsWith('/nightowl-win-x64.zip')) {
            Copy-Item -LiteralPath $global:NightOwlFixtureArchive -Destination $OutFile
            return
        }
        if (-not $Uri.EndsWith('/SHA256SUMS.txt')) {
            throw "Unexpected fixture URL: $Uri"
        }

        switch ($global:NightOwlChecksumCase) {
            'valid' { Copy-Item -LiteralPath $global:NightOwlFixtureSums -Destination $OutFile }
            'missing' { throw (New-NightOwl404Exception) }
            'mismatch' { Set-Content -LiteralPath $OutFile -Value "$('0' * 64)  nightowl-win-x64.zip" }
            'malformed' { Set-Content -LiteralPath $OutFile -Value "$('0' * 64)  another-file.zip" }
            'transport' { throw (New-Object System.Exception 'connection reset') }
            default { throw "Unexpected checksum fixture case: $global:NightOwlChecksumCase" }
        }
    }
}

function Invoke-InstallerCase {
    param(
        [Parameter(Mandatory)][string]$Case,
        [Parameter(Mandatory)][bool]$ShouldSucceed,
        [string]$ExpectedError,
        [string]$Version = 'v-test',
        [string]$Archive
    )

    if ($Archive) { $global:NightOwlFixtureArchive = $Archive }
    if ($global:NightOwlFixtureArchive -and $global:NightOwlFixtureSums -and $Case -eq 'valid') {
        $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $global:NightOwlFixtureArchive).Hash.ToLowerInvariant()
        Set-Content -LiteralPath $global:NightOwlFixtureSums -Value "$hash  nightowl-win-x64.zip"
    }

    $global:NightOwlChecksumCase = $Case
    $installDir = Join-Path $tmp "install-$Case-$([guid]::NewGuid())"
    $succeeded = $false
    $failure = ''
    try {
        & $installer -Version $Version -InstallDir $installDir -NoPathUpdate
        $succeeded = $true
    }
    catch {
        $failure = $_.Exception.Message
    }

    if ($succeeded -ne $ShouldSucceed) {
        throw "Case '$Case' success was $succeeded; expected $ShouldSucceed. Error: $failure"
    }
    if (-not $ShouldSucceed -and -not $failure.Contains($ExpectedError)) {
        throw "Case '$Case' failed with unexpected error: $failure"
    }

    $installedExe = Join-Path $installDir 'nightowl.exe'
    if ($ShouldSucceed -ne (Test-Path -LiteralPath $installedExe)) {
        throw "Case '$Case' produced an unexpected installed-file state."
    }
}

function Clear-NightOwlInstallerTestState {
    Remove-Item Function:\global:Invoke-WebRequest -ErrorAction SilentlyContinue
    Remove-Item Env:NIGHTOWL_RID -ErrorAction SilentlyContinue
    Remove-Item Env:NIGHTOWL_ASSET_BASE -ErrorAction SilentlyContinue
    Remove-Item Env:NIGHTOWL_SKIP_SMOKE -ErrorAction SilentlyContinue
    Remove-Item Env:NIGHTOWL_ALLOW_UNVERIFIED -ErrorAction SilentlyContinue
}
