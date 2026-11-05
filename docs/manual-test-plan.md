# NightOwl — Manual Test Plan

Run this on **each target platform** before tagging a release. It takes ~5 minutes per OS.
No spare machines? On an Apple Silicon Mac the Linux and Windows sheets can be run locally —
see [testing with local VMs](testing-with-local-vms.md).

## Why this exists

The automated suite covers all the *logic* — argument parsing, mode selection, backend
selection, the IOKit assertion types / `systemd-inhibit` arguments, exit codes, and hold
release (the macOS native path even round-trips a real assertion on the macOS runner). What it
**cannot** cover is the *physical effect*: that the operating system actually won't idle-sleep
while NightOwl runs. A CI runner can't be suspended, so that last mile is verified by hand.

## The trick: don't wait for sleep — ask the OS

You never have to sit and watch a screen for 20 minutes. Each OS has a command that lists the
**active sleep inhibitors** it is currently honouring. NightOwl is verified by confirming its
lock **appears while running** and **disappears after exit**. These commands query the *same*
facility the OS uses to decide whether to sleep, so a present lock means it will not idle-sleep.

| OS | Inspect command | NightOwl shows up as |
|----|-----------------|----------------------|
| macOS | `pmset -g assertions` | the **`nightowl`** process itself holding `PreventUserIdleSystemSleep` (no child process); with `--block-sleep`, also `PreventSystemSleep` |
| Windows | `powercfg /requests` *(Run as Administrator)* | `nightowl.exe` under the **SYSTEM:** heading, with reason string `nightowl`; with `--block-sleep`, also under **EXECUTION:** |
| Linux | `systemd-inhibit --list` | a row with `WHO=nightowl`, `WHAT=sleep:idle`, `MODE=block` (held natively by the `nightowl` process — `COMM=nightowl`, no child); with `--block-sleep`, `WHAT=sleep:idle:handle-lid-switch` |

Use **two terminals**: **A** runs NightOwl (it blocks in the foreground), **B** inspects.

## Fastest path: run the script

Each platform has a script that automates the whole sheet below — it drives NightOwl through
every mode and asserts the OS lock appears and clears, printing `PASS`/`FAIL` and exiting
non-zero on any failure. Point it at the binary you're releasing:

```bash
# macOS / Linux
tests/manual/verify-macos.sh   ./nightowl
tests/manual/verify-linux.sh   ./nightowl
```
```powershell
# Windows — run from an *elevated* terminal (powercfg needs admin)
tests/manual/verify-windows.ps1 .\nightowl.exe
```

If a script passes end-to-end, that platform is signed off. The manual sheets below are the
same checks by hand, for when you want to eyeball the OS output or a script isn't handy.

---

## Pre-flight (any platform) — 30 seconds

```
nightowl --version          # prints "nightowl <x.y.z>"
nightowl --help             # prints usage
nightowl --frobnicate       # prints an error + "Try 'nightowl --help'."
echo $?                     # -> 2     (Windows: echo %ERRORLEVEL%  /  $LASTEXITCODE)
```

- [ ] version, help, and the exit-code-2 error all behave as above.

```
nightowl --pid 999999        # a PID that (almost certainly) does not exist
echo $?                      # -> 1, printed *immediately* (no hang, unlike `caffeinate -w`)
```

- [ ] bogus PID errors immediately, exits non-zero, and never appears in the OS lock list below.

---

## macOS

> Replace `nightowl` with the path to your binary (e.g. `./nightowl`) if it is not on `PATH`.

> NightOwl now holds the lock **natively** (IOKit), so the assertion is attributed to the
> `nightowl` process itself — there is **no `caffeinate` child** to look for.

