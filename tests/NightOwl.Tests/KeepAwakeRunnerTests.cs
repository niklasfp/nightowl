using NightOwl.Cli;
using NightOwl.Modes;
using NightOwl.PidWait;
using Shouldly;

namespace NightOwl.Tests;

public class KeepAwakeRunnerTests
{
    private static (KeepAwakeRunner Runner, FakeSleepInhibitor Inhibitor, FakeProcessRunner Processes, FakePidWaiter PidWaiter, FakeLog Log) Build()
    {
        var inhibitor = new FakeSleepInhibitor();
        var processes = new FakeProcessRunner();
        var pidWaiter = new FakePidWaiter();
        var log = new FakeLog();
        return (new KeepAwakeRunner(inhibitor, processes, pidWaiter, log), inhibitor, processes, pidWaiter, log);
    }

    [Test]
    public async Task Timed_completes_and_releases_the_hold()
    {
        var (runner, inhibitor, _, _, _) = Build();
        var options = new CliOptions(AwakeMode.Timed, TimeSpan.FromMilliseconds(20), null, false, false, false, false, []);

        var code = await runner.RunAsync(options, CancellationToken.None);

        code.ShouldBe(0);
        inhibitor.Hold.Disposed.ShouldBeTrue();
    }

    [Test]
    public async Task Display_request_is_forwarded_to_the_inhibitor()
    {
        var (runner, inhibitor, _, _, _) = Build();
        var options = new CliOptions(AwakeMode.Timed, TimeSpan.FromMilliseconds(1), null, true, false, false, false, []);

        await runner.RunAsync(options, CancellationToken.None);

        inhibitor.DisplayRequested.ShouldBe(true);
    }

    [Test]
    public async Task Block_sleep_request_is_forwarded_to_the_inhibitor()
    {
        var (runner, inhibitor, _, _, _) = Build();
        var options = new CliOptions(AwakeMode.Timed, TimeSpan.FromMilliseconds(1), null, false, true, false, false, []);

        await runner.RunAsync(options, CancellationToken.None);

        inhibitor.BlockSleepRequested.ShouldBe(true);
    }

    [Test]
    public async Task Status_line_names_block_sleep_when_requested()
    {
        var (runner, _, _, _, log) = Build();
        var options = new CliOptions(AwakeMode.Timed, TimeSpan.FromMilliseconds(1), null, false, true, false, false, []);

        await runner.RunAsync(options, CancellationToken.None);

        log.Infos.ShouldContain(m => m.Contains("blocking system sleep"));
    }

    [Test]
    public async Task Status_line_omits_block_sleep_when_not_requested()
    {
        var (runner, _, _, _, log) = Build();
        var options = new CliOptions(AwakeMode.Timed, TimeSpan.FromMilliseconds(1), null, false, false, false, false, []);

        await runner.RunAsync(options, CancellationToken.None);

        log.Infos.ShouldNotContain(m => m.Contains("blocking system sleep"));
    }

    [Test]
    public async Task Wrap_propagates_the_childs_exit_code()
    {
        var (runner, inhibitor, processes, _, _) = Build();
        var options = new CliOptions(AwakeMode.Wrap, null, null, false, false, false, false, ["mytool", "--flag"]);

        var task = runner.RunAsync(options, CancellationToken.None);
        processes.LastHandle.CompleteWith(42);
        var code = await task;

        code.ShouldBe(42);
        processes.LastSpec.FileName.ShouldBe("mytool");
        processes.LastSpec.Arguments.ShouldBe(["--flag"]);
        inhibitor.Hold.Disposed.ShouldBeTrue();
    }

    [Test]
    public async Task Wrap_failure_to_start_returns_127()
    {
        var inhibitor = new FakeSleepInhibitor();
        var processes = new FakeProcessRunner(new InvalidOperationException("no such tool"));
        var runner = new KeepAwakeRunner(inhibitor, processes, new FakePidWaiter(), new FakeLog());
        var options = new CliOptions(AwakeMode.Wrap, null, null, false, false, false, false, ["ghost"]);

        var code = await runner.RunAsync(options, CancellationToken.None);

        code.ShouldBe(127);
        inhibitor.Hold.Disposed.ShouldBeTrue();
    }

    [Test]
    public async Task Indefinite_runs_until_cancelled_then_releases_the_hold()
    {
        var (runner, inhibitor, _, _, _) = Build();
        var options = new CliOptions(AwakeMode.Indefinite, null, null, false, false, false, false, []);
        using var cts = new CancellationTokenSource();

        var task = runner.RunAsync(options, cts.Token);
        task.IsCompleted.ShouldBeFalse();

        await cts.CancelAsync();
        var code = await task;

        code.ShouldBe(0);
        inhibitor.Hold.Disposed.ShouldBeTrue();
    }

    [Test]
    public async Task Wrap_cancellation_kills_the_child_and_reports_interrupted()
    {
        var (runner, inhibitor, processes, _, _) = Build();
        var options = new CliOptions(AwakeMode.Wrap, null, null, false, false, false, false, ["long-runner"]);
        using var cts = new CancellationTokenSource();

        var task = runner.RunAsync(options, cts.Token);
        await cts.CancelAsync();
        var code = await task;

        code.ShouldBe(130);
        processes.LastHandle.Killed.ShouldBeTrue();
        inhibitor.Hold.Disposed.ShouldBeTrue();
    }

