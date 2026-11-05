using System.Runtime.InteropServices;
using NightOwl.Cli;
using NightOwl.Inhibitors;
using NightOwl.Logging;
using NightOwl.Modes;
using NightOwl.Os;
using NightOwl.PidWait;
using NightOwl.Process;

return await Entrypoint.RunAsync(args);

internal static class Entrypoint
{
    public static async Task<int> RunAsync(string[] args)
    {
        switch (ArgumentParser.Parse(args))
        {
            case ParseResult.ShowHelp:
                Console.WriteLine(HelpText.Usage);
                return 0;

            case ParseResult.ShowVersion:
                Console.WriteLine($"nightowl {HelpText.Version}");
                return 0;

            case ParseResult.Error error:
                Console.Error.WriteLine(error.Message);
                return 2;

            case ParseResult.Parsed parsed:
                return await KeepAwakeAsync(parsed.Options);

            default:
                Console.Error.WriteLine("Unexpected parse result. Try 'nightowl --help'.");
                return 2;
        }
    }

    private static async Task<int> KeepAwakeAsync(CliOptions options)
    {
        var log = new ConsoleLog(options.Verbose, options.Quiet);
        var os = new OsInfo();
        var processRunner = new SystemProcessRunner();

        // Ctrl+C and SIGTERM both feed one cancellation token; the runner releases the hold cleanly.
        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancelKey = (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        Console.CancelKeyPress += onCancelKey;
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            cts.Cancel();
        });

        try
        {
            var inhibitor = InhibitorFactory.Create(os, processRunner, log);
            var pidWaiter = PidWaiterFactory.Create(os, log);
            var runner = new KeepAwakeRunner(inhibitor, processRunner, pidWaiter, log);
            return await runner.RunAsync(options, cts.Token);
        }
        catch (InhibitorException ex)
        {
            log.Warn(ex.Message);
            return 1;
        }
        catch (PidWaitException ex)
        {
            // Belt-and-suspenders: KeepAwakeRunner already catches PidWaitException around both
            // the fail-fast registration and the wait itself, but nothing should ever crash with
            // a raw stack trace merely because a PID-wait native call failed.
            log.Warn(ex.Message);
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= onCancelKey;
        }
    }
}
