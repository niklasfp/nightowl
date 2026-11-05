namespace NightOwl.Inhibitors;

/// <summary>
/// A platform backend that prevents the system from sleeping. <see cref="Acquire"/> takes a
/// hold; disposing the returned handle releases it.
/// </summary>
internal interface ISleepInhibitor
{
    /// <param name="keepDisplayOn">Also keep the display awake, not just the system.</param>
    /// <param name="blockSleep">
    /// Hold the strongest system-sleep block the platform offers (not just idle-sleep
    /// prevention), best-effort — never throws on its own account.
    /// </param>
    /// <returns>A hold; dispose to release the sleep assertion.</returns>
    IAsyncDisposable Acquire(bool keepDisplayOn, bool blockSleep);
}
