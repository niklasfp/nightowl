using NightOwl.Inhibitors;
using Shouldly;

namespace NightOwl.Tests;

public class InhibitorFactoryTests
{
    private static ISleepInhibitor Create(FakeOsInfo os) =>
        InhibitorFactory.Create(os, new FakeProcessRunner(), new FakeLog());

    [Test]
    public void Selects_windows_backend()
    {
        // Assert by name rather than `ShouldBeOfType<WindowsSleepInhibitor>()`: that type is
        // [SupportedOSPlatform("windows")], so referencing it here would trip CA1416 on macOS/Linux.
        Create(new FakeOsInfo(windows: true)).GetType().Name.ShouldBe("WindowsSleepInhibitor");
    }

    [Test]
    public void Selects_macos_backend()
    {
        // Name-based for the same reason as Windows: MacOsSleepInhibitor is now
        // [SupportedOSPlatform("macos")], so referencing the type here would trip CA1416.
        Create(new FakeOsInfo(macOs: true)).GetType().Name.ShouldBe("MacOsSleepInhibitor");
    }

    [Test]
    public void Selects_linux_backend()
    {
        // Name-based for the same reason as Windows: LinuxSleepInhibitor is now
        // [SupportedOSPlatform("linux")], so referencing the type here would trip CA1416.
        Create(new FakeOsInfo(linux: true)).GetType().Name.ShouldBe("LinuxSleepInhibitor");
    }

    [Test]
    public void Unsupported_os_throws()
    {
        Should.Throw<InhibitorException>(() => Create(new FakeOsInfo()));
    }
}
