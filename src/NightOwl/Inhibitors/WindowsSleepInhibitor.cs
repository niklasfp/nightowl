using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NightOwl.Logging;

namespace NightOwl.Inhibitors;

/// <summary>
/// Windows backend: kernel power requests via <c>PowerCreateRequest</c>/<c>PowerSetRequest</c>
/// (Windows 7+), migrated off the legacy <c>SetThreadExecutionState</c> API. A power request is
/// process-scoped (unlike the per-thread STES flag), so no dedicated keep-alive thread is needed,
/// and it carries a <c>REASON_CONTEXT</c> reason string visible in <c>powercfg /requests</c>.
/// Baseline holds <c>PowerRequestSystemRequired</c>; <c>-d/--display</c> adds
/// <c>PowerRequestDisplayRequired</c>; <c>-s/--block-sleep</c> adds
/// <c>PowerRequestExecutionRequired</c>, which keeps the process running under Modern Standby
/// (and implies SystemRequired on S3 systems) — the strongest thing an unprivileged process can
/// ask for. Away mode (<c>PowerRequestAwayModeRequired</c>) is deliberately never used: it does
/// not block sleep, it fakes it, and is documented as media-app-only. Per Microsoft's own docs,
/// no user-mode API can block *user-initiated* sleep (lid close, power button, Start > Sleep) —
/// power requests are terminated on sleep entry regardless; this and the Modern-Standby-on-DC
/// ~5-minute request lifetime are reported under <c>--verbose</c> only.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsSleepInhibitor(ILog log) : ISleepInhibitor
{
    private enum PowerRequestType
    {
        DisplayRequired = 0,
        SystemRequired = 1,
        AwayModeRequired = 2,
        ExecutionRequired = 3,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        public IntPtr SimpleReasonString;
    }

    private const uint ReasonContextVersion = 0;
    private const uint PowerRequestContextSimpleString = 0x1;
    private const string ReasonText = "nightowl";

    // PowerCreateRequest's documented failure return is INVALID_HANDLE_VALUE ((HANDLE)-1), not
    // NULL — checking only for IntPtr.Zero would let a real failure through, silently discarding
    // every following PowerSetRequest call while the "hold" reports success.
    private static readonly IntPtr InvalidHandleValue = new(-1);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr PowerCreateRequest(ref ReasonContext context);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PowerSetRequest(IntPtr powerRequest, PowerRequestType requestType);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PowerClearRequest(IntPtr powerRequest, PowerRequestType requestType);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    public IAsyncDisposable Acquire(bool keepDisplayOn, bool blockSleep)
    {
        var types = new List<PowerRequestType> { PowerRequestType.SystemRequired };
        if (keepDisplayOn)
        {
            types.Add(PowerRequestType.DisplayRequired);
        }

        if (blockSleep)
        {
            types.Add(PowerRequestType.ExecutionRequired);
        }

        log.Verbose($"Windows backend: PowerCreateRequest/PowerSetRequest({string.Join(", ", types)})");
        if (blockSleep)
        {
            log.Verbose(
                "Windows: user-initiated sleep (lid close, power button, Start > Sleep) cannot be "
                + "blocked — power requests are terminated on sleep entry. On battery under Modern "
                + "Standby, the OS may terminate the hold after about 5 minutes.");
        }

        return new WindowsHold(types, log);
    }

    private sealed class WindowsHold : IAsyncDisposable
    {
        private readonly IReadOnlyList<PowerRequestType> _types;
        private readonly IntPtr _handle;
        private readonly IntPtr _reasonPtr;

        public WindowsHold(IReadOnlyList<PowerRequestType> types, ILog log)
        {
            _types = types;
            _reasonPtr = Marshal.StringToHGlobalUni(ReasonText);

            var context = new ReasonContext
            {
                Version = ReasonContextVersion,
                Flags = PowerRequestContextSimpleString,
                SimpleReasonString = _reasonPtr,
            };

            _handle = PowerCreateRequest(ref context);
            if (_handle == InvalidHandleValue || _handle == IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_reasonPtr);
                throw new InhibitorException(
                    $"PowerCreateRequest failed (error {Marshal.GetLastPInvokeError()}).");
            }

            foreach (var type in types)
            {
                if (!PowerSetRequest(_handle, type))
                {
                    // Best-effort: keep whichever request types the OS did accept rather than
                    // failing the whole hold over one rejected type.
                    log.Verbose(
                        $"Windows: PowerSetRequest({type}) failed "
                        + $"(error {Marshal.GetLastPInvokeError()}); continuing without it.");
                }
            }
        }

        public ValueTask DisposeAsync()
        {
            foreach (var type in _types)
            {
                PowerClearRequest(_handle, type);
            }

            CloseHandle(_handle);
            Marshal.FreeHGlobal(_reasonPtr);
            return ValueTask.CompletedTask;
        }
    }
}
