using NightOwl.Logging;
using NightOwl.Os;

namespace NightOwl.PidWait;

/// <summary>Selects the platform <see cref="IPidWaiter"/> backend from <see cref="IOsInfo"/>.</summary>
internal static class PidWaiterFactory
{
    public static IPidWaiter Create(IOsInfo os, ILog log)
    {
        if (os.IsWindows)
        {
            // Constructing the backend is platform-agnostic; only its WaitForExitAsync() P/Invoke
            // is Windows-only (and annotated). The factory contract guarantees it runs there.
#pragma warning disable CA1416 // Validate platform compatibility
            return new WindowsPidWaiter(log);
#pragma warning restore CA1416
        }

        if (os.IsMacOs)
        {
            // Like Windows, only its WaitForExitAsync() P/Invoke is macOS-only (and annotated);
            // constructing it is platform-agnostic and the factory contract guarantees the OS.
#pragma warning disable CA1416 // Validate platform compatibility
            return new MacOsPidWaiter(log);
#pragma warning restore CA1416
        }

        if (os.IsLinux)
        {
            // Only its native pidfd_open/kill(2) P/Invokes are Linux-only (and annotated);
            // constructing it is platform-agnostic. The factory contract guarantees the OS.
#pragma warning disable CA1416 // Validate platform compatibility
            return new LinuxPidWaiter(log);
#pragma warning restore CA1416
        }

        throw new PidWaitException(
            "Unsupported operating system. NightOwl supports Windows, macOS, and Linux.");
    }
}
