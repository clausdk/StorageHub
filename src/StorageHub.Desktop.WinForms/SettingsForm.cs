using Krypton.Toolkit;

namespace StorageHub.Desktop;

public sealed class SettingsForm : KryptonForm
{
    private readonly ListBox _categories;
    private readonly TextBox _search;
    private readonly Panel _content;
    private readonly Label _categoryTitle;
    private readonly Label _categorySummary;

    public SettingsForm()
    {
        Text = "Settings — StorageHub";
        AccessibleName = "StorageHub Settings";
        AccessibleDescription = "Configure safe defaults for the desktop, providers, transfers, synchronization, and security.";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(900, 620);
        Size = new Size(1060, 730);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = StorageHubTheme.Canvas;
        Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

        _categories = new ListBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            IntegralHeight = false,
            ItemHeight = 30,
            AccessibleName = "Settings categories",
            AccessibleDescription = "Choose a settings category."
        };
        _categories.Items.AddRange(SettingsPresentationCatalog.All.Cast<object>().ToArray());
        _categories.SelectedIndexChanged += CategorySelected;

        _search = new TextBox
        {
            Dock = DockStyle.Top,
            PlaceholderText = "Search settings…",
            AccessibleName = "Search settings"
        };
        _search.TextChanged += SearchTextChanged;

        var leftHeader = new Panel { Dock = DockStyle.Top, Height = 76, Padding = new Padding(0, 0, 0, 10) };
        var heading = UiControlFactory.CreateSectionTitle("Settings");
        heading.Dock = DockStyle.Top;
        _search.Dock = DockStyle.Bottom;
        leftHeader.Controls.Add(_search);
        leftHeader.Controls.Add(heading);

