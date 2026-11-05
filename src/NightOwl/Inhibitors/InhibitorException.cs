namespace NightOwl.Inhibitors;

/// <summary>A user-facing failure while acquiring or selecting a sleep inhibitor.</summary>
internal sealed class InhibitorException(string message, Exception? innerException = null)
    : Exception(message, innerException);
