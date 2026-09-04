using System.Text.Json;
using System.Text.Json.Serialization;

namespace StorageHub.Desktop;

public enum WorkspaceSplitOrientation
{
    Vertical = 1,
    Horizontal = 2
}

public enum WorkspaceDockEdge
{
    Left = 1,
    Top = 2,
    Right = 3,
    Bottom = 4
}

public enum PaneContentKind
{
    ThisPc = 1,
    ConnectionsHome = 2,
    SavedStorage = 3,
    SshClient = 4,
    Unresolved = 5
}

public abstract record WorkspaceLayoutNode;

public sealed record WorkspacePaneLeaf(Guid PaneId) : WorkspaceLayoutNode;

public sealed record WorkspaceSplitNode(
    WorkspaceSplitOrientation Orientation,
    double Ratio,
    WorkspaceLayoutNode First,
    WorkspaceLayoutNode Second) : WorkspaceLayoutNode;

public sealed record BrowserPaneState(
    PaneContentKind ContentKind,
    Guid? ProfileId = null,
    string? DisplayNameHint = null,
    string? FolderPath = null,
    string? Filter = null,
    BrowserSortColumn SortColumn = BrowserSortColumn.Name,
    bool SortAscending = true);

public sealed class WorkspaceLayoutModel
{
    public const int MaximumPanes = 4;
    public const double MinimumRatio = 0.15;
    public const double MaximumRatio = 0.85;

    public WorkspaceLayoutModel(WorkspaceLayoutNode root)
    {
        Root = root ?? throw new ArgumentNullException(nameof(root));
        ValidateTree(root);
    }

    public WorkspaceLayoutNode Root { get; private set; }
    public IReadOnlyList<Guid> PaneIds => Traverse(Root).ToArray();
    public int PaneCount => PaneIds.Count;

    public static WorkspaceLayoutModel CreatePreset(int paneCount, WorkspaceLayout preferredLayout)
    {
        if (paneCount is < 1 or > MaximumPanes) throw new ArgumentOutOfRangeException(nameof(paneCount));
        var ids = Enumerable.Range(0, paneCount).Select(_ => Guid.NewGuid()).ToArray();
        var vertical = preferredLayout == WorkspaceLayout.SideBySide;
        WorkspaceLayoutNode root = paneCount switch
        {
            1 => new WorkspacePaneLeaf(ids[0]),
            2 => Split(vertical, new WorkspacePaneLeaf(ids[0]), new WorkspacePaneLeaf(ids[1])),
            3 when vertical => Split(true, new WorkspacePaneLeaf(ids[0]), Split(false, new WorkspacePaneLeaf(ids[1]), new WorkspacePaneLeaf(ids[2]))),
            3 => Split(false, new WorkspacePaneLeaf(ids[0]), Split(true, new WorkspacePaneLeaf(ids[1]), new WorkspacePaneLeaf(ids[2]))),
            4 => Split(true,
                Split(false, new WorkspacePaneLeaf(ids[0]), new WorkspacePaneLeaf(ids[1])),
                Split(false, new WorkspacePaneLeaf(ids[2]), new WorkspacePaneLeaf(ids[3]))),
            _ => throw new InvalidOperationException()
        };
        return new WorkspaceLayoutModel(root);
    }

    public bool Split(Guid paneId, WorkspaceDockEdge edge, Guid newPaneId)
    {
        if (PaneCount >= MaximumPanes || newPaneId == Guid.Empty || PaneIds.Contains(newPaneId)) return false;
        var orientation = edge is WorkspaceDockEdge.Left or WorkspaceDockEdge.Right
            ? WorkspaceSplitOrientation.Vertical : WorkspaceSplitOrientation.Horizontal;
        return Replace(paneId, leaf => edge is WorkspaceDockEdge.Left or WorkspaceDockEdge.Top
            ? new WorkspaceSplitNode(orientation, .5, new WorkspacePaneLeaf(newPaneId), leaf)
            : new WorkspaceSplitNode(orientation, .5, leaf, new WorkspacePaneLeaf(newPaneId)));
    }

    public bool Close(Guid paneId)
    {
        if (PaneCount == 1 || !PaneIds.Contains(paneId)) return false;
        Root = Remove(Root, paneId) ?? throw new InvalidOperationException("Closing a pane produced an empty workspace.");
        return true;
    }

    public bool Swap(Guid first, Guid second)
    {
        if (first == second || !PaneIds.Contains(first) || !PaneIds.Contains(second)) return false;
        Root = Map(Root, id => id == first ? second : id == second ? first : id);
        return true;
    }

