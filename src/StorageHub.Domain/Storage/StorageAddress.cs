using System.Text;
using StorageHub.Contracts.Results;
using StorageHub.Domain.Identifiers;

namespace StorageHub.Domain.Storage;

/// <summary>A version-aware address scoped to one immutable connection root identity.</summary>
public sealed record StorageAddress
{
    private const int MaximumPathLength = 32_768;
    private const int MaximumOpaqueValueLength = 8_192;

    private StorageAddress(
        ConnectionProfileId profileId,
        string rootIdentity,
        string canonicalRelativePath,
        string? nativeItemId,
        string? versionId,
        string? entityTag)
    {
        ProfileId = profileId;
        RootIdentity = rootIdentity;
        CanonicalRelativePath = canonicalRelativePath;
        NativeItemId = nativeItemId;
        VersionId = versionId;
        EntityTag = entityTag;
    }

    public ConnectionProfileId ProfileId { get; }

    /// <summary>An opaque identity that changes when the configured endpoint root changes.</summary>
    public string RootIdentity { get; }

    /// <summary>A Unicode-NFC, slash-separated path relative to the session root.</summary>
    public string CanonicalRelativePath { get; }

    /// <summary>A provider-owned stable item ID. It is never parsed as a path.</summary>
    public string? NativeItemId { get; }

    /// <summary>A provider-owned object version or generation ID.</summary>
    public string? VersionId { get; }

    /// <summary>
    /// A provider-owned entity tag captured with this address. It is opaque identity evidence,
    /// never a path, and can be used only by endpoints advertising conditional mutations.
    /// </summary>
    public string? EntityTag { get; }

    public bool IsRoot => CanonicalRelativePath.Length == 0;

    public string Name => IsRoot
        ? string.Empty
        : CanonicalRelativePath[(CanonicalRelativePath.LastIndexOf('/') + 1)..];

    public StorageAddress Parent
    {
        get
        {
            if (IsRoot)
            {
                return this;
            }

            var separator = CanonicalRelativePath.LastIndexOf('/');
            var parentPath = separator < 0 ? string.Empty : CanonicalRelativePath[..separator];
            return new StorageAddress(ProfileId, RootIdentity, parentPath, null, null, null);
        }
    }

    public static StorageResult<StorageAddress> Create(
        ConnectionProfileId profileId,
        string rootIdentity,
        string relativePath,
        string? nativeItemId = null,
        string? versionId = null,
        string? entityTag = null)
    {
        if (profileId.IsEmpty)
        {
            return Invalid("A non-empty connection profile ID is required.");
        }

        var rootValidation = ValidateOpaque(rootIdentity, "root identity", required: true);
        if (rootValidation is not null)
        {
            return Invalid(rootValidation);
        }

        var nativeValidation = ValidateOpaque(nativeItemId, "native item ID", required: false);
        if (nativeValidation is not null)
        {
            return Invalid(nativeValidation);
        }

        var versionValidation = ValidateOpaque(versionId, "version ID", required: false);
        if (versionValidation is not null)
        {
            return Invalid(versionValidation);
        }

        var entityTagValidation = ValidateOpaque(entityTag, "entity tag", required: false);
        if (entityTagValidation is not null)
        {
            return Invalid(entityTagValidation);
        }

        var normalized = NormalizeRelativePath(relativePath);
        if (normalized.IsFailure)
        {
            return StorageResult<StorageAddress>.Fail(normalized.Error);
        }

        return StorageResult<StorageAddress>.Success(new StorageAddress(
            profileId,
            rootIdentity,
            normalized.Value,
            nativeItemId,
            versionId,
            entityTag));
    }

    public StorageResult<StorageAddress> Append(string relativeChildPath)
    {
        var combined = IsRoot
            ? relativeChildPath
            : CanonicalRelativePath + "/" + relativeChildPath;
        return Create(ProfileId, RootIdentity, combined);
    }

    public override string ToString() => $"{ProfileId}:{CanonicalRelativePath}";

    private static StorageResult<string> NormalizeRelativePath(string? path)
    {
        if (path is null)
        {
            return InvalidPath("A relative path is required.");
        }

        if (path.Length > MaximumPathLength)
        {
            return InvalidPath($"The relative path exceeds {MaximumPathLength} characters.");
        }

        if (path.Length > 0 && (path[0] is '/' or '\\'))
        {
            return InvalidPath("An absolute or UNC path cannot be used as a root-relative address.");
        }

        if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
        {
            return InvalidPath("A drive-qualified path cannot be used as a root-relative address.");
        }

        if (path.Any(char.IsControl))
        {
            return InvalidPath("A relative path cannot contain control characters.");
        }

        var parts = path
            .Normalize(NormalizationForm.FormC)
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        var canonical = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            if (part == ".")
            {
                continue;
            }

            if (part == ".." || IsEncodedTraversal(part))
            {
                return InvalidPath("Parent traversal is not allowed in a storage address.");
            }

            canonical.Add(part);
        }

        return StorageResult<string>.Success(string.Join('/', canonical));
    }

    private static bool IsEncodedTraversal(string part)
    {
        if (!part.Contains('%', StringComparison.Ordinal))
        {
            return false;
        }

        var decoded = part;
        for (var pass = 0; pass < 2; pass++)
        {
            var previous = decoded;
            decoded = Uri.UnescapeDataString(decoded).Replace('\\', '/');
            if (decoded.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment == ".."))
            {
                return true;
            }

            if (decoded == previous)
            {
                break;
            }
        }

        return decoded is "." or "..";
    }

    private static string? ValidateOpaque(string? value, string description, bool required)
    {
        if (value is null)
        {
            return required ? $"A {description} is required." : null;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return $"The {description} cannot be empty or whitespace.";
        }

        if (value.Length > MaximumOpaqueValueLength)
        {
            return $"The {description} exceeds {MaximumOpaqueValueLength} characters.";
        }

        return value.Any(char.IsControl)
            ? $"The {description} cannot contain control characters."
            : null;
    }

    private static StorageResult<StorageAddress> Invalid(string message) =>
        StorageResult<StorageAddress>.Fail(new StorageFailure(
            "storage.address.invalid",
            StorageFailureKind.Validation,
            message));

    private static StorageResult<string> InvalidPath(string message) =>
        StorageResult<string>.Fail(new StorageFailure(
            "storage.address.invalid_path",
            StorageFailureKind.Validation,
            message));
}
