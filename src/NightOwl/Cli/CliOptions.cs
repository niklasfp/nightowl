namespace NightOwl.Cli;

/// <summary>How long NightOwl holds the sleep inhibitor.</summary>
internal enum AwakeMode
{
    /// <summary>Keep awake until Ctrl+C (default).</summary>
    Indefinite,

    /// <summary>Keep awake for <see cref="CliOptions.Duration"/>, then exit.</summary>
    Timed,

    /// <summary>Keep awake while <see cref="CliOptions.Command"/> runs; exit with its code.</summary>
    Wrap,

    /// <summary>Keep awake while the existing process <see cref="CliOptions.Pid"/> runs; exit 0 when it does.</summary>
    WaitPid,
}

/// <summary>Validated, normalized options the runner acts on.</summary>
internal sealed record CliOptions(
    AwakeMode Mode,
    TimeSpan? Duration,
    int? Pid,
    bool KeepDisplayOn,
    bool BlockSleep,
    bool Verbose,
    bool Quiet,
    IReadOnlyList<string> Command);
