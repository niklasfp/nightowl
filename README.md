<p align="center">
  <a href="https://niklasfp.github.io/nightowl/"><img src="site/favicon.svg" width="120"
    alt="NightOwl mark: an owl perched in front of a crescent moon"></a>
</p>

<h1 align="center">NightOwl</h1>

> A small, cross-platform **"keep the computer awake"** CLI. NightOwl holds a system
> sleep-inhibitor for as long as you ask — indefinitely, for a fixed duration, or while a
> command runs — then releases it and exits. No daemon, no tray, no config files.

Inspired mostly by macOS **`caffeinate`** (and a little by PowerToys **Awake**), but portable:
one foreground process, a tiny dependency-free codebase, and a native-AOT build for every
desktop platform.

Website: **https://niklasfp.github.io/nightowl/**

| Platform | Mechanism |
|----------|-----------|
| Windows  | `PowerCreateRequest`/`PowerSetRequest(SystemRequired [+ DisplayRequired] [+ ExecutionRequired])` with a `REASON_CONTEXT` reason string |
| macOS    | IOKit power assertions — `IOPMAssertionCreateWithName(PreventUserIdleSystemSleep [+ PreventUserIdleDisplaySleep] [+ PreventSystemSleep])` |
| Linux    | logind `Inhibit` fd over D-Bus via `libsystemd` (falls back to `systemd-inhibit … cat`, tied to NightOwl's lifetime) |

## Install

**Install script** (macOS/Linux — installs to `~/.local/bin`, override with
`--install-dir`):

```bash
curl -fsSL https://raw.githubusercontent.com/niklasfp/nightowl/main/install.sh | sh
# Or with flags, e.g. a custom install directory:
curl -fsSL https://raw.githubusercontent.com/niklasfp/nightowl/main/install.sh | sh -s -- --install-dir ~/bin
```

**Install script** (Windows PowerShell — installs to `%LOCALAPPDATA%\Programs\NightOwl` and
adds it to your user PATH):

```powershell
irm https://raw.githubusercontent.com/niklasfp/nightowl/main/install.ps1 | iex
```

Both scripts require the release's `SHA256SUMS.txt` and refuse missing, malformed, or
mismatched checksums.

**Manual download** — no runtime needed — download the archive for your platform from the
[GitHub Releases](https://github.com/niklasfp/nightowl/releases) page (`win-x64`, `win-arm64`,
`osx-arm64`, `linux-x64`, `linux-arm64`), unpack, and run `nightowl`.

**Portable .NET tool (alternative)** — if you already use the .NET SDK, NightOwl is also
published as a dotnet tool on **GitHub Packages**. Requires the .NET 10 runtime; add the feed
once (authenticate with a GitHub token that has `read:packages`), then install:

```bash
dotnet nuget add source "https://nuget.pkg.github.com/niklasfp/index.json" \
  --name github --username <you> --password <GH_TOKEN> --store-password-in-clear-text
dotnet tool install -g NightOwl --add-source github
nightowl --version
```

Publishing to nuget.org is planned — once live, this becomes a single
`dotnet tool install -g NightOwl` with no feed setup.

### Update

Re-run the same install command for the method you used:

1. **Install script (macOS/Linux)** — re-run
   `curl -fsSL https://raw.githubusercontent.com/niklasfp/nightowl/main/install.sh | sh`
   (fetches the latest release and replaces the binary in place; pass your `--install-dir`
   again if you overrode it).
2. **Install script (Windows)** — re-run
   `irm https://raw.githubusercontent.com/niklasfp/nightowl/main/install.ps1 | iex`.
3. **Manual download** — download the new archive and replace the unpacked binary.
4. **Portable .NET tool** — `dotnet tool update -g NightOwl --add-source github`.

### Uninstall

1. **Install script (macOS/Linux)** —
   `curl -fsSL https://raw.githubusercontent.com/niklasfp/nightowl/main/install.sh | sh -s -- --uninstall`
   (or just `rm ~/.local/bin/nightowl`; use your `--install-dir` if you overrode it).
2. **Install script (Windows)** —
   `& ([scriptblock]::Create((irm https://raw.githubusercontent.com/niklasfp/nightowl/main/install.ps1))) -Uninstall`
   (also removes the PATH entry it added).
3. **Manual download** — delete the unpacked `nightowl` binary (and revert any PATH edit you
   made yourself).
4. **Portable .NET tool** — `dotnet tool uninstall -g NightOwl`.

## Usage

```
nightowl [options] [-- <command> [args...]]

  (no args)              Keep the system awake until Ctrl+C
  -t, --for <duration>   Keep awake for a duration, then exit (e.g. 30s, 90m, 2h, 1h30m)
  -w, --pid <pid>        Keep awake while process <pid> runs; exit 0 when it does
  -- <command> [args]    Keep awake while <command> runs; exit with its exit code
  -d, --display          Also keep the display awake (not just the system)
  -s, --block-sleep      Block system sleep as strongly as the platform allows
  -v, --verbose          Log which backend/assertion is used
  -q, --quiet            Don't print the status line on start
  -h, --help             Show help
      --version          Show version
```

Examples:

```bash
nightowl                       # stay awake until you press Ctrl+C
nightowl --for 90m             # stay awake for 90 minutes, then exit
nightowl --display --for 1h    # keep the screen on for an hour too
nightowl -- ./long-build.sh    # stay awake while the build runs; exit with its code
nightowl --block-sleep --for 2h  # also block system sleep (not just idle), for 2 hours
nightowl --pid 4821            # stay awake while an existing process (PID 4821) runs
```

`--for`, `--pid`, and a wrapped `-- <command>` are mutually exclusive, as are `--quiet` and
`--verbose`. Unknown options exit with code 2.

`--pid <pid>` is the `caffeinate -w` equivalent: it holds the inhibitor while an **existing**
process runs, without spawning or owning it, and exits 0 once that process exits.

- **Fail fast:** if the PID doesn't exist (or can't be opened/watched) at startup, NightOwl
  prints a distinct error to stderr and exits non-zero **without ever taking the sleep lock**.
  This is deliberately unlike `caffeinate -w <dead-pid>`, which hangs forever on macOS.