| # | Terminal A (run) | Terminal B (verify) | Expected |
|---|------------------|---------------------|----------|
| 1 | `nightowl -v` | `pmset -g assertions \| grep -E 'pid.*nightowl\|PreventUserIdle'` | a `pid …(nightowl)` row holds **PreventUserIdleSystemSleep**; the `PreventUserIdleDisplaySleep` count stays `0` |
| 2 | *Ctrl+C in A* | re-run the same grep | the `nightowl` assertion is **gone** |
| 3 | `nightowl --display -v` | `pmset -g assertions \| grep 'pid.*nightowl'` | the same `nightowl` pid holds **both** `PreventUserIdleSystemSleep` **and** `PreventUserIdleDisplaySleep` |
| 4 | `nightowl --for 20s -v` | `pmset -g assertions \| grep 'pid.*nightowl'` while it runs, then again after ~20s | assertion present during the 20s, process exits on its own, assertion **gone** afterwards |
| 5 | `nightowl -- sleep 3` ; then `echo $?` | — | command runs ~3s, NightOwl exits with **0**; `nightowl -- sh -c 'exit 5'; echo $?` exits **5** |
| 6 | `nightowl &` then `kill -TERM %1` | `pmset -g assertions \| grep 'pid.*nightowl'` after the kill | SIGTERM releases cleanly; **no** leftover `nightowl` assertion |
| 7 | `nightowl --block-sleep -v` (on **AC power**) | `pmset -g assertions \| grep 'pid.*nightowl'` | the `nightowl` pid holds **both** `PreventUserIdleSystemSleep` **and** `PreventSystemSleep` |
| 8 | *Ctrl+C in A* | re-run the same grep | both assertions are **gone** |
| 9 | in a spare terminal: `sleep 120 & echo $!` (note the PID); then `nightowl --pid <that PID> -v` | `pmset -g assertions \| grep 'pid.*nightowl'` while it runs, then `kill <that PID>` and re-check | assertion present while the target runs; killing the target makes NightOwl exit **0** and the assertion disappear |
| 10 | `nightowl --pid 999999` ; then `echo $?` | `pmset -g assertions \| grep 'pid.*nightowl'` (run concurrently if possible) | fails **immediately** with a clear error, exits **non-zero**, **no** assertion ever appears |

- [ ] 1 system inhibit appears  - [ ] 2 released on Ctrl+C  - [ ] 3 display inhibit appears
- [ ] 4 timed auto-release  - [ ] 5 wrap exit codes  - [ ] 6 SIGTERM clean release
- [ ] 7 `--block-sleep` adds `PreventSystemSleep` (AC power)  - [ ] 8 released on Ctrl+C
- [ ] 9 `--pid` releases when the target exits  - [ ] 10 bogus `--pid` fails fast, no lock taken

---

## Windows

> Use an **elevated** terminal for `powercfg /requests` (otherwise it hides process names).
> Use the published `nightowl.exe`.

> NightOwl holds the request via `PowerCreateRequest`/`PowerSetRequest` (not the legacy
> `SetThreadExecutionState`), so `powercfg /requests` shows `nightowl.exe` alongside its
> `REASON_CONTEXT` reason string (`nightowl`).

| # | Terminal A (run) | Terminal B — *Admin* (verify) | Expected |
|---|------------------|-------------------------------|----------|
| 1 | `nightowl -v` | `powercfg /requests` | under **SYSTEM:** a `[PROCESS] …\nightowl.exe` with reason `nightowl`; **DISPLAY:** and **EXECUTION:** say `None.` |
| 2 | *Ctrl+C in A* | `powercfg /requests` | SYSTEM: now `None.` |
| 3 | `nightowl --display -v` | `powercfg /requests` | `nightowl.exe` appears under **both** SYSTEM: and DISPLAY: |
| 4 | `nightowl --for 20s -v` | `powercfg /requests` during, then after ~20s | request present during the 20s, gone after the process exits |
| 5 | `nightowl -- cmd /c "exit 5"` ; then `echo %ERRORLEVEL%` | — | NightOwl exits with **5** |
| 6 | start `nightowl`, then **close the console window** / `taskkill /PID <pid>` | `powercfg /requests` | request is released; SYSTEM: back to `None.` |
| 7 | `nightowl --block-sleep -v` | `powercfg /requests` | `nightowl.exe` appears under **both** SYSTEM: and **EXECUTION:** |
| 8 | *Ctrl+C in A* | `powercfg /requests` | EXECUTION: back to `None.` |
| 9 | start `notepad.exe`, note its PID (Task Manager); then `nightowl --pid <that PID> -v` | `powercfg /requests` while it runs, then close Notepad and re-check | request present while Notepad runs; closing it makes NightOwl exit **0** and the request disappear |
| 10 | `nightowl --pid 999999` ; then `echo %ERRORLEVEL%` | `powercfg /requests` (run concurrently if possible) | fails **immediately** with a clear error, exits **non-zero**, **no** request ever appears |

- [ ] 1 system request appears  - [ ] 2 released on Ctrl+C  - [ ] 3 display request appears
- [ ] 4 timed auto-release  - [ ] 5 wrap exit code  - [ ] 6 clean release on close
- [ ] 7 `--block-sleep` adds EXECUTION request  - [ ] 8 released on Ctrl+C
- [ ] 9 `--pid` releases when the target exits  - [ ] 10 bogus `--pid` fails fast, no lock taken

