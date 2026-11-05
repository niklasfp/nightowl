using System.Reflection;

namespace NightOwl.Cli;

/// <summary>Static usage/version text.</summary>
internal static class HelpText
{
    public const string Usage = """
        nightowl — keep the computer awake.

        USAGE:
          nightowl [options] [-- <command> [args...]]

        MODES:
          (no args)              Keep the system awake until Ctrl+C
          -t, --for <duration>   Keep awake for a duration, then exit (e.g. 30s, 90m, 2h, 1h30m)
          -w, --pid <pid>        Keep awake while process <pid> runs; exit 0 when it does
          -- <command> [args]    Keep awake while <command> runs; exit with its exit code

        OPTIONS:
          -d, --display          Also keep the display awake (not just the system)
          -s, --block-sleep      Block system sleep as strongly as the platform allows
          -v, --verbose          Log which backend/assertion is used
          -q, --quiet            Don't print the status line on start
          -h, --help             Show this help
              --version          Show version

        Notes:
          --for, --pid, and a wrapped command (-- ...) are mutually exclusive.
          --pid fails fast (no lock taken) if the PID doesn't exist; process identity is only
          checked at startup, so a PID recycled afterward is indistinguishable from the original.
          On Linux, --display is best-effort (system/idle sleep is always inhibited).
        """;

    /// <summary>The informational assembly version (e.g. "0.1.0"), trimming any build metadata.</summary>
    public static string Version
    {
        get
        {
            var info = typeof(HelpText).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;

            if (string.IsNullOrEmpty(info))
            {
                return "0.0.0";
            }

            var plus = info.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }
    }
}