- **PID-reuse caveat:** process identity is checked once, at startup. If the target exits and the
  operating system recycles its PID afterward, NightOwl (like any PID-based tool on platforms
  without handle semantics) cannot tell the recycled process apart from the original — it keeps
  waiting on whatever now holds that PID. Windows is reuse-immune once the handle is open, but
  macOS/Linux only close the race up to registration time, not indefinitely afterward.

On start NightOwl prints a one-line status reflecting the chosen options (e.g.
`NightOwl: keeping the system awake until interrupted (Ctrl+C).`, or `NightOwl: keeping the
system and display awake, blocking system sleep …`
with `--display --block-sleep`) to stderr — stdout stays reserved for the wrapped command's
output — and `--quiet` suppresses it.

## Platform notes

- **`-s/--block-sleep`** holds the strongest system-sleep block each platform offers, instead of
  only preventing *idle* sleep. It composes with `-d`, `--for`, and wrap mode; the startup status
  line stays generic (`blocking system sleep`) and per-platform caveats only appear with
  `--verbose`:
  - **macOS** adds a `PreventSystemSleep` assertion. The OS only honors it **on AC power** —
    NightOwl adds no power-source logic of its own, so on battery this behaves like the baseline.
  - **Windows** additionally requests `PowerRequestExecutionRequired`, which keeps the process
    running under Modern Standby. No user-mode API can block *user-initiated* sleep (lid close,
    power button, Start > Sleep) — Windows terminates power requests on sleep entry regardless —
    and on battery under Modern Standby the OS may terminate the hold after ~5 minutes anyway.
  - **Linux** extends the logind lock to also cover lid-close suspend
    (`sleep:idle:handle-lid-switch`), degrading one step at a time if polkit denies it
    (`sleep:idle`, then `idle`) — e.g. over SSH, with no local session.
- **Linux `--display` is best-effort.** System/idle sleep is always inhibited; preventing the
  *display* from sleeping varies across X11/Wayland/DBus, so NightOwl documents it as not
  guaranteed rather than faking it (run with `--verbose` to see what it does).
- **Linux requires `systemd-inhibit`** (from systemd). If it is missing, NightOwl fails with a
  clear message.
- **Killing NightOwl always releases the Linux lock — even `kill -9`.** The native path holds a
  logind inhibitor fd, which the kernel closes when the process dies; the fallback's
  `systemd-inhibit … cat` child reads a pipe from NightOwl and exits on EOF when NightOwl dies.
  Inspect active locks any time with `systemd-inhibit --list` (a stale lock can be released by
  killing the PID shown there).
- **Non-active sessions (e.g. SSH) may be denied on Linux.** logind's polkit policy grants sleep
  inhibitors to local sessions; elsewhere the request can be refused, and NightOwl then exits
  **1** with a clear error instead of pretending to hold a lock.

## Development

```bash
dotnet build                                         # IL build (portable)
dotnet test --coverage --coverage-output-format cobertura
dotnet format                                        # run before committing
dotnet publish src/NightOwl -r osx-arm64 -c Release -p:PublishAot=true   # native binary
```

Preview the website locally: `node site/serve.ts` → open the printed URL.

The platform difference is isolated behind a single `ISleepInhibitor` (and, for `--pid`, a
parallel `IPidWaiter`); all mode/timing logic is platform-agnostic and lives in
`KeepAwakeRunner`. Everything else (`IOsInfo`, `IProcessRunner`, `ILog`) is injected so the logic
is unit-tested without spawning real processes.

The unit tests can't verify the *physical effect* (a CI runner can't be suspended), so before
each release follow the per-platform [manual test plan](docs/manual-test-plan.md) — it confirms
the OS actually holds NightOwl's sleep lock, in about 5 minutes per platform.

## License

[MIT](LICENSE)
