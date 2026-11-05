#Requires -Version 5.1
<#
.SYNOPSIS
    NightOwl installer for Windows.
.DESCRIPTION
    Downloads the native win-x64/win-arm64 binary from GitHub Releases, verifies its checksum,
    installs it user-scoped (no admin), and adds the install directory to the user PATH.

    One-liner:
      irm https://raw.githubusercontent.com/niklasfp/nightowl/main/install.ps1 | iex

    With parameters, download first:
      iwr -useb https://raw.githubusercontent.com/niklasfp/nightowl/main/install.ps1 -OutFile install.ps1
      .\install.ps1 -Version v0.1.0 -InstallDir C:\Tools\NightOwl

    Update: re-run the same one-liner (or with the same -InstallDir). The installer copies
    only nightowl.exe into the install directory, and the PATH entry is only ever added once,
    so this is safe to repeat.

    Uninstall (no download, no network):
      & ([scriptblock]::Create((irm https://raw.githubusercontent.com/niklasfp/nightowl/main/install.ps1))) -Uninstall
.PARAMETER Version
    Tagged release to install (e.g. v0.1.0). Default: newest release.
.PARAMETER InstallDir
    Install directory. Default: %LOCALAPPDATA%\Programs\NightOwl.
.PARAMETER NoPathUpdate
    Skip adding the install directory to the user PATH (install) or removing it (-Uninstall).
.PARAMETER Uninstall
    Remove the installed binary (and, if the install directory is now empty, the directory
    and its user PATH entry) instead of installing. Idempotent, downloads nothing.
#>
[CmdletBinding()]
param(
    [string]$Version = $env:NIGHTOWL_VERSION,
    [string]$InstallDir,
    [switch]$NoPathUpdate,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$repo = 'niklasfp/nightowl'
$onWindows = $env:OS -eq 'Windows_NT'

# Shared by the install-time PATH-add below and -Uninstall's PATH-remove: broadcasts
# WM_SETTINGCHANGE so Explorer — and every terminal it spawns — picks up the new user PATH
# without waiting for the next sign-in. A raw registry write alone doesn't send that message
# ([Environment]::SetEnvironmentVariable would, but it can't preserve REG_EXPAND_SZ — see below).
function Send-EnvironmentChangeBroadcast {
    if (-not ('NightOwl.Native' -as [type])) {
        Add-Type -Namespace NightOwl -Name Native -MemberDefinition @'
[DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, UIntPtr wParam, string lParam, uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);
'@
    }
    [UIntPtr]$broadcastResult = [UIntPtr]::Zero
    # HWND_BROADCAST (0xFFFF), WM_SETTINGCHANGE (0x1A), SMTO_ABORTIFHUNG (0x2), 5s timeout.
    [NightOwl.Native]::SendMessageTimeout([IntPtr]0xFFFF, 0x1A, [UIntPtr]::Zero,
        'Environment', 0x2, 5000, [ref]$broadcastResult) | Out-Null
}

# Inspect the zip before extracting: the only safe member is a file named nightowl.exe
# (optionally ./nightowl.exe). Absolute paths, `..`, extra files, directories, and
# renamed entries are rejected so a malicious archive cannot write outside the extract dir.
function Extract-NightOwlExe {
    param(
        [Parameter(Mandatory)][string]$ZipPath,
        [Parameter(Mandatory)][string]$DestinationPath,
        [Parameter(Mandatory)][string]$AssetName
    )

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        $entries = @($zip.Entries)
        if ($entries.Count -ne 1) {
            throw "Archive $AssetName must contain exactly one member named nightowl.exe (found $($entries.Count))."
        }
        $entry = $entries[0]
        $normalized = $entry.FullName.Replace('\', '/')
        if ($normalized.StartsWith('./')) { $normalized = $normalized.Substring(2) }
        if ($normalized.EndsWith('/')) {
            throw "Archive $AssetName member '$($entry.FullName)' is a directory."
        }
        if ($normalized -match '^[A-Za-z]:' -or $normalized.StartsWith('/')) {
            throw "Archive $AssetName member '$($entry.FullName)' is not a safe path."
        }
        if ($normalized -ne 'nightowl.exe' -or $entry.Name -ne 'nightowl.exe') {
            throw "Archive $AssetName has unexpected member '$($entry.FullName)' (expected only 'nightowl.exe')."
        }

        # ZIP stores Unix type/mode in the high 16 bits of ExternalAttributes when present.
        # 0xA000 = symlink, 0x4000 = directory, 0x8000 = regular file. Zero means DOS
        # attributes only (typical Windows-created zip of a regular file) and is allowed.
        if ($null -eq $entry.PSObject.Properties['ExternalAttributes'] -or $null -eq $entry.ExternalAttributes) {
            throw "Archive ${AssetName}: zip ExternalAttributes is unavailable; refusing to extract."
        }
        $unixMode = [int](($entry.ExternalAttributes -shr 16) -band 0xFFFF)
        $fileType = $unixMode -band 0xF000
        if ($fileType -eq 0xA000) {
            throw "Archive $AssetName member 'nightowl.exe' is a symlink."
        }
        if ($fileType -eq 0x4000) {
            throw "Archive $AssetName member 'nightowl.exe' is a directory."
        }
        if ($fileType -ne 0 -and $fileType -ne 0x8000) {
            throw "Archive $AssetName member 'nightowl.exe' is not a regular file."
        }

        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $DestinationPath, $true)
    }
    finally {
        $zip.Dispose()
    }
}

if (-not $InstallDir) {
    $InstallDir = if ($env:NIGHTOWL_INSTALL_DIR) { $env:NIGHTOWL_INSTALL_DIR }
    elseif ($onWindows) { Join-Path $env:LOCALAPPDATA 'Programs\NightOwl' }
    else { throw 'Pass -InstallDir (or NIGHTOWL_INSTALL_DIR) when running outside Windows (test mode).' }
}

if ($Uninstall) {
    $exe = Join-Path $InstallDir 'nightowl.exe'
    $exeExisted = Test-Path $exe
    $didSomething = $false

    if ($exeExisted) {
        Remove-Item -Force $exe
        Write-Host "Removed $exe"
        $didSomething = $true
    }

    # Only tear down the install directory (and its PATH entry) once it is empty — a custom
    # -InstallDir may be shared with other tools, and yanking either out from under them would
    # be more surprising than leaving an empty-ish directory or one dangling PATH entry behind.
    # Conservative choice: if the directory still holds other files, we leave both alone.
    $dirTornDown = -not (Test-Path $InstallDir)
    if (-not $dirTornDown -and -not (Get-ChildItem -Force -LiteralPath $InstallDir -ErrorAction SilentlyContinue)) {
        Remove-Item -Force -LiteralPath $InstallDir
        Write-Host "Removed empty directory $InstallDir"
        $dirTornDown = $true
        $didSomething = $true
    }

    if ($onWindows -and -not $NoPathUpdate -and $dirTornDown) {
        $envKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment', $true)
        try {
            $userPath = [string]$envKey.GetValue('Path', '',
                [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
            $entries = @($userPath -split ';' | Where-Object { $_ -ne '' })
            if ($entries -contains $InstallDir) {
                $kind = $envKey.GetValueKind('Path')
                $newPath = ($entries | Where-Object { $_ -ne $InstallDir }) -join ';'
                $envKey.SetValue('Path', $newPath, $kind)
                Send-EnvironmentChangeBroadcast
                $env:Path = ($env:Path -split ';' | Where-Object { $_ -ne $InstallDir }) -join ';'
                Write-Host "Removed $InstallDir from your user PATH (restart open terminals to pick it up)."
                $didSomething = $true
            }
        }
        finally {
            $envKey.Dispose()
        }
    }

    if (-not $didSomething) {
        Write-Host "Nothing to uninstall: $exe not found."
    }
    exit 0
}

$rid = if ($env:NIGHTOWL_RID) { $env:NIGHTOWL_RID } else {
    switch ("$([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture)") {
        'X64' { 'win-x64' }
        'Arm64' { 'win-arm64' }
        default { throw "NightOwl ships no Windows build for architecture '$_'." }
    }
}

$apiHeaders = @{ Accept = 'application/vnd.github+json' }
if ($env:GITHUB_TOKEN) { $apiHeaders.Authorization = "Bearer $($env:GITHUB_TOKEN)" }

if (-not $Version) {
    $api = "https://api.github.com/repos/$repo/releases"
    try {
        # /releases/latest ignores prereleases (404 when only prereleases exist).
        $Version = (Invoke-RestMethod -Uri "$api/latest" -Headers $apiHeaders).tag_name
    }
    catch {
        try {
            $releases = @(Invoke-RestMethod -Uri "${api}?per_page=1" -Headers $apiHeaders)
            if (-not $releases -or -not $releases[0].tag_name) {
                throw 'Could not determine the latest release from the GitHub API; pass -Version vX.Y.Z.'
            }
            $Version = $releases[0].tag_name
        }
        catch {
            # Any failure here (network error, rate-limit 403, malformed response) should
            # surface the same friendly message rather than a raw HTTP exception.
            throw 'Could not determine the latest release from the GitHub API; pass -Version vX.Y.Z.'
        }
    }
}

$asset = "nightowl-$rid.zip"
$base = if ($env:NIGHTOWL_ASSET_BASE) { $env:NIGHTOWL_ASSET_BASE } else { "https://github.com/$repo/releases/download/$Version" }
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) "nightowl-install-$([System.IO.Path]::GetRandomFileName())"
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    Write-Host "Downloading $asset ($Version) ..."
    $zipPath = Join-Path $tmp $asset
    try {
        Invoke-WebRequest -Uri "$base/$asset" -OutFile $zipPath -UseBasicParsing
    }
    catch {
        throw "Download failed: $base/$asset - does release $Version exist and carry this asset? ($_)"
    }

    $sumsPath = Join-Path $tmp 'SHA256SUMS.txt'
    try {
        Invoke-WebRequest -Uri "$base/SHA256SUMS.txt" -OutFile $sumsPath -UseBasicParsing
    }
    catch {
        $statusCode = $null
        if ($_.Exception.PSObject.Properties['Response'] -and $_.Exception.Response) {
            $statusCode = [int]$_.Exception.Response.StatusCode
        }
        elseif ($_.Exception.PSObject.Properties['StatusCode'] -and $_.Exception.StatusCode) {
            $statusCode = [int]$_.Exception.StatusCode
        }
        if ($statusCode -eq 404) {
            throw "Release $Version has no SHA256SUMS.txt; refusing an unverified install."
        }
        throw "Could not download $base/SHA256SUMS.txt$(if ($statusCode) { " (HTTP $statusCode)" })."
    }
    $sums = @(Get-Content -LiteralPath $sumsPath)
    if (-not $sums) { throw "SHA256SUMS.txt on release $Version is empty." }
    $entry = $sums | Where-Object { ($_ -split '\s+', 2)[1] -eq $asset } | Select-Object -First 1
    if (-not $entry) { throw "SHA256SUMS.txt on release $Version has no entry for $asset." }
    $expected = ($entry -split '\s+')[0].ToLowerInvariant()
    $actual = (Get-FileHash -Algorithm SHA256 -Path $zipPath).Hash.ToLowerInvariant()
    if ($expected -ne $actual) { throw "Checksum mismatch for $asset (expected $expected, got $actual)." }
    Write-Host 'Checksum verified.'

    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    $exe = Join-Path $InstallDir 'nightowl.exe'
    $staging = Join-Path $InstallDir 'nightowl.exe.new'
    Extract-NightOwlExe -ZipPath $zipPath -DestinationPath $staging -AssetName $asset
    Move-Item -LiteralPath $staging -Destination $exe -Force
    Write-Host "Installed nightowl $Version -> $exe"

    if ($onWindows -and -not $NoPathUpdate) {
        # Read and write the user PATH via the registry, preserving its value kind: a PATH with
        # %VAR% entries is REG_EXPAND_SZ, and [Environment]::SetEnvironmentVariable would rewrite
        # it as REG_SZ, permanently breaking those entries' expansion.
        $envKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment', $true)
        try {
            $userPath = [string]$envKey.GetValue('Path', '',
                [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
            if (($userPath -split ';') -notcontains $InstallDir) {
                $kind = if ($userPath) { $envKey.GetValueKind('Path') } else { [Microsoft.Win32.RegistryValueKind]::ExpandString }
                $envKey.SetValue('Path', "$userPath;$InstallDir".Trim(';'), $kind)
                Send-EnvironmentChangeBroadcast
                $env:Path = "$($env:Path);$InstallDir"
                Write-Host "Added $InstallDir to your user PATH (restart open terminals to pick it up)."
            }
        }
        finally {
            $envKey.Dispose()
        }
    }
    if ($onWindows -and $env:NIGHTOWL_SKIP_SMOKE -ne '1') {
        & $exe --version | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Installed binary failed to run (exit $LASTEXITCODE)." }
    }
}
finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
