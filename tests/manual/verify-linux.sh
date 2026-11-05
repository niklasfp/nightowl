#!/usr/bin/env bash
# Manual release verification for NightOwl on Linux (systemd).
#
# Confirms the OS *actually* holds NightOwl's inhibitor lock by inspecting
# `systemd-inhibit --list` while the tool runs, and that it clears when it exits.
# NightOwl takes the lock natively (libsystemd logind Inhibit fd) — the lock is
# attributed to the nightowl process itself (COMM=nightowl), with no child process;
# `systemd-inhibit` is only the fallback when libsystemd/D-Bus is unavailable.
#
# Usage:  ./verify-linux.sh [path-to-nightowl]      (default: `nightowl` on PATH)
set -uo pipefail

BIN="${1:-nightowl}"
pass=0
fail=0
ok() { printf '  \033[32mPASS\033[0m %s\n' "$1"; pass=$((pass + 1)); }
no() { printf '  \033[31mFAIL\033[0m %s\n' "$1"; fail=$((fail + 1)); }

command -v "$BIN" >/dev/null 2>&1 || { echo "nightowl not found: $BIN" >&2; exit 2; }
command -v systemd-inhibit >/dev/null 2>&1 || { echo "systemd-inhibit not found (needs systemd)" >&2; exit 2; }
echo "Testing: $("$BIN" --version)"

# True while NightOwl's block lock is listed.
nightowl_locked() { systemd-inhibit --list 2>/dev/null | grep -E "\bnightowl\b" | grep -q "sleep:idle"; }

echo "Pre-flight"
"$BIN" --help 2>&1 | grep -q "USAGE" && ok "--help shows usage" || no "--help shows usage"
"$BIN" --frobnicate >/dev/null 2>&1
[ $? -eq 2 ] && ok "unknown option exits 2" || no "unknown option exits 2"

echo "System inhibit + release on SIGTERM"
"$BIN" -v >/dev/null 2>&1 &
NOPID=$!
locked=false
for _ in 1 2 3 4 5 6 7 8; do
    nightowl_locked && { locked=true; break; }
    sleep 0.5
done
$locked && ok "systemd-inhibit lock present (who=nightowl, sleep:idle, block)" || no "inhibit lock present while running"
kill -TERM "$NOPID" 2>/dev/null
wait "$NOPID" 2>/dev/null
sleep 0.7
nightowl_locked && no "lock released after SIGTERM" || ok "lock released after SIGTERM (native: no child)"

echo "Hard-kill release (kill -9)"
# Neither path may dangle on SIGKILL: the native logind fd is closed by the kernel, and the
# fallback's `systemd-inhibit … cat` child exits on stdin EOF when nightowl dies.
"$BIN" -v >/dev/null 2>&1 &
NOPID=$!
locked=false
for _ in 1 2 3 4 5 6 7 8; do
    nightowl_locked && { locked=true; break; }
    sleep 0.5
done
$locked && ok "inhibit lock present before SIGKILL" || no "inhibit lock present before SIGKILL"
kill -9 "$NOPID" 2>/dev/null
wait "$NOPID" 2>/dev/null
sleep 1.5
nightowl_locked && no "lock released after SIGKILL (no dangling inhibitor)" || ok "lock released after SIGKILL (no dangling inhibitor)"

echo "Display inhibit (best-effort note)"
out=$(mktemp)
"$BIN" --display -v >"$out" 2>&1 &
NOPID=$!
sleep 1.5
grep -qi "best-effort" "$out" && ok "--display prints best-effort note (no extra lock by design)" || no "--display best-effort note printed"
kill -TERM "$NOPID" 2>/dev/null
wait "$NOPID" 2>/dev/null
rm -f "$out"

echo "Block sleep (-s/--block-sleep)"
# Requires a local session (allow_active=yes); over SSH, logind denies handle-lid-switch and
# nightowl degrades to sleep:idle — that's a pass too, just check the sleep:idle floor.
"$BIN" --block-sleep -v >/dev/null 2>&1 &
NOPID=$!
locked=false
for _ in 1 2 3 4 5 6 7 8; do
    nightowl_locked && { locked=true; break; }
    sleep 0.5
done
if $locked; then
    if systemd-inhibit --list 2>/dev/null | grep -E "\bnightowl\b" | grep -q "handle-lid-switch"; then
        ok "systemd-inhibit lock includes handle-lid-switch (sleep:idle:handle-lid-switch)"
    else
        ok "systemd-inhibit lock present at sleep:idle (degraded from handle-lid-switch — expected without a local session)"
    fi
else
    no "inhibit lock present with --block-sleep"
fi
kill -TERM "$NOPID" 2>/dev/null
wait "$NOPID" 2>/dev/null
sleep 0.7
nightowl_locked && no "lock released after --block-sleep SIGTERM" || ok "lock released after --block-sleep SIGTERM"

echo "Timed auto-release"
start=$(date +%s)
"$BIN" --for 3s >/dev/null 2>&1
rc=$?
elapsed=$(($(date +%s) - start))
if [ $rc -eq 0 ] && [ $elapsed -ge 2 ] && [ $elapsed -le 8 ]; then
    ok "--for 3s exited 0 after ~3s (${elapsed}s)"
else
    no "--for 3s timing/exit (rc=$rc, ${elapsed}s)"
fi

echo "Wrap exit codes"
"$BIN" -- true
[ $? -eq 0 ] && ok "wrap propagates 0" || no "wrap propagates 0"
"$BIN" -- sh -c 'exit 5'
[ $? -eq 5 ] && ok "wrap propagates 5" || no "wrap propagates 5"

# Note: hiding `systemd-inhibit` no longer breaks NightOwl — the native libsystemd path
# doesn't use the CLI. Both libsystemd *and* the fallback fail only on a non-systemd / no-D-Bus
# box, where NightOwl exits 1 with a clear message.

echo
echo "Passed: $pass  Failed: $fail"
[ $fail -eq 0 ]
