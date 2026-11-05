using SysProcess = System.Diagnostics.Process;
using SysProcessStartInfo = System.Diagnostics.ProcessStartInfo;

namespace NightOwl.Process;

/// <summary>Default <see cref="IProcessRunner"/> backed by <c>System.Diagnostics.Process</c>.</summary>
internal sealed class SystemProcessRunner : IProcessRunner
{
    public IProcessHandle Start(ProcessSpec spec)
    {
        var startInfo = new SysProcessStartInfo(spec.FileName)
        {
            UseShellExecute = false,
            RedirectStandardInput = spec.RedirectStandardInput,
        };

        foreach (var arg in spec.Arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        var process = SysProcess.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start process '{spec.FileName}'.");

        return new SystemProcessHandle(process);
    }

    private sealed class SystemProcessHandle(SysProcess process) : IProcessHandle
    {
        public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode;
        }

        public void Kill()
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // Process already exited between the check and the kill — nothing to do.
            }
        }

        public ValueTask DisposeAsync()
        {
            Kill();
            process.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
