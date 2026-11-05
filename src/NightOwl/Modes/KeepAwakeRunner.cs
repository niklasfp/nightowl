using NightOwl.Cli;
using NightOwl.Inhibitors;
using NightOwl.Logging;
using NightOwl.PidWait;
using NightOwl.Process;

namespace NightOwl.Modes;

/// <summary>
/// Platform-agnostic mode/timing orchestration. Acquires a hold, runs the chosen mode until it
/// completes or is cancelled, then releases the hold in a <c>finally</c> (via <c>await using</c>).
/// </summary>
internal sealed class KeepAwakeRunner(ISleepInhibitor inhibitor, IProcessRunner runner, IPidWaiter pidWaiter, ILog log)
{
    // Conventional exit codes.
    private const int Ok = 0;
    private const int Interrupted = 130;   // 128 + SIGINT
    private const int CommandNotRun = 127;
    private const int PidWaitFailed = 1;   // PID not found at startup, or lost track of it later

    public async Task<int> RunAsync(CliOptions options, CancellationToken cancellationToken)
    {
        // WaitPid must confirm the PID before ever taking the inhibitor (fail fast, no lock
        // taken), so it acquires the hold itself instead of sharing the other modes' prelude.
        if (options.Mode == AwakeMode.WaitPid)
        {
            return await RunWaitPidAsync(options, cancellationToken);
        }

        await using var hold = inhibitor.Acquire(options.KeepDisplayOn, options.BlockSleep);

        // The status line names what is being kept awake, so chosen options are visible at a glance.
        // It stays generic and identical across platforms; per-platform caveats about -s/--block-sleep
        // only appear under --verbose, logged by the backend that knows about them.
        var subject = options.KeepDisplayOn ? "system and display" : "system";
        var blockNote = options.BlockSleep ? ", blocking system sleep" : "";

        return options.Mode switch
        {
            AwakeMode.Indefinite => await RunIndefiniteAsync(subject, blockNote, cancellationToken),
            AwakeMode.Timed => await RunTimedAsync(subject, blockNote, options.Duration!.Value, cancellationToken),
            AwakeMode.Wrap => await RunWrapAsync(subject, blockNote, options.Command, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(options), options.Mode, "Unknown mode."),
        };
    }

    private async Task<int> RunIndefiniteAsync(string subject, string blockNote, CancellationToken cancellationToken)
    {
        log.Info($"NightOwl: keeping the {subject} awake{blockNote} until interrupted (Ctrl+C).");
        await WaitForCancellationAsync(cancellationToken);
        return Ok;
    }

    private async Task<int> RunTimedAsync(string subject, string blockNote, TimeSpan duration, CancellationToken cancellationToken)
    {
        log.Info($"NightOwl: keeping the {subject} awake{blockNote} for {duration}.");
        try
        {
            await Task.Delay(duration, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Interrupted before the timer elapsed — still a clean stop.
        }

        return Ok;
    }

    private async Task<int> RunWrapAsync(string subject, string blockNote, IReadOnlyList<string> command, CancellationToken cancellationToken)
    {
        var fileName = command[0];
        var arguments = command.Skip(1).ToArray();
        log.Info($"NightOwl: keeping the {subject} awake{blockNote} while running: {string.Join(' ', command)}");

        IProcessHandle child;
        try
        {
            child = runner.Start(new ProcessSpec(fileName, arguments));
        }
        catch (Exception ex)
        {
            log.Warn($"Could not start '{fileName}': {ex.Message}");
            return CommandNotRun;
        }

        await using (child)
        {
            try
            {
                return await child.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Interrupted: stop the wrapped command and report an interrupted exit.
                child.Kill();
                return Interrupted;
            }
        }
    }

    private async Task<int> RunWaitPidAsync(CliOptions options, CancellationToken cancellationToken)
    {
        var pid = options.Pid!.Value;

        Task waitTask;
        try
        {
            // Registration happens synchronously inside this call (see IPidWaiter), so a PID that
            // doesn't exist/can't be watched throws right here — before the inhibitor is touched.
            waitTask = pidWaiter.WaitForExitAsync(pid, cancellationToken);
        }
        catch (PidWaitException ex)
        {
            log.Warn($"Could not wait for PID {pid}: {ex.Message}");
            return PidWaitFailed;
        }

        await using var hold = inhibitor.Acquire(options.KeepDisplayOn, options.BlockSleep);
        var subject = options.KeepDisplayOn ? "system and display" : "system";
        var blockNote = options.BlockSleep ? ", blocking system sleep" : "";
        log.Info($"NightOwl: keeping the {subject} awake{blockNote} while PID {pid} runs.");

        try
        {
            await waitTask;
            return Ok;
        }
        catch (OperationCanceledException)
        {
            return Interrupted;
        }
        catch (PidWaitException ex)
        {
            // The wait registered fine but failed later (e.g. a poll()/kevent() error) -- the
            // `await using` above still releases the hold on the way out.
            log.Warn($"Lost track of PID {pid}: {ex.Message}");
            return PidWaitFailed;
        }
    }

    private static async Task WaitForCancellationAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
