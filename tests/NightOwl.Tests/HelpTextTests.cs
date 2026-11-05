using NightOwl.Cli;
using Shouldly;

namespace NightOwl.Tests;

public class HelpTextTests
{
    [Test]
    public void Usage_mentions_every_mode_and_flag()
    {
        HelpText.Usage.ShouldContain("--for");
        HelpText.Usage.ShouldContain("--display");
        HelpText.Usage.ShouldContain("--block-sleep");
        HelpText.Usage.ShouldContain("--verbose");
        HelpText.Usage.ShouldContain("--quiet");
        HelpText.Usage.ShouldContain("-- <command>");
    }

    [Test]
    public void Version_is_a_clean_dotted_number_without_build_metadata()
    {
        var version = HelpText.Version;

        version.ShouldNotBeNullOrWhiteSpace();
        version.ShouldNotContain("+");
        version.ShouldContain(".");
    }
}
