using NightOwl.Process;

namespace NightOwl.Inhibitors;

/// <summary>
/// A hold backed by a helper process (the Linux <c>systemd-inhibit</c> backend). Disposing it
/// kills the child, releasing the lock.
/// </summary>
internal sealed class ProcessHold(IProcessHandle handle) : IAsyncDisposable
{
    /// <summary>
    /// Wraps <paramref name="handle"/> as a hold, but first confirms the child survives
    /// <paramref name="gracePeriod"/>. A <c>systemd-inhibit</c> that cannot take the lock (e.g.
    /// polkit denies sleep inhibitors for a non-active session, such as over SSH) prints an error
    /// and exits at once — without this check that dead child would masquerade as a valid hold
    /// while nothing is inhibited.
    /// </summary>
    /// <exception cref="InhibitorException">The child exited within the grace period.</exception>
    public static ProcessHold StartVerified(IProcessHandle handle, TimeSpan gracePeriod)
    {
        var exit = handle.WaitForExitAsync(CancellationToken.None);

        bool exited;
        try
        {
            exited = exit.Wait(gracePeriod);
        }
        catch (AggregateException)
        {
            // The wait itself failed — the child is unobservable; treat it like an early death.
            exited = true;
        }

        if (!exited)
        {
            return new ProcessHold(handle);
        }

        var code = exit.Status == TaskStatus.RanToCompletion ? $"exit code {exit.Result}" : "unknown exit";
        handle.DisposeAsync().AsTask().GetAwaiter().GetResult();
        throw new InhibitorException(
            $"'systemd-inhibit' exited immediately ({code}) instead of holding the lock, so nothing "
            + "is inhibited. Commonly polkit denies sleep inhibitors for non-active sessions "
            + "(e.g. over SSH); see the error it printed above.");
    }

    public ValueTask DisposeAsync() => handle.DisposeAsync();
}
