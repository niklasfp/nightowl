# NightOwl — Agent Instructions

NightOwl is a cross-platform "keep the computer awake" CLI (the `nightowl` command / `NightOwl`
.NET tool). It holds a system sleep-inhibitor indefinitely, for a duration, or while a wrapped
command runs. See [README.md](README.md) for user-facing docs. This file is the single source of
truth for repo conventions.

## Stack

- **.NET 10** (`net10.0`), pinned via `global.json` (rollForward `latestMinor`).
- **Central Package Management** — all versions live in `Directory.Packages.props`; project
  `<PackageReference>`s carry **no** `Version`.
- **Tests: TUnit + Shouldly** on Microsoft.Testing.Platform, coverage via `coverlet.collector`.
- AOT-clean: the CLI project sets `IsAotCompatible=true`, so trim/AOT analyzers run on every
  build. Keep it warning-free. Native AOT is enabled only at publish time
  (`-p:PublishAot=true`), never in the csproj.

## Build / test / run

```bash
dotnet build                                              # IL build
dotnet test --coverage --coverage-output-format cobertura # tests + coverage
dotnet format                                             # run before every commit
dotnet run --project src/NightOwl -- --for 5s --verbose   # run the CLI
dotnet publish src/NightOwl -r <rid> -c Release -p:PublishAot=true   # native binary
```

## Architecture

- The only platform-specific code is behind `ISleepInhibitor` (`Windows`/`MacOs`/`Linux`
  backends, chosen by `InhibitorFactory` from `IOsInfo`). **All mode/timing logic is
  platform-agnostic** and lives in `Modes/KeepAwakeRunner.cs`.
- `IProcessRunner`/`IProcessHandle`, `IOsInfo`, and `ILog` are seams so backends and wrap-mode
  are unit-tested **without spawning real processes** (see `tests/.../TestDoubles.cs`). Only the
  Linux backend still spawns a helper process (`systemd-inhibit`); Windows and macOS are native.
- The CLI parser is hand-rolled (`Cli/ArgumentParser.cs`) — no `System.CommandLine`, to keep the
  build tiny and AOT/trim-warning-free.
- All three backends use native source-generated `[LibraryImport]` P/Invoke, each guarded with
  `[SupportedOSPlatform(...)]` (compiles on every OS, only called on its own).
  - **Windows** calls `kernel32!SetThreadExecutionState` on a dedicated thread (the flag is
    per-thread).
  - **macOS** calls IOKit `IOPMAssertionCreateWithName` (+ CoreFoundation for the `CFString`
    args); its assertion is process-global (not thread-bound), so the hold just stores the
    assertion ids and releases them — no helper thread.
  - **Linux** calls `libsystemd` sd-bus to take a logind `Inhibit` lock and holds the returned fd.
    `SystemdInhibitSpec` + `IProcessRunner` are the **fallback** to `systemd-inhibit` when
    libsystemd/D-Bus is unavailable.

## Testing & coverage

- Cover the AOT-clean logic: arg parsing, duration parsing, factory selection, the Linux
  fallback's `systemd-inhibit` command (the pure `SystemdInhibitSpec`), and `KeepAwakeRunner` modes.
- Platform P/Invoke paths are guarded with an OS check so the suite stays green on every CI leg.
  The macOS and Linux guards go further than Windows: on their own runner each acquires and
  releases the real native lock (`Macos_native_assertion_round_trips`,
  `Linux_native_or_fallback_round_trips`), exercising the native path. Off-Linux, the Linux tests
  instead exercise the `systemd-inhibit` fallback (libsystemd can't load).
- Run coverage and confirm it is meaningful before opening a PR.
- Unit tests can't verify the physical "machine stays awake" effect (CI can't be suspended).
  Before tagging a release, run [docs/manual-test-plan.md](docs/manual-test-plan.md) on each
  platform — it checks the OS sleep-lock list (`pmset` / `powercfg` / `systemd-inhibit`) rather
  than waiting for sleep.

## CI workflows

