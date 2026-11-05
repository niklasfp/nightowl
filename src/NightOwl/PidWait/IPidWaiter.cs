namespace NightOwl.PidWait;

/// <summary>
/// Seam over the platform "wait for a non-child PID to exit" mechanism. Deliberately not
/// <c>System.Diagnostics.Process</c>: its async Windows path demands <c>PROCESS_ALL_ACCESS</c>
/// (fails against elevated targets) and its Unix non-child wait is a <c>kill(pid, 0)</c> polling
/// loop with an acknowledged PID-reuse race.
/// </summary>
internal interface IPidWaiter
{
    /// <summary>
    /// Completes when <paramref name="pid"/> exits. Registration happens synchronously before
    /// any awaiting is needed, so a PID that does not exist / cannot be watched throws
    /// <see cref="PidWaitException"/> from this call rather than from the returned task —
    /// callers can rely on that to fail fast before taking any other action (e.g. acquiring the
    /// sleep inhibitor). Honours <paramref name="cancellationToken"/>.
    /// </summary>
    Task WaitForExitAsync(int pid, CancellationToken cancellationToken);
}

/// <summary>A user-facing failure while registering or waiting on a PID.</summary>
internal sealed class PidWaitException(string message, Exception? innerException = null)
    : Exception(message, innerException);
