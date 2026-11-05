# Packaging & Distribution — Decision Log

Decisions and assumptions behind [the packaging plan](packaging-plan.md) (Homebrew, winget,
and the cross-platform install scripts). Dated 2026-07-19. Each entry lists the decision, the
reasoning, and rejected alternatives, so future changes can revisit them deliberately.

## D1 — Install paths need anonymously downloadable release assets

Homebrew formulas, winget manifests, `curl | sh`, and the site's download buttons all need
**anonymously downloadable** release assets (winget-pkgs rejects non-public installer URLs).
This repository is public, and `pages.yml` deploys the site on pushes to `main`.

**Decision:** build and commit everything targeting that public-repo state; verify locally with
hermetic tests (local asset server, authenticated downloads).

## D2 — Homebrew: in-repo formula (`Formula/nightowl.rb`), not a separate tap repo

A conventional `niklasfp/homebrew-tap` repo gives the shortest install line, but requires
creating and pushing to a **new public repository** — an outward-facing action not taken
autonomously (and pointless while assets are private).

**Decision:** keep the formula in this repo at `Formula/nightowl.rb`. Homebrew can tap any git
repo: `brew tap niklasfp/nightowl https://github.com/niklasfp/nightowl` then
`brew install nightowl`. Migrating the file to a dedicated tap repo later is a copy, not a
rewrite. Homebrew **core** was not considered — it has notability requirements (forks, stars)
this project doesn't meet yet.

## D3 — winget: manifests prepared in-repo; first submission is manual, updates automated

`wingetcreate update` can only update a package that already exists in
[microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs); the first submission
(`wingetcreate new` + human-reviewed PR) must be done by the maintainer. Opening a PR against
Microsoft's repo is also not an action to take autonomously.

**Decision:** commit ready-to-submit manifests under `packaging/winget/`
(PackageIdentifier `niklasfp.NightOwl`, ManifestVersion 1.6.0, zip installer with nested
portable exe aliased `nightowl`), and add a `winget` release job that runs
`wingetcreate update --submit` for **stable (non-hyphenated) tags** when a `WINGET_TOKEN`
secret exists. No secret / prerelease tag → the job no-ops. `wingetcreate.exe` is pinned and
SHA-256-verified in `.github/workflows/release.yml` (the workflow is the source of truth for
the version and bare hex digest). The floating `https://aka.ms/wingetcreate` shortcut is not
used. To bump, take `microsoft/winget-create`'s `wingetcreate.exe` digest, strip a leading
`sha256:` prefix, and update both values in the workflow together:

```powershell
gh api repos/microsoft/winget-create/releases/latest --jq '{tag: .tag_name, sha256: (.assets[] | select(.name=="wingetcreate.exe") | .digest | ltrimstr("sha256:"))}'
```

## D4 — Found & fixed: AOT binaries don't carry the tag version

`release.yml` stamps `-p:Version=` from the tag when **packing the nupkg**, but not when
**AOT-publishing** — so every released binary reports the csproj default (`0.1.0`) from
`--version` (which reads `AssemblyInformationalVersion`). Homebrew's `test do` and general
sanity want the binary to report its release version.

**Decision:** stamp `-p:Version=${tag#v}` in both AOT publish paths too. The formula's test
block asserts a lenient version pattern (`/\d+\.\d+/`) rather than the exact version, because
the currently-referenced `v0.1.0-beta.1` binaries predate the fix.

## D5 — `/releases/latest` 404s while only prereleases exist

GitHub's `releases/latest` API (and the site's `releases/latest/download/...` buttons) exclude
prereleases; every NightOwl release so far is a prerelease, so "latest" is a 404 today.

**Decision:** both install scripts resolve the version via `/releases/latest` **with a
fallback** to the newest entry of `/releases`. The site's download buttons are left as-is —
they self-heal on the first stable release, and rewriting them to hit the API from a static
page adds complexity for a temporary state.

## D6 — `v0.1.0-beta.2` is an empty release (no assets)

The newest release has zero assets (its build presumably failed after the release was cut).
The install scripts' "newest release" fallback would pick it and fail.

**Decision:** scripts fail with a clear, actionable error (asset URL + "pass `--version`")
rather than silently walking back through history. Recommended manual cleanup (not done
autonomously — deleting a release is destructive): delete or re-publish `v0.1.0-beta.2`.

## D7 — Intel macOS (`osx-x64`) is unsupported

The current release matrix builds `osx-arm64` only. (`v0.1.0-beta.1` *does* carry an
`osx-x64` asset — a leftover from an older matrix.)