        var left = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            BackColor = StorageHubTheme.Surface
        };
        left.Controls.Add(_categories);
        left.Controls.Add(leftHeader);

        _categoryTitle = new Label
        {
            Dock = DockStyle.Top,
            Height = 34,
            Font = StorageHubTheme.CreateSectionFont(),
            ForeColor = StorageHubTheme.Text,
            AccessibleName = "Selected settings category"
        };
        _categorySummary = new Label
        {
            Dock = DockStyle.Top,
            Height = 38,
            ForeColor = StorageHubTheme.TextMuted,
            AccessibleName = "Settings category summary"
        };
        _content = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = StorageHubTheme.Surface,
            AccessibleName = "Settings values"
        };

        var rightHeader = new Panel
        {
            Dock = DockStyle.Top,
            Height = 90,
            Padding = new Padding(18, 14, 18, 4),
            BackColor = StorageHubTheme.Surface
        };
        rightHeader.Controls.Add(_categorySummary);
        rightHeader.Controls.Add(_categoryTitle);

        var right = new Panel { Dock = DockStyle.Fill, BackColor = StorageHubTheme.Surface };
        right.Controls.Add(_content);
        right.Controls.Add(rightHeader);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Size = new Size(950, 600),
            SplitterDistance = 260,
            FixedPanel = FixedPanel.Panel1,
            Panel1MinSize = 210,
            Panel2MinSize = 500,
            BackColor = StorageHubTheme.Border,
            AccessibleName = "Settings navigation and values"
        };
        split.Panel1.Padding = new Padding(0, 0, 3, 0);
        split.Panel2.Padding = new Padding(3, 0, 0, 0);
        split.Panel1.Controls.Add(left);
        split.Panel2.Controls.Add(right);

        var footer = BuildFooter();
        Controls.Add(split);
        Controls.Add(footer);
        _categories.SelectedIndex = 0;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _categories.SelectedIndexChanged -= CategorySelected;
            _search.TextChanged -= SearchTextChanged;
        }

        base.Dispose(disposing);
    }

    private TableLayoutPanel BuildFooter()
    {
        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 58,
            ColumnCount = 2,
            Padding = new Padding(12, 8, 12, 8),
            BackColor = StorageHubTheme.Surface
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var safety = new Label
        {
            Text = "Security-sensitive changes take effect in the background agent after Apply.",
            AutoSize = true,
            ForeColor = StorageHubTheme.TextMuted,
            Padding = new Padding(5, 9, 5, 0)
        };
        footer.Controls.Add(safety, 0, 0);

        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        var reset = new Button { Text = "Reset category" };
        StorageHubTheme.StyleSecondaryButton(reset);
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
        StorageHubTheme.StyleSecondaryButton(cancel);
        var apply = new Button { Text = "Apply" };
        StorageHubTheme.StyleSecondaryButton(apply);
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK };
        StorageHubTheme.StylePrimaryButton(ok);
        buttons.Controls.Add(reset);
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(apply);
        buttons.Controls.Add(ok);
        footer.Controls.Add(buttons, 1, 0);
        AcceptButton = ok;
        CancelButton = cancel;
        return footer;
    }

    private void CategorySelected(object? sender, EventArgs e)
    {
        if (_categories.SelectedItem is SettingsCategoryDescriptor category)
        {
            ShowCategory(category);
        }
    }

    private void SearchTextChanged(object? sender, EventArgs e)
    {
        var query = _search.Text.Trim();
        var filtered = string.IsNullOrEmpty(query)
            ? SettingsPresentationCatalog.All
            : SettingsPresentationCatalog.All.Where(category =>
                    category.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                    category.Summary.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                    category.Settings.Any(setting =>
                        setting.Label.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                        setting.Description.Contains(query, StringComparison.CurrentCultureIgnoreCase)))
                .ToArray();

        _categories.BeginUpdate();
        try
        {
            _categories.Items.Clear();
            _categories.Items.AddRange(filtered.Cast<object>().ToArray());
            if (_categories.Items.Count > 0)
            {
                _categories.SelectedIndex = 0;
            }
            else
            {
                ShowNoResults();
            }
        }
        finally
        {
            _categories.EndUpdate();
        }
    }

    private void ShowCategory(SettingsCategoryDescriptor category)
    {
        _categoryTitle.Text = category.Name;
        _categorySummary.Text = category.Summary;
        DisposeChildren(_content);

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            Padding = new Padding(18, 4, 18, 18),
            BackColor = StorageHubTheme.Surface
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 225));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var setting in category.Settings)
        {
            UiControlFactory.AddLabeledRow(table, setting.Label, CreateSettingControl(setting), setting.Description);
        }

        _content.Controls.Add(table);
    }

    private void ShowNoResults()
    {
        _categoryTitle.Text = "No matching settings";
        _categorySummary.Text = "Try a provider, transfer, sync, security, or interface term.";
        DisposeChildren(_content);
        _content.Controls.Add(new Label
        {
            Text = "No settings match this search.",
            Dock = DockStyle.Top,
            Height = 48,
            Padding = new Padding(22, 14, 22, 8),
            ForeColor = StorageHubTheme.TextMuted
        });
    }

    private static Control CreateSettingControl(SettingDescriptor setting)
    {
        return setting.Kind switch
        {
            ConnectionFieldKind.Toggle => new CheckBox
            {
                Text = bool.TryParse(setting.DefaultValue, out var enabled) && enabled ? "Enabled" : "Disabled",
                Checked = enabled,
                AutoSize = true
            },
            ConnectionFieldKind.Number => new NumericUpDown
            {
                Minimum = 0,
                Maximum = 100000,
                Value = decimal.TryParse(setting.DefaultValue, out var number) ? number : 0,
                Width = 140
            },
            ConnectionFieldKind.Choice => CreateChoice(setting),
            _ => new TextBox { Text = setting.DefaultValue }
        };
    }

    private static ComboBox CreateChoice(SettingDescriptor setting)
    {
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        combo.Items.AddRange(setting.Choices?.Cast<object>().ToArray() ?? []);
        if (combo.Items.Count > 0)
        {
            var index = combo.FindStringExact(setting.DefaultValue);
            combo.SelectedIndex = index >= 0 ? index : 0;
        }

        return combo;
    }

    private static void DisposeChildren(Control parent)
    {
        var children = parent.Controls.Cast<Control>().ToArray();
        parent.Controls.Clear();
        foreach (var child in children)
        {
            child.Dispose();
        }
    }
}
