namespace NightOwl.Cli;

/// <summary>
/// Hand-rolled CLI parser (no <c>System.CommandLine</c> dependency — keeps the build
/// tiny and AOT/trim-warning-free). Produces a <see cref="ParseResult"/> union.
/// </summary>
internal static class ArgumentParser
{
    private const string HelpHint = "Try 'nightowl --help'.";

    public static ParseResult Parse(string[] args)
    {
        // Help/version win over any usage error. Only options *before* a "--" count,
        // so "-- mytool --help" still passes --help through to the wrapped command.
        foreach (var arg in args)
        {
            if (arg == "--")
            {
                break;
            }

            if (arg is "-h" or "--help")
            {
                return new ParseResult.ShowHelp();
            }

            if (arg == "--version")
            {
                return new ParseResult.ShowVersion();
            }
        }

        var keepDisplayOn = false;
        var blockSleep = false;
        var verbose = false;
        var quiet = false;
        TimeSpan? duration = null;
        int? pid = null;
        string[]? command = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            // Everything after a bare "--" is the wrapped command, verbatim.
            if (arg == "--")
            {
                var rest = args[(i + 1)..];
                if (rest.Length == 0)
                {
                    return new ParseResult.Error($"No command given after '--'. {HelpHint}");
                }

                command = rest;
                break;
            }

            if (arg is "-d" or "--display")
            {
                keepDisplayOn = true;
                continue;
            }

            if (arg is "-s" or "--block-sleep")
            {
                blockSleep = true;
                continue;
            }

            if (arg is "-v" or "--verbose")
            {
                verbose = true;
                continue;
            }

            if (arg is "-q" or "--quiet")
            {
                quiet = true;
                continue;
            }

            if (TryMatchValueOption(arg, "-t", "--for", args, ref i, out var value))
            {
                if (value is null)
                {
                    return new ParseResult.Error($"Option '{arg}' requires a duration (e.g. 30s, 90m, 1h30m). {HelpHint}");
                }

                if (!DurationParser.TryParse(value, out var parsed))
                {
                    return new ParseResult.Error($"Invalid duration '{value}'. Use forms like 30s, 90m, 2h, 1h30m. {HelpHint}");
                }

                duration = parsed;
                continue;
            }

            if (TryMatchValueOption(arg, "-w", "--pid", args, ref i, out var pidValue))
            {
                if (pidValue is null)
                {
                    return new ParseResult.Error($"Option '{arg}' requires a PID (e.g. --pid 1234). {HelpHint}");
                }

                if (pid is not null)
                {
                    return new ParseResult.Error($"'--pid' cannot be given more than once. {HelpHint}");
                }

                if (!int.TryParse(pidValue, out var parsedPid) || parsedPid <= 0)
                {
                    return new ParseResult.Error($"Invalid PID '{pidValue}'. Expected a positive integer. {HelpHint}");
                }

                pid = parsedPid;
                continue;
            }

            return arg.StartsWith('-')
                ? new ParseResult.Error($"Unknown option '{arg}'. {HelpHint}")
                : new ParseResult.Error($"Unexpected argument '{arg}'. To wrap a command, put it after '--'. {HelpHint}");
        }

        var modeCount = (duration is not null ? 1 : 0) + (pid is not null ? 1 : 0) + (command is not null ? 1 : 0);
        if (modeCount > 1)
        {
            return new ParseResult.Error(
                $"'--for', '--pid', and a wrapped command ('-- …') are mutually exclusive. {HelpHint}");
        }

        if (quiet && verbose)
        {
            return new ParseResult.Error($"'--quiet' and '--verbose' are mutually exclusive. {HelpHint}");
        }

        var mode = command is not null
            ? AwakeMode.Wrap
            : pid is not null
                ? AwakeMode.WaitPid
                : duration is not null
                    ? AwakeMode.Timed
                    : AwakeMode.Indefinite;

        var options = new CliOptions(
            mode,
            duration,
            pid,
            keepDisplayOn,
            blockSleep,
            verbose,
            quiet,
            command ?? []);

        return new ParseResult.Parsed(options);
    }

    /// <summary>
    /// Matches a value-taking option in either <c>--for 90m</c> or <c>--for=90m</c> form
    /// (and the short alias). On the space-separated form this advances <paramref name="i"/>
    /// past the consumed value. <paramref name="value"/> is <c>null</c> when the option is
    /// recognized but its value is missing.
    /// </summary>
    private static bool TryMatchValueOption(
        string arg,
        string shortName,
        string longName,
        string[] args,
        ref int i,
        out string? value)
    {
        value = null;

        // "--for=90m" / "-t=90m"
        foreach (var name in (ReadOnlySpan<string>)[longName, shortName])
        {
            var prefix = name + "=";
            if (arg.StartsWith(prefix, StringComparison.Ordinal))
            {
                var v = arg[prefix.Length..];
                value = v.Length == 0 ? null : v;
                return true;
            }
        }

        // "--for 90m" / "-t 90m"
        if (arg == shortName || arg == longName)
        {
            if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
            {
                value = args[++i];
            }

            return true;
        }

        return false;
    }
}