    public bool MoveBeside(Guid moving, Guid target, WorkspaceDockEdge edge)
    {
        if (moving == target || !PaneIds.Contains(moving) || !PaneIds.Contains(target)) return false;
        var without = Remove(Root, moving);
        if (without is null) return false;
        var orientation = edge is WorkspaceDockEdge.Left or WorkspaceDockEdge.Right
            ? WorkspaceSplitOrientation.Vertical : WorkspaceSplitOrientation.Horizontal;
        var inserted = ReplaceNode(without, target, targetLeaf => edge is WorkspaceDockEdge.Left or WorkspaceDockEdge.Top
            ? new WorkspaceSplitNode(orientation, .5, new WorkspacePaneLeaf(moving), targetLeaf)
            : new WorkspaceSplitNode(orientation, .5, targetLeaf, new WorkspacePaneLeaf(moving)), out var changed);
        if (!changed) return false;
        Root = inserted;
        return true;
    }

    public bool SetRatio(WorkspaceSplitNode node, double ratio)
    {
        if (!double.IsFinite(ratio)) return false;
        ratio = Math.Clamp(ratio, MinimumRatio, MaximumRatio);
        var replacement = node with { Ratio = ratio };
        var changed = false;
        var paneSet = Traverse(node).ToHashSet();
        Root = ReplaceMatchingSplit(Root, paneSet, replacement, ref changed);
        return changed;
    }

    private bool Replace(Guid paneId, Func<WorkspacePaneLeaf, WorkspaceLayoutNode> replacement)
    {
        Root = ReplaceNode(Root, paneId, replacement, out var changed);
        return changed;
    }

    private static WorkspaceLayoutNode ReplaceNode(WorkspaceLayoutNode node, Guid paneId,
        Func<WorkspacePaneLeaf, WorkspaceLayoutNode> replacement, out bool changed)
    {
        if (node is WorkspacePaneLeaf leaf)
        {
            changed = leaf.PaneId == paneId;
            return changed ? replacement(leaf) : leaf;
        }
        var split = (WorkspaceSplitNode)node;
        var first = ReplaceNode(split.First, paneId, replacement, out changed);
        if (changed) return split with { First = first };
        var second = ReplaceNode(split.Second, paneId, replacement, out changed);
        return changed ? split with { Second = second } : split;
    }

    private static WorkspaceLayoutNode? Remove(WorkspaceLayoutNode node, Guid paneId)
    {
        if (node is WorkspacePaneLeaf leaf) return leaf.PaneId == paneId ? null : leaf;
        var split = (WorkspaceSplitNode)node;
        var first = Remove(split.First, paneId);
        var second = Remove(split.Second, paneId);
        if (first is null) return second;
        if (second is null) return first;
        return split with { First = first, Second = second };
    }

    private static WorkspaceLayoutNode Map(WorkspaceLayoutNode node, Func<Guid, Guid> map) => node switch
    {
        WorkspacePaneLeaf leaf => leaf with { PaneId = map(leaf.PaneId) },
        WorkspaceSplitNode split => split with { First = Map(split.First, map), Second = Map(split.Second, map) },
        _ => throw new InvalidOperationException()
    };

    private static WorkspaceLayoutNode ReplaceMatchingSplit(WorkspaceLayoutNode current, HashSet<Guid> paneSet,
        WorkspaceSplitNode replacement, ref bool changed)
    {
        if (current is not WorkspaceSplitNode split) return current;
        if (Traverse(split).ToHashSet().SetEquals(paneSet)) { changed = true; return replacement; }
        var first = ReplaceMatchingSplit(split.First, paneSet, replacement, ref changed);
        var second = ReplaceMatchingSplit(split.Second, paneSet, replacement, ref changed);
        return ReferenceEquals(first, split.First) && ReferenceEquals(second, split.Second)
            ? split : split with { First = first, Second = second };
    }

    public static IEnumerable<Guid> Traverse(WorkspaceLayoutNode node)
    {
        if (node is WorkspacePaneLeaf leaf) { yield return leaf.PaneId; yield break; }
        var split = (WorkspaceSplitNode)node;
        foreach (var id in Traverse(split.First)) yield return id;
        foreach (var id in Traverse(split.Second)) yield return id;
    }

    private static WorkspaceSplitNode Split(bool vertical, WorkspaceLayoutNode first, WorkspaceLayoutNode second) =>
        new(vertical ? WorkspaceSplitOrientation.Vertical : WorkspaceSplitOrientation.Horizontal, .5, first, second);

    private static void ValidateTree(WorkspaceLayoutNode root)
    {
        var ids = Traverse(root).ToArray();
        if (ids.Length is < 1 or > MaximumPanes || ids.Any(id => id == Guid.Empty) || ids.Distinct().Count() != ids.Length)
            throw new ArgumentException("The workspace layout must reference one to four unique pane IDs.", nameof(root));
        ValidateSplits(root);
    }

    private static void ValidateSplits(WorkspaceLayoutNode node)
    {
        if (node is not WorkspaceSplitNode split) return;
        if (!Enum.IsDefined(split.Orientation) || !double.IsFinite(split.Ratio) || split.Ratio is < MinimumRatio or > MaximumRatio)
            throw new ArgumentException("The workspace contains an invalid split.", nameof(node));
        ValidateSplits(split.First); ValidateSplits(split.Second);
    }
}

