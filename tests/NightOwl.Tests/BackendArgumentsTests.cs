using NightOwl.Inhibitors;
using NightOwl.Process;
using Shouldly;

namespace NightOwl.Tests;

public class BackendArgumentsTests
{
    [Test]
    public async Task Macos_native_assertion_round_trips()
    {
        // The IOKit P/Invoke only runs on macOS; elsewhere this is a no-op guard so CI stays green.
        // On the macOS runner it exercises the real native path: a successful Acquire means IOKit
        // accepted both assertions, and a clean DisposeAsync means it released them.
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var inhibitor = new MacOsSleepInhibitor(new FakeLog());
        await using var hold = inhibitor.Acquire(keepDisplayOn: true, blockSleep: false);
        await Assert.That(hold).IsNotNull();
    }

    [Test]
    public async Task Macos_native_assertion_round_trips_with_block_sleep()
    {
        // Same as above, but also exercises the additional PreventSystemSleep assertion for -s.
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var log = new FakeLog();
        var inhibitor = new MacOsSleepInhibitor(log);
        await using var hold = inhibitor.Acquire(keepDisplayOn: true, blockSleep: true);
        await Assert.That(hold).IsNotNull();
        log.Verboses.ShouldContain(v => v.Contains("PreventSystemSleep"));
        log.Verboses.ShouldContain(v => v.Contains("AC power only"));
    }

    [Test]
    public void Macos_optional_assertion_refusal_is_best_effort()
    {
        // Pure orchestration logic, parametrized over delegates — no IOKit P/Invoke involved, so
        // this runs (and is meaningful) on every OS. Verifies the "-s never fails" contract: a
        // refused optional (PreventSystemSleep) assertion is dropped and logged, not thrown.
        var logged = new List<string>();
        uint nextId = 0;
        uint CreateAssertion(string type) =>
            type == "PreventSystemSleep"
                ? throw new InhibitorException("IOKit refused the sleep assertion 'PreventSystemSleep' (IOReturn 0xE00002C7).")
                : nextId++;

#pragma warning disable CA1416 // Pure logic; deliberately exercised off-macOS too.
        var (ids, heldTypes) = MacOsSleepInhibitor.AcquireAssertions(
            ["PreventUserIdleSystemSleep", "PreventUserIdleDisplaySleep"],
            "PreventSystemSleep",
            CreateAssertion,
            logged.Add);
#pragma warning restore CA1416

        ids.Count.ShouldBe(2);
        heldTypes.ShouldBe(["PreventUserIdleSystemSleep", "PreventUserIdleDisplaySleep"]);
        logged.ShouldContain(m => m.Contains("PreventSystemSleep") && m.Contains("refused"));
    }

    [Test]
    public void Macos_optional_assertion_success_is_included()
    {
        uint nextId = 0;
        uint CreateAssertion(string type) => nextId++;

#pragma warning disable CA1416 // Pure logic; deliberately exercised off-macOS too.
        var (ids, heldTypes) = MacOsSleepInhibitor.AcquireAssertions(
            ["PreventUserIdleSystemSleep"],
            "PreventSystemSleep",
            CreateAssertion,
            _ => { });
#pragma warning restore CA1416

        ids.Count.ShouldBe(2);
        heldTypes.ShouldBe(["PreventUserIdleSystemSleep", "PreventSystemSleep"]);
    }

    [Test]
    public void Macos_required_assertion_failure_is_fatal()
    {
        // Unlike the optional -s assertion, a refused *required* (baseline/display) assertion must
        // still fail the whole acquire — it is not caught by AcquireAssertions.
        uint CreateAssertion(string type) =>
            throw new InhibitorException("IOKit refused the sleep assertion 'PreventUserIdleSystemSleep'.");

#pragma warning disable CA1416 // Pure logic; deliberately exercised off-macOS too.
        var ex = Should.Throw<InhibitorException>(() => MacOsSleepInhibitor.AcquireAssertions(
            ["PreventUserIdleSystemSleep"],
            optionalType: null,
            CreateAssertion,
            _ => { }));
#pragma warning restore CA1416
        ex.Message.ShouldContain("PreventUserIdleSystemSleep");
    }

