using Microsoft.Data.Sqlite;

namespace StorageHub.Desktop;

/// <summary>
/// Disk-backed, page-cached view of one pane listing. Metadata is spooled outside the UI heap,
/// while ListView virtual-row reads retain only a small LRU of decoded pages.
/// </summary>
internal sealed class PagedListingIndex : IDisposable
{
    private const int CachePageSize = 256;
    private const int MaximumCachedPages = 16;
    private readonly object _gate = new();
    private readonly string _databasePath;
    private readonly SqliteConnection _connection;
    private readonly Dictionary<(long Version, int Page), CachePage> _cache = [];
    private long _cacheSequence;
    private long _queryVersion;
    private bool _disposed;

    internal int CachedPageCount
    {
        get { lock (_gate) return _cache.Count; }
    }

    public PagedListingIndex()
    {
        var cacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StorageHub",
            "Desktop",
            "listing-cache");
        Directory.CreateDirectory(cacheRoot);
        Scavenge(cacheRoot);
        _databasePath = Path.Combine(cacheRoot, $"listing-{Environment.ProcessId}-{Guid.NewGuid():N}.db");
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        _connection.Open();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=MEMORY;
            PRAGMA synchronous=OFF;
            PRAGMA temp_store=MEMORY;
            CREATE TABLE item (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                size_text TEXT NOT NULL,
                type_text TEXT NOT NULL,
                modified_text TEXT NOT NULL,
                status_text TEXT NOT NULL,
                location TEXT NULL,
                is_container INTEGER NOT NULL,
                kind INTEGER NOT NULL,
                length INTEGER NULL,
                native_id TEXT NULL,
                version_id TEXT NULL,
                entity_tag TEXT NULL,
                modified_ticks INTEGER NULL
            );
            CREATE INDEX item_name ON item(is_container DESC, name COLLATE NOCASE, sequence);
            CREATE INDEX item_size ON item(is_container DESC, length, name COLLATE NOCASE, sequence);
            CREATE INDEX item_type ON item(is_container DESC, type_text COLLATE NOCASE, name COLLATE NOCASE, sequence);
            CREATE INDEX item_modified ON item(is_container DESC, modified_ticks, name COLLATE NOCASE, sequence);
            CREATE INDEX item_status ON item(is_container DESC, status_text COLLATE NOCASE, name COLLATE NOCASE, sequence);
            CREATE UNIQUE INDEX item_location_unique ON item(location) WHERE location IS NOT NULL;
            """;
        _ = command.ExecuteNonQuery();
    }

    public IReadOnlyList<BrowserListItem> CreateView(
        BrowserSortColumn sortColumn,
        bool ascending,
        string? filter = null)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return new IndexedView(this, ++_queryVersion, sortColumn, ascending, filter?.Trim());
        }
    }

    public void Reset(IEnumerable<BrowserListItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        lock (_gate)
        {
            ThrowIfDisposed();
            using var clear = _connection.CreateCommand();
            clear.CommandText = "DELETE FROM item; DELETE FROM sqlite_sequence WHERE name = 'item';";
            _ = clear.ExecuteNonQuery();
            AppendCore(items);
            InvalidateCache();
        }
    }

    public void Append(IEnumerable<BrowserListItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        lock (_gate)
        {
            ThrowIfDisposed();
            AppendCore(items);
            InvalidateCache();
        }
    }

    public int? FindIndex(
        BrowserSortColumn sortColumn,
        bool ascending,
        string? filter,
        string location)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        lock (_gate)
        {
            ThrowIfDisposed();
            using var command = _connection.CreateCommand();
            var where = WhereClause(filter?.Trim(), command);
            var direction = ascending ? "ASC" : "DESC";
            var sort = SortExpression(sortColumn, direction);
            command.CommandText = $"""
                SELECT row_index FROM (
                    SELECT location,
                           ROW_NUMBER() OVER (
                               ORDER BY is_container DESC, {sort},
                                        name COLLATE NOCASE {direction}, sequence) - 1 AS row_index
                    FROM item{where}
                ) ranked
                WHERE location = $location
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$location", location);
            var value = command.ExecuteScalar();
            return value is long index ? checked((int)index) : null;
        }
    }

    public IReadOnlyList<BrowserListItem> FindByNames(IReadOnlyCollection<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (names.Count == 0) return [];
        lock (_gate)
        {
            ThrowIfDisposed();
            using var command = _connection.CreateCommand();
            var parameters = new List<string>(names.Count);
            var parameterIndex = 0;
            foreach (var name in names.Distinct(StringComparer.Ordinal))
            {
                var parameterName = "$name" + parameterIndex++;
                parameters.Add(parameterName);
                command.Parameters.AddWithValue(parameterName, name);
            }
            command.CommandText = $"""
                SELECT name, size_text, type_text, modified_text, status_text, location,
                       is_container, kind, length, native_id, version_id, entity_tag, modified_ticks
                FROM item
                WHERE name IN ({string.Join(",", parameters)});
                """;
            using var reader = command.ExecuteReader();
            var items = new List<BrowserListItem>();
            while (reader.Read()) items.Add(ReadItem(reader));
            return items;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _cache.Clear();
            _connection.Dispose();
        }

        TryDelete(_databasePath);
        TryDelete(_databasePath + "-shm");
        TryDelete(_databasePath + "-wal");
    }

    private void AppendCore(IEnumerable<BrowserListItem> items)
    {
        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO item (
                name, size_text, type_text, modified_text, status_text, location,
                is_container, kind, length, native_id, version_id, entity_tag, modified_ticks)
            VALUES (
                $name, $size, $type, $modified, $status, $location,
                $container, $kind, $length, $native, $version, $etag, $ticks);
            """;
        var name = command.Parameters.Add("$name", SqliteType.Text);
        var size = command.Parameters.Add("$size", SqliteType.Text);
        var type = command.Parameters.Add("$type", SqliteType.Text);
        var modified = command.Parameters.Add("$modified", SqliteType.Text);
        var status = command.Parameters.Add("$status", SqliteType.Text);
        var location = command.Parameters.Add("$location", SqliteType.Text);
        var container = command.Parameters.Add("$container", SqliteType.Integer);
        var kind = command.Parameters.Add("$kind", SqliteType.Integer);
        var length = command.Parameters.Add("$length", SqliteType.Integer);
        var native = command.Parameters.Add("$native", SqliteType.Text);
        var version = command.Parameters.Add("$version", SqliteType.Text);
        var etag = command.Parameters.Add("$etag", SqliteType.Text);
        var ticks = command.Parameters.Add("$ticks", SqliteType.Integer);
        foreach (var item in items)
        {
            if (item.IsParentNavigation) continue;
            name.Value = item.Name;
            size.Value = item.Size;
            type.Value = item.Type;
            modified.Value = item.Modified;
            status.Value = item.Status;
            location.Value = DbValue(item.Location);
            container.Value = item.IsContainer ? 1 : 0;
            kind.Value = (int)item.Kind;
            length.Value = DbValue(item.Length);
            native.Value = DbValue(item.NativeItemId);
            version.Value = DbValue(item.VersionId);
            etag.Value = DbValue(item.EntityTag);
            ticks.Value = DbValue(item.ModifiedUtc?.UtcTicks);
            _ = command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private int Count(IndexedView view)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM item" + WhereClause(view.Filter, command) + ";";
            return checked((int)(long)command.ExecuteScalar()!);
        }
    }

    private BrowserListItem Get(IndexedView view, int index)
    {
        if (index < 0 || index >= view.Count) throw new ArgumentOutOfRangeException(nameof(index));
        var pageNumber = index / CachePageSize;
        var pageOffset = index % CachePageSize;
        lock (_gate)
        {
            ThrowIfDisposed();
            var key = (view.Version, pageNumber);
            if (!_cache.TryGetValue(key, out var page))
            {
                page = new CachePage(ReadPage(view, pageNumber), ++_cacheSequence);
                _cache[key] = page;
                TrimCache();
            }
            else
            {
                page.LastAccess = ++_cacheSequence;
            }
            return page.Items[pageOffset];
        }
    }

    private BrowserListItem[] ReadPage(IndexedView view, int pageNumber)
    {
        using var command = _connection.CreateCommand();
        var where = WhereClause(view.Filter, command);
        var direction = view.Ascending ? "ASC" : "DESC";
        var sort = SortExpression(view.SortColumn, direction);
        command.CommandText = $"""
            SELECT name, size_text, type_text, modified_text, status_text, location,
                   is_container, kind, length, native_id, version_id, entity_tag, modified_ticks
            FROM item{where}
            ORDER BY is_container DESC, {sort}, name COLLATE NOCASE {direction}, sequence
            LIMIT {CachePageSize} OFFSET {pageNumber * CachePageSize};
            """;
        using var reader = command.ExecuteReader();
        var items = new List<BrowserListItem>(CachePageSize);
        while (reader.Read())
        {
            items.Add(ReadItem(reader));
        }
        return items.ToArray();
    }

    private static BrowserListItem ReadItem(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetInt64(6) != 0,
        (StorageHub.Contracts.Ipc.StorageItemKind)reader.GetInt32(7),
        reader.IsDBNull(8) ? null : reader.GetInt64(8),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        reader.IsDBNull(10) ? null : reader.GetString(10),
        reader.IsDBNull(11) ? null : reader.GetString(11),
        reader.IsDBNull(12) ? null : new DateTimeOffset(reader.GetInt64(12), TimeSpan.Zero));

    private static string WhereClause(string? filter, SqliteCommand command)
    {
        if (string.IsNullOrWhiteSpace(filter)) return string.Empty;
        command.Parameters.AddWithValue("$filter", ToLikePattern(filter));
        return " WHERE name LIKE $filter ESCAPE '\\' COLLATE NOCASE";
    }

    private static string SortExpression(BrowserSortColumn column, string direction) => column switch
    {
        BrowserSortColumn.Name => $"name COLLATE NOCASE {direction}",
        BrowserSortColumn.Size => $"length {direction}",
        BrowserSortColumn.Type => $"type_text COLLATE NOCASE {direction}",
        BrowserSortColumn.Modified => $"modified_ticks {direction}",
        BrowserSortColumn.Status => $"status_text COLLATE NOCASE {direction}",
        _ => $"name COLLATE NOCASE {direction}"
    };

    private static string ToLikePattern(string filter)
    {
        var escaped = filter.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal)
            .Replace("*", "%", StringComparison.Ordinal)
            .Replace("?", "_", StringComparison.Ordinal);
        return filter.IndexOfAny(['*', '?']) >= 0 ? escaped : "%" + escaped + "%";
    }

    private void InvalidateCache()
    {
        _cache.Clear();
        _queryVersion++;
    }

    private void TrimCache()
    {
        while (_cache.Count > MaximumCachedPages)
        {
            var oldest = _cache.MinBy(static pair => pair.Value.LastAccess).Key;
            _cache.Remove(oldest);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    private static object DbValue(object? value) => value ?? DBNull.Value;
    private static void TryDelete(string path) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    private static void Scavenge(string root)
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(root, "listing-*.db*"))
            {
                if (File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddDays(-1)) TryDelete(path);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class IndexedView : IReadOnlyList<BrowserListItem>
    {
        private readonly PagedListingIndex _owner;

        public IndexedView(
            PagedListingIndex owner,
            long version,
            BrowserSortColumn sortColumn,
            bool ascending,
            string? filter)
        {
            _owner = owner;
            Version = version;
            SortColumn = sortColumn;
            Ascending = ascending;
            Filter = filter;
            Count = owner.Count(this);
        }

        public long Version { get; }
        public BrowserSortColumn SortColumn { get; }
        public bool Ascending { get; }
        public string? Filter { get; }
        public int Count { get; }
        public BrowserListItem this[int index] => _owner.Get(this, index);
        public IEnumerator<BrowserListItem> GetEnumerator() { for (var index = 0; index < Count; index++) yield return this[index]; }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed record CachePage(BrowserListItem[] Items, long InitialAccess)
    {
        public long LastAccess { get; set; } = InitialAccess;
    }
}
