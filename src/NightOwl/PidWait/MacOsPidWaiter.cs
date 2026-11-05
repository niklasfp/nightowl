using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NightOwl.Logging;

namespace NightOwl.PidWait;

/// <summary>
/// macOS backend: kqueue <c>EVFILT_PROC | NOTE_EXIT</c> (the same kernel facility Apple's own
/// <c>caffeinate -w</c> uses via libdispatch). Registering the filter on a PID that does not
/// exist fails immediately with <c>ESRCH</c> — this closes the dead-PID race that makes
/// <c>caffeinate -w &lt;dead-pid&gt;</c> hang forever. A periodic <c>EVFILT_TIMER</c> is
/// registered alongside it purely so the wait loop can observe cancellation without leaving the
/// kqueue fd shared across threads.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed partial class MacOsPidWaiter(ILog log) : IPidWaiter
{
    private const string Libc = "libc";

    // From <sys/event.h>: filters, flags, and the NOTE_EXIT fflag. Values are identical on
    // arm64/x86_64 macOS; the struct layout below matches the same header.
    private const short EvfiltProc = -5;
    private const short EvfiltTimer = -7;
    private const ushort EvAdd = 0x0001;
    private const ushort EvOneshot = 0x0010;
    private const ushort EvError = 0x4000;
    private const uint NoteExit = 0x80000000;

    private const nuint TimerIdent = 1;
    private const int PollIntervalMs = 100;

    // errno for "Interrupted system call" — identical on Linux and macOS.
    private const int Eintr = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct KEvent
    {
        public nuint Ident;
        public short Filter;
        public ushort Flags;
        public uint Fflags;
        public nint Data;
        public nint Udata;
    }

    [LibraryImport(Libc, SetLastError = true)]
    private static partial int kqueue();

    [LibraryImport(Libc, SetLastError = true)]
    private static unsafe partial int kevent(
        int kq, KEvent* changelist, int nchanges, KEvent* eventlist, int nevents, IntPtr timeout);

    [LibraryImport(Libc, SetLastError = true)]
    private static partial int close(int fd);

    public unsafe Task WaitForExitAsync(int pid, CancellationToken cancellationToken)
    {
        var kq = kqueue();
        if (kq < 0)
        {
            throw new PidWaitException($"kqueue() failed (error {Marshal.GetLastPInvokeError()}).");
        }

        log.Verbose($"macOS backend: kqueue EVFILT_PROC|NOTE_EXIT on PID {pid}.");

        bool exited;
        try
        {
            // Registers both filters and blocks until the first tick or an error. A registration
            // failure (dead/inaccessible PID) is reported as an EV_ERROR event here, near
            // instantly — before this method returns any Task — so a bad PID throws synchronously.
            exited = PollFirst(kq, pid);
        }
        catch
        {
            close(kq);
            throw;
        }

        if (exited)
        {
            close(kq);
            return Task.CompletedTask;
        }

        return PollRemainingAsync(kq, cancellationToken);
    }

    private static unsafe bool PollFirst(int kq, int pid)
    {
        var changes = stackalloc KEvent[2];
        changes[0] = new KEvent { Ident = (nuint)pid, Filter = EvfiltProc, Flags = EvAdd | EvOneshot, Fflags = NoteExit };
        changes[1] = new KEvent { Ident = TimerIdent, Filter = EvfiltTimer, Flags = EvAdd, Data = PollIntervalMs };

        return PollCommon(kq, changes, 2);
    }

    private static unsafe bool PollOnce(int kq) => PollCommon(kq, null, 0);

    private static unsafe bool PollCommon(int kq, KEvent* changes, int nchanges)
    {
        var events = stackalloc KEvent[2];
        var n = kevent(kq, changes, nchanges, events, 2, IntPtr.Zero);
        if (n < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            if (errno == Eintr)
            {
                // A signal (e.g. SIGINT/Ctrl+C) interrupted the blocked kevent() -- routine, not a
                // failure. The changelist (if any) was already applied, so report "no event yet"
                // and let the caller re-check cancellation and poll again.
                return false;
            }

            throw new PidWaitException($"kevent() failed (error {errno}).");
        }

        for (var i = 0; i < n; i++)
        {
            if (events[i].Filter == EvfiltProc)
            {
                if ((events[i].Flags & EvError) != 0)
                {
                    throw new PidWaitException($"Process not found (error {(int)events[i].Data}).");
                }

                return true;
            }
        }

        return false; // only the timer ticked; the target is still running
    }

    private static async Task PollRemainingAsync(int kq, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var exited = await Task.Run(() => PollOnce(kq), CancellationToken.None).ConfigureAwait(false);
                if (exited)
                {
                    return;
                }
            }
        }
        finally
        {
            close(kq);
        }
    }
}
