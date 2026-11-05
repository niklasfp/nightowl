#!/usr/bin/env bash
# Reproduce the CI linux-arm64 Native AOT build locally, using the same container image the
# release workflow uses (mcr.microsoft.com/dotnet/sdk:10.0-noble-aot, which ships clang+zlib).
#
# On an arm64 host (Apple Silicon, arm64 Linux) this runs NATIVELY — no QEMU.
# On an x64 host you need binfmt/QEMU registered first:  docker run --privileged --rm tonistiigi/binfmt --install arm64
#
# Usage:  ./build-linux-arm64.sh [output-dir]      (default: ./publish/linux-arm64)
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
out="${1:-$repo_root/publish/linux-arm64}"
mkdir -p "$out"

echo "Building NightOwl linux-arm64 (AOT) via $(docker --version)…"
docker run --rm --platform linux/arm64 \
    -v "$repo_root":/src -v "$out":/out -w /src \
    mcr.microsoft.com/dotnet/sdk:10.0-noble-aot \
    bash -c "dotnet publish src/NightOwl -r linux-arm64 -c Release -p:PublishAot=true -o /out && chmod +x /out/nightowl"

echo
echo "Built: $out/nightowl"
# `file` may not exist everywhere; fall back to a header peek.
if command -v file >/dev/null 2>&1; then
    file "$out/nightowl"
else
    echo "(install 'file' to confirm it's an ELF aarch64 binary)"
fi
