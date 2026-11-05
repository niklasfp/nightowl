using NightOwl.Logging;
using Shouldly;

namespace NightOwl.Tests;

public class ConsoleLogTests
{
    private static string CaptureStderr(bool verbose, bool quiet, Action<ConsoleLog> act)
    {
        using var stderr = new StringWriter();
        act(new ConsoleLog(verbose, quiet, stderr));
        return stderr.ToString();
    }

    [Test]
    public void Info_writes_the_status_line_by_default()
    {
        CaptureStderr(verbose: false, quiet: false, l => l.Info("hello"))
            .ShouldContain("hello");
    }

    [Test]
    public void Quiet_suppresses_info()
    {
        CaptureStderr(verbose: false, quiet: true, l => l.Info("hello"))
            .ShouldBeEmpty();
    }

    [Test]
    public void Quiet_does_not_suppress_warnings()
    {
        CaptureStderr(verbose: false, quiet: true, l => l.Warn("uh oh"))
            .ShouldContain("uh oh");
    }

    [Test]
    public void Verbose_lines_only_appear_with_verbose_enabled()
    {
        CaptureStderr(verbose: false, quiet: false, l => l.Verbose("detail"))
            .ShouldBeEmpty();
        CaptureStderr(verbose: true, quiet: false, l => l.Verbose("detail"))
            .ShouldContain("detail");
    }
}
