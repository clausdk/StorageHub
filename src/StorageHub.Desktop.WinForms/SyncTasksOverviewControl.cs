using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop;

public sealed class SyncTasksOverviewControl : UserControl
{
    private const int MaximumLoadedRuns = 1_000;
    private readonly ISyncManagementAgentClient _client;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Label _enabledValue;
    private readonly Label _disabledValue;
    private readonly Label _knownRunsValue;
    private readonly ListView _profiles;
    private readonly ListView _runs;
    private readonly Label _status;
    private readonly TabControl _views;
    private readonly TabPage _runReviewPage;
    private readonly SyncRunsControl _runReview;
    private readonly List<SyncRunSummary> _sessionRuns = [];
    private int _refreshing;
    private bool _disposed;

    public SyncTasksOverviewControl()
        : this(new NamedPipeSyncManagementAgentClient())
    {
    }

    internal SyncTasksOverviewControl(ISyncManagementAgentClient client)
    {
        _client = client;
        Dock = DockStyle.Fill;
        BackColor = StorageHubTheme.Canvas;
        AccessibleName = "Synchronization task overview";

        _views = new TabControl
        {
            Dock = DockStyle.Fill,
            AccessibleName = "Sync task views",
            HotTrack = true,
            Padding = new Point(16, 4)
        };
        StorageHubTheme.ConfigureTabs(_views);

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(28, 24, 28, 24),
            BackColor = StorageHubTheme.Canvas
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        content.Controls.Add(new Label
        {
            Text = "Sync tasks",
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 20F),
            ForeColor = StorageHubTheme.Text,
            Margin = new Padding(0, 0, 0, 2)
        });
        content.Controls.Add(new Label
        {
            Text = "Saved synchronization profiles and durable run history from the background agent.",
            AutoSize = true,
            ForeColor = StorageHubTheme.TextMuted,
            Margin = new Padding(1, 0, 0, 16)
        });

        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 0, 0, 18) };
        actions.Controls.Add(CreateButton("New sync profile", UiGlyph.Add, (_, _) => NewProfileRequested?.Invoke(this, EventArgs.Empty), primary: true));
        actions.Controls.Add(CreateButton("Schedules", UiGlyph.Run, (_, _) => SchedulesRequested?.Invoke(this, EventArgs.Empty)));
        actions.Controls.Add(CreateButton("Run history and review", UiGlyph.Compare, ReviewRunClicked));
        actions.Controls.Add(CreateButton("Refresh", UiGlyph.Refresh, async (_, _) => await RefreshAsync()));
        content.Controls.Add(actions);

        var metrics = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 108,
            MinimumSize = new Size(0, 108),
            ColumnCount = 3,
            Margin = new Padding(0, 0, 0, 18)
        };
        metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33F));
        metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33F));
        metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34F));
        _enabledValue = AddMetric(metrics, 0, "Enabled tasks", UiGlyph.Run, StorageHubTheme.Success);
        _disabledValue = AddMetric(metrics, 1, "Disabled tasks", UiGlyph.Pause, StorageHubTheme.TextMuted);
        _knownRunsValue = AddMetric(metrics, 2, "Runs this session", UiGlyph.Compare, StorageHubTheme.Primary);
        content.Controls.Add(metrics);

        var lists = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 260,
            BackColor = StorageHubTheme.Border,
            Margin = Padding.Empty
        };
        _profiles = CreateList("Saved sync tasks", "State", UiGlyph.Compare, out var profilesCard);
        _profiles.Columns.Insert(1, "Behavior", 180);
        _runs = CreateList("Last syncs", "State", UiGlyph.Run, out var runsCard);
        lists.Panel1.Padding = new Padding(0, 0, 0, 5);
        lists.Panel2.Padding = new Padding(0, 5, 0, 0);
        lists.Panel1.Controls.Add(profilesCard);
        lists.Panel2.Controls.Add(runsCard);
        content.Controls.Add(lists);

        _status = new Label
        {
            Text = "Sync tasks will load when this tab is opened.",
            AutoSize = true,
            ForeColor = StorageHubTheme.TextMuted,
            Margin = new Padding(0, 10, 0, 0)
        };
        content.Controls.Add(_status);

        var tasksPage = new TabPage("Tasks")
        {
            AccessibleName = "Synchronization tasks"
        };
        tasksPage.Controls.Add(content);
        _runReview = new SyncRunsControl(_client);
        _runReviewPage = new TabPage("Run history and review")
        {
            AccessibleName = "Synchronization run history and review"
        };
        _runReviewPage.Controls.Add(_runReview);
        _views.TabPages.Add(tasksPage);
        _views.TabPages.Add(_runReviewPage);
        Controls.Add(_views);
        PopulateRuns();
    }

    public event EventHandler? NewProfileRequested;

    public event EventHandler? SchedulesRequested;

    public event EventHandler? ReviewRunRequested;

    public SyncRunsControl RunReview => _runReview;

    public void ShowRunReview()
    {
        _views.SelectedTab = _runReviewPage;
        ReviewRunRequested?.Invoke(this, EventArgs.Empty);
    }

    public void RecordRun(SyncRunSummary run)
    {
        _sessionRuns.RemoveAll(candidate => candidate.SyncRunId == run.SyncRunId);
        _sessionRuns.Insert(0, run);
        if (_sessionRuns.Count > 20)
        {
            _sessionRuns.RemoveRange(20, _sessionRuns.Count - 20);
        }

        PopulateRuns();
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _refreshing, 1) != 0 || _disposed)
        {
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        try
        {
            _status.Text = "Refreshing sync tasks...";
            var response = await _client.ListProfilesAsync(new SyncProfileListRequest(
                IncludeDisabled: true,
                MaximumCount: SyncManagementIpcLimits.MaximumProfileResults), linked.Token).ConfigureAwait(true);
            if (response.Failure is not null)
            {
                throw new InvalidOperationException(response.Failure.Message);
            }

            var runHistory = await LoadRunHistoryAsync(linked.Token).ConfigureAwait(true);

            var enabled = response.Profiles.Count(static value => value.Enabled);
            _enabledValue.Text = enabled.ToString(System.Globalization.CultureInfo.CurrentCulture);
            _disabledValue.Text = (response.Profiles.Length - enabled).ToString(System.Globalization.CultureInfo.CurrentCulture);
            PopulateProfiles(response.Profiles);
            _sessionRuns.Clear();
            _sessionRuns.AddRange(runHistory);
            PopulateRuns();
            _status.Text = $"Updated {DateTime.Now:t}. Showing {_sessionRuns.Count:N0} durable run(s).";
            _status.ForeColor = StorageHubTheme.TextMuted;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested || cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _status.Text = $"Sync tasks unavailable: {exception.Message}";
            _status.ForeColor = StorageHubTheme.Warning;
        }
        finally
        {
            Volatile.Write(ref _refreshing, 0);
        }
    }

    private void ReviewRunClicked(object? sender, EventArgs e) => ShowRunReview();

    private async Task<IReadOnlyList<SyncRunSummary>> LoadRunHistoryAsync(CancellationToken cancellationToken)
    {
        var runs = new List<SyncRunSummary>();
        string? continuation = null;
        do
        {
            var response = await _client.ListRunsAsync(new SyncRunListRequest(
                PageSize: Math.Min(SyncManagementIpcLimits.MaximumPageSize, MaximumLoadedRuns - runs.Count),
                ContinuationToken: continuation), cancellationToken).ConfigureAwait(true);
            if (response.Failure is not null)
            {
                throw new InvalidOperationException(response.Failure.Message);
            }

            runs.AddRange(response.Runs);
            if (runs.Count >= MaximumLoadedRuns)
            {
                break;
            }

            if (response.ContinuationToken is not null &&
                string.Equals(response.ContinuationToken, continuation, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The agent returned a repeated sync-history page token.");
            }

            continuation = response.ContinuationToken;
        }
        while (continuation is not null);

        return runs;
    }

    protected override async void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible && IsHandleCreated)
        {
            await RefreshAsync();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _lifetime.Cancel();
            _client.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _lifetime.Dispose();
        }

        base.Dispose(disposing);
    }

    private void PopulateProfiles(IEnumerable<SyncProfileSummary> profiles)
    {
        _profiles.BeginUpdate();
        try
        {
            _profiles.Items.Clear();
            foreach (var profile in profiles.OrderByDescending(static value => value.UpdatedUtc))
            {
                var item = new ListViewItem(profile.DisplayName, profile.Enabled ? "enabled" : "disabled")
                {
                    Tag = profile.ProfileId
                };
                item.SubItems.Add(SyncBehaviorPickerControl.GetDisplayName(profile.Behavior));
                item.SubItems.Add(profile.Enabled ? "Enabled" : "Disabled");
                item.SubItems.Add(profile.UpdatedUtc.LocalDateTime.ToString(
                    "g",
                    System.Globalization.CultureInfo.CurrentCulture));
                _profiles.Items.Add(item);
            }

            if (_profiles.Items.Count == 0)
            {
                _profiles.Items.Add(new ListViewItem("No sync tasks configured", "empty"));
            }
        }
        finally
        {
            _profiles.EndUpdate();
        }
    }

    private void PopulateRuns()
    {
        _knownRunsValue.Text = _sessionRuns.Count.ToString(System.Globalization.CultureInfo.CurrentCulture);
        _runs.BeginUpdate();
        try
        {
            _runs.Items.Clear();
            foreach (var run in _sessionRuns)
            {
                var item = new ListViewItem(run.SyncRunId.ToString("D"), "run") { Tag = run.SyncRunId };
                item.SubItems.Add(run.Phase.ToString());
                item.SubItems.Add(run.UpdatedUtc.LocalDateTime.ToString(
                    "g",
                    System.Globalization.CultureInfo.CurrentCulture));
                _runs.Items.Add(item);
            }

            if (_runs.Items.Count == 0)
            {
                var item = new ListViewItem("No sync run opened in this session", "empty");
                item.SubItems.Add("Use Review & run or open an existing run");
                _runs.Items.Add(item);
            }
        }
        finally
        {
            _runs.EndUpdate();
        }
    }

    private static Label AddMetric(TableLayoutPanel host, int column, string title, UiGlyph glyph, Color accent)
    {
        var card = CreateCard();
        card.Dock = DockStyle.Fill;
        card.Margin = new Padding(column == 0 ? 0 : 6, 0, column == 2 ? 0 : 6, 0);
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Padding = new Padding(12, 10, 12, 10),
            Margin = Padding.Empty
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        var icon = new PictureBox
        {
            Image = UiIconFactory.Create(glyph, accent, 24),
            SizeMode = PictureBoxSizeMode.CenterImage,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty
        };
        var value = new Label
        {
            Text = "0",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft,
            Font = new Font("Segoe UI Semibold", 15F),
            ForeColor = StorageHubTheme.Text,
            Margin = Padding.Empty
        };
        var titleLabel = new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopLeft,
            ForeColor = StorageHubTheme.TextMuted,
            Margin = Padding.Empty
        };
        grid.Controls.Add(icon, 0, 0);
        grid.SetRowSpan(icon, 2);
        grid.Controls.Add(value, 1, 0);
        grid.Controls.Add(titleLabel, 1, 1);
        card.Controls.Add(grid);
        host.Controls.Add(card, column, 0);
        return value;
    }

    private static ListView CreateList(string title, string thirdColumn, UiGlyph glyph, out Panel card)
    {
        card = CreateCard();
        card.Dock = DockStyle.Fill;
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Padding = new Padding(14, 10, 14, 14),
            Margin = Padding.Empty
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var icon = new PictureBox
        {
            Image = UiIconFactory.Create(glyph, StorageHubTheme.Primary, 20),
            SizeMode = PictureBoxSizeMode.CenterImage,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty
        };
        var titleLabel = new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = StorageHubTheme.CreateSectionFont(),
            ForeColor = StorageHubTheme.Text,
            Margin = Padding.Empty
        };
        var images = new ImageList { ImageSize = new Size(18, 18), ColorDepth = ColorDepth.Depth32Bit };
        images.Images.Add("enabled", UiIconFactory.Create(UiGlyph.Test, StorageHubTheme.Success, 18));
        images.Images.Add("disabled", UiIconFactory.Create(UiGlyph.Pause, StorageHubTheme.TextMuted, 18));
        images.Images.Add("run", UiIconFactory.Create(UiGlyph.Run, StorageHubTheme.Primary, 18));
        images.Images.Add("empty", UiIconFactory.Create(UiGlyph.More, StorageHubTheme.TextMuted, 18));
        var list = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            SmallImageList = images,
            BackColor = StorageHubTheme.Surface,
            ForeColor = StorageHubTheme.Text,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 8, 0, 0)
        };
        StorageHubTheme.ConfigureList(list);
        list.Columns.Add("Name", 360);
        list.Columns.Add(thirdColumn, 180);
        list.Columns.Add("Updated", 180);
        grid.Controls.Add(icon, 0, 0);
        grid.Controls.Add(titleLabel, 1, 0);
        grid.Controls.Add(list, 0, 1);
        grid.SetColumnSpan(list, 2);
        card.Controls.Add(grid);
        return list;
    }

    private static Panel CreateCard() => new()
    {
        BackColor = StorageHubTheme.Surface,
        BorderStyle = BorderStyle.FixedSingle
    };

    private static Button CreateButton(string text, UiGlyph glyph, EventHandler handler, bool primary = false)
    {
        var button = new Button
        {
            Text = text,
            Image = UiIconFactory.Create(glyph, primary ? Color.White : StorageHubTheme.Text, 18),
            TextImageRelation = TextImageRelation.ImageBeforeText,
            AutoSize = true,
            Margin = new Padding(0, 0, 8, 0)
        };
        if (primary)
        {
            StorageHubTheme.StylePrimaryButton(button);
        }
        else
        {
            StorageHubTheme.StyleSecondaryButton(button);
        }

        button.Click += handler;
        return button;
    }
}
