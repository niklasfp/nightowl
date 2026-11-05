namespace NightOwl.PidWait;

/// <summary>
/// Interprets a POSIX <c>kill(pid, 0)</c> liveness probe — the same non-child check
/// dotnet/runtime's process-wait fallback uses. Kept separate from <see cref="LinuxPidWaiter"/>
/// (which is <c>[SupportedOSPlatform("linux")]</c>) so the fallback's decision logic is pure and
/// unit-testable on any OS, mirroring <c>SystemdInhibitSpec</c>.
/// </summary>
internal static class KillProbe
{
    /// <summary><c>errno</c> value for "No such process" — identical across Linux and macOS.</summary>
    public const int Esrch = 3;

    public enum Result
    {
        /// <summary>The process exists (or existence could not be disproved).</summary>
        Alive,

        /// <summary>The process is gone.</summary>
        Exited,
    }

    /// <summary>
    /// <c>0</c> means the process exists and is signalable. <c>ESRCH</c> means it is gone. Any
    /// other errno (notably <c>EPERM</c> — exists but not signalable by us) is conservatively
    /// treated as still alive, matching dotnet/runtime's non-child wait: a permission loss can't
    /// be told apart from continued liveness, so it must not be misreported as exit.
    /// </summary>
    public static Result Interpret(int killReturnValue, int errno) =>
        killReturnValue == 0 ? Result.Alive
        : errno == Esrch ? Result.Exited
        : Result.Alive;
}
