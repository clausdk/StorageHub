namespace StorageHub.Desktop;

internal enum ConnectionProfileSectionKind
{
    Favorites,
    Folder,
    Provider,
    Disabled
}

internal sealed record ConnectionProfileTreeSection(
    ConnectionProfileSectionKind Kind,
    string Key,
    string Label,
    IReadOnlyList<ConnectionCardModel> Connections);

internal static class ConnectionProfileTree
{
    internal static IReadOnlyList<ConnectionProfileTreeSection> Build(
        IEnumerable<ConnectionCardModel> connections,
        string? searchText = null)
    {
        ArgumentNullException.ThrowIfNull(connections);
        var query = searchText?.Trim() ?? string.Empty;
        return connections
            .Where(connection => Matches(connection, query))
            .GroupBy(SectionFor, SectionIdentityComparer.Instance)
            .Select(group => new ConnectionProfileTreeSection(
                group.Key.Kind,
                group.Key.Key,
                group.Key.Label,
                group
                    .OrderBy(static connection => connection.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(static connection => connection.Endpoint, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(static connection => connection.ConnectionId)
                    .ToArray()))
            .OrderBy(static section => section.Kind)
            .ThenBy(static section => section.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static bool Matches(ConnectionCardModel connection, string query)
    {
        if (query.Length == 0)
        {
            return true;
        }

        return connection.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            connection.Endpoint.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            connection.State.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            (connection.FolderPath?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false) ||
            connection.Descriptor.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            connection.DisplayTags.Any(tag =>
                tag.Contains(query, StringComparison.CurrentCultureIgnoreCase));
    }

    private static SectionIdentity SectionFor(ConnectionCardModel connection)
    {
        if (!connection.IsEnabled)
        {
            return new SectionIdentity(
                ConnectionProfileSectionKind.Disabled,
                "disabled",
                "Disabled");
        }

        if (connection.IsFavorite)
        {
            return new SectionIdentity(
                ConnectionProfileSectionKind.Favorites,
                "favorites",
                "Favorites");
        }

        var folder = connection.FolderPath?.Trim();
        if (!string.IsNullOrEmpty(folder))
        {
            return new SectionIdentity(
                ConnectionProfileSectionKind.Folder,
                folder,
                folder);
        }

        return new SectionIdentity(
            ConnectionProfileSectionKind.Provider,
            connection.Provider.ToString(),
            connection.Descriptor.DisplayName);
    }

    private sealed record SectionIdentity(
        ConnectionProfileSectionKind Kind,
        string Key,
        string Label);

    private sealed class SectionIdentityComparer : IEqualityComparer<SectionIdentity>
    {
        internal static SectionIdentityComparer Instance { get; } = new();

        public bool Equals(SectionIdentity? left, SectionIdentity? right) =>
            ReferenceEquals(left, right) ||
            left is not null &&
            right is not null &&
            left.Kind == right.Kind &&
            StringComparer.CurrentCultureIgnoreCase.Equals(left.Key, right.Key);

        public int GetHashCode(SectionIdentity value) => HashCode.Combine(
            value.Kind,
            StringComparer.CurrentCultureIgnoreCase.GetHashCode(value.Key));
    }
}
