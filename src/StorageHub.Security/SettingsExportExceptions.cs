namespace StorageHub.Security;

/// <summary>
/// A settings export file could not be opened.
///
/// Deliberately one exception with one message for every cause — a wrong password, a truncated
/// file, a tampered header, an unsupported version. Reporting which it was would tell a caller
/// holding a stolen file whether a guessed password was correct, and the distinction is of no use
/// to an honest one. This mirrors how <c>KeyMaterialInspector</c> reports an unreadable PKCS#12.
/// </summary>
public sealed class SettingsExportCorruptedException : Exception
{
    internal const string StandardMessage =
        "The export file could not be opened. Check the password, or the file may be damaged.";

    public SettingsExportCorruptedException()
        : base(StandardMessage)
    {
    }

    public SettingsExportCorruptedException(string message)
        : base(message)
    {
    }

    public SettingsExportCorruptedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
