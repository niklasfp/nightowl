# Shared helpers for POSIX installer tests. Sourced by checksum-verification.sh
# and archive-extraction.sh; expects $root and $tmp to be set.

setup_installer_shims() {
    mkdir -p "$tmp/bin"
    cat >"$tmp/bin/uname" <<'EOF'
#!/bin/sh
case "${1:-}" in
    -s) printf 'Linux\n' ;;
    -m) printf 'x86_64\n' ;;
    *) exit 2 ;;
esac
EOF

    cat >"$tmp/bin/curl" <<'EOF'
#!/bin/sh
set -eu
out=
url=
write_status=
while [ "$#" -gt 0 ]; do
    case "$1" in
        -o) out="$2"; shift 2 ;;
        -w) write_status=1; shift 2 ;;
        -H) shift 2 ;;
        -*) shift ;;
        *) url="$1"; shift ;;
    esac
done
[ -n "$out" ] && [ -n "$url" ] || exit 2

emit() {
    code=$1
    if [ -n "$write_status" ]; then
        printf '%s' "$code"
    elif [ "$code" != 200 ]; then
        exit 22
    fi
}

case "$url" in
    */nightowl-linux-x64.tar.gz)
        cp "$FIXTURE_ASSET" "$out"
        emit 200
        ;;
    */SHA256SUMS.txt)
        case "${CHECKSUM_CASE:-valid}" in
            valid)
                cp "$FIXTURE_SUMS" "$out"
                emit 200
                ;;
            missing)
                emit 404
                [ -n "$write_status" ] || exit 22
                ;;
            mismatch)
                printf '%064d  nightowl-linux-x64.tar.gz\n' 0 >"$out"
                emit 200
                ;;
            malformed)
                printf '%064d  another-file.tar.gz\n' 0 >"$out"
                emit 200
                ;;
            transport)
                emit 000
                exit 1
                ;;
            *) exit 2 ;;
        esac
        ;;
    *)
        emit 404
        [ -n "$write_status" ] || exit 22
        ;;
esac
EOF
    chmod +x "$tmp/bin/uname" "$tmp/bin/curl"
}

write_sums_for() {
    archive=$1
    sums=$2
    hash=$(sha256sum "$archive" | cut -d' ' -f1)
    printf '%s  nightowl-linux-x64.tar.gz\n' "$hash" >"$sums"
}

run_installer() {
    install_dir=$1
    shift
    PATH="$tmp/bin:$PATH" \
        NIGHTOWL_VERSION="${NIGHTOWL_VERSION:-v-test}" \
        NIGHTOWL_INSTALL_DIR="$install_dir" \
        NIGHTOWL_ASSET_BASE="https://fixtures.invalid" \
        sh "$root/install.sh" "$@"
}

assert_failure() {
    expected=$1
    install_dir=$2
    shift 2
    if output=$(run_installer "$install_dir" "$@" 2>&1); then
        printf 'expected installer to fail for %s\n' "$install_dir" >&2
        exit 1
    fi
    case "$output" in
        *"$expected"*) ;;
        *)
            printf 'unexpected failure for %s:\n%s\n' "$install_dir" "$output" >&2
            exit 1
            ;;
    esac
    [ ! -e "$install_dir/nightowl" ] || {
        printf '%s installed a binary after a failed install\n' "$install_dir" >&2
        exit 1
    }
}