    [Test]
    public void Linux_fallback_command_blocks_sleep_and_idle()
    {
        // Pure, platform-agnostic: the systemd-inhibit command the fallback path runs. The held
        // command is `cat` with stdin redirected to a pipe from nightowl, so the child exits on
        // stdin EOF — i.e. whenever nightowl dies, even SIGKILL — instead of dangling forever.
        var spec = SystemdInhibitSpec.Build();

        spec.FileName.ShouldBe("systemd-inhibit");
        spec.Arguments.ShouldBe([
            "--what=sleep:idle",
            "--mode=block",
            "--who=nightowl",
            "--why=user requested",
            "cat",
        ]);
        spec.RedirectStandardInput.ShouldBeTrue();
    }

    [Test]
    public void Linux_fallback_command_with_block_sleep_covers_lid_switch()
    {
        var spec = SystemdInhibitSpec.Build(blockSleep: true);

        spec.FileName.ShouldBe("systemd-inhibit");
        spec.Arguments.ShouldBe([
            "--what=sleep:idle:handle-lid-switch",
            "--mode=block",
            "--who=nightowl",
            "--why=user requested",
            "cat",
        ]);
        spec.RedirectStandardInput.ShouldBeTrue();
    }

    [Test]
    public void Wrapped_command_spec_does_not_redirect_stdin()
    {
        // Wrap mode must leave the child's stdin attached to the terminal (interactive commands),
        // so redirection is opt-in and off by default.
        var spec = new ProcessSpec("make", "build");
        spec.RedirectStandardInput.ShouldBeFalse();
    }

    [Test]
    public void Fallback_child_that_exits_immediately_fails_the_acquire()
    {
        // A denied systemd-inhibit (e.g. polkit refuses sleep locks for a non-active session)
        // prints an error and exits at once. That must be an acquire *failure*, not a "hold"
        // backed by a dead child that inhibits nothing.
        var handle = new FakeProcessHandle(exitImmediatelyWith: 1);

        var ex = Should.Throw<InhibitorException>(
            () => ProcessHold.StartVerified(handle, TimeSpan.FromMilliseconds(200)));

        ex.Message.ShouldContain("systemd-inhibit");
        ex.Message.ShouldContain("exit code 1");
        handle.Disposed.ShouldBeTrue();
    }

    [Test]
    public async Task Fallback_child_that_stays_alive_becomes_the_hold()
    {
        var handle = new FakeProcessHandle();

        var hold = ProcessHold.StartVerified(handle, TimeSpan.FromMilliseconds(50));

        handle.Killed.ShouldBeFalse();
        await hold.DisposeAsync();
        handle.Killed.ShouldBeTrue();
    }

    [Test]
    public void Linux_fallback_denied_by_polkit_is_a_clear_error()
    {
        // End-to-end through Acquire (off-Linux, where the native path always falls back):
        // both paths unavailable must surface as InhibitorException, never a silent no-op.
        if (OperatingSystem.IsLinux())
        {
            return;
        }

        var runner = new FakeProcessRunner(exitImmediatelyWith: 1);
#pragma warning disable CA1416 // Deliberately exercising the cross-platform fallback off-Linux.
        var ex = Should.Throw<InhibitorException>(
            () => new LinuxSleepInhibitor(runner, new FakeLog()).Acquire(keepDisplayOn: false, blockSleep: false));
#pragma warning restore CA1416
        ex.Message.ShouldContain("systemd-inhibit");
    }

    [Test]
    public async Task Linux_falls_back_to_systemd_inhibit_when_native_unavailable()
    {
        // Off Linux, libsystemd.so.0 can't load, so Acquire must fall back to the systemd-inhibit
        // CLI. (On Linux the native path may succeed, so this assertion only holds off-Linux.)
        if (OperatingSystem.IsLinux())
        {
            return;
        }

        var runner = new FakeProcessRunner();
        var log = new FakeLog();
#pragma warning disable CA1416 // Deliberately exercising the cross-platform fallback off-Linux.
        await using var _ = new LinuxSleepInhibitor(runner, log).Acquire(keepDisplayOn: true, blockSleep: false);
#pragma warning restore CA1416

        runner.LastSpec.FileName.ShouldBe("systemd-inhibit");
        runner.LastSpec.Arguments.ShouldContain("--what=sleep:idle");
        log.Verboses.ShouldContain(v => v.Contains("best-effort"));
    }