After installing (either `install.ps1` or a raw zip extract), check the owl icon is embedded in
`nightowl.exe` itself, not just the nupkg:

- [ ] File Explorer shows the owl icon on `nightowl.exe` (not the generic EXE icon), and it
      also appears in the taskbar/Alt-Tab switcher while `nightowl` is running.

---

## Linux (systemd)

> Requires `systemd-inhibit` (from systemd). On a headless box only the system/idle lock matters.

| # | Terminal A (run) | Terminal B (verify) | Expected |
|---|------------------|---------------------|----------|
| 1 | `nightowl -v` | `systemd-inhibit --list` | a row: `WHO=nightowl  WHAT=sleep:idle  WHY=user requested  MODE=block` |
| 2 | *Ctrl+C in A* | `systemd-inhibit --list` | the `nightowl` lock is **gone** (`0 inhibitors`) |
| 3 | `nightowl --display -v` | read Terminal A's output | A prints the verbose note *"--display is best-effort; inhibiting system/idle sleep only."* The lock list is unchanged from #1 — **display is not guaranteed on Linux, by design** |
| 4 | `nightowl --for 20s -v` | `systemd-inhibit --list \| grep nightowl` during, then after ~20s | lock present during the 20s, gone after exit |
| 5 | `nightowl -- sleep 3` ; then `echo $?` | — | exits **0**; `nightowl -- sh -c 'exit 5'; echo $?` exits **5** |
| 6 | `nightowl &` then `kill -TERM %1` | `systemd-inhibit --list` | SIGTERM releases the lock cleanly; no leftover lock or child |
| 7 | run with `-v` and confirm the path taken | read Terminal A's output | verbose says *"holding logind inhibitor fd (native libsystemd)"*; on a non-systemd / no-D-Bus box it instead falls back, and if that also fails exits **1** with a clear message |
| 8 | `nightowl --block-sleep -v` (local session, e.g. console/GUI login, not SSH) | `systemd-inhibit --list` | a row: `WHO=nightowl  WHAT=sleep:idle:handle-lid-switch  MODE=block` |
| 9 | *Ctrl+C in A* | `systemd-inhibit --list` | the lock is **gone** |
| 10 | `nightowl --block-sleep -v` over **SSH** (no local session) | read Terminal A's output | logind denies the lid-switch lock; verbose shows the downgrade (`sleep:idle:handle-lid-switch` → `sleep:idle`) and the lock still appears with `WHAT=sleep:idle` |
| 11 | `sleep 120 & echo $!` (note the PID); then `nightowl --pid <that PID> -v` | `systemd-inhibit --list` while it runs, then `kill <that PID>` and re-check | lock present while the target runs; verbose shows `pidfd_open` (or the `kill(2)` polling fallback on an old kernel); killing the target makes NightOwl exit **0** and the lock disappear |
| 12 | `nightowl --pid 999999` ; then `echo $?` | `systemd-inhibit --list` (run concurrently if possible) | fails **immediately** with a clear error, exits **non-zero**, **no** lock ever appears |
| 13 | `nightowl &` then `kill -9 %1` | `systemd-inhibit --list` | even SIGKILL leaves **no dangling lock**: the native path's fd is closed by the kernel; on a fallback box the `systemd-inhibit … cat` child exits on stdin EOF |

- [ ] 1 inhibit lock appears  - [ ] 2 released on Ctrl+C  - [ ] 3 display note printed (best-effort)
- [ ] 4 timed auto-release  - [ ] 5 wrap exit codes  - [ ] 6 SIGTERM clean release  - [ ] 7 native path (or clean fallback/error)
- [ ] 8 `--block-sleep` lock includes `handle-lid-switch`  - [ ] 9 released on Ctrl+C  - [ ] 10 SSH degrades gracefully
- [ ] 11 `--pid` releases when the target exits  - [ ] 12 bogus `--pid` fails fast, no lock taken
- [ ] 13 SIGKILL leaves no dangling lock

---

## Optional deep check (one machine, once per release)

If you want to confirm the real-world effect end-to-end rather than trust the OS lock list:

1. Set the idle **sleep** timer short — macOS: `sudo pmset -a sleep 1`; Windows:
   `powercfg /change standby-timeout-ac 1`; Linux (GNOME): set *Automatic Suspend* to 1 min.
