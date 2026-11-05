namespace NightOwl.Process;

/// <summary>A process to launch: an executable plus its argument vector (already split).</summary>
internal sealed record ProcessSpec(string FileName, IReadOnlyList<string> Arguments)
{
    public ProcessSpec(string fileName, params string[] arguments)
        : this(fileName, (IReadOnlyList<string>)arguments)
    {
    }

    /// <summary>
    /// Redirect the child's stdin to a pipe held by this process. The pipe is never written to;
    /// its purpose is lifetime coupling — the kernel closes it when this process dies (any signal,
    /// including SIGKILL), so a child that reads stdin sees EOF and can exit. Off by default so
    /// wrapped commands keep the terminal's stdin.
    /// </summary>
    public bool RedirectStandardInput { get; init; }
}

/// <summary>
/// Seam over <c>System.Diagnostics.Process</c> so backends and wrap-mode are unit-testable
/// without spawning real processes.
/// </summary>
internal interface IProcessRunner
{
    IProcessHandle Start(ProcessSpec spec);
}

/// <summary>A running child process. Dispose kills it if still alive.</summary>
internal interface IProcessHandle : IAsyncDisposable
{
    /// <summary>Waits for exit and returns the exit code. Honours <paramref name="cancellationToken"/>.</summary>
    Task<int> WaitForExitAsync(CancellationToken cancellationToken);

    /// <summary>Terminates the process (and its tree) if it has not already exited.</summary>
    void Kill();
}
