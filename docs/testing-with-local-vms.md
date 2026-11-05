# Testing with local VMs (Linux via Apple `container`, Windows via Parallels)

CI runs the unit suite on all three OSes, but the [manual test plan](manual-test-plan.md)
inspects the real OS sleep-lock list (`systemd-inhibit --list`, `powercfg /requests`), which CI
can't do meaningfully. On an Apple Silicon Mac both the Linux and Windows halves can be covered
locally: Linux with Apple's [`container`](https://github.com/apple/container) tool, Windows with
a Parallels VM. This doc records the working recipes and their sharp edges.

## Linux — Apple `container`

Prerequisite: `container system start` (and `container system stop` when done).

### Unit suite on real Linux (arm64)

```bash
container run --rm -v "$PWD":/src mcr.microsoft.com/dotnet/sdk:10.0 bash -c '
  cp -r /src /tmp/w &&
  find /tmp/w -type d \( -name obj -o -name bin \) -prune -exec rm -rf {} + &&
  cd /tmp/w &&
  dotnet test'
```

- **Copy before building.** Building against the mount directly would mix root-owned Linux
  `obj/bin` into your macOS checkout; copying and pruning `obj`/`bin` gives a clean restore.
- **SDK pin mismatch:** if the cached image's SDK is older than `global.json`'s version, either
  `container image pull mcr.microsoft.com/dotnet/sdk:10.0` or patch the *copy*:
  `sed -i "s/<pinned>/<installed>/" global.json` before `dotnet test`.
- On Linux this runs the native paths the macOS suite can only guard (e.g. `pidfd_open`), but
  there is no logind in the SDK image, so inhibitor round-trips take the fallback/skip route —
  hence the next section.

### Real logind (systemd as PID 1)

The plain images have no systemd; boot one:

```bash
container run --detach --name owl-sysd -v "<dir-with-binaries>":/pub ubuntu:latest bash -c \
  'apt-get update -qq && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq systemd dbus >/dev/null 2>&1 \
   && exec /lib/systemd/systemd'
# give it ~45s, then:
container exec owl-sysd systemctl is-system-running       # -> "running"
container exec owl-sysd systemctl status systemd-logind   # -> active
```

Now run the manual-test-plan Linux rows inside, e.g.:

```bash
container exec owl-sysd bash -c \
  '/pub/NightOwl -v & sleep 2; systemd-inhibit --list | grep nightowl; kill -TERM %1; wait; \
   sleep 1; systemd-inhibit --list | grep nightowl || echo released'
```

- **Binaries:** IL publish cross-OSes fine from macOS —
  `dotnet publish src/NightOwl -r linux-arm64 -c Release --self-contained -o <dir>`. Set
  `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` when running IL builds (the image has no ICU).
  Native AOT must be built *inside* a Linux container: `tests/manual/build-linux-arm64.sh`
  (docker), or the same `mcr.microsoft.com/dotnet/sdk:10.0-noble-aot` image via `container`
  with the copy-then-publish pattern above.
- **Polkit caveat:** the container runs everything as root, so logind grants the strongest lock
  unconditionally — permission-denial degradation behavior cannot be observed here.
- Cleanup: `container stop owl-sysd && container rm owl-sysd`.

## Windows — Parallels VM

Windows AOT can't be cross-compiled from macOS (CI's release workflow covers that); publish a
single-file self-contained IL binary instead — one exe, no .NET install needed in the guest:

```bash
dotnet publish src/NightOwl -r win-arm64 -c Release --self-contained -p:PublishSingleFile=true -o out/
```

### Getting files in and commands running

```bash
prlctl start "<vm-name>"
prlctl set "<vm-name>" --shf-host on
prlctl set "<vm-name>" --shf-host-add nightowl --path "$PWD/out"
# wait until the guest answers:
until prlctl exec "<vm-name>" cmd /c "echo ok" 2>/dev/null | grep -q ok; do sleep 5; done
```

The share appears as `\\Mac\nightowl` in the guest (also from the SYSTEM session). Copy the exe
guest-local before testing — power requests are attributed by image path, and a UNC path in
`powercfg /requests` output is harder to grep.

Two `prlctl exec` sharp edges:

1. **It runs as `NT AUTHORITY\SYSTEM` in session 0.** Elevation is what `powercfg /requests`
   needs to show process names, so that part is free. But complex `cmd.exe` one-liners get
   mangled crossing the prlctl/quoting boundary — put anything nontrivial in a `.ps1` on the
   share and run
   `prlctl exec "<vm-name>" powershell -NoProfile -ExecutionPolicy Bypass -File '\\Mac\nightowl\script.ps1'`.
2. **Session 0 has no display**, so `PowerSetRequest(DisplayRequired)` fails there with
   `ERROR_NOT_SUPPORTED` (NightOwl degrades best-effort and notes it under `--verbose`). To test
   `--display` for real, run in the logged-on user's interactive session via a scheduled task —
   SYSTEM may register one against the existing logon without a password:

   ```powershell
   $a  = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument '-NoProfile -ExecutionPolicy Bypass -File C:\path\to\check.ps1'
   $pr = New-ScheduledTaskPrincipal -UserId '<user>' -LogonType Interactive -RunLevel Highest
   Register-ScheduledTask -TaskName 'nightowl-verify' -Action $a -Principal $pr -Force
   Start-ScheduledTask -TaskName 'nightowl-verify'
   # read the task's output file, then:
   Unregister-ScheduledTask -TaskName 'nightowl-verify' -Confirm:$false
   ```

### Running the plan

`tests/manual/verify-windows.ps1 C:\path\to\nightowl.exe` automates the Windows sheet of the
manual test plan; expect the `--display` row to need the scheduled-task detour above, and
everything else to work straight from `prlctl exec`.

### Restore

```bash
prlctl exec "<vm-name>" powershell -NoProfile -Command 'Remove-Item -Recurse -Force C:\path\to\test-dir'
prlctl stop "<vm-name>"
prlctl set "<vm-name>" --shf-host-del nightowl
prlctl set "<vm-name>" --shf-host off
```

### What a VM cannot show

Real sleep entry, lid-close behavior, and Modern Standby battery semantics need physical
hardware — in a VM the plan's `powercfg /requests` rows are the meaningful signal, same as the
plan's own "check the lock list, don't wait for sleep" philosophy.
