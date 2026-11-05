#!/bin/sh
set -eu

root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
# shellcheck disable=SC1091
. "$root/tests/installers/lib.sh"
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT INT TERM

mkdir -p "$tmp/payload"
cat >"$tmp/payload/nightowl" <<'EOF'
#!/bin/sh
printf 'nightowl test\n'
EOF
chmod +x "$tmp/payload/nightowl"
tar -czf "$tmp/nightowl-linux-x64.tar.gz" -C "$tmp/payload" nightowl
write_sums_for "$tmp/nightowl-linux-x64.tar.gz" "$tmp/SHA256SUMS.txt"
FIXTURE_ASSET="$tmp/nightowl-linux-x64.tar.gz"
FIXTURE_SUMS="$tmp/SHA256SUMS.txt"
export FIXTURE_ASSET FIXTURE_SUMS
setup_installer_shims

CHECKSUM_CASE=missing assert_failure "refusing an unverified install" "$tmp/install-missing"
CHECKSUM_CASE=mismatch assert_failure "checksum mismatch" "$tmp/install-mismatch"
CHECKSUM_CASE=malformed assert_failure "has no entry" "$tmp/install-malformed"
CHECKSUM_CASE=transport assert_failure "could not download" "$tmp/install-transport"

CHECKSUM_CASE=valid
export CHECKSUM_CASE
run_installer "$tmp/install-valid" >/dev/null
[ -x "$tmp/install-valid/nightowl" ] || {
    printf 'valid checksum case did not install the binary\n' >&2
    exit 1
}

printf 'POSIX installer checksum tests passed.\n'