**Decision:** `install.sh` errors explicitly on Intel macOS, matching the matrix. The formula
simply provides no `osx-x64` URL, so Homebrew itself reports it as unsupported with its
generic message. Re-adding an `osx-x64` leg (macOS AOT can cross-compile x64 from the
arm64 runner) is a possible follow-up, decided separately.

**Update (2026-07-21):** decided — Intel macOS is **not** a target platform and will not be
supported (maintainer decision; #39 closed as not planned). Recorded as a hard rule in
AGENTS.md's Distribution & packaging section.

## D8 — Releases gain a `SHA256SUMS.txt`; scripts require verification

Homebrew and winget both embed per-asset SHA-256s, and `curl | sh` without verification is
needlessly weak.

**Decision:** the release job generates `SHA256SUMS.txt` over all assets and attaches it.
Install scripts fail closed when the file is absent (HTTP 404), empty, lacks the selected
asset, or does not match the downloaded archive. Transport failures downloading the sums file
are reported as download errors rather than "release has no checksums". `v0.1.0-beta.1` was
backfilled with a `SHA256SUMS.txt` built from the same digests already committed in
`Formula/nightowl.rb` and `packaging/winget/*.installer.yaml`, so there is no unverified
install escape hatch.

## D9 — User-scoped installs, no elevation

**Decision:** `install.sh` defaults to `~/.local/bin` (override: `--install-dir` /
`NIGHTOWL_INSTALL_DIR`); `install.ps1` defaults to `%LOCALAPPDATA%\Programs\NightOwl` and
appends it to the **user** PATH. No `sudo`, no admin prompts, no machine-wide state.
`/usr/local/bin` fans can pass the flag.

## D10 — Scripts live at the repo root and are published to the website

**Decision:** `install.sh` / `install.ps1` sit at the repo root (the conventional, discoverable
spot; curl-able from `raw.githubusercontent.com/niklasfp/nightowl/main/...`), and `pages.yml`
stages them into the Pages artifact so `https://niklasfp.github.io/nightowl/install.sh` also
works. Docs lead with the raw GitHub URL; Pages serves the same scripts.

## D11 — One generator script owns the formula and manifests

Hand-edited version/checksum pairs across four files *will* drift.

**Decision:** `scripts/update-packaging.ts` (plain Node + native TS type-stripping, zero
dependencies — matches `site/serve.ts` precedent and the no-Python rule) fully **regenerates**
`Formula/nightowl.rb` and `packaging/winget/*.yaml` from `<version, SHA256SUMS.txt>`. The
committed files are its verbatim output for `v0.1.0-beta.1` (real checksums, computed from the
actual release assets). A `packaging` release job reruns it on every tag and commits the result
back to `main` as `github-actions[bot]`.

## D12 — Verification strategy (what's tested now vs. deferred)

CI can't be suspended, so end-to-end "brew install from GitHub" is not part of CI.
Per-artifact verification now:

- `install.sh` — `shellcheck`, `sh -n`, plus a **hermetic end-to-end test**: real
  `v0.1.0-beta.1` assets served from a local HTTP server, `NIGHTOWL_ASSET_BASE` pointed at it,
  asserting the installed binary runs.
- `install.ps1` — pwsh parse check, and the same hermetic flow on macOS pwsh via a
  `NIGHTOWL_RID` override (Windows-only steps guarded).
- Formula — `ruby -c` + `brew style`; real `brew install`/`brew test` deferred to the manual
  test plan (needs public assets).
- winget — structural review only; `winget validate` needs Windows and is added to the manual
  test plan (Parallels VM).
- Workflows — `actionlint`; a real tag-push rehearsal is deferred (would publish a release).

This is a deliberate deviation from strict TDD: the changes are infra scripts and manifests
with no .NET surface, so verification is lint + hermetic integration rather than unit tests.

## D13 — Naming and metadata

Formula class `Nightowl`, install command `nightowl`, homepage
`https://niklasfp.github.io/nightowl/`; winget `niklasfp.NightOwl`, Publisher
`Niklas Petersen`, Moniker `nightowl`, License MIT. All mirror the csproj/README exactly.

## D14 — Installers extract only the NightOwl binary

Checksums prove the archive bytes match the release, but they do not by themselves stop a
compromised archive from using tar/zip member names to write outside the extract directory
(`../`, absolute paths, extra files, symlinks).

**Decision:** `install.sh` and `install.ps1` inspect archive members and extract only a
regular file named `nightowl` / `nightowl.exe` to a staging path under the install
directory (`tar -O` / `ExtractToFile` to `.nightowl.new` / `nightowl.exe.new`), then rename
into place. Unexpected members fail the install. The release Package step also asserts each
archive contains exactly that one member (`COPYFILE_DISABLE=1` on macOS tar).
