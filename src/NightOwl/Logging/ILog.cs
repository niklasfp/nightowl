namespace NightOwl.Logging;

/// <summary>
/// Minimal logging seam. Everything goes to stderr — stdout stays reserved for a wrapped
/// command's output. Info is the default status line; Warn is for errors; Verbose only
/// appears when enabled.
/// </summary>
internal interface ILog
{
    void Info(string message);

    void Warn(string message);

    void Verbose(string message);
}

/// <summary>
/// Writes to the console. Info output is suppressed with <c>--quiet</c>; verbose output is
/// suppressed unless <c>--verbose</c>. The writer is injectable for tests only.
/// </summary>
internal sealed class ConsoleLog(bool verbose, bool quiet, TextWriter stderr) : ILog
{
    public ConsoleLog(bool verbose, bool quiet)
        : this(verbose, quiet, Console.Error)
    {
    }

    public void Info(string message)
    {
        if (!quiet)
        {
            stderr.WriteLine(message);
        }
    }

    public void Warn(string message) => stderr.WriteLine(message);

    public void Verbose(string message)
    {
        if (verbose)
        {
            stderr.WriteLine(message);
        }
    }
}
