# Packaging & Distribution Implementation Plan

> **Status: implemented** (PR #32). Kept for history — the unchecked `- [ ]` task boxes below
> reflect the plan as authored, not outstanding work. See
> [packaging-decision-log.md](packaging-decision-log.md) for what shipped and why.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make NightOwl installable via Homebrew, winget, `curl | sh` (macOS/Linux), and
`irm | iex` (Windows), with checksummed releases and CI automation that keeps the packaging
manifests up to date on every tag.

**Architecture:** Release assets gain a `SHA256SUMS.txt` and tag-stamped versions. A single
generator (`scripts/update-packaging.ts`) owns `Formula/nightowl.rb` and
`packaging/winget/*.yaml`; a new `packaging` job in `release.yml` reruns it per tag and commits
to `main`, and a gated `winget` job submits stable releases via `wingetcreate`. Two standalone
installers (`install.sh`, `install.ps1`) at the repo root download from GitHub Releases and are
also published to the website. All decisions/assumptions:
[docs/packaging-decision-log.md](packaging-decision-log.md).

**Tech Stack:** GitHub Actions (SHA-pinned), POSIX sh, PowerShell 5.1+, Node 24 native
TS type-stripping (zero npm deps), Homebrew formula (Ruby), winget YAML manifests 1.6.0.

## Global Constraints

- All GitHub Actions must be SHA-pinned with a `# vX.Y.Z` comment (look up SHAs with
  `gh api repos/{owner}/{repo}/git/ref/tags/{tag} --jq '.object.sha'`).
- No new NuGet/npm dependencies. No Python. Scripts: POSIX sh / PowerShell / Node-TS only.
- `install.sh` must pass `shellcheck` and `sh -n`; it must run under `/bin/sh` (dash-compatible,
  no bashisms).
- The .NET code is untouched; do not modify anything under `src/` or `tests/` except nothing —
  if a task seems to need it, stop and flag it.
- Repo conventions: run `dotnet format` before committing when .NET files changed (none should
  be); keep commits focused and atomic, one per task.
- Asset names are fixed: `nightowl-{win-x64,win-arm64}.zip`,
  `nightowl-{osx-arm64,linux-x64,linux-arm64}.tar.gz`, plus new `SHA256SUMS.txt`.
- `v0.1.0-beta.1` asset SHA-256s (already computed from the real assets — reuse verbatim):
  - `nightowl-linux-arm64.tar.gz` = `048a5000a946c65cafb793c109c9c8667554f9c95607eea46198594b37bf0cad`
  - `nightowl-linux-x64.tar.gz` = `90be65c26997d7b0d26e52087e5480910cee08b49ca412bff334c745aaa82b7b`
  - `nightowl-osx-arm64.tar.gz` = `264b973c73cf3be07f5273f1e1f52240e5f3a78fec817a8efe378dfcf2795fdb`
  - `nightowl-win-arm64.zip` = `5e313fd1e7d997e942d57cd7a10c494689f4880a96f43564ed92696a8d245ebd`
  - `nightowl-win-x64.zip` = `14251224b84c4384c8b80bf3eab0ecb773895833a6304d165ea60b767509f253`
- Tooling: if `shellcheck` or `actionlint` is missing locally, `brew install` it. `pwsh` may be
  absent — then note it in the task report instead of failing.

---

### Task 1: Version-stamp AOT publishes and add SHA256SUMS.txt to releases

**Files:**
- Modify: `.github/workflows/release.yml` (the two `Publish (AOT…)` steps and the
  `Create GitHub Release` step)

**Interfaces:**
- Produces: releases carry `SHA256SUMS.txt` (sha256sum format: `<hex>  <filename>`, two
  spaces) alongside the 5 archives + nupkg; released binaries report the tag version from
  `nightowl --version`. Tasks 2, 3, 4 rely on both.

- [ ] **Step 1: Stamp the tag version into the non-container AOT publish**

Replace the step at `.github/workflows/release.yml:43-45`:

```yaml
      - name: Publish (AOT)
        if: matrix.rid != 'linux-arm64'
        run: dotnet publish src/NightOwl -r ${{ matrix.rid }} -c Release -p:PublishAot=true -o publish/${{ matrix.rid }}
```

with (note `shell: bash` — the step also runs on the Windows legs):

```yaml
      # Binaries are version-stamped from the tag (v1.2.3 -> 1.2.3) so `nightowl --version`
      # matches the release; non-tag runs keep the csproj default.
      - name: Publish (AOT)
        if: matrix.rid != 'linux-arm64'
        shell: bash
        run: |
          set -euo pipefail
          version=""
          [[ "${{ github.ref }}" == refs/tags/v* ]] && version="-p:Version=${GITHUB_REF_NAME#v}"
          dotnet publish src/NightOwl -r ${{ matrix.rid }} -c Release -p:PublishAot=true -o publish/${{ matrix.rid }} $version
```

- [ ] **Step 2: Stamp the version in the linux-arm64 container publish**

In the `Publish (AOT, emulated arm64 container)` step, compute the same `$version` before the
`docker run` and pass it into the inner `dotnet publish`:

```yaml
      - name: Publish (AOT, emulated arm64 container)
        if: matrix.rid == 'linux-arm64'
        run: |
          set -euxo pipefail
          version=""
          [[ "${{ github.ref }}" == refs/tags/v* ]] && version="-p:Version=${GITHUB_REF_NAME#v}"
          docker run --rm --platform linux/arm64 -v "$PWD":/src -w /src \
            mcr.microsoft.com/dotnet/sdk:10.0-noble-aot \
            bash -c "dotnet publish src/NightOwl -r linux-arm64 -c Release -p:PublishAot=true -o publish/linux-arm64 $version && \
                     chmod +x publish/linux-arm64/nightowl"
          sudo chown -R "$(id -u):$(id -g)" publish/linux-arm64
```

- [ ] **Step 3: Generate SHA256SUMS.txt in the release job**

In the `release` job, insert between `Download all build artifacts` and
`Create GitHub Release`:

```yaml
      # One checksum file over every asset — consumed by the install scripts, the Homebrew
      # formula, and the winget manifests (via the packaging job).
      - name: Generate checksums
        run: |
          set -euo pipefail
          cd dist
          sha256sum * > SHA256SUMS.txt
          cat SHA256SUMS.txt
```

(`gh release create "$GITHUB_REF_NAME" dist/*` already picks the file up — no change needed
there.)

- [ ] **Step 4: Verify**

Run: `actionlint .github/workflows/release.yml` — expected: no output, exit 0.

Locally prove the stamp flows to `--version`:

```bash
dotnet publish src/NightOwl -r osx-arm64 -c Release -p:PublishAot=true -p:Version=9.9.9-test -o /tmp/nightowl-stamp-test
/tmp/nightowl-stamp-test/nightowl --version
```

Expected output contains `9.9.9-test`.

- [ ] **Step 5: Commit**

```bash
git add .github/workflows/release.yml
git commit -m "ci: stamp tag version into AOT binaries and attach SHA256SUMS.txt"
```

---

### Task 2: `install.sh` for macOS/Linux

**Files:**
- Create: `install.sh` (repo root, mode 755)

**Interfaces:**
- Consumes: release assets + optional `SHA256SUMS.txt` (Task 1 format).
- Produces: `install.sh` honoring `--version`, `--install-dir`, `NIGHTOWL_VERSION`,
  `NIGHTOWL_INSTALL_DIR`, `NIGHTOWL_ASSET_BASE` (test/mirror hook), `GITHUB_TOKEN` (API auth
  only). Task 5 publishes it to Pages; Task 6 documents it.

- [ ] **Step 1: Write `install.sh`** with exactly this content:

```sh
#!/bin/sh
# NightOwl installer for macOS and Linux.
#
# Downloads the native binary for this machine from GitHub Releases, verifies its
# checksum when the release ships one, and installs it (default: ~/.local/bin).
#
#   curl -fsSL https://raw.githubusercontent.com/niklasfp/nightowl/main/install.sh | sh
#
# Options (flags win over environment variables):
#   --version <vX.Y.Z>    install a specific tagged release (default: newest release)
#   --install-dir <dir>   where to put the binary (default: ~/.local/bin)
#   -h, --help            show this help
#
# Environment:
#   NIGHTOWL_VERSION      same as --version
#   NIGHTOWL_INSTALL_DIR  same as --install-dir
#   NIGHTOWL_ASSET_BASE   override the asset download base URL (testing / mirrors)
#   GITHUB_TOKEN          bearer token for the GitHub API (rate limits); never sent to
#                         asset downloads
set -eu

REPO="niklasfp/nightowl"
VERSION="${NIGHTOWL_VERSION:-}"
INSTALL_DIR="${NIGHTOWL_INSTALL_DIR:-$HOME/.local/bin}"

say() { printf '%s\n' "$*" >&2; }
err() { say "install.sh: error: $*"; exit 1; }
have() { command -v "$1" >/dev/null 2>&1; }

usage() {
    cat >&2 <<'EOF'
NightOwl installer for macOS and Linux.

Usage: install.sh [--version vX.Y.Z] [--install-dir DIR]

  --version <vX.Y.Z>    install a specific tagged release (default: newest release)
  --install-dir <dir>   where to put the binary (default: ~/.local/bin)
  -h, --help            show this help

Environment: NIGHTOWL_VERSION, NIGHTOWL_INSTALL_DIR, NIGHTOWL_ASSET_BASE, GITHUB_TOKEN.
EOF
}

# API requests may authenticate (private repos, rate limits); asset downloads never do —
# GitHub redirects them to S3, which rejects requests carrying an Authorization header.
fetch_api() { # fetch_api <url>  -> body on stdout
    if have curl; then
        if [ -n "${GITHUB_TOKEN:-}" ]; then
            curl -fsSL -H "Authorization: Bearer $GITHUB_TOKEN" "$1"
        else
            curl -fsSL "$1"
        fi
    elif have wget; then
        if [ -n "${GITHUB_TOKEN:-}" ]; then
            wget -q --header "Authorization: Bearer $GITHUB_TOKEN" -O - "$1"
        else
            wget -q -O - "$1"
        fi
    else
        err "this script needs curl or wget"
    fi
}

fetch_file() { # fetch_file <url> <output-path>
    if have curl; then
        curl -fsSL -o "$2" "$1"
    elif have wget; then
        wget -q -O "$2" "$1"
    else
        err "this script needs curl or wget"
    fi
}

json_first_tag() { # read a GitHub releases JSON payload on stdin, print the first tag_name
    tr ',{' '\n\n' | grep -m1 '"tag_name"' | cut -d'"' -f4
}

resolve_version() {
    api="https://api.github.com/repos/$REPO/releases"
    # /releases/latest ignores prereleases (404 when only prereleases exist) — fall back
    # to the newest release of any kind.
    VERSION=$( { fetch_api "$api/latest" 2>/dev/null || true; } | { json_first_tag || true; } )
    if [ -z "$VERSION" ]; then
        VERSION=$( { fetch_api "$api?per_page=1" 2>/dev/null || true; } | { json_first_tag || true; } )
    fi
    [ -n "$VERSION" ] || err "could not determine the latest release from the GitHub API; pass --version vX.Y.Z"
}

while [ $# -gt 0 ]; do
    case "$1" in
        --version) [ $# -ge 2 ] || err "--version needs a value"; VERSION="$2"; shift 2 ;;
        --install-dir) [ $# -ge 2 ] || err "--install-dir needs a value"; INSTALL_DIR="$2"; shift 2 ;;
        -h | --help) usage; exit 0 ;;
        *) err "unknown option: $1 (try --help)" ;;
    esac
done

os=$(uname -s)
arch=$(uname -m)
case "$os" in
    Darwin)
        case "$arch" in
            arm64) rid="osx-arm64" ;;
            *) err "NightOwl ships no macOS build for '$arch' (Apple Silicon only)" ;;
        esac
        ;;
    Linux)
        case "$arch" in
            x86_64 | amd64) rid="linux-x64" ;;
            aarch64 | arm64) rid="linux-arm64" ;;
            *) err "NightOwl ships no Linux build for '$arch'" ;;
        esac
        ;;
    *) err "unsupported OS '$os' — on Windows, use install.ps1" ;;
esac

[ -n "$VERSION" ] || resolve_version
asset="nightowl-$rid.tar.gz"
base="${NIGHTOWL_ASSET_BASE:-https://github.com/$REPO/releases/download/$VERSION}"

tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT INT TERM

say "Downloading $asset ($VERSION) ..."
fetch_file "$base/$asset" "$tmp/$asset" \
    || err "download failed: $base/$asset — does release $VERSION exist and carry this asset? (pass --version to pick another)"

if fetch_file "$base/SHA256SUMS.txt" "$tmp/SHA256SUMS.txt" 2>/dev/null; then
    if have sha256sum; then
        actual=$(sha256sum "$tmp/$asset" | cut -d' ' -f1)
    else
        actual=$(shasum -a 256 "$tmp/$asset" | cut -d' ' -f1)
    fi
    expected=$(awk -v f="$asset" '$2 == f { print $1 }' "$tmp/SHA256SUMS.txt")
    [ -n "$expected" ] || err "SHA256SUMS.txt on release $VERSION has no entry for $asset"
    [ "$expected" = "$actual" ] || err "checksum mismatch for $asset (expected $expected, got $actual)"
    say "Checksum verified."
else
    say "Note: release $VERSION has no SHA256SUMS.txt; skipping checksum verification."
fi

tar -xzf "$tmp/$asset" -C "$tmp"
[ -f "$tmp/nightowl" ] || err "archive $asset did not contain a 'nightowl' binary"
mkdir -p "$INSTALL_DIR"
cp "$tmp/nightowl" "$INSTALL_DIR/nightowl"
chmod 755 "$INSTALL_DIR/nightowl"

if v=$("$INSTALL_DIR/nightowl" --version 2>/dev/null); then
    say "Installed nightowl $v -> $INSTALL_DIR/nightowl"
else
    say "Installed $INSTALL_DIR/nightowl ($VERSION)"
fi
case ":$PATH:" in
    *:"$INSTALL_DIR":*) ;;
    *) say "Note: $INSTALL_DIR is not on your PATH — add it, e.g.: export PATH=\"$INSTALL_DIR:\$PATH\"" ;;
esac
```

Then: `chmod +x install.sh`

- [ ] **Step 2: Lint**

Run: `sh -n install.sh && shellcheck install.sh`
Expected: exit 0, no findings. Fix any findings by adjusting the script (keep POSIX sh).

- [ ] **Step 3: Hermetic end-to-end test against the real v0.1.0-beta.1 assets**

```bash
T=$(mktemp -d)
gh release download v0.1.0-beta.1 --repo niklasfp/nightowl -p 'nightowl-osx-arm64.tar.gz' -D "$T"
( cd "$T" && shasum -a 256 nightowl-osx-arm64.tar.gz > SHA256SUMS.txt )
( cd "$T" && node -e 'const h=require("http"),f=require("fs"),p=require("path");h.createServer((q,s)=>{f.readFile(p.join(process.cwd(),q.url),(e,d)=>{if(e){s.statusCode=404;s.end();}else s.end(d);});}).listen(8931,()=>console.log("serving"))' & echo $! > "$T/server.pid" )
sleep 1
NIGHTOWL_ASSET_BASE=http://127.0.0.1:8931 sh install.sh --version v0.1.0-beta.1 --install-dir "$T/bin"
"$T/bin/nightowl" --version
kill "$(cat "$T/server.pid")"; rm -rf "$T"
```

(The node one-liner just serves the cwd; any equivalent static server is fine — e.g.
`npx serve` is NOT fine (adds a dep); a python server is NOT fine (no Python rule). Adjust the
one-liner if it misbehaves, keeping it dependency-free.)

Expected: "Checksum verified.", "Installed nightowl 0.1.0 -> …" and the final `--version`
prints `0.1.0` (beta binaries predate version stamping — that's expected, see decision log D4).

Also test the failure path: `sh install.sh --version v0.0.0-nope --install-dir "$T/bin"` (no
ASSET_BASE) — expected: exits non-zero with the "download failed" message.

- [ ] **Step 4: Commit**

```bash
git add install.sh
git commit -m "feat: add install.sh for macOS/Linux"
```

---

### Task 3: `install.ps1` for Windows

**Files:**
- Create: `install.ps1` (repo root)

**Interfaces:**
- Consumes: release assets + optional `SHA256SUMS.txt` (Task 1 format).
- Produces: `install.ps1` honoring `-Version`, `-InstallDir`, `-NoPathUpdate`,
  `NIGHTOWL_VERSION`, `NIGHTOWL_INSTALL_DIR`, `NIGHTOWL_ASSET_BASE`, `NIGHTOWL_RID` (test
  hook), `GITHUB_TOKEN`. Task 5 publishes it to Pages; Task 6 documents it.

- [ ] **Step 1: Write `install.ps1`** with exactly this content:

```powershell
#Requires -Version 5.1
<#
.SYNOPSIS
    NightOwl installer for Windows.
.DESCRIPTION
    Downloads the native win-x64/win-arm64 binary from GitHub Releases, verifies its
    checksum when the release ships one, installs it user-scoped (no admin), and adds
    the install directory to the user PATH.

    One-liner:
      irm https://raw.githubusercontent.com/niklasfp/nightowl/main/install.ps1 | iex

    With parameters, download first:
      iwr -useb https://raw.githubusercontent.com/niklasfp/nightowl/main/install.ps1 -OutFile install.ps1
      .\install.ps1 -Version v0.1.0 -InstallDir C:\Tools\NightOwl
.PARAMETER Version
    Tagged release to install (e.g. v0.1.0). Default: newest release.
.PARAMETER InstallDir
    Install directory. Default: %LOCALAPPDATA%\Programs\NightOwl.
.PARAMETER NoPathUpdate
    Skip adding the install directory to the user PATH.
#>
[CmdletBinding()]
param(
    [string]$Version = $env:NIGHTOWL_VERSION,
    [string]$InstallDir,
    [switch]$NoPathUpdate
)

$ErrorActionPreference = 'Stop'
$repo = 'niklasfp/nightowl'
$onWindows = $env:OS -eq 'Windows_NT'

if (-not $InstallDir) {
    $InstallDir = if ($env:NIGHTOWL_INSTALL_DIR) { $env:NIGHTOWL_INSTALL_DIR }
    elseif ($onWindows) { Join-Path $env:LOCALAPPDATA 'Programs\NightOwl' }
    else { throw 'Pass -InstallDir (or NIGHTOWL_INSTALL_DIR) when running outside Windows (test mode).' }
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
        $releases = @(Invoke-RestMethod -Uri "${api}?per_page=1" -Headers $apiHeaders)
        if (-not $releases -or -not $releases[0].tag_name) {
            throw 'Could not determine the latest release from the GitHub API; pass -Version vX.Y.Z.'
        }
        $Version = $releases[0].tag_name
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
        throw "Download failed: $base/$asset — does release $Version exist and carry this asset? ($_)"
    }

    $sumsPath = Join-Path $tmp 'SHA256SUMS.txt'
    $sums = $null
    try {
        Invoke-WebRequest -Uri "$base/SHA256SUMS.txt" -OutFile $sumsPath -UseBasicParsing
        $sums = Get-Content $sumsPath
    }
    catch {
        Write-Host "Note: release $Version has no SHA256SUMS.txt; skipping checksum verification."
    }
    if ($sums) {
        $entry = $sums | Where-Object { ($_ -split '\s+', 2)[1] -eq $asset } | Select-Object -First 1
        if (-not $entry) { throw "SHA256SUMS.txt on release $Version has no entry for $asset." }
        $expected = ($entry -split '\s+')[0].ToLowerInvariant()
        $actual = (Get-FileHash -Algorithm SHA256 -Path $zipPath).Hash.ToLowerInvariant()
        if ($expected -ne $actual) { throw "Checksum mismatch for $asset (expected $expected, got $actual)." }
        Write-Host 'Checksum verified.'
    }

    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    Expand-Archive -Path $zipPath -DestinationPath $InstallDir -Force
    $exe = Join-Path $InstallDir 'nightowl.exe'
    if (-not (Test-Path $exe)) { throw "Archive $asset did not contain nightowl.exe." }
    Write-Host "Installed nightowl $Version -> $exe"

    if ($onWindows -and -not $NoPathUpdate) {
        $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
        if (($userPath -split ';') -notcontains $InstallDir) {
            [Environment]::SetEnvironmentVariable('Path', "$userPath;$InstallDir".Trim(';'), 'User')
            $env:Path = "$($env:Path);$InstallDir"
            Write-Host "Added $InstallDir to your user PATH (restart open terminals to pick it up)."
        }
    }
    if ($onWindows) { & $exe --version | Out-Null }
}
finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
```

- [ ] **Step 2: Parse check**

If `pwsh` is available:
`pwsh -NoProfile -Command '$t=[System.Management.Automation.Language.Parser]::ParseFile("install.ps1",[ref]$null,[ref]$e); if($e){$e; exit 1}; "parse OK"'`
Expected: `parse OK`. If `pwsh` is missing, record that in the task report and skip.

- [ ] **Step 3: Hermetic run on macOS pwsh (skip if no pwsh)**

Reuse the Task 2 local-server pattern, but download the **win-x64 zip** asset and force the
rid:

```bash
T=$(mktemp -d)
gh release download v0.1.0-beta.1 --repo niklasfp/nightowl -p 'nightowl-win-x64.zip' -D "$T"
( cd "$T" && shasum -a 256 nightowl-win-x64.zip > SHA256SUMS.txt )
( cd "$T" && node -e 'const h=require("http"),f=require("fs"),p=require("path");h.createServer((q,s)=>{f.readFile(p.join(process.cwd(),q.url),(e,d)=>{if(e){s.statusCode=404;s.end();}else s.end(d);});}).listen(8932,()=>console.log("serving"))' & echo $! > "$T/server.pid" )
sleep 1
NIGHTOWL_ASSET_BASE=http://127.0.0.1:8932 NIGHTOWL_RID=win-x64 \
  pwsh -NoProfile -File install.ps1 -Version v0.1.0-beta.1 -InstallDir "$T/bin"
test -f "$T/bin/nightowl.exe" && echo "exe installed OK"
kill "$(cat "$T/server.pid")"; rm -rf "$T"
```

Expected: "Checksum verified.", "Installed nightowl v0.1.0-beta.1 -> …", `exe installed OK`
(the exe is not *run* off-Windows; the `$onWindows` guards skip PATH + `--version`).

- [ ] **Step 4: Commit**

```bash
git add install.ps1
git commit -m "feat: add install.ps1 for Windows"
```

---

### Task 4: Packaging generator + Homebrew formula + winget manifests

**Files:**
- Create: `scripts/update-packaging.ts`
- Create (generated): `Formula/nightowl.rb`, `packaging/winget/niklasfp.NightOwl.yaml`,
  `packaging/winget/niklasfp.NightOwl.installer.yaml`,
  `packaging/winget/niklasfp.NightOwl.locale.en-US.yaml`

**Interfaces:**
- Consumes: `SHA256SUMS.txt` format from Task 1.
- Produces: `node scripts/update-packaging.ts <version-without-v> <path-to-SHA256SUMS.txt>`
  fully regenerates all four output files. Task 5's `packaging` job calls it exactly like
  that; Task 6 documents the "generated — don't hand-edit" rule.

- [ ] **Step 1: Write `scripts/update-packaging.ts`** with exactly this content:

```ts
// Regenerates the Homebrew formula and winget manifests for a release.
//
//   node scripts/update-packaging.ts <version-without-v> <path-to-SHA256SUMS.txt>
//
// The committed Formula/nightowl.rb and packaging/winget/*.yaml are the verbatim output of
// this script; the `packaging` job in release.yml reruns it on every tag and commits the
// result to main. Zero dependencies — runs on Node >= 22.18 via native TS type-stripping.
import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";

const root = join(import.meta.dirname, "..");
const [version, sumsPath] = process.argv.slice(2);
if (!version || !sumsPath) {
  console.error("usage: node scripts/update-packaging.ts <version> <SHA256SUMS.txt>");
  process.exit(2);
}
if (version.startsWith("v")) {
  console.error(`version must not include the leading 'v' (got '${version}')`);
  process.exit(2);
}

const sums = new Map<string, string>();
for (const line of readFileSync(sumsPath, "utf8").split("\n")) {
  const m = line.trim().match(/^([0-9a-fA-F]{64})\s+\*?(.+)$/);
  if (m) sums.set(m[2], m[1].toLowerCase());
}
function sha(asset: string): string {
  const s = sums.get(asset);
  if (!s) {
    console.error(`missing checksum for ${asset} in ${sumsPath}`);
    process.exit(1);
  }
  return s;
}
const url = (asset: string) =>
  `https://github.com/niklasfp/nightowl/releases/download/v${version}/${asset}`;

const formula = `# Generated by scripts/update-packaging.ts — do not edit by hand.
class Nightowl < Formula
  desc "Cross-platform keep-the-computer-awake CLI"
  homepage "https://niklasfp.github.io/nightowl/"
  version "${version}"
  license "MIT"

  on_macos do
    on_arm do
      url "${url("nightowl-osx-arm64.tar.gz")}"
      sha256 "${sha("nightowl-osx-arm64.tar.gz")}"
    end
  end

  on_linux do
    on_intel do
      url "${url("nightowl-linux-x64.tar.gz")}"
      sha256 "${sha("nightowl-linux-x64.tar.gz")}"
    end
    on_arm do
      url "${url("nightowl-linux-arm64.tar.gz")}"
      sha256 "${sha("nightowl-linux-arm64.tar.gz")}"
    end
  end

  def install
    bin.install "nightowl"
  end

  test do
    assert_match(/\\d+\\.\\d+/, shell_output("#{bin}/nightowl --version"))
  end
end
`;

const manifestVersion = "1.6.0";
const id = "niklasfp.NightOwl";

const versionManifest = `# Generated by scripts/update-packaging.ts — do not edit by hand.
PackageIdentifier: ${id}
PackageVersion: ${version}
DefaultLocale: en-US
ManifestType: version
ManifestVersion: ${manifestVersion}
`;

const installerManifest = `# Generated by scripts/update-packaging.ts — do not edit by hand.
PackageIdentifier: ${id}
PackageVersion: ${version}
InstallerType: zip
NestedInstallerType: portable
NestedInstallerFiles:
  - RelativeFilePath: nightowl.exe
    PortableCommandAlias: nightowl
Installers:
  - Architecture: x64
    InstallerUrl: ${url("nightowl-win-x64.zip")}
    InstallerSha256: ${sha("nightowl-win-x64.zip").toUpperCase()}
  - Architecture: arm64
    InstallerUrl: ${url("nightowl-win-arm64.zip")}
    InstallerSha256: ${sha("nightowl-win-arm64.zip").toUpperCase()}
ManifestType: installer
ManifestVersion: ${manifestVersion}
`;

const localeManifest = `# Generated by scripts/update-packaging.ts — do not edit by hand.
PackageIdentifier: ${id}
PackageVersion: ${version}
PackageLocale: en-US
Publisher: Niklas Petersen
PublisherUrl: https://github.com/niklasfp
PublisherSupportUrl: https://github.com/niklasfp/nightowl/issues
PackageName: NightOwl
PackageUrl: https://niklasfp.github.io/nightowl/
License: MIT
LicenseUrl: https://github.com/niklasfp/nightowl/blob/main/LICENSE
ShortDescription: Cross-platform "keep the computer awake" CLI
Moniker: nightowl
Tags:
  - awake
  - caffeinate
  - cli
  - keep-awake
  - sleep-inhibitor
ManifestType: defaultLocale
ManifestVersion: ${manifestVersion}
`;

mkdirSync(join(root, "Formula"), { recursive: true });
writeFileSync(join(root, "Formula", "nightowl.rb"), formula);
const wingetDir = join(root, "packaging", "winget");
mkdirSync(wingetDir, { recursive: true });
writeFileSync(join(wingetDir, `${id}.yaml`), versionManifest);
writeFileSync(join(wingetDir, `${id}.installer.yaml`), installerManifest);
writeFileSync(join(wingetDir, `${id}.locale.en-US.yaml`), localeManifest);
console.log(`Packaging manifests updated for v${version}.`);
```

- [ ] **Step 2: Generate the committed manifests from the real beta.1 checksums**

Build a sums file from the Global Constraints table (exact two-space sha256sum format), then
run the generator:

```bash
cat > /tmp/beta1-SHA256SUMS.txt <<'EOF'
048a5000a946c65cafb793c109c9c8667554f9c95607eea46198594b37bf0cad  nightowl-linux-arm64.tar.gz
90be65c26997d7b0d26e52087e5480910cee08b49ca412bff334c745aaa82b7b  nightowl-linux-x64.tar.gz
264b973c73cf3be07f5273f1e1f52240e5f3a78fec817a8efe378dfcf2795fdb  nightowl-osx-arm64.tar.gz
5e313fd1e7d997e942d57cd7a10c494689f4880a96f43564ed92696a8d245ebd  nightowl-win-arm64.zip
14251224b84c4384c8b80bf3eab0ecb773895833a6304d165ea60b767509f253  nightowl-win-x64.zip
EOF
node scripts/update-packaging.ts 0.1.0-beta.1 /tmp/beta1-SHA256SUMS.txt
```

Expected: `Packaging manifests updated for v0.1.0-beta.1.` and the four files created.

- [ ] **Step 3: Verify the formula**

Run: `ruby -c Formula/nightowl.rb` — expected `Syntax OK`.
Run: `brew style Formula/nightowl.rb` — fix offenses **by changing the template in
update-packaging.ts and re-running Step 2** (never edit the generated file), unless the fix
conflicts with the generated-file header comment (RuboCop may flag it — a
`# typed:`/comment-related offense is acceptable to leave; note it).

- [ ] **Step 4: Sanity-check idempotency**

Re-run the Step 2 generator command; `git status --porcelain` must show no *new* modifications
after the first run (byte-identical output).

- [ ] **Step 5: Commit**

```bash
git add scripts/update-packaging.ts Formula packaging
git commit -m "feat: add Homebrew formula, winget manifests, and their generator"
```

---

### Task 5: Release automation (packaging + winget jobs) and Pages staging

**Files:**
- Modify: `.github/workflows/release.yml` (append two jobs)
- Modify: `.github/workflows/pages.yml` (trigger paths + artifact staging)

**Interfaces:**
- Consumes: `SHA256SUMS.txt` release asset (Task 1), `scripts/update-packaging.ts` CLI
  (Task 4), `install.sh`/`install.ps1` at repo root (Tasks 2–3).

- [ ] **Step 1: Append the `packaging` job to release.yml**

Append (the `actions/setup-node` SHA below was resolved from tag `v7.0.0` on 2026-07-19 via
`gh api repos/actions/setup-node/git/ref/tags/v7.0.0`; the checkout SHA is the one already
pinned throughout the file):

```yaml
  # Regenerate Formula/nightowl.rb + packaging/winget/*.yaml from the released checksums and
  # commit them back to main, so the packaging manifests always match the newest release.
  packaging:
    name: update packaging manifests
    needs: [release]
    runs-on: ubuntu-latest
    if: startsWith(github.ref, 'refs/tags/')
    permissions:
      contents: write
    steps:
      - uses: actions/checkout@9c091bb21b7c1c1d1991bb908d89e4e9dddfe3e0 # v7.0.0
        with:
          ref: main

      - name: Setup Node
        uses: actions/setup-node@820762786026740c76f36085b0efc47a31fe5020 # v7.0.0
        with:
          node-version: '24'

      - name: Regenerate manifests
        env:
          GH_TOKEN: ${{ github.token }}
        run: |
          set -euo pipefail
          gh release download "$GITHUB_REF_NAME" --repo "$GITHUB_REPOSITORY" -p SHA256SUMS.txt -D /tmp/release
          node scripts/update-packaging.ts "${GITHUB_REF_NAME#v}" /tmp/release/SHA256SUMS.txt

      - name: Commit to main
        run: |
          set -euo pipefail
          git config user.name "github-actions[bot]"
          git config user.email "41898282+github-actions[bot]@users.noreply.github.com"
          git add Formula packaging
          if git diff --cached --quiet; then
            echo "No packaging changes."
            exit 0
          fi
          git commit -m "chore: update packaging manifests for ${GITHUB_REF_NAME}"
          git push origin HEAD:main
```

- [ ] **Step 2: Append the `winget` job**

```yaml
  # Submit stable releases to microsoft/winget-pkgs. Skipped for prerelease (hyphenated)
  # tags and when the WINGET_TOKEN secret is absent. The FIRST submission must be done
  # manually with `wingetcreate new` (see docs/packaging-decision-log.md D3) — this job
  # only handles version updates once the package exists upstream.
  winget:
    name: submit to winget
    needs: [release]
    runs-on: windows-latest
    if: startsWith(github.ref, 'refs/tags/v') && !contains(github.ref_name, '-')
    steps:
      - name: Update winget-pkgs via wingetcreate
        shell: pwsh
        env:
          WINGET_TOKEN: ${{ secrets.WINGET_TOKEN }}
        run: |
          if (-not $env:WINGET_TOKEN) {
            Write-Host 'WINGET_TOKEN secret not set; skipping winget submission.'
            exit 0
          }
          $version = $env:GITHUB_REF_NAME.TrimStart('v')
          $base = "https://github.com/$env:GITHUB_REPOSITORY/releases/download/$env:GITHUB_REF_NAME"
          # Microsoft's documented CI pattern for wingetcreate (not a GitHub Action, so
          # SHA-pinning does not apply).
          Invoke-WebRequest https://aka.ms/wingetcreate -OutFile wingetcreate.exe
          .\wingetcreate.exe update niklasfp.NightOwl --version $version `
            --urls "$base/nightowl-win-x64.zip" "$base/nightowl-win-arm64.zip" `
            --submit --token $env:WINGET_TOKEN
```

- [ ] **Step 3: Stage install scripts into the Pages artifact**

In `pages.yml`: extend the push trigger paths to
`['site/**', 'install.sh', 'install.ps1', '.github/workflows/pages.yml']`, and replace the
`Upload site artifact` step with a staging step + upload:

```yaml
      # The install scripts are served from the site root too, so
      # https://niklasfp.github.io/nightowl/install.sh works once Pages is re-enabled.
      - name: Stage site with install scripts
        run: |
          set -euo pipefail
          mkdir -p _site_out
          cp -R site/. _site_out/
          cp install.sh install.ps1 _site_out/

      - name: Upload site artifact
        uses: actions/upload-pages-artifact@fc324d3547104276b827a68afc52ff2a11cc49c9 # v5.0.0
        with:
          path: _site_out
```

- [ ] **Step 4: Verify**

Run: `actionlint .github/workflows/release.yml .github/workflows/pages.yml`
Expected: exit 0. (Note: `github.ref_name` inside `if:` is valid; if actionlint flags the
job-level `if:` string, wrap it in `${{ }}`.)

- [ ] **Step 5: Commit**

```bash
git add .github/workflows/release.yml .github/workflows/pages.yml
git commit -m "ci: auto-update packaging manifests and submit stable releases to winget"
```

---

### Task 6: Documentation and website sync

**Files:**
- Modify: `README.md` (Install section)
- Modify: `site/index.html` (Install section)
- Modify: `AGENTS.md` (new Distribution & packaging section)
- Modify: `docs/manual-test-plan.md` (append packaging checks)

- [ ] **Step 1: Rework the README Install section**

Replace the current `## Install` body (keep the heading) with — order: Homebrew, winget,
scripts, native binaries, .NET tool (the existing native-binaries and .NET-tool paragraphs move
but keep their exact wording):

````markdown
> **Note:** while this repository is private, the Homebrew/winget/script installs below
> cannot fetch release assets anonymously — see the
> [packaging decision log](docs/packaging-decision-log.md).

**Homebrew** (macOS Apple Silicon, Linux):

```bash
brew tap niklasfp/nightowl https://github.com/niklasfp/nightowl
brew install nightowl
```

**winget** (Windows; available once the first stable release is accepted into
[winget-pkgs](https://github.com/microsoft/winget-pkgs)):

```powershell
winget install niklasfp.NightOwl
```

**Install script** (macOS/Linux — installs to `~/.local/bin`, override with
`--install-dir`):

```bash
curl -fsSL https://raw.githubusercontent.com/niklasfp/nightowl/main/install.sh | sh
```

**Install script** (Windows PowerShell — installs to `%LOCALAPPDATA%\Programs\NightOwl` and
adds it to your user PATH):

```powershell
irm https://raw.githubusercontent.com/niklasfp/nightowl/main/install.ps1 | iex
```
````

- [ ] **Step 2: Mirror it on the website**

In `site/index.html`'s `#install` section, add matching `install-block` divs (Homebrew,
winget, both scripts) using the existing terminal-markup pattern (`<div class="terminal">` …
`<button class="copy-btn">`), before the existing ".NET tool" and "Native binaries" blocks.
Keep relative URLs; command text exactly as in the README. Verify with `node site/serve.ts`:
fetch the page (`curl -s http://localhost:<port>/nightowl/ | grep -c install-block`) and
expect the count to have grown by 4; also confirm no console errors by loading it once if a
browser is handy (optional).

- [ ] **Step 3: AGENTS.md**

Add after the "## CI workflows" section:

```markdown
## Distribution & packaging

- Release assets: the 5 AOT archives plus `SHA256SUMS.txt`; AOT binaries are version-stamped
  from the tag (`-p:Version`), like the nupkg.
- `Formula/nightowl.rb` (Homebrew — tapped straight from this repo) and
  `packaging/winget/*.yaml` (winget, id `niklasfp.NightOwl`) are **generated** by
  `scripts/update-packaging.ts`; never edit them by hand. The `packaging` job in
  `release.yml` regenerates them on every tag and commits to `main`; the `winget` job
  submits stable tags via `wingetcreate` when a `WINGET_TOKEN` secret exists (the first
  winget-pkgs submission is manual).
- `install.sh` / `install.ps1` (repo root) are the user-facing installers; `pages.yml` also
  serves them from the website root. Keep them in sync with the release asset names/RIDs.
- Decisions and assumptions: [docs/packaging-decision-log.md](docs/packaging-decision-log.md).
```

- [ ] **Step 4: Manual test plan**

Read `docs/manual-test-plan.md` and append a `## Packaging` section in its existing style
covering: (a) `install.sh` end-to-end on macOS/Linux against the newest release, (b)
`install.ps1` on Windows (Parallels VM) including PATH pickup in a new terminal, (c)
`winget validate packaging/winget/` on Windows, (d) after the repo is public:
`brew tap niklasfp/nightowl <url> && brew install nightowl && brew test nightowl`.

- [ ] **Step 5: Verify docs consistency**

`grep -n "install.sh\|install.ps1\|Formula\|winget" README.md AGENTS.md site/index.html` —
confirm names/paths/URLs agree across all three. README claims must match what the scripts
actually do (install dirs, PATH behavior).

- [ ] **Step 6: Commit**

```bash
git add README.md site/index.html AGENTS.md docs/manual-test-plan.md
git commit -m "docs: document Homebrew, winget, and install-script distribution"
```