internal sealed record WorkspaceFileDocument(
    int SchemaVersion,
    string Name,
    Guid ActivePaneId,
    WorkspaceFileNode Layout,
    Dictionary<Guid, BrowserPaneState> Panes);

internal sealed record WorkspaceFileNode(
    string Kind,
    Guid? PaneId = null,
    WorkspaceSplitOrientation? Orientation = null,
    double? Ratio = null,
    WorkspaceFileNode? First = null,
    WorkspaceFileNode? Second = null);

internal static class WorkspaceFileStore
{
    internal const int SchemaVersion = 1;
    internal const int MaximumFileBytes = 256 * 1024;
    internal const string Filter = "StorageHub workspace (*.shw)|*.shw|All files (*.*)|*.*";
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    internal static WorkspaceFileDocument Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var file = new FileInfo(Path.GetFullPath(path));
        if (!file.Exists) throw new InvalidDataException("The workspace file does not exist.");
        if (file.Length is <= 0 or > MaximumFileBytes) throw new InvalidDataException("The workspace file is empty or too large.");
        WorkspaceFileDocument document;
        try
        {
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
            document = JsonSerializer.Deserialize<WorkspaceFileDocument>(stream, Options)
                ?? throw new InvalidDataException("The workspace file is empty.");
        }
        catch (JsonException error) { throw new InvalidDataException("The workspace file is not valid JSON.", error); }
        Validate(document);
        return document;
    }

    internal static void Save(string path, WorkspaceFileDocument document)
    {
        Validate(document);
        path = Path.GetFullPath(Path.ChangeExtension(path, ".shw"));
        var directory = Path.GetDirectoryName(path) ?? throw new IOException("The workspace directory is unavailable.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, document, Options);
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static WorkspaceFileDocument Capture(string name, Guid activePaneId, WorkspaceLayoutNode root,
        IReadOnlyDictionary<Guid, BrowserPaneState> panes) =>
        new(SchemaVersion, name, activePaneId, ToFileNode(root), panes.ToDictionary());

    internal static WorkspaceLayoutNode ToLayout(WorkspaceFileNode node) => node.Kind switch
    {
        "leaf" when node.PaneId is { } id => new WorkspacePaneLeaf(id),
        "split" when node.Orientation is { } orientation && node.Ratio is { } ratio && node.First is not null && node.Second is not null =>
            new WorkspaceSplitNode(orientation, ratio, ToLayout(node.First), ToLayout(node.Second)),
        _ => throw new InvalidDataException("The workspace layout contains an incomplete node.")
    };

    private static WorkspaceFileNode ToFileNode(WorkspaceLayoutNode node) => node switch
    {
        WorkspacePaneLeaf leaf => new("leaf", PaneId: leaf.PaneId),
        WorkspaceSplitNode split => new("split", Orientation: split.Orientation, Ratio: split.Ratio,
            First: ToFileNode(split.First), Second: ToFileNode(split.Second)),
        _ => throw new InvalidOperationException()
    };

    private static void Validate(WorkspaceFileDocument document)
    {
        if (document.SchemaVersion != SchemaVersion) throw new InvalidDataException($"Workspace schema version {document.SchemaVersion} is not supported.");
        if (!IsText(document.Name, 128)) throw new InvalidDataException("The workspace name is missing or too long.");
        WorkspaceLayoutModel model;
        try { model = new WorkspaceLayoutModel(ToLayout(document.Layout)); }
        catch (ArgumentException error) { throw new InvalidDataException(error.Message, error); }
        var ids = model.PaneIds;
        if (!ids.Contains(document.ActivePaneId) || document.Panes.Count != ids.Count || !ids.All(document.Panes.ContainsKey))
            throw new InvalidDataException("The workspace pane records do not match its layout.");
        foreach (var (id, pane) in document.Panes)
        {
            if (id == Guid.Empty || !Enum.IsDefined(pane.ContentKind) || !Enum.IsDefined(pane.SortColumn) ||
                !IsOptionalText(pane.DisplayNameHint, 256) || !IsOptionalText(pane.FolderPath, 4096) || !IsOptionalText(pane.Filter, 512) ||
                pane.ContentKind is PaneContentKind.SavedStorage or PaneContentKind.SshClient && pane.ProfileId is null ||
                pane.ContentKind == PaneContentKind.ThisPc && pane.FolderPath is not null && !Path.IsPathFullyQualified(pane.FolderPath) ||
                pane.ContentKind == PaneContentKind.SavedStorage && pane.FolderPath is not null && Path.IsPathFullyQualified(pane.FolderPath) ||
                pane.ContentKind == PaneContentKind.ConnectionsHome && (pane.ProfileId is not null || pane.FolderPath is not null) ||
                pane.ContentKind == PaneContentKind.SshClient && pane.FolderPath is not null)
                throw new InvalidDataException("A workspace pane contains invalid or out-of-bounds state.");
        }
    }

    private static bool IsText(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && IsOptionalText(value, maximum);
    private static bool IsOptionalText(string? value, int maximum) => value is null || value.Length <= maximum && !value.Any(char.IsControl);
}
