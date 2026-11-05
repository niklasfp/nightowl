using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using NightOwl.Logging;

namespace NightOwl.PidWait;

/// <summary>
/// Windows backend: <c>OpenProcess(SYNCHRONIZE)</c> then a kernel object wait on the handle.
/// <c>SYNCHRONIZE</c> is grantable even on elevated/protected processes, and the wait is
/// reuse-immune once the handle is open (a handle names the kernel process object, not the PID
/// number, and stays valid — and signals — after the process terminates).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsPidWaiter(ILog log) : IPidWaiter
{
    private const uint Synchronize = 0x00100000;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    public Task WaitForExitAsync(int pid, CancellationToken cancellationToken)
    {
        // Synchronous and effectively instant: a failed open (nonexistent PID, or access denied)
        // throws here, before any Task is returned, so callers can rely on this call to fail fast.
        var handle = OpenProcess(Synchronize, false, unchecked((uint)pid));
        if (handle == IntPtr.Zero)
        {
            throw new PidWaitException(
                $"Could not open process {pid} (error {Marshal.GetLastPInvokeError()}); it may not exist.");
        }

        log.Verbose($"Windows backend: OpenProcess(SYNCHRONIZE) on PID {pid}.");

        var waitHandle = new SafeWaitHandle(handle, ownsHandle: true);
        return WaitOnHandleAsync(waitHandle, cancellationToken);
    }

    /// <summary>Registers a threadpool wait on the process handle becoming signaled (i.e. exit).</summary>
    private static async Task WaitOnHandleAsync(SafeWaitHandle waitHandle, CancellationToken cancellationToken)
    {
        using var evt = new ManualResetEvent(initialState: false) { SafeWaitHandle = waitHandle };

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ThreadPool.RegisterWaitForSingleObject(
            evt,
            (_, _) => tcs.TrySetResult(),
            state: null,
            timeout: Timeout.InfiniteTimeSpan,
            executeOnlyOnce: true);

        using var ctReg = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        try
        {
            await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            registration.Unregister(null);
        }
    }
}