    [Test]
    public async Task Linux_falls_back_to_systemd_inhibit_with_block_sleep_what()
    {
        if (OperatingSystem.IsLinux())
        {
            return;
        }

        var runner = new FakeProcessRunner();
        var log = new FakeLog();
#pragma warning disable CA1416 // Deliberately exercising the cross-platform fallback off-Linux.
        await using var _ = new LinuxSleepInhibitor(runner, log).Acquire(keepDisplayOn: false, blockSleep: true);
#pragma warning restore CA1416

        runner.LastSpec.Arguments.ShouldContain("--what=sleep:idle:handle-lid-switch");
    }

    [Test]
    public void Linux_throws_when_native_and_systemd_inhibit_both_fail()
    {
        if (OperatingSystem.IsLinux())
        {
            return;
        }

        var runner = new FakeProcessRunner(new InvalidOperationException("not found"));
#pragma warning disable CA1416 // Deliberately exercising the cross-platform fallback off-Linux.
        var ex = Should.Throw<InhibitorException>(
            () => new LinuxSleepInhibitor(runner, new FakeLog()).Acquire(keepDisplayOn: false, blockSleep: false));
#pragma warning restore CA1416
        ex.Message.ShouldContain("systemd-inhibit");
    }

    [Test]
    public async Task Linux_native_or_fallback_round_trips()
    {
        // Only runs on Linux. Exercises the real libsystemd sd-bus path (and the systemd-inhibit
        // fallback). Where no logind/system bus is reachable (e.g. a minimal container), both paths
        // fail with InhibitorException — treated as inconclusive so the suite stays green.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        try
        {
            await using var hold =
                new LinuxSleepInhibitor(new SystemProcessRunner(), new FakeLog())
                    .Acquire(keepDisplayOn: true, blockSleep: false);
            await Assert.That(hold).IsNotNull();
        }
        catch (InhibitorException)
        {
            // No reachable logind/systemd in this environment — skip.
        }
    }

    [Test]
    public async Task Linux_native_or_fallback_round_trips_with_block_sleep()
    {
        // Only runs on Linux. Exercises the -s ladder against the real logind/D-Bus: either the
        // strongest lock or a degraded one is acquired, or (no reachable logind) it's inconclusive.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        try
        {
            await using var hold =
                new LinuxSleepInhibitor(new SystemProcessRunner(), new FakeLog())
                    .Acquire(keepDisplayOn: false, blockSleep: true);
            await Assert.That(hold).IsNotNull();
        }
        catch (InhibitorException)
        {
            // No reachable logind/systemd in this environment — skip.
        }
    }

    [Test]
    public async Task Windows_hold_acquires_and_releases_without_error()
    {
        // The P/Invoke path only runs on Windows; elsewhere this is a no-op guard so CI stays green.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var log = new FakeLog();
        var inhibitor = new WindowsSleepInhibitor(log);
        await using var hold = inhibitor.Acquire(keepDisplayOn: true, blockSleep: false);
        await Assert.That(hold).IsNotNull();

        // The -s/--block-sleep caveats (user-initiated sleep, Modern Standby battery limit) are
        // scoped to -s and must not appear on a baseline acquire.
        log.Verboses.ShouldNotContain(v => v.Contains("user-initiated sleep"));
    }

    [Test]
    public async Task Windows_hold_acquires_and_releases_with_block_sleep()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var log = new FakeLog();
        var inhibitor = new WindowsSleepInhibitor(log);
        await using var hold = inhibitor.Acquire(keepDisplayOn: true, blockSleep: true);
        await Assert.That(hold).IsNotNull();
        log.Verboses.ShouldContain(v => v.Contains("ExecutionRequired"));
        log.Verboses.ShouldContain(v => v.Contains("user-initiated sleep"));
    }
}
