using NightOwl.Cli;
using Shouldly;

namespace NightOwl.Tests;

public class ArgumentParserTests
{
    private static CliOptions Parse(params string[] args)
    {
        var result = ArgumentParser.Parse(args);
        return result.ShouldBeOfType<ParseResult.Parsed>().Options;
    }

    [Test]
    public void No_args_is_indefinite_mode()
    {
        var opts = Parse();
        opts.Mode.ShouldBe(AwakeMode.Indefinite);
        opts.Duration.ShouldBeNull();
        opts.Pid.ShouldBeNull();
        opts.KeepDisplayOn.ShouldBeFalse();
        opts.BlockSleep.ShouldBeFalse();
        opts.Verbose.ShouldBeFalse();
        opts.Command.ShouldBeEmpty();
    }

    [Test]
    [Arguments("-d")]
    [Arguments("--display")]
    public void Display_flag_sets_keep_display_on(string flag)
    {
        Parse(flag).KeepDisplayOn.ShouldBeTrue();
    }

    [Test]
    [Arguments("-v")]
    [Arguments("--verbose")]
    public void Verbose_flag_sets_verbose(string flag)
    {
        Parse(flag).Verbose.ShouldBeTrue();
    }

    [Test]
    [Arguments("-s")]
    [Arguments("--block-sleep")]
    public void Block_sleep_flag_sets_block_sleep(string flag)
    {
        Parse(flag).BlockSleep.ShouldBeTrue();
    }

    [Test]
    public void Block_sleep_is_off_by_default()
    {
        Parse().BlockSleep.ShouldBeFalse();
    }

    [Test]
    public void Block_sleep_composes_with_display_for_and_wrap()
    {
        var opts = Parse("--block-sleep", "--display", "--for", "5s");
        opts.BlockSleep.ShouldBeTrue();
        opts.KeepDisplayOn.ShouldBeTrue();
        opts.Mode.ShouldBe(AwakeMode.Timed);

        var wrapped = Parse("-s", "--", "sleep", "1");
        wrapped.BlockSleep.ShouldBeTrue();
        wrapped.Mode.ShouldBe(AwakeMode.Wrap);
    }

    [Test]
    [Arguments("--for", "90m")]
    [Arguments("-t", "90m")]
    [Arguments("--for=90m")]
    [Arguments("-t=90m")]
    public void For_flag_sets_timed_mode(params string[] args)
    {
        var opts = Parse(args);
        opts.Mode.ShouldBe(AwakeMode.Timed);
        opts.Duration.ShouldBe(TimeSpan.FromMinutes(90));
    }

    [Test]
    [Arguments("-q")]
    [Arguments("--quiet")]
    public void Quiet_flag_sets_quiet(string flag)
    {
        Parse(flag).Quiet.ShouldBeTrue();
    }

    [Test]
    public void Quiet_is_off_by_default()
    {
        Parse().Quiet.ShouldBeFalse();
    }

    [Test]
    public void Quiet_and_verbose_are_mutually_exclusive()
    {
        var error = ArgumentParser.Parse(["--quiet", "--verbose"])
            .ShouldBeOfType<ParseResult.Error>();
        error.Message.ShouldContain("--quiet");
        error.Message.ShouldContain("--verbose");
    }

    [Test]
    public void Flags_combine()
    {
        var opts = Parse("--for", "5s", "--display", "--verbose");
        opts.Mode.ShouldBe(AwakeMode.Timed);
        opts.Duration.ShouldBe(TimeSpan.FromSeconds(5));
        opts.KeepDisplayOn.ShouldBeTrue();
        opts.Verbose.ShouldBeTrue();
    }

    [Test]
    public void Double_dash_captures_wrapped_command_verbatim()
    {
        var opts = Parse("--display", "--", "ping", "-c", "3", "example.com");
        opts.Mode.ShouldBe(AwakeMode.Wrap);
        opts.KeepDisplayOn.ShouldBeTrue();
        opts.Command.ShouldBe(["ping", "-c", "3", "example.com"]);
    }

    [Test]
    public void Options_after_double_dash_belong_to_command()
    {
        // --verbose here is the command's flag, not NightOwl's.
        var opts = Parse("--", "mytool", "--verbose", "--help");
        opts.Verbose.ShouldBeFalse();
        opts.Command.ShouldBe(["mytool", "--verbose", "--help"]);
    }