- `.github/workflows/ci.yml` — build + test on `{ ubuntu, macos, windows }`.
- `.github/workflows/release.yml` — on `v*` tags, AOT-publish the 5 RIDs and attach to the GitHub
  Release; pack + push the tool nupkg to **GitHub Packages** (`GITHUB_TOKEN`, `packages: write`).
  AOT can't cross-OS but can cross-arch on the same OS (the Windows runner has the VS ARM64 C++
  tools; macOS only ships `osx-arm64`, native on the arm64 runner). `linux-arm64` is the exception — free GitHub arm64 runners are
  public-repo only, so it's built natively in an emulated arm64 container
  (`mcr.microsoft.com/dotnet/sdk:10.0-noble-aot` via `docker/setup-qemu-action`). Reproduce that
  build locally with `tests/manual/build-linux-arm64.sh` (native, no QEMU, on an arm64 host).
- All GitHub Actions are **SHA-pinned** with a `# vX.Y.Z` comment — never floating `@v4` tags.
  Look up a SHA with `gh api repos/{owner}/{repo}/git/ref/tags/{tag} --jq '.object.sha'`.
  The `winget` job similarly pins `wingetcreate.exe` to a `microsoft/winget-create` release
  URL and verifies its SHA-256 before execution.
- `.github/dependabot.yml` runs weekly grouped updates: nuget bumps arrive as `microsoft` and
  `test` bundles (ungrouped packages get their own PR), github-actions as one combined PR, and
  dotnet-sdk keeps `global.json` current. Dependabot is expected to keep Actions SHA-pinned and
  refresh the `# vX.Y.Z` comments itself when it bumps them.

## Distribution & packaging

- Release assets: the 5 AOT archives, the tool nupkg, plus `SHA256SUMS.txt` (checksums cover
  every asset); AOT binaries are version-stamped from the tag (`-p:Version`), like the nupkg.
- `Formula/nightowl.rb` (Homebrew — tapped straight from this repo) and
  `packaging/winget/*.yaml` (winget, id `niklasfp.NightOwl`) are **generated** by
  `scripts/update-packaging.ts`; never edit them by hand. The `packaging` job in
  `release.yml` regenerates them on every tag and commits to `main` (falling back to an
  auto-opened PR if branch protection rejects the direct push); the `winget` job submits
  stable tags via `wingetcreate` when a `WINGET_TOKEN` secret exists (the first winget-pkgs
  submission is manual). A run for a tag older than the version already committed in
  `Formula/nightowl.rb` skips regeneration (tags aren't monotonic — a hotfix can be cut after a
  newer series shipped); pass `--force` (or `NIGHTOWL_PACKAGING_FORCE=1`) to override for a
  deliberate rollback.
- `install.sh` / `install.ps1` (repo root) are the user-facing installers; `pages.yml` also
  serves them from the website root. Keep them in sync with the release asset names/RIDs.
  Both require `SHA256SUMS.txt` and extract only a regular `nightowl` / `nightowl.exe`
  member — never `..`, absolute paths, extra files, or symlinks.
- **macOS x64 (Intel, `osx-x64`) is not a supported target platform** — a deliberate decision,
  not an oversight. Do not add an `osx-x64` release leg, formula block, or installer mapping;
  macOS is Apple Silicon (`osx-arm64`) only, and `install.sh` errors on Intel by design
  (decision log D7).
- Decisions and assumptions: [docs/packaging-decision-log.md](docs/packaging-decision-log.md).

## Website

- `site/` is a zero-dependency static landing page (plain HTML + CSS, no build step), deployed
  to GitHub Pages by `.github/workflows/pages.yml`; its content (tagline, install, usage,
  platform table, download links) is sourced from `README.md`.
- **Any significant change that updates documentation or usage** — new/changed CLI options,
  install steps, platform behavior, release asset names — **must also update the website** so
  `site/index.html` stays in sync with `README.md`.
- Preview locally with `node site/serve.ts` (serves the real `/nightowl/` subpath); opening
  `site/index.html` via `file://` must also keep working (relative URLs only).
- The README's centered header reuses `site/favicon.svg` (the same owl mark as the site hero)
  and mirrors the hero tagline — no site screenshots are kept in the repo, so there is nothing
  to retake after a redesign; keep the mark and tagline as the single shared sources.

## Core guidelines

- Be autonomous — make reasonable decisions and move forward.
- Commit early and often; keep commits focused and atomic. Run `dotnet format` first.
- Session branches are prefixed `work/` (e.g. `work/nightowl`); the main branch stays clean.
- When done, open a PR back to `main`.
- Label every PR and issue you create with the repo's fitting labels (`gh label list`; e.g.
  `bug`/`enhancement`, `area:*`, `platform:*`). If no suitable label exists, leave it unlabeled
  and tell the user so.
