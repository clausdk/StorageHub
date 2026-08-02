using System.Globalization;
using System.Text.Json.Serialization;

namespace StorageHub.Contracts.Ipc;

/// <summary>The independently negotiated StorageHub IPC protocol version.</summary>
public readonly record struct ProtocolVersion
{
    [JsonConstructor]
    public ProtocolVersion(int major, int minor)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(major);
        ArgumentOutOfRangeException.ThrowIfNegative(minor);
        Major = major;
        Minor = minor;
    }

    public static ProtocolVersion Current { get; } = new(1, 0);

    public int Major { get; }

    public int Minor { get; }

    public bool IsCompatibleWith(ProtocolVersion other) => Major == other.Major;

    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"{Major}.{Minor}");
}
