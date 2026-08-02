using System.Collections.ObjectModel;

namespace StorageHub.Domain.Capabilities;

public enum FeatureSupportLevel
{
    Unsupported,
    Emulated,
    Native
}

/// <summary>Describes how an effective endpoint session supplies a feature.</summary>
public sealed record FeatureSupport
{
    private FeatureSupport(FeatureSupportLevel level, string? detail)
    {
        Level = level;
        Detail = string.IsNullOrWhiteSpace(detail) ? null : detail.Trim();
    }

    public FeatureSupportLevel Level { get; }
    public string? Detail { get; }
    public bool IsSupported => Level is not FeatureSupportLevel.Unsupported;

    public static FeatureSupport Native(string? detail = null) =>
        new(FeatureSupportLevel.Native, detail);

    public static FeatureSupport Emulated(string? detail = null) =>
        new(FeatureSupportLevel.Emulated, detail);

    public static FeatureSupport Unsupported(string? reason = null) =>
        new(FeatureSupportLevel.Unsupported, reason);
}

public enum StorageFeature
{
    List,
    PaginatedList,
    RecursiveList,
    ReadStream,
    WriteStream,
    ResumeUpload,
    ResumeDownload,
    MultipartUpload,
    CreateDirectory,
    Delete,
    Rename,
    /// <summary>
    /// Legacy aggregate move capability. It is supported only when both files and directories can be moved;
    /// use <see cref="FileMove"/> or <see cref="DirectoryMove"/> for operation-specific decisions.
    /// </summary>
    Move,
    /// <summary>
    /// Legacy aggregate copy capability. It is supported only when both files and directories can be copied;
    /// use <see cref="FileCopy"/> or <see cref="DirectoryCopy"/> for operation-specific decisions.
    /// </summary>
    Copy,
    ServerSideCopy,
    AtomicRename,
    SetModifiedTime,
    Permissions,
    SymbolicLinks,
    Checksums,
    ObjectVersioning,
    Trash,
    Metadata,
    Tags,
    ContentType,
    TemporaryFiles,
    ChangeNotifications,
    RemoteHashing,
    DirectRemoteToRemoteTransfer,
    /// <summary>Atomically creates a destination only when it does not already exist.</summary>
    ConditionalCreate,
    /// <summary>Atomically replaces an item only when its captured version or entity tag matches.</summary>
    ConditionalUpdate,
    /// <summary>Atomically deletes an item only when its captured version or entity tag matches.</summary>
    ConditionalDelete,
    /// <summary>Publishes a complete replacement without exposing partial destination content.</summary>
    AtomicReplace,
    /// <summary>Updates provider user metadata without replacing object content.</summary>
    MetadataWrite,
    /// <summary>Creates temporary secret-bearing URLs for object reads.</summary>
    SignedReadUrls,
    /// <summary>Creates temporary secret-bearing URLs for object writes.</summary>
    SignedWriteUrls,
    /// <summary>Reads or updates provider-native access-control lists.</summary>
    AccessControlLists,
    /// <summary>Acquires provider-native object leases.</summary>
    Leases,
    /// <summary>Appends bytes without replacing the existing object.</summary>
    Append,
    /// <summary>Copies a file through the endpoint's same-session copy operation.</summary>
    FileCopy,
    /// <summary>Copies a directory tree through the endpoint's same-session copy operation.</summary>
    DirectoryCopy,
    /// <summary>Moves a file through the endpoint's same-session move operation.</summary>
    FileMove,
    /// <summary>Moves a directory tree through the endpoint's same-session move operation.</summary>
    DirectoryMove
}

public enum StorageCaseSensitivity
{
    Sensitive,
    Insensitive,
    ProviderDefined
}

/// <summary>Capabilities resolved for one live endpoint, including server and root constraints.</summary>
public sealed class EffectiveStorageCapabilities
{
    private static readonly FeatureSupport NotAdvertised =
        FeatureSupport.Unsupported("The endpoint did not advertise this feature.");
    private readonly ReadOnlyDictionary<StorageFeature, FeatureSupport> _features;

    public EffectiveStorageCapabilities(
        IEnumerable<KeyValuePair<StorageFeature, FeatureSupport>> features,
        StorageCaseSensitivity caseSensitivity = StorageCaseSensitivity.ProviderDefined,
        long? maxObjectSizeBytes = null,
        int? maxPathLength = null,
        string nativePathSeparator = "/",
        IEnumerable<char>? invalidPathCharacters = null,
        int? maxPageSize = null,
        long? maxSingleUploadBytes = null,
        int? maxMetadataBytes = null,
        int? maxTags = null,
        int? maxBatchItems = null,
        int? preferredUploadPartBytes = null)
    {
        ArgumentNullException.ThrowIfNull(features);
        if (maxObjectSizeBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxObjectSizeBytes));
        }

        if (maxPathLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPathLength));
        }

        ValidatePositive(maxPageSize, nameof(maxPageSize));
        ValidatePositive(maxSingleUploadBytes, nameof(maxSingleUploadBytes));
        ValidatePositive(maxMetadataBytes, nameof(maxMetadataBytes));
        ValidatePositive(maxTags, nameof(maxTags));
        ValidatePositive(maxBatchItems, nameof(maxBatchItems));
        ValidatePositive(preferredUploadPartBytes, nameof(preferredUploadPartBytes));

        if (string.IsNullOrEmpty(nativePathSeparator))
        {
            throw new ArgumentException("A native path separator is required.", nameof(nativePathSeparator));
        }

        var featureSnapshot = new Dictionary<StorageFeature, FeatureSupport>();
        foreach (var (feature, support) in features)
        {
            featureSnapshot.Add(feature, support ?? throw new ArgumentException(
                $"Feature '{feature}' has no support descriptor.",
                nameof(features)));
        }

        _features = new ReadOnlyDictionary<StorageFeature, FeatureSupport>(featureSnapshot);
        CaseSensitivity = caseSensitivity;
        MaxObjectSizeBytes = maxObjectSizeBytes;
        MaxPathLength = maxPathLength;
        MaxPageSize = maxPageSize;
        MaxSingleUploadBytes = maxSingleUploadBytes;
        MaxMetadataBytes = maxMetadataBytes;
        MaxTags = maxTags;
        MaxBatchItems = maxBatchItems;
        PreferredUploadPartBytes = preferredUploadPartBytes;
        NativePathSeparator = nativePathSeparator;
        InvalidPathCharacters = Array.AsReadOnly(
            (invalidPathCharacters ?? []).Distinct().Order().ToArray());
    }

    public static EffectiveStorageCapabilities None { get; } = new([]);

    public IReadOnlyDictionary<StorageFeature, FeatureSupport> Features => _features;
    public StorageCaseSensitivity CaseSensitivity { get; }
    public long? MaxObjectSizeBytes { get; }
    public int? MaxPathLength { get; }
    public int? MaxPageSize { get; }
    public long? MaxSingleUploadBytes { get; }
    public int? MaxMetadataBytes { get; }
    public int? MaxTags { get; }
    public int? MaxBatchItems { get; }
    public int? PreferredUploadPartBytes { get; }
    public string NativePathSeparator { get; }
    public IReadOnlyList<char> InvalidPathCharacters { get; }
    public FeatureSupport this[StorageFeature feature] =>
        _features.TryGetValue(feature, out var support) ? support : NotAdvertised;

    public bool Supports(StorageFeature feature) => this[feature].IsSupported;

    private static void ValidatePositive<T>(T? value, string parameterName)
        where T : struct, IComparable<T>
    {
        if (value is { } actual && actual.CompareTo(default) <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
