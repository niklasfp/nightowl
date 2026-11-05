using NightOwl.Inhibitors;
using NightOwl.Logging;
using NightOwl.Os;
using NightOwl.PidWait;
using NightOwl.Process;

namespace NightOwl.Tests;

/// <summary>Hands back a hold that records its disposal; remembers the display request.</summary>
internal sealed class FakeSleepInhibitor : ISleepInhibitor
{
    public bool? DisplayRequested { get; private set; }

    public bool? BlockSleepRequested { get; private set; }

    public FakeHold Hold { get; } = new();

    public IAsyncDisposable Acquire(bool keepDisplayOn, bool blockSleep)
    {
        DisplayRequested = keepDisplayOn;
        BlockSleepRequested = blockSleep;
        return Hold;
    }
}

/// <summary>A hold that records whether it was released.</summary>
internal sealed class FakeHold : IAsyncDisposable
{
    public bool Disposed { get; private set; }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Records every spawn and hands back a controllable <see cref="FakeProcessHandle"/>.</summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    private readonly Exception? _throwOnStart;
    private readonly int? _exitImmediatelyWith;

    public FakeProcessRunner(Exception? throwOnStart = null, int? exitImmediatelyWith = null)
    {
        _throwOnStart = throwOnStart;
        _exitImmediatelyWith = exitImmediatelyWith;
    }

    public List<ProcessSpec> StartedSpecs { get; } = [];

    public FakeProcessHandle LastHandle { get; private set; } = new();

    public ProcessSpec LastSpec => StartedSpecs[^1];

    public IProcessHandle Start(ProcessSpec spec)
    {
        StartedSpecs.Add(spec);

        if (_throwOnStart is not null)
        {
            throw _throwOnStart;
        }

        return LastHandle = new FakeProcessHandle(_exitImmediatelyWith);
    }
}

/// <summary>
/// A process whose exit can be driven by the test, recording Kill/Dispose. Constructing it with
/// <paramref name="exitImmediatelyWith"/> models a child that dies right after starting (e.g.
/// systemd-inhibit refused by polkit).
/// </summary>
internal sealed class FakeProcessHandle(int? exitImmediatelyWith = null) : IProcessHandle
{
    private readonly TaskCompletionSource<int> _exit = exitImmediatelyWith is int code
        ? CreateCompleted(code)
        : new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<int> CreateCompleted(int exitCode)
    {
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult(exitCode);
        return tcs;
    }

    public bool Killed { get; private set; }

    public bool Disposed { get; private set; }

    public void CompleteWith(int exitCode) => _exit.TrySetResult(exitCode);

    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        using var reg = cancellationToken.Register(() => _exit.TrySetCanceled(cancellationToken));
        return await _exit.Task.ConfigureAwait(false);
    }

    public void Kill()
    {
        Killed = true;
        _exit.TrySetResult(137);
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        Kill();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// A PID wait whose exit can be driven by the test, mirroring <see cref="FakeProcessHandle"/>.
/// Passing an exception to the constructor makes <see cref="WaitForExitAsync"/> throw it
/// synchronously (before returning a task) — modeling the native waiters' fail-fast registration.
/// </summary>
internal sealed class FakePidWaiter : IPidWaiter
{
    private readonly Exception? _throwOnStart;

    private readonly TaskCompletionSource _exit =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakePidWaiter(Exception? throwOnStart = null) => _throwOnStart = throwOnStart;

    public int? LastPid { get; private set; }

    public void CompleteExit() => _exit.TrySetResult();

    /// <summary>Faults the in-flight wait, modeling a native wait that fails *after* registering
    /// successfully (e.g. a later poll()/kevent() error) rather than at the fail-fast check.</summary>
    public void FailExit(Exception ex) => _exit.TrySetException(ex);

    public Task WaitForExitAsync(int pid, CancellationToken cancellationToken)
    {
        LastPid = pid;

        if (_throwOnStart is not null)
        {
            throw _throwOnStart;
        }

        return WaitAsync(cancellationToken);
    }

    private async Task WaitAsync(CancellationToken cancellationToken)
    {
        using var reg = cancellationToken.Register(() => _exit.TrySetCanceled(cancellationToken));
        await _exit.Task.ConfigureAwait(false);
    }
}

/// <summary>An <see cref="IOsInfo"/> with explicitly set platform flags.</summary>
internal sealed class FakeOsInfo(bool windows = false, bool macOs = false, bool linux = false)
    : IOsInfo
{
    public bool IsWindows { get; } = windows;

    public bool IsMacOs { get; } = macOs;

    public bool IsLinux { get; } = linux;
}

/// <summary>Captures log lines per level for assertions.</summary>
internal sealed class FakeLog : ILog
{
    public List<string> Infos { get; } = [];

    public List<string> Warns { get; } = [];

    public List<string> Verboses { get; } = [];

    public void Info(string message) => Infos.Add(message);

    public void Warn(string message) => Warns.Add(message);

    public void Verbose(string message) => Verboses.Add(message);
}
