#!/usr/bin/env bash
# Manual release verification for NightOwl on macOS.
#
# Confirms the OS *actually* holds NightOwl's sleep assertions by inspecting
# `pmset -g assertions` while the tool runs, and that they clear when it exits.
# This is the physical-effect check that the unit tests can't make.
#
# NightOwl holds the lock natively (IOKit), so the assertion is attributed to the
# `nightowl` process itself — there is no `caffeinate` child to inspect.
#
# Usage:  ./verify-macos.sh [path-to-nightowl]      (default: `nightowl` on PATH)
set -uo pipefail

BIN="${1:-nightowl}"
pass=0
fail=0
ok() { printf '  \033[32mPASS\033[0m %s\n' "$1"; pass=$((pass + 1)); }
no() { printf '  \033[31mFAIL\033[0m %s\n' "$1"; fail=$((fail + 1)); }

command -v "$BIN" >/dev/null 2>&1 || { echo "nightowl not found: $BIN" >&2; exit 2; }
echo "Testing: $("$BIN" --version)"

# The pmset assertion line(s) for a given pid — the nightowl process holds them directly.
assertion_for_pid() { pmset -g assertions | grep -E "pid $1\("; }

# Run nightowl in the background; set global NOPID and wait for its assertion to register.
# Not a command-substitution (that would background inside a subshell and lose the pid).
NOPID=""
start_and_wait() {
    "$BIN" "$@" >/dev/null 2>&1 &
    NOPID=$!
    for _ in 1 2 3 4 5 6 7 8; do
        assertion_for_pid "$NOPID" | grep -q PreventUserIdle && break
        sleep 0.5
    done
}

echo "Pre-flight"
"$BIN" --help 2>&1 | grep -q "USAGE" && ok "--help shows usage" || no "--help shows usage"
"$BIN" --frobnicate >/dev/null 2>&1
[ $? -eq 2 ] && ok "unknown option exits 2" || no "unknown option exits 2"

echo "System inhibit + release on SIGTERM"
start_and_wait -v
if assertion_for_pid "$NOPID" | grep -q PreventUserIdleSystemSleep; then
    ok "nightowl holds PreventUserIdleSystemSleep (pid $NOPID)"
else
    no "system assertion present while running"
fi
kill -TERM "$NOPID" 2>/dev/null
wait "$NOPID" 2>/dev/null
sleep 0.7
if assertion_for_pid "$NOPID" | grep -q .; then
    no "assertion released after SIGTERM"
else
    ok "assertion released after SIGTERM"
fi

echo "Display inhibit"
start_and_wait --display -v
if assertion_for_pid "$NOPID" | grep -q PreventUserIdleDisplaySleep; then
    ok "nightowl holds PreventUserIdleDisplaySleep with --display"
else
    no "display assertion present with --display"
fi
kill -TERM "$NOPID" 2>/dev/null
wait "$NOPID" 2>/dev/null

echo "Block sleep (-s/--block-sleep)"
start_and_wait --block-sleep -v
if assertion_for_pid "$NOPID" | grep -q PreventSystemSleep; then
    ok "nightowl holds PreventSystemSleep with --block-sleep (honored on AC power only)"
else
    no "PreventSystemSleep assertion present with --block-sleep"
fi
kill -TERM "$NOPID" 2>/dev/null
wait "$NOPID" 2>/dev/null
sleep 0.7
if assertion_for_pid "$NOPID" | grep -q .; then
    no "assertions released after --block-sleep SIGTERM"
else
    ok "assertions released after --block-sleep SIGTERM"
fi

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

echo
echo "Passed: $pass  Failed: $fail"
[ $fail -eq 0 ]
