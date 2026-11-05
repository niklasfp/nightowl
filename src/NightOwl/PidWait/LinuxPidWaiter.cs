using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NightOwl.Logging;

namespace NightOwl.PidWait;

/// <summary>
/// Linux backend: <c>pidfd_open(2)</c> (kernel >= 5.3) + <c>poll(2)</c> on the returned fd, which
/// becomes readable when the process exits. Reuse-immune once opened, and a nonexistent PID
/// fails at open time with <c>ESRCH</c>. Where <c>pidfd_open</c> is unavailable (older kernel,
/// seccomp), falls back to a pure <c>kill(pid, 0)</c> polling loop — the same non-child liveness
/// probe dotnet/runtime uses — via <see cref="KillProbe"/>.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed partial class LinuxPidWaiter(ILog log) : IPidWaiter
{
    private const string Libc = "libc";

    // SYS_pidfd_open: uniform 434 across architectures for post-5.0 syscalls (verified against
    // asm-generic/unistd.h). The glibc wrapper is only available since glibc 2.36, so this goes
    // straight through the raw syscall(2) for portability.
    private const int SysPidfdOpen = 434;
    private const short PollIn = 0x0001;
    private const int PollIntervalMs = 100;

    // errno for "Interrupted system call" — identical on Linux and macOS.
    private const int Eintr = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    [LibraryImport(Libc, SetLastError = true)]
    private static partial nint syscall(nint number, int pid, uint flags);

    [LibraryImport(Libc, SetLastError = true)]
    private static unsafe partial int poll(PollFd* fds, uint nfds, int timeout);

    [LibraryImport(Libc, SetLastError = true)]
    private static partial int close(int fd);

    [LibraryImport(Libc, SetLastError = true)]
    private static partial int kill(int pid, int sig);

    public Task WaitForExitAsync(int pid, CancellationToken cancellationToken)
    {
        // Synchronous and effectively instant: pidfd_open either opens a watchable fd or fails
        // with ESRCH for a nonexistent PID, so a bad PID throws before any Task is returned.
        var fd = (int)syscall(SysPidfdOpen, pid, 0);
        if (fd >= 0)
        {
            log.Verbose($"Linux backend: pidfd_open on PID {pid}.");
            return PollFdAsync(fd, cancellationToken);
        }

        var errno = Marshal.GetLastPInvokeError();
        if (errno == KillProbe.Esrch)
        {
            throw new PidWaitException($"Process {pid} not found (error {errno}).");
        }

        // pidfd_open unavailable for a reason other than "gone" (ENOSYS on kernel < 5.3, EINVAL
        // under seccomp, ...) — fall back to polling kill(pid, 0). Still validate synchronously
        // up front so a nonexistent PID fails fast here too.
        log.Verbose($"Linux backend: pidfd_open unavailable (error {errno}); falling back to kill(2) polling.");

        var probe = kill(pid, 0);
        var probeErrno = probe == 0 ? 0 : Marshal.GetLastPInvokeError();
        if (KillProbe.Interpret(probe, probeErrno) == KillProbe.Result.Exited)
        {
            throw new PidWaitException($"Process {pid} not found (error {probeErrno}).");
        }

        return PollKillAsync(pid, cancellationToken);
    }

    private static async Task PollFdAsync(int fd, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ready = await Task.Run(() => PollOnce(fd), CancellationToken.None).ConfigureAwait(false);
                if (ready)
                {
                    return;
                }
            }
        }
        finally
        {
            close(fd);
        }
    }

    private static unsafe bool PollOnce(int fd)
    {
        var pfd = new PollFd { Fd = fd, Events = PollIn };
        var n = poll(&pfd, 1, PollIntervalMs);
        if (n < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            if (errno == Eintr)
            {
                // A signal (e.g. SIGINT/Ctrl+C) interrupted the blocked poll() -- routine, not a
                // failure. Report "not ready" so the loop re-checks cancellation and polls again.
                return false;
            }

            throw new PidWaitException($"poll() failed (error {errno}).");
        }

        return n > 0;
    }

    private static async Task PollKillAsync(int pid, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = kill(pid, 0);
            var errno = result == 0 ? 0 : Marshal.GetLastPInvokeError();
            if (KillProbe.Interpret(result, errno) == KillProbe.Result.Exited)
            {
                return;
            }

            await Task.Delay(PollIntervalMs, cancellationToken).ConfigureAwait(false);
        }
    }
}
