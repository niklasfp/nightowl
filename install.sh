#!/bin/sh
# NightOwl installer for macOS and Linux.
#
# Downloads the native binary for this machine from GitHub Releases, verifies its
# checksum, and installs it (default: ~/.local/bin).
#
#   curl -fsSL https://raw.githubusercontent.com/niklasfp/nightowl/main/install.sh | sh
#
# Update: re-run the same command (or with --install-dir if you overrode it). The download
# is copied in and atomically renamed over the old binary, so this is safe to repeat.
#
# Uninstall (no network needed):
#   curl -fsSL https://raw.githubusercontent.com/niklasfp/nightowl/main/install.sh | sh -s -- --uninstall
#
# Options (flags win over environment variables):
#   --version <vX.Y.Z>    install a specific tagged release (default: newest release)
#   --install-dir <dir>   where to put the binary (default: ~/.local/bin)
#   --uninstall           remove the installed binary and exit (idempotent, no network)
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
UNINSTALL=0

say() { printf '%s\n' "$*" >&2; }
err() { say "install.sh: error: $*"; exit 1; }
have() { command -v "$1" >/dev/null 2>&1; }

usage() {
    cat >&2 <<'EOF'
NightOwl installer for macOS and Linux.

Usage: install.sh [--version vX.Y.Z] [--install-dir DIR]
       install.sh --uninstall [--install-dir DIR]

  --version <vX.Y.Z>    install a specific tagged release (default: newest release)
  --install-dir <dir>   where to put the binary (default: ~/.local/bin)
  --uninstall           remove the installed binary and exit (idempotent, no network)
  -h, --help            show this help

Re-run with no arguments (or the same --install-dir) to update an existing install.

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

# Download SHA256SUMS.txt, distinguishing "release has no checksums" (HTTP 404) from
# transport failures. curl is preferred because it can report the status code; wget
# failures are treated as download errors rather than a missing file.
fetch_sums() { # fetch_sums <url> <output-path>
    url=$1
    dest=$2
    if have curl; then
        code=$(curl -sS -L -o "$dest" -w '%{http_code}' "$url") || code=000
        case "$code" in
            200) return 0 ;;
            404) err "release $VERSION has no SHA256SUMS.txt; refusing an unverified install" ;;
            *) err "could not download $url (HTTP $code)" ;;
        esac
    elif have wget; then
        wget -q -O "$dest" "$url" || err "could not download $url"
    else
        err "this script needs curl or wget"
    fi
}

