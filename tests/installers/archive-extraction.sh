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

tar -czf "$tmp/good.tar.gz" -C "$tmp/payload" nightowl
printf 'extra\n' >"$tmp/payload/extra"
tar -czf "$tmp/extra.tar.gz" -C "$tmp/payload" nightowl extra
rm -f "$tmp/payload/extra"

mkdir -p "$tmp/link"
ln -s /etc/passwd "$tmp/link/nightowl"
tar -czf "$tmp/symlink.tar.gz" -C "$tmp/link" nightowl

python3 - "$tmp" "$tmp/payload" <<'PY'
import io, os, sys, tarfile

def write(path, info, data=b""):
    with tarfile.open(path, "w:gz") as tf:
        if info.size:
            tf.addfile(info, io.BytesIO(data))
        else:
            tf.addfile(info)

root, payload = sys.argv[1], sys.argv[2]
data = b"pwned\n"

info = tarfile.TarInfo("../evil")
info.size = len(data)
write(os.path.join(root, "slip.tar.gz"), info, data)

info = tarfile.TarInfo("/tmp/evil")
info.size = len(data)
write(os.path.join(root, "absolute.tar.gz"), info, data)

info = tarfile.TarInfo("nightowl")
info.type = tarfile.LNKTYPE
info.linkname = "missing"
write(os.path.join(root, "hardlink.tar.gz"), info)

info = tarfile.TarInfo("nightowl")
info.type = tarfile.CHRTYPE
info.devmajor = 1
info.devminor = 3
write(os.path.join(root, "device.tar.gz"), info)

info = tarfile.TarInfo("./nightowl")
with open(os.path.join(payload, "nightowl"), "rb") as fh:
    blob = fh.read()
info.size = len(blob)
write(os.path.join(root, "dot.tar.gz"), info, blob)
PY

setup_installer_shims
CHECKSUM_CASE=valid
export CHECKSUM_CASE FIXTURE_ASSET FIXTURE_SUMS

run_archive() {
    archive=$1
    install_dir=$2
    FIXTURE_ASSET=$archive
    FIXTURE_SUMS="$tmp/sums-$(basename "$archive").txt"
    write_sums_for "$archive" "$FIXTURE_SUMS"
    export FIXTURE_ASSET FIXTURE_SUMS
    run_installer "$install_dir"
}

assert_archive_failure() {
    name=$1
    archive=$2
    expected=$3
    install_dir="$tmp/install-$name"
    if output=$(run_archive "$archive" "$install_dir" 2>&1); then
        printf 'expected %s extraction case to fail\n' "$name" >&2
        exit 1
    fi
    case "$output" in
        *"$expected"*) ;;
        *)
            printf 'unexpected %s failure:\n%s\n' "$name" "$output" >&2
            exit 1
            ;;
    esac
    [ ! -e "$install_dir/nightowl" ] || {
        printf '%s case installed a binary\n' "$name" >&2
        exit 1
    }
}

assert_archive_failure extra "$tmp/extra.tar.gz" "unexpected member"
assert_archive_failure symlink "$tmp/symlink.tar.gz" "is not a regular file"
assert_archive_failure slip "$tmp/slip.tar.gz" "unexpected member"
assert_archive_failure absolute "$tmp/absolute.tar.gz" "is not a safe path"
assert_archive_failure hardlink "$tmp/hardlink.tar.gz" "is not a regular file"
assert_archive_failure device "$tmp/device.tar.gz" "is not a regular file"

run_archive "$tmp/good.tar.gz" "$tmp/install-good" >/dev/null
[ -x "$tmp/install-good/nightowl" ] || {
    printf 'valid archive did not install the binary\n' >&2
    exit 1
}

run_archive "$tmp/dot.tar.gz" "$tmp/install-dot" >/dev/null
[ -x "$tmp/install-dot/nightowl" ] || {
    printf './nightowl archive did not install the binary\n' >&2
    exit 1
}

printf 'POSIX installer archive-extraction tests passed.\n'
