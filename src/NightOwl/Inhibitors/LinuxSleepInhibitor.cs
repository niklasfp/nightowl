using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NightOwl.Logging;
using NightOwl.Process;

namespace NightOwl.Inhibitors;

/// <summary>
/// Linux backend: takes a logind sleep inhibitor natively over the system bus via libsystemd
/// (<c>org.freedesktop.login1.Manager.Inhibit("sleep:idle", …, "block")</c>) and holds the
/// returned file descriptor — closing it releases the lock, with no child process. If libsystemd
/// or the system bus is unavailable, it falls back to spawning <c>systemd-inhibit … cat</c> with
/// stdin piped from this process, so the child exits (releasing the lock) whenever NightOwl dies,
/// even on SIGKILL. Keeping the *display* awake is best-effort on Linux (X11/Wayland/DBus
/// divergence), so <c>--display</c> only logs a note and still inhibits system/idle sleep.
/// <c>-s/--block-sleep</c> extends the lock to also cover lid-close suspend
/// (<c>sleep:idle:handle-lid-switch</c>), degrading it one step at a time on polkit denial —
/// <c>sleep:idle:handle-lid-switch</c> → <c>sleep:idle</c> → <c>idle</c> — since
/// <c>inhibit-handle-lid-switch</c> is denied outright (no prompt) outside a local session (e.g.
/// SSH). Each downgrade is reported only under <c>--verbose</c>.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed partial class LinuxSleepInhibitor(IProcessRunner runner, ILog log) : ISleepInhibitor
{
    private const string Systemd = "libsystemd.so.0";
    private const string Libc = "libc";

    // F_DUPFD_CLOEXEC (Linux, architecture-independent): duplicate an fd with close-on-exec set.
    private const int FDupFdCloexec = 1030;
    private const byte DbusString = (byte)'s';
    private const byte DbusUnixFd = (byte)'h';

    /// <summary>Baseline lock: blocks idle sleep and logind-routed suspend/hibernate.</summary>
    private const string BaselineWhat = "sleep:idle";

    /// <summary>
    /// Degradation ladder tried, in order, when <c>-s/--block-sleep</c> is requested: the
    /// strongest lock first, falling back one step at a time if logind's polkit policy denies it
    /// (never interactive — a denial is a clean error, not a prompt).
    /// </summary>
    private static readonly string[] BlockSleepWhatLadder =
    [
        "sleep:idle:handle-lid-switch",
        "sleep:idle",
        "idle",
    ];

    [LibraryImport(Systemd)]
    private static partial int sd_bus_open_system(out IntPtr bus);

    [LibraryImport(Systemd, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int sd_bus_message_new_method_call(
        IntPtr bus, out IntPtr m, string destination, string path, string iface, string member);

    [LibraryImport(Systemd, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int sd_bus_message_append_basic(IntPtr m, byte type, string value);

    [LibraryImport(Systemd)]
    private static partial int sd_bus_call(IntPtr bus, IntPtr m, ulong usec, IntPtr retError, out IntPtr reply);

    [LibraryImport(Systemd)]
    private static partial int sd_bus_message_read_basic(IntPtr m, byte type, out int value);

    [LibraryImport(Systemd)]
    private static partial IntPtr sd_bus_message_unref(IntPtr m);

    [LibraryImport(Systemd)]
    private static partial IntPtr sd_bus_unref(IntPtr bus);

    [LibraryImport(Libc, SetLastError = true)]
    private static partial int fcntl(int fd, int cmd, int arg);

    [LibraryImport(Libc, SetLastError = true)]
    private static partial int close(int fd);

    public IAsyncDisposable Acquire(bool keepDisplayOn, bool blockSleep)
    {
        if (keepDisplayOn)
        {
            log.Verbose("Linux: --display is best-effort; inhibiting system/idle sleep only.");
        }

        try
        {
            return AcquireNative(blockSleep);
        }
        catch (Exception ex)
        {
            log.Verbose(
                $"Linux: native libsystemd inhibitor unavailable ({ex.Message}); falling back to systemd-inhibit.");
            return AcquireViaSystemdInhibit(blockSleep);
        }
    }

    /// <summary>
    /// Native path: hold a logind inhibitor fd obtained through libsystemd's sd-bus. When
    /// <paramref name="blockSleep"/> is set, walks <see cref="BlockSleepWhatLadder"/> — the
    /// strongest lock first — degrading one step on each polkit denial.
    /// </summary>
    private IAsyncDisposable AcquireNative(bool blockSleep)
    {
        string[] whatCandidates = blockSleep ? BlockSleepWhatLadder : [BaselineWhat];

        Check(sd_bus_open_system(out var bus), "sd_bus_open_system");
        try
        {
            for (var i = 0; i < whatCandidates.Length; i++)
            {
                var isLastCandidate = i == whatCandidates.Length - 1;
                try
                {
                    return AcquireWithWhat(bus, whatCandidates[i]);
                }
                catch (InhibitorException) when (!isLastCandidate)
                {
                    log.Verbose(
                        $"Linux: logind denied inhibitor lock '{whatCandidates[i]}'; "
                        + $"degrading to '{whatCandidates[i + 1]}'.");
                }
            }

            // Unreachable: the loop above always either returns or, on the last candidate,
            // lets the InhibitorException propagate instead of being caught.
            throw new InhibitorException("logind Inhibit failed for every candidate lock.");
        }
        finally
        {
            sd_bus_unref(bus);
        }
    }

    /// <summary>Makes one <c>Inhibit(what, "nightowl", "user requested", "block")</c> D-Bus call.</summary>
    private IAsyncDisposable AcquireWithWhat(IntPtr bus, string what)
    {
        Check(
            sd_bus_message_new_method_call(
                bus,
                out var msg,
                "org.freedesktop.login1",
                "/org/freedesktop/login1",
                "org.freedesktop.login1.Manager",
                "Inhibit"),
            "sd_bus_message_new_method_call");

        var reply = IntPtr.Zero;
        try
        {
            Check(sd_bus_message_append_basic(msg, DbusString, what), "append what");
            Check(sd_bus_message_append_basic(msg, DbusString, "nightowl"), "append who");
            Check(sd_bus_message_append_basic(msg, DbusString, "user requested"), "append why");
            Check(sd_bus_message_append_basic(msg, DbusString, "block"), "append mode");

            Check(sd_bus_call(bus, msg, 0, IntPtr.Zero, out reply), "sd_bus_call Inhibit");
            Check(sd_bus_message_read_basic(reply, DbusUnixFd, out var fd), "sd_bus_message_read_basic");

            // The fd is owned by the reply message (closed on unref), so duplicate our own copy
            // with close-on-exec set so wrapped child commands don't inherit (and prolong) the lock.
            var held = fcntl(fd, FDupFdCloexec, 3);
            if (held < 0)
            {
                throw new InhibitorException("Could not duplicate the logind inhibitor fd.");
            }

            log.Verbose($"Linux backend: holding logind inhibitor fd (native libsystemd, what={what}).");
            return new FdHold(held);
        }
        finally
        {
            if (reply != IntPtr.Zero)
            {
                sd_bus_message_unref(reply);
            }

            sd_bus_message_unref(msg);
        }
    }

    /// <summary>
    /// How long the fallback child must survive before its hold is trusted. A denied
    /// <c>systemd-inhibit</c> exits within milliseconds; a healthy one blocks on stdin forever.
    /// </summary>
    private static readonly TimeSpan FallbackStartupGracePeriod = TimeSpan.FromMilliseconds(500);

    /// <summary>Fallback: hold the lock by spawning <c>systemd-inhibit … cat</c>.</summary>
    private IAsyncDisposable AcquireViaSystemdInhibit(bool blockSleep)
    {
        var spec = SystemdInhibitSpec.Build(blockSleep);
        log.Verbose($"Linux backend: {spec.FileName} {string.Join(' ', spec.Arguments)}");

        IProcessHandle handle;
        try
        {
            handle = runner.Start(spec);
        }
        catch (Exception ex)
        {
            throw new InhibitorException(
                "Could not inhibit sleep via libsystemd or 'systemd-inhibit'. "
                + "Is this a systemd Linux with D-Bus/logind available?",
                ex);
        }

        return ProcessHold.StartVerified(handle, FallbackStartupGracePeriod);
    }

    private static void Check(int result, string what)
    {
        if (result < 0)
        {
            throw new InhibitorException($"libsystemd {what} failed (errno {-result}).");
        }
    }

    /// <summary>Holds the logind inhibitor file descriptor; closing it releases the lock.</summary>
    private sealed class FdHold(int fd) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            close(fd);
            return ValueTask.CompletedTask;
        }
    }
}
