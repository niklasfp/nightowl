namespace NightOwl.Cli;

/// <summary>Outcome of <see cref="ArgumentParser.Parse"/> — a small closed union.</summary>
internal abstract record ParseResult
{
    private ParseResult() { }

    /// <summary>Arguments parsed into runnable options.</summary>
    public sealed record Parsed(CliOptions Options) : ParseResult;

    /// <summary>User asked for <c>--help</c>.</summary>
    public sealed record ShowHelp : ParseResult;

    /// <summary>User asked for <c>--version</c>.</summary>
    public sealed record ShowVersion : ParseResult;

    /// <summary>Invalid usage; <see cref="Message"/> is shown on stderr, exit code 2.</summary>
    public sealed record Error(string Message) : ParseResult;
}
