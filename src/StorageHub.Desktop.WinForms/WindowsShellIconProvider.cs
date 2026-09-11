using System.Runtime.InteropServices;

namespace StorageHub.Desktop;

/// <summary>Small association-icon cache. Remote names are resolved with USEFILEATTRIBUTES so
/// Windows never attempts to touch a provider path; thumbnails are intentionally never requested.</summary>
internal sealed class WindowsShellIconProvider : IDisposable
{
    private const uint ShgfiIcon = 0x100;
    private const uint ShgfiSmallIcon = 0x1;
    private const uint ShgfiUseFileAttributes = 0x10;
    private const uint FileAttributeNormal = 0x80;
    private const uint FileAttributeDirectory = 0x10;
    private readonly ImageList _images;
    /// <summary>Rows inspected by <see cref="Prime"/>. Comfortably larger than any visible page,
    /// so a first paint finds its icons already cached.</summary>
    private const int MaximumPrimeScan = 512;
    private readonly Dictionary<string, string> _keys = new(StringComparer.OrdinalIgnoreCase);

    public WindowsShellIconProvider(ImageList images) => _images = images ?? throw new ArgumentNullException(nameof(images));

    /// <summary>Loads association icons before a virtual ListView starts requesting rows. Mutating
    /// its native ImageList from RetrieveVirtualItem can abort the current paint pass and leave
    /// rows blank until they are individually invalidated by the mouse.</summary>
    /// <remarks>
    /// Icons are cached per file-extension rather than per row, so only the leading rows need to be
    /// inspected to cover the extensions a first paint can display. The list is a virtualized view
    /// backed by SQLite, so walking all of it here would page every row in from disk on each
    /// filter keystroke. Rows beyond the scan window still resolve on demand through
    /// <see cref="GetKey"/>; they just miss this pre-paint warm-up.
    /// </remarks>
    public void Prime(IEnumerable<BrowserListItem> items, bool local)
    {
        ArgumentNullException.ThrowIfNull(items);
        var scanned = 0;
        foreach (var item in items)
        {
            if (scanned++ >= MaximumPrimeScan)
            {
                return;
            }

            _ = GetKey(item, local);
        }
    }

    public string GetKey(BrowserListItem item, bool local)
    {
        if (item.IsParentNavigation)
        {
            return "folder";
        }

        var path = local && !string.IsNullOrWhiteSpace(item.Location) ? item.Location! : item.Name;
        var attributes = item.IsContainer ? FileAttributeDirectory : FileAttributeNormal;
        var identity = (item.IsContainer ? "directory" : Path.GetExtension(item.Name).ToLowerInvariant()) + ":" + attributes;
        if (_keys.TryGetValue(identity, out var existing)) return existing;
        var info = SHGetFileInfo(path, attributes, out var shell, (uint)Marshal.SizeOf<SHFILEINFO>(), ShgfiIcon | ShgfiSmallIcon | (!local ? ShgfiUseFileAttributes : 0));
        if (info == IntPtr.Zero || shell.hIcon == IntPtr.Zero) return item.IsContainer ? "folder" : "file";
        using var icon = Icon.FromHandle(shell.hIcon);
        try
        {
            var key = "shell:" + identity;
            _images.Images.Add(key, icon.ToBitmap());
            _keys[identity] = key;
            return key;
        }
        finally { _ = DestroyIcon(shell.hIcon); }
    }

    public void Dispose() => _keys.Clear();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO { public IntPtr hIcon; public int iIcon; public uint dwAttributes; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SHGetFileInfo(string path, uint attributes, out SHFILEINFO info, uint size, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(IntPtr icon);
}
