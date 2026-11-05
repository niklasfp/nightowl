namespace NightOwl.Os;

/// <summary>Testable wrapper over <see cref="OperatingSystem"/> platform checks.</summary>
internal interface IOsInfo
{
    bool IsWindows { get; }

    bool IsMacOs { get; }

    bool IsLinux { get; }
}

/// <summary>Default implementation backed by the runtime's platform checks.</summary>
internal sealed class OsInfo : IOsInfo
{
    public bool IsWindows => OperatingSystem.IsWindows();

    public bool IsMacOs => OperatingSystem.IsMacOS();

    public bool IsLinux => OperatingSystem.IsLinux();
}