# Inspect the archive before extracting: the only safe member is a regular file named
# `nightowl` (optionally `./nightowl`). Absolute paths, `..`, extra files, directories,
# and links are rejected so a malicious archive cannot write outside $tmp. Bytes are
# written to a path we control (`tar -O`) so tar never materializes a symlink or device.
extract_nightowl() { # extract_nightowl <archive> <destdir>
    archive=$1
    destdir=$2
    members=$(LC_ALL=C tar -tzf "$archive") || err "could not read archive $archive"
    [ -n "$members" ] || err "archive $archive is empty"
    member_count=0
    raw_member=
    while IFS= read -r member || [ -n "$member" ]; do
        [ -n "$member" ] || continue
        member_count=$((member_count + 1))
        raw_member=$member
        case "$member" in
            nightowl | ./nightowl) ;;
            /* | [A-Za-z]:*)
                err "archive $archive member '$member' is not a safe path"
                ;;
            *)
                err "archive $archive has unexpected member '$member' (expected only 'nightowl')"
                ;;
        esac
    done <<EOF
$members
EOF
    [ "$member_count" -eq 1 ] || err "archive $archive must contain exactly one member named 'nightowl' (found $member_count)"
    listing=$(LC_ALL=C tar -tvzf "$archive") || err "could not list archive $archive"
    typechar=$(printf '%s' "$listing" | sed '/^$/d' | head -n 1 | cut -c1)
    [ "$typechar" = "-" ] || err "archive $archive member '$raw_member' is not a regular file"
    mkdir -p "$destdir"
    LC_ALL=C tar -xzOf "$archive" "$raw_member" >"$destdir/nightowl" || err "failed to extract 'nightowl' from $archive"
}

json_first_tag() { # read a GitHub releases JSON payload on stdin, print the first tag_name
    # Match the whole key:value token so commas or braces inside other string fields
    # (release names, bodies) cannot split it apart. `grep -o` is not POSIX but is
    # supported by GNU, BSD/macOS, and busybox grep — every platform this script targets.
    grep -o '"tag_name"[[:space:]]*:[[:space:]]*"[^"]*"' | head -n 1 | cut -d'"' -f4
}

resolve_version() {
    api="https://api.github.com/repos/$REPO/releases"
    # /releases/latest ignores prereleases (404 when only prereleases exist) — fall back
    # to the newest release of any kind. Caveat: with a GITHUB_TOKEN that has push access,
    # that fallback can see an asset-less draft release; the download below then fails
    # with the actionable "pass --version" error rather than silently mis-installing.
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
        --uninstall) UNINSTALL=1; shift ;;
        -h | --help) usage; exit 0 ;;
        *) err "unknown option: $1 (try --help)" ;;
    esac
done

if [ "$UNINSTALL" -eq 1 ]; then
    # No OS/arch detection, no network — just remove the binary this script would have
    # installed. This script never edits PATH (see the header), so there's nothing to
    # undo there either.
    target="$INSTALL_DIR/nightowl"
    if [ -e "$target" ] || [ -L "$target" ]; then
        rm -f "$target"
        say "Uninstalled $target"
    else
        say "Nothing to uninstall: $target not found."
    fi
    exit 0
fi

os=$(uname -s)
arch=$(uname -m)
case "$os" in
    Darwin)
        case "$arch" in
            arm64) rid="osx-arm64" ;;
            # Rosetta reports the host arch as x86_64 to translated processes — check
            # sysctl.proc_translated to catch an x86_64 shell running on Apple Silicon.
            *) if [ "$(sysctl -n sysctl.proc_translated 2>/dev/null || echo 0)" = "1" ]; then
                   rid="osx-arm64"
               else
                   err "NightOwl ships no macOS build for '$arch' (Apple Silicon only)"
               fi ;;
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

fetch_sums "$base/SHA256SUMS.txt" "$tmp/SHA256SUMS.txt"
if have sha256sum; then
    actual=$(sha256sum "$tmp/$asset" | cut -d' ' -f1)
else
    actual=$(shasum -a 256 "$tmp/$asset" | cut -d' ' -f1)
fi
expected=$(awk -v f="$asset" '$2 == f { print $1 }' "$tmp/SHA256SUMS.txt")
[ -n "$expected" ] || err "SHA256SUMS.txt on release $VERSION has no entry for $asset"
[ "$expected" = "$actual" ] || err "checksum mismatch for $asset (expected $expected, got $actual)"
say "Checksum verified."

extract_nightowl "$tmp/$asset" "$tmp/extract"
mkdir -p "$INSTALL_DIR"
# Copy to a temp name in the same directory, then rename atomically — rename replaces a
# currently-running binary in place (avoids ETXTBSY on Linux) instead of overwriting it live.
cp "$tmp/extract/nightowl" "$INSTALL_DIR/.nightowl.new"
chmod 755 "$INSTALL_DIR/.nightowl.new"
mv -f "$INSTALL_DIR/.nightowl.new" "$INSTALL_DIR/nightowl"

if v=$("$INSTALL_DIR/nightowl" --version 2>/dev/null); then
    say "Installed $v -> $INSTALL_DIR/nightowl"
else
    say "Installed $INSTALL_DIR/nightowl ($VERSION)"
fi
case ":$PATH:" in
    *:"$INSTALL_DIR":*) ;;
    *) say "Note: $INSTALL_DIR is not on your PATH — add it, e.g.: export PATH=\"$INSTALL_DIR:\$PATH\"" ;;
esac
