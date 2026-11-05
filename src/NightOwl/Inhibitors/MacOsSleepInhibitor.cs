using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NightOwl.Logging;

namespace NightOwl.Inhibitors;

/// <summary>
/// macOS backend: native IOKit power assertions via <c>IOPMAssertionCreateWithName</c>. Holds
/// <c>PreventUserIdleSystemSleep</c> (was <c>caffeinate -i</c>), adding
/// <c>PreventUserIdleDisplaySleep</c> to also keep the display awake (was <c>-d</c>), and
/// <c>PreventSystemSleep</c> for <c>-s/--block-sleep</c> (was <c>caffeinate -s</c>) — the OS only
/// honors that assertion on AC power, and NightOwl adds no power-source logic of its own (locked
/// decision: native AC-only behavior stands, documented under <c>--verbose</c>). Per the locked
/// "best-effort, never fails" semantics for <c>-s</c>, if IOKit refuses the <c>PreventSystemSleep</c>
/// assertion the hold still succeeds with the required (baseline/display) assertions — only a
/// refusal of one of *those* is fatal. Unlike the Windows per-thread flag, an IOKit assertion is
/// process-global and lives until released, so the hold simply remembers the assertion IDs and
/// releases them on dispose — no helper thread, no child process.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed partial class MacOsSleepInhibitor(ILog log) : ISleepInhibitor
{
    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundation =
        "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    // kIOPMAssertionLevelOn; kIOReturnSuccess; kCFStringEncodingUTF8.
    private const uint AssertionLevelOn = 255;
    private const int Success = 0;
    private const uint EncodingUtf8 = 0x08000100;

    // Values of kIOPMAssertionTypePreventUserIdle{System,Display}Sleep and
    // kIOPMAssertionTypePreventSystemSleep. IOKit matches assertion types by string content, so
    // building our own CFString is equivalent to the exported constant.
    private const string PreventUserIdleSystemSleep = "PreventUserIdleSystemSleep";
    private const string PreventUserIdleDisplaySleep = "PreventUserIdleDisplaySleep";
    private const string PreventSystemSleep = "PreventSystemSleep";

    [LibraryImport(IOKit)]
    private static partial int IOPMAssertionCreateWithName(
        IntPtr assertionType, uint assertionLevel, IntPtr assertionName, out uint assertionId);

    [LibraryImport(IOKit)]
    private static partial int IOPMAssertionRelease(uint assertionId);

    [LibraryImport(CoreFoundation, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr CFStringCreateWithCString(IntPtr alloc, string cStr, uint encoding);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(IntPtr cf);

    public IAsyncDisposable Acquire(bool keepDisplayOn, bool blockSleep)
    {
        var requiredTypes = new List<string>(2) { PreventUserIdleSystemSleep };
        if (keepDisplayOn)
        {
            requiredTypes.Add(PreventUserIdleDisplaySleep);
        }

        if (blockSleep)
        {
            log.Verbose("macOS: system-sleep block (PreventSystemSleep) is honored on AC power only.");
        }

        var (ids, heldTypes) = AcquireAssertions(
            requiredTypes,
            blockSleep ? PreventSystemSleep : null,
            CreateAssertion,
            log.Verbose);

        log.Verbose($"macOS backend: IOKit assertion(s) {string.Join(", ", heldTypes)}");

        return new MacOsHold(ids);
    }

    /// <summary>
    /// Creates the required assertions (any failure here is fatal — thrown as
    /// <see cref="InhibitorException"/>) and then, if given, the one *optional* assertion for
    /// <c>-s/--block-sleep</c>. Per the locked "best-effort, never fails" semantics for that flag,
    /// a refusal of the optional assertion only logs under <c>--verbose</c> and the hold continues
    /// with whatever it already has — it does not fail the whole acquire, and it does not orphan
    /// the already-created required assertion ids. Parametrized over the creation/logging
    /// delegates so this orchestration is unit-testable without any native IOKit call.
    /// </summary>
    internal static (List<uint> Ids, List<string> HeldTypes) AcquireAssertions(
        IReadOnlyList<string> requiredTypes,
        string? optionalType,
        Func<string, uint> createAssertion,
        Action<string> logVerbose)
    {
        var ids = new List<uint>(requiredTypes.Count + 1);
        var heldTypes = new List<string>(requiredTypes.Count + 1);

        foreach (var type in requiredTypes)
        {
            ids.Add(createAssertion(type));
            heldTypes.Add(type);
        }

        if (optionalType is not null)
        {
            try
            {
                ids.Add(createAssertion(optionalType));
                heldTypes.Add(optionalType);
            }
            catch (InhibitorException ex)
            {
                logVerbose($"macOS: '{optionalType}' assertion refused ({ex.Message}); continuing without it.");
            }
        }

        return (ids, heldTypes);
    }

    /// <summary>Creates one IOKit power assertion of the given type, returning its id.</summary>
    private static uint CreateAssertion(string assertionType)
    {
        var type = CFStringCreateWithCString(IntPtr.Zero, assertionType, EncodingUtf8);
        var name = CFStringCreateWithCString(IntPtr.Zero, "nightowl", EncodingUtf8);
        try
        {
            var result = IOPMAssertionCreateWithName(type, AssertionLevelOn, name, out var id);
            if (result != Success)
            {
                throw new InhibitorException(
                    $"IOKit refused the sleep assertion '{assertionType}' (IOReturn 0x{result:X8}).");
            }

            return id;
        }
        finally
        {
            if (type != IntPtr.Zero)
            {
                CFRelease(type);
            }

            if (name != IntPtr.Zero)
            {
                CFRelease(name);
            }
        }
    }

    /// <summary>Holds one or more IOKit assertion ids; disposing releases each one.</summary>
    private sealed class MacOsHold(IReadOnlyList<uint> assertionIds) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            foreach (var id in assertionIds)
            {
                IOPMAssertionRelease(id);
            }

            return ValueTask.CompletedTask;
        }
    }
}
