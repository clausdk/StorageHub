namespace StorageHub.Desktop;

internal static class ShortcutSettings
{
    internal static IReadOnlyList<UiCommandDefinition> Commands { get; } = UiCommandCatalog.Definitions
        .Where(command => MainForm.IsAvailableCommand(command.Label)).ToArray();

    internal static Dictionary<string, Keys> Resolve(IReadOnlyDictionary<string, Keys>? overrides)
    {
        var result = Commands.ToDictionary(command => command.Id, command => command.Shortcut, StringComparer.Ordinal);
        if (overrides is null) return result;
        foreach (var command in Commands)
            if (overrides.TryGetValue(command.Id, out var keys)) result[command.Id] = keys;
        return Validate(result) is null ? result : Resolve(null);
    }

    internal static string Format(Keys keys) => keys == Keys.None ? "Unassigned" : new KeysConverter().ConvertToString(keys) ?? keys.ToString();

    internal static bool IsValid(Keys keys)
    {
        if (keys == Keys.None) return true;
        var code = keys & Keys.KeyCode;
        if (!Enum.IsDefined(code) || code is Keys.None or Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin)
            return false;
        if ((keys & ~(Keys.KeyCode | Keys.Control | Keys.Shift | Keys.Alt)) != 0) return false;
        if (keys is (Keys.Alt | Keys.F4) or (Keys.Control | Keys.Alt | Keys.Delete)) return false;
        return (keys & (Keys.Control | Keys.Alt)) != 0 || code is >= Keys.F1 and <= Keys.F24 || keys == Keys.Delete;
    }

    internal static string? Validate(IReadOnlyDictionary<string, Keys> shortcuts)
    {
        var assigned = new Dictionary<Keys, string>();
        foreach (var command in Commands)
        {
            var keys = shortcuts.GetValueOrDefault(command.Id, command.Shortcut);
            if (!IsValid(keys)) return $"Choose Ctrl/Alt with a key, a function key, or Delete for {command.Label}.";
            if (keys == Keys.None) continue;
            if (assigned.TryGetValue(keys, out var other)) return $"{Format(keys)} is already assigned to {other}. Clear that assignment first.";
            assigned.Add(keys, command.Label);
        }
        return null;
    }

    internal static bool IsPaneCommand(UiCommandDefinition command) =>
        command.Menu is "Edit" or "Go" || command.Label == "Refresh";

    internal static bool CanDispatch(UiCommandDefinition command, bool sshFocused, bool textFocused, bool hasPane) =>
        !sshFocused && !textFocused && (!IsPaneCommand(command) || hasPane);
}
