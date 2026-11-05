using NightOwl.Logging;
using NightOwl.Os;
using NightOwl.Process;

namespace NightOwl.Inhibitors;

/// <summary>Selects the platform backend from <see cref="IOsInfo"/>.</summary>
internal static class InhibitorFactory
{
    public static ISleepInhibitor Create(IOsInfo os, IProcessRunner runner, ILog log)
    {
        if (os.IsWindows)
        {
            // Constructing the backend is platform-agnostic; only its Acquire() P/Invoke is
            // Windows-only (and annotated). The factory contract guarantees it runs there.
#pragma warning disable CA1416 // Validate platform compatibility
            return new WindowsSleepInhibitor(log);
#pragma warning restore CA1416
        }

        if (os.IsMacOs)
        {
            // Like the Windows backend, only its Acquire() P/Invoke is macOS-only (and annotated);
            // constructing it is platform-agnostic and the factory contract guarantees the OS.
#pragma warning disable CA1416 // Validate platform compatibility
            return new MacOsSleepInhibitor(log);
#pragma warning restore CA1416
        }

        if (os.IsLinux)
        {
            // Only its native sd-bus P/Invoke is Linux-only (and annotated); constructing it — and
            // its systemd-inhibit fallback — is platform-agnostic. The factory guarantees the OS.
#pragma warning disable CA1416 // Validate platform compatibility
            return new LinuxSleepInhibitor(runner, log);
#pragma warning restore CA1416
        }

        throw new InhibitorException(
            "Unsupported operating system. NightOwl supports Windows, macOS, and Linux.");
    }
}
