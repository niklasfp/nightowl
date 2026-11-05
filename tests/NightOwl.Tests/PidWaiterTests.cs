using NightOwl.PidWait;
using Shouldly;
using SysProcess = System.Diagnostics.Process;
using SysProcessStartInfo = System.Diagnostics.ProcessStartInfo;

namespace NightOwl.Tests;

public class PidWaiterFactoryTests
{
    private static IPidWaiter Create(FakeOsInfo os) => PidWaiterFactory.Create(os, new FakeLog());

    [Test]
    public void Selects_windows_backend()
    {
        // Assert by name rather than ShouldBeOfType<WindowsPidWaiter>(): that type is
        // [SupportedOSPlatform("windows")], so referencing it here would trip CA1416 on macOS/Linux.
        Create(new FakeOsInfo(windows: true)).GetType().Name.ShouldBe("WindowsPidWaiter");
    }

    [Test]
    public void Selects_macos_backend()
    {
        Create(new FakeOsInfo(macOs: true)).GetType().Name.ShouldBe("MacOsPidWaiter");
    }

    [Test]
    public void Selects_linux_backend()
    {
        Create(new FakeOsInfo(linux: true)).GetType().Name.ShouldBe("LinuxPidWaiter");
    }

    [Test]
    public void Unsupported_os_throws()
    {
        Should.Throw<PidWaitException>(() => Create(new FakeOsInfo()));
    }
}

public class KillProbeTests
{
    [Test]
    public void Zero_return_value_means_alive()
    {
        KillProbe.Interpret(0, errno: 0).ShouldBe(KillProbe.Result.Alive);
    }

    [Test]
    public void Esrch_means_exited()
    {
        KillProbe.Interpret(-1, KillProbe.Esrch).ShouldBe(KillProbe.Result.Exited);
    }

    [Test]
    public void Eperm_is_treated_as_alive()
    {
        // EPERM means the process exists but we can't signal it -- can't be told apart from
        // continued liveness, so (matching dotnet/runtime) it must not be reported as exited.
        const int eperm = 1;
        KillProbe.Interpret(-1, eperm).ShouldBe(KillProbe.Result.Alive);
    }

    [Test]
    public void Unexpected_errno_is_conservatively_treated_as_alive()
    {
        KillProbe.Interpret(-1, errno: 9999).ShouldBe(KillProbe.Result.Alive);
    }
}

public class PidWaiterBackendTests
{
    [Test]
    public async Task Macos_native_wait_round_trips_on_a_real_short_lived_process()
    {
        // The kqueue P/Invoke only runs on macOS; elsewhere this is a no-op guard so CI stays
        // green. On the macOS runner it exercises the real native path end-to-end: a short-lived
        // real process is spawned, WaitForExitAsync is awaited against its actual PID, and it
        // must complete once (and only once) that process exits.
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        using var process = SysProcess.Start(new SysProcessStartInfo("sleep", "1") { UseShellExecute = false })!;
        var waiter = new MacOsPidWaiter(new FakeLog());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await waiter.WaitForExitAsync(process.Id, cts.Token);

        // Our native wait can observe exit fractionally before .NET's own child-reaping
        // path (SIGCHLD/waitpid) updates HasExited; give that a moment to catch up.
        await process.WaitForExitAsync(cts.Token);
        process.HasExited.ShouldBeTrue();
    }

    [Test]
    public async Task Macos_native_wait_fails_fast_on_a_nonexistent_pid()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var waiter = new MacOsPidWaiter(new FakeLog());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        PidWaitException? caught = null;
        try
        {
            // A PID that (almost certainly) does not exist. macOS PIDs top out well below this.
            await waiter.WaitForExitAsync(2_000_000, cts.Token);
        }
        catch (PidWaitException ex)
        {
            caught = ex;
        }

        caught.ShouldNotBeNull();
    }

    [Test]
    public async Task Windows_native_wait_round_trips_on_a_real_short_lived_process()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var process = SysProcess.Start(new SysProcessStartInfo("cmd.exe", "/c timeout 1") { UseShellExecute = false })!;
        var waiter = new WindowsPidWaiter(new FakeLog());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await waiter.WaitForExitAsync(process.Id, cts.Token);

        // Our native wait can observe exit fractionally before .NET's own child-reaping
        // path (SIGCHLD/waitpid) updates HasExited; give that a moment to catch up.
        await process.WaitForExitAsync(cts.Token);
        process.HasExited.ShouldBeTrue();
    }

    [Test]
    public async Task Windows_native_wait_fails_fast_on_a_nonexistent_pid()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var waiter = new WindowsPidWaiter(new FakeLog());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        PidWaitException? caught = null;
        try
        {
            await waiter.WaitForExitAsync(99999999, cts.Token);
        }
        catch (PidWaitException ex)
        {
            caught = ex;
        }

        caught.ShouldNotBeNull();
    }

    [Test]
    public async Task Linux_native_wait_round_trips_on_a_real_short_lived_process()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var process = SysProcess.Start(new SysProcessStartInfo("sleep", "1") { UseShellExecute = false })!;
        var waiter = new LinuxPidWaiter(new FakeLog());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await waiter.WaitForExitAsync(process.Id, cts.Token);

        // Our native wait can observe exit fractionally before .NET's own child-reaping
        // path (SIGCHLD/waitpid) updates HasExited; give that a moment to catch up.
        await process.WaitForExitAsync(cts.Token);
        process.HasExited.ShouldBeTrue();
    }

    [Test]
    public async Task Linux_native_wait_fails_fast_on_a_nonexistent_pid()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var waiter = new LinuxPidWaiter(new FakeLog());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        PidWaitException? caught = null;
        try
        {
            await waiter.WaitForExitAsync(2_000_000, cts.Token);
        }
        catch (PidWaitException ex)
        {
            caught = ex;
        }

        caught.ShouldNotBeNull();
    }
}