    [Test]
    public async Task Indefinite_announces_the_status_at_info_level()
    {
        var (runner, _, _, _, log) = Build();
        var options = new CliOptions(AwakeMode.Indefinite, null, null, false, false, false, false, []);
        using var cts = new CancellationTokenSource();

        var task = runner.RunAsync(options, cts.Token);
        await cts.CancelAsync();
        await task;

        log.Infos.ShouldContain(m => m.Contains("until interrupted"));
    }

    [Test]
    public async Task Timed_announces_the_duration_at_info_level()
    {
        var (runner, _, _, _, log) = Build();
        var options = new CliOptions(AwakeMode.Timed, TimeSpan.FromMilliseconds(1), null, false, false, false, false, []);

        await runner.RunAsync(options, CancellationToken.None);

        log.Infos.ShouldContain(m => m.Contains("NightOwl: keeping the system awake for"));
    }

    [Test]
    public async Task Status_line_names_the_display_when_requested()
    {
        var (runner, _, _, _, log) = Build();
        var options = new CliOptions(AwakeMode.Timed, TimeSpan.FromMilliseconds(1), null, true, false, false, false, []);

        await runner.RunAsync(options, CancellationToken.None);

        log.Infos.ShouldContain(m => m.Contains("NightOwl: keeping the system and display awake"));
    }

    [Test]
    public async Task Status_line_omits_the_display_when_not_requested()
    {
        var (runner, _, _, _, log) = Build();
        var options = new CliOptions(AwakeMode.Timed, TimeSpan.FromMilliseconds(1), null, false, false, false, false, []);

        await runner.RunAsync(options, CancellationToken.None);

        log.Infos.ShouldNotContain(m => m.Contains("display"));
    }

    [Test]
    public async Task Wrap_announces_the_command_at_info_level()
    {
        var (runner, _, processes, _, log) = Build();
        var options = new CliOptions(AwakeMode.Wrap, null, null, false, false, false, false, ["mytool", "--flag"]);

        var task = runner.RunAsync(options, CancellationToken.None);
        processes.LastHandle.CompleteWith(0);
        await task;

        log.Infos.ShouldContain(m => m.Contains("mytool --flag"));
    }

    [Test]
    public async Task WaitPid_completes_and_releases_the_hold_when_the_target_exits()
    {
        var (runner, inhibitor, _, pidWaiter, _) = Build();
        var options = new CliOptions(AwakeMode.WaitPid, null, 4321, false, false, false, false, []);

        var task = runner.RunAsync(options, CancellationToken.None);
        pidWaiter.LastPid.ShouldBe(4321);
        pidWaiter.CompleteExit();
        var code = await task;

        code.ShouldBe(0);
        inhibitor.Hold.Disposed.ShouldBeTrue();
    }

    [Test]
    public async Task WaitPid_announces_the_pid_at_info_level()
    {
        var (runner, _, _, pidWaiter, log) = Build();
        var options = new CliOptions(AwakeMode.WaitPid, null, 4321, false, false, false, false, []);

        var task = runner.RunAsync(options, CancellationToken.None);
        pidWaiter.CompleteExit();
        await task;

        log.Infos.ShouldContain(m => m.Contains("PID 4321"));
    }

    [Test]
    public async Task WaitPid_fails_fast_without_ever_acquiring_the_inhibitor()
    {
        var inhibitor = new FakeSleepInhibitor();
        var pidWaiter = new FakePidWaiter(new PidWaitException("no such process"));
        var runner = new KeepAwakeRunner(inhibitor, new FakeProcessRunner(), pidWaiter, new FakeLog());
        var options = new CliOptions(AwakeMode.WaitPid, null, 99999, false, false, false, false, []);

        var code = await runner.RunAsync(options, CancellationToken.None);

        code.ShouldBe(1);
        inhibitor.Hold.Disposed.ShouldBeFalse();
        inhibitor.DisplayRequested.ShouldBeNull();
    }

    [Test]
    public async Task WaitPid_fail_fast_logs_a_distinct_warning()
    {
        var inhibitor = new FakeSleepInhibitor();
        var pidWaiter = new FakePidWaiter(new PidWaitException("no such process"));
        var log = new FakeLog();
        var runner = new KeepAwakeRunner(inhibitor, new FakeProcessRunner(), pidWaiter, log);
        var options = new CliOptions(AwakeMode.WaitPid, null, 99999, false, false, false, false, []);

        await runner.RunAsync(options, CancellationToken.None);

        log.Warns.ShouldContain(m => m.Contains("99999"));
    }

    [Test]
    public async Task WaitPid_cancellation_releases_the_hold_and_reports_interrupted()
    {
        var (runner, inhibitor, _, pidWaiter, _) = Build();
        var options = new CliOptions(AwakeMode.WaitPid, null, 4321, false, false, false, false, []);
        using var cts = new CancellationTokenSource();

        var task = runner.RunAsync(options, cts.Token);
        pidWaiter.LastPid.ShouldBe(4321);
        await cts.CancelAsync();
        var code = await task;

        code.ShouldBe(130);
        inhibitor.Hold.Disposed.ShouldBeTrue();
    }

    [Test]
    public async Task WaitPid_late_failure_is_caught_logged_and_releases_the_hold()
    {
        var (runner, inhibitor, _, pidWaiter, log) = Build();
        var options = new CliOptions(AwakeMode.WaitPid, null, 4321, false, false, false, false, []);

        var task = runner.RunAsync(options, CancellationToken.None);
        pidWaiter.LastPid.ShouldBe(4321);
        pidWaiter.FailExit(new PidWaitException("kevent() failed (error 9)."));
        var code = await task;

        code.ShouldNotBe(0);
        inhibitor.Hold.Disposed.ShouldBeTrue();
        log.Warns.ShouldContain(m => m.Contains("4321"));
    }
}