    [Test]
    [Arguments("-h")]
    [Arguments("--help")]
    public void Help_flag_requests_help(string flag)
    {
        ArgumentParser.Parse([flag]).ShouldBeOfType<ParseResult.ShowHelp>();
    }

    [Test]
    public void Version_flag_requests_version()
    {
        ArgumentParser.Parse(["--version"]).ShouldBeOfType<ParseResult.ShowVersion>();
    }

    [Test]
    public void Help_takes_priority_over_other_errors()
    {
        ArgumentParser.Parse(["--bogus", "--help"]).ShouldBeOfType<ParseResult.ShowHelp>();
    }

    [Test]
    public void For_and_wrapped_command_are_mutually_exclusive()
    {
        var error = ArgumentParser.Parse(["--for", "10s", "--", "sleep", "1"])
            .ShouldBeOfType<ParseResult.Error>();
        error.Message.ShouldContain("--for");
    }

    [Test]
    public void Unknown_option_is_an_error_with_help_hint()
    {
        var error = ArgumentParser.Parse(["--nope"]).ShouldBeOfType<ParseResult.Error>();
        error.Message.ShouldContain("--nope");
        error.Message.ShouldContain("nightowl --help");
    }

    [Test]
    public void Unexpected_positional_argument_is_an_error()
    {
        ArgumentParser.Parse(["wat"]).ShouldBeOfType<ParseResult.Error>();
    }

    [Test]
    [Arguments("--for")]
    [Arguments("-t")]
    public void For_without_value_is_an_error(string flag)
    {
        var error = ArgumentParser.Parse([flag]).ShouldBeOfType<ParseResult.Error>();
        error.Message.ShouldContain("duration");
    }

    [Test]
    public void For_with_invalid_duration_is_an_error()
    {
        ArgumentParser.Parse(["--for", "banana"]).ShouldBeOfType<ParseResult.Error>();
    }

    [Test]
    public void Empty_wrapped_command_is_an_error()
    {
        // `--` with nothing after it: no command to wrap.
        ArgumentParser.Parse(["--"]).ShouldBeOfType<ParseResult.Error>();
    }

    [Test]
    [Arguments("--pid", "1234")]
    [Arguments("-w", "1234")]
    [Arguments("--pid=1234")]
    [Arguments("-w=1234")]
    public void Pid_flag_sets_wait_pid_mode(params string[] args)
    {
        var opts = Parse(args);
        opts.Mode.ShouldBe(AwakeMode.WaitPid);
        opts.Pid.ShouldBe(1234);
    }

    [Test]
    public void Pid_composes_with_display_and_verbose()
    {
        var opts = Parse("--pid", "42", "--display", "--verbose");
        opts.Mode.ShouldBe(AwakeMode.WaitPid);
        opts.Pid.ShouldBe(42);
        opts.KeepDisplayOn.ShouldBeTrue();
        opts.Verbose.ShouldBeTrue();
    }

    [Test]
    [Arguments("--pid")]
    [Arguments("-w")]
    public void Pid_without_value_is_an_error(string flag)
    {
        var error = ArgumentParser.Parse([flag]).ShouldBeOfType<ParseResult.Error>();
        error.Message.ShouldContain("PID");
    }

    [Test]
    [Arguments("banana")]
    [Arguments("0")]
    [Arguments("-5")]
    [Arguments("1.5")]
    public void Pid_with_non_positive_or_non_integer_value_is_an_error(string value)
    {
        var error = ArgumentParser.Parse(["--pid", value]).ShouldBeOfType<ParseResult.Error>();
        error.Message.ShouldContain("PID");
    }

    [Test]
    public void Repeated_pid_is_an_error()
    {
        var error = ArgumentParser.Parse(["--pid", "1", "--pid", "2"]).ShouldBeOfType<ParseResult.Error>();
        error.Message.ShouldContain("--pid");
    }

    [Test]
    public void Pid_and_for_are_mutually_exclusive()
    {
        var error = ArgumentParser.Parse(["--pid", "123", "--for", "10s"])
            .ShouldBeOfType<ParseResult.Error>();
        error.Message.ShouldContain("--pid");
        error.Message.ShouldContain("--for");
    }

    [Test]
    public void Pid_and_wrapped_command_are_mutually_exclusive()
    {
        var error = ArgumentParser.Parse(["--pid", "123", "--", "sleep", "1"])
            .ShouldBeOfType<ParseResult.Error>();
        error.Message.ShouldContain("--pid");
    }
}
