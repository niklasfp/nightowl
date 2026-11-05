using NightOwl.Process;

namespace NightOwl.Inhibitors;

/// <summary>
/// Builds the <c>systemd-inhibit</c> command used by the Linux fallback path. Kept separate from
/// <see cref="LinuxSleepInhibitor"/> (which is <c>[SupportedOSPlatform("linux")]</c>) so the
/// command construction stays platform-agnostic and unit-testable on any OS.
/// </summary>
internal static class SystemdInhibitSpec
{
    /// <summary>Baseline lock: blocks idle sleep and logind-routed suspend/hibernate.</summary>
    private const string BaselineWhat = "sleep:idle";

    /// <summary>
    /// <c>-s/--block-sleep</c> lock: also covers lid-close suspend, which a plain <c>sleep</c>
    /// lock does not stop under stock logind config (<c>LidSwitchIgnoreInhibited=yes</c>).
    /// </summary>
    private const string BlockSleepWhat = "sleep:idle:handle-lid-switch";

    /// <remarks>
    /// The held command is <c>cat</c> reading a stdin pipe from nightowl rather than
    /// <c>sleep infinity</c>: when nightowl dies — cleanly or via SIGKILL, where no disposer runs —
    /// the kernel closes the pipe, <c>cat</c> sees EOF and exits, and <c>systemd-inhibit</c>
    /// releases the lock. A free-running <c>sleep infinity</c> would be orphaned by a hard kill
    /// and hold the inhibitor forever. This mirrors the fd-lifetime semantics of the native path.
    /// </remarks>
    public static ProcessSpec Build(bool blockSleep = false) => new ProcessSpec(
        "systemd-inhibit",
        $"--what={(blockSleep ? BlockSleepWhat : BaselineWhat)}",
        "--mode=block",
        "--who=nightowl",
        "--why=user requested",
        "cat")
    {
        RedirectStandardInput = true,
    };
}
