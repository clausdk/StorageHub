using Microsoft.Win32;

namespace StorageHub.Desktop;

/// <summary>Resolves the selected desktop appearance and reapplies it to open WinForms windows.</summary>
public static class DesktopAppearanceService
{
    private static readonly ToolStripRenderer SharedMenuRenderer =
        new ToolStripProfessionalRenderer(new DesktopMenuColorTable());
    private static Func<bool> _systemDarkModeReader = ReadSystemDarkMode;
    private static readonly List<WeakReference<Form>> RegisteredWindows = [];
    private static readonly object RegisteredWindowsLock = new();

    static DesktopAppearanceService()
    {
        ToolStripManager.Renderer = SharedMenuRenderer;
        SystemEvents.UserPreferenceChanged += SystemPreferenceChanged;
    }

    public static DesktopAppearance Appearance { get; private set; } = DesktopAppearance.System;

    public static DesktopAppearance EffectiveAppearance { get; private set; } = ResolveEffective(DesktopAppearance.System);

    public static event EventHandler? AppearanceChanged;

    public static void SetAppearance(DesktopAppearance appearance)
    {
        if (!Enum.IsDefined(appearance))
        {
            throw new ArgumentOutOfRangeException(nameof(appearance));
        }

        var previousEffective = EffectiveAppearance;
        var effective = ResolveEffective(appearance);
        if (Appearance == appearance && previousEffective == effective)
        {
            return;
        }

        Appearance = appearance;
        EffectiveAppearance = effective;
        ApplyToOpenForms(previousEffective);
        AppearanceChanged?.Invoke(null, EventArgs.Empty);
    }

    internal static ToolStripRenderer MenuRenderer => SharedMenuRenderer;

    internal static void RegisterWindow(Form form)
    {
        lock (RegisteredWindowsLock)
        {
            RegisteredWindows.RemoveAll(reference => !reference.TryGetTarget(out var target) || target.IsDisposed);
            if (!RegisteredWindows.Any(reference => reference.TryGetTarget(out var target) && ReferenceEquals(target, form)))
            {
                RegisteredWindows.Add(new WeakReference<Form>(form));
            }
        }
    }

    internal static void RefreshSystemAppearance()
    {
        if (Appearance != DesktopAppearance.System)
        {
            return;
        }

        var previousEffective = EffectiveAppearance;
        var effective = ResolveEffective(Appearance);
        if (effective == previousEffective)
        {
            return;
        }

        EffectiveAppearance = effective;
        ApplyToOpenForms(previousEffective);
        AppearanceChanged?.Invoke(null, EventArgs.Empty);
    }

    internal static void SetSystemDarkModeReaderForTests(Func<bool>? reader)
    {
        _systemDarkModeReader = reader ?? ReadSystemDarkMode;
        RefreshSystemAppearance();
    }

    private static DesktopAppearance ResolveEffective(DesktopAppearance appearance) => appearance switch
    {
        DesktopAppearance.Light => DesktopAppearance.Light,
        DesktopAppearance.Dark => DesktopAppearance.Dark,
        DesktopAppearance.System => _systemDarkModeReader() ? DesktopAppearance.Dark : DesktopAppearance.Light,
        _ => DesktopAppearance.Light
    };

    private static bool ReadSystemDarkMode()
    {
        try
        {
            var value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme",
                1);
            return value is int appsUseLightTheme && appsUseLightTheme == 0;
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static void SystemPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.Color or UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle))
        {
            return;
        }

        RefreshSystemAppearance();
    }

    private static void ApplyToOpenForms(DesktopAppearance previousEffective)
    {
        void Apply()
        {
            foreach (Form form in System.Windows.Forms.Application.OpenForms)
            {
                StorageHubTheme.Apply(form, previousEffective);
            }
        }

        Form? owner;
        lock (RegisteredWindowsLock)
        {
            owner = RegisteredWindows
                .Select(reference => reference.TryGetTarget(out var target) ? target : null)
                .FirstOrDefault(form => form is { IsDisposed: false, IsHandleCreated: true });
        }
        if (owner is null || owner.IsDisposed || !owner.IsHandleCreated)
        {
            return;
        }

        if (owner.InvokeRequired)
        {
            owner.BeginInvoke(Apply);
        }
        else
        {
            Apply();
        }
    }

    private sealed class DesktopMenuColorTable : ProfessionalColorTable
    {
        private static StorageHubPalette Palette => StorageHubTheme.CurrentPalette;

        public override Color ToolStripGradientBegin => Palette.Surface;
        public override Color ToolStripGradientMiddle => Palette.Surface;
        public override Color ToolStripGradientEnd => Palette.Surface;
        public override Color MenuStripGradientBegin => Palette.Surface;
        public override Color MenuStripGradientEnd => Palette.Surface;
        public override Color ToolStripDropDownBackground => Palette.Surface;
        public override Color ImageMarginGradientBegin => Palette.Surface;
        public override Color ImageMarginGradientMiddle => Palette.Surface;
        public override Color ImageMarginGradientEnd => Palette.Surface;
        public override Color ToolStripBorder => Palette.Border;
        public override Color MenuBorder => Palette.Border;
        public override Color MenuItemBorder => Palette.Selection;
        public override Color MenuItemSelected => Palette.Selection;
        public override Color MenuItemSelectedGradientBegin => Palette.Selection;
        public override Color MenuItemSelectedGradientEnd => Palette.Selection;
        public override Color ButtonSelectedHighlight => Palette.Selection;
        public override Color ButtonPressedHighlight => Palette.SelectionPressed;
        public override Color SeparatorDark => Palette.Border;
        public override Color SeparatorLight => Palette.Surface;
        public override Color StatusStripGradientBegin => Palette.Surface;
        public override Color StatusStripGradientEnd => Palette.Surface;
    }
}