2. Run `nightowl`, leave the machine idle for **3 minutes** → it must **stay awake**.
3. Quit NightOwl, leave it idle again → it should now sleep normally.
4. **Restore** your original sleep timer.

This is slow and disruptive, which is exactly why the lock-list checks above are the default —
they verify the same OS facility without the wait.

---

## Packaging

The install scripts and package manifests are covered by lint + hermetic tests (see
[docs/packaging-decision-log.md](packaging-decision-log.md), D12), but a few checks need a
real machine and/or a public release, so they belong here instead.

**a. `install.sh` end-to-end (macOS/Linux)**

Against the newest published release:

```bash
curl -fsSL https://raw.githubusercontent.com/niklasfp/nightowl/main/install.sh | sh
~/.local/bin/nightowl --version
```

- [ ] downloads and installs to `~/.local/bin` (default, no `--install-dir`), the binary runs
      and reports the released version.

Update — install an older release, then re-run to pick up the newest one:

```bash
curl -fsSL https://raw.githubusercontent.com/niklasfp/nightowl/main/install.sh | sh -s -- --version <older vX.Y.Z>
curl -fsSL https://raw.githubusercontent.com/niklasfp/nightowl/main/install.sh | sh
~/.local/bin/nightowl --version
```

- [ ] the second run exits 0 and `nightowl --version` now reports the newer version (the
      binary was replaced, not left stale or duplicated).

Uninstall — no network needed:

```bash
curl -fsSL https://raw.githubusercontent.com/niklasfp/nightowl/main/install.sh | sh -s -- --uninstall
ls ~/.local/bin/nightowl                      # -> No such file or directory
curl -fsSL https://raw.githubusercontent.com/niklasfp/nightowl/main/install.sh | sh -s -- --uninstall
```

- [ ] first run removes the binary and exits 0; second (already-uninstalled) run prints a
      clear "nothing to uninstall" message and also exits 0.

**b. `install.ps1` on Windows (Parallels VM)**

See [testing with local VMs](testing-with-local-vms.md) for the Parallels setup.

```powershell
irm https://raw.githubusercontent.com/niklasfp/nightowl/main/install.ps1 | iex
```

- [ ] installs to `%LOCALAPPDATA%\Programs\NightOwl` and updates the user PATH.
- [ ] **open a new terminal** (PATH changes don't apply to the one that ran the installer) and
      confirm `nightowl --version` resolves without a full path.

Update — install an older release, then re-run to pick up the newest one:

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/niklasfp/nightowl/main/install.ps1))) -Version <older vX.Y.Z>
irm https://raw.githubusercontent.com/niklasfp/nightowl/main/install.ps1 | iex
nightowl --version
```

- [ ] the second run exits 0, `nightowl --version` reports the newer version, and the user
      PATH (`[Environment]::GetEnvironmentVariable('Path', 'User')`) contains the install
      directory **exactly once** — no duplicate entry from re-running the installer.

Uninstall — no network needed, in a fresh (non-elevated) terminal:

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/niklasfp/nightowl/main/install.ps1))) -Uninstall
Test-Path "$env:LOCALAPPDATA\Programs\NightOwl\nightowl.exe"   # -> False
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/niklasfp/nightowl/main/install.ps1))) -Uninstall
```

- [ ] first run removes `nightowl.exe` (and the now-empty install directory) and exits 0;
      second (already-uninstalled) run prints a clear "nothing to uninstall" message and also
      exits 0.
- [ ] **open a new terminal** afterward and confirm the user PATH no longer contains the
      install directory (`[Environment]::GetEnvironmentVariable('Path', 'User') -split ';'`
      does not contain `%LOCALAPPDATA%\Programs\NightOwl`).

**c. `winget validate` on Windows**

```powershell
winget validate packaging/winget/
```

- [ ] validates cleanly against the current manifest schema.

**d. Homebrew, after the repo is public**

Homebrew needs anonymously downloadable release assets, so this only runs once the repo (and
its releases) are public — see [docs/packaging-decision-log.md](packaging-decision-log.md), D1.

```bash
brew tap niklasfp/nightowl https://github.com/niklasfp/nightowl
brew install nightowl
brew test nightowl
```

- [ ] taps, installs, and `brew test` passes (asserts `nightowl --version` output).

---

## Release sign-off

A release is good to tag once the per-platform sheets pass:

- [ ] **macOS** (Apple Silicon and/or Intel)
- [ ] **Windows** (x64)
- [ ] **Linux** (systemd, x64)

Record the version, date, and tester for each in the release notes / PR.
