using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop;

internal sealed record ConnectionActivationEventArgs(ConnectionSummary Connection, bool InNewPane);

/// <summary>
/// The shell's saved-connection panel: search at the top, a grouped list in the middle, and the
/// selected connection's details at the bottom.
///
/// This is the only saved-connection list in the app. The Connection Manager is a pure editor that
/// this panel opens; keeping one list means the shell and the editor cannot disagree about what is
/// saved, which is exactly what the dialog's two parallel lists used to do.
/// </summary>
internal sealed class ConnectionsPanelControl : UserControl
{
    private readonly IRemoteStorageAgentClient _storageClient;
    private readonly IRemoteConnectionProfileClient _profileClient;
    private readonly IRemoteSecretVaultClient _secretClient;
    private readonly ConnectionManagerController _controller;
    private readonly bool _ownsClients;
    private readonly CancellationTokenSource _lifetime = new();

    private readonly ConnectionSidebarControl _sidebar;
    private readonly ConnectionDetailView _detail;
    private readonly TextBox _searchBox;
    private readonly Label _status;
    private readonly ContextMenuStrip _rowMenu;

    private readonly List<ConnectionCardModel> _cards = [];
    private readonly Dictionary<Guid, ConnectionSummary> _summaries = [];
    private ConnectionCardModel? _menuTarget;
    private CancellationTokenSource? _detailLoad;
    private int _refreshing;

    internal ConnectionsPanelControl()
        : this(
            new NamedPipeRemoteStorageAgentClient(),
            new NamedPipeRemoteConnectionProfileClient(),
            new NamedPipeRemoteSecretVaultClient(),
            ownsClients: true)
    {
    }

    internal ConnectionsPanelControl(
        IRemoteStorageAgentClient storageClient,
        IRemoteConnectionProfileClient profileClient,
        IRemoteSecretVaultClient secretClient)
        : this(storageClient, profileClient, secretClient, ownsClients: false)
    {
    }

    private ConnectionsPanelControl(
        IRemoteStorageAgentClient storageClient,
        IRemoteConnectionProfileClient profileClient,
        IRemoteSecretVaultClient secretClient,
        bool ownsClients)
    {
        _storageClient = storageClient ?? throw new ArgumentNullException(nameof(storageClient));
        _profileClient = profileClient ?? throw new ArgumentNullException(nameof(profileClient));
        _secretClient = secretClient ?? throw new ArgumentNullException(nameof(secretClient));
        _ownsClients = ownsClients;
        _controller = new ConnectionManagerController(_profileClient, _secretClient);

        Dock = DockStyle.Fill;
        BackColor = StorageHubTheme.Surface;
        AccessibleName = "Connections";
        AccessibleDescription = "Saved connections, grouped by favourites, type, and folder.";

        _sidebar = new ConnectionSidebarControl { ShowRowActions = true };
        _sidebar.ConnectionSelected += (_, card) => ShowDetail(card.ConnectionId);
        _sidebar.ConnectionActivated += (_, card) => RaiseActivation(card, inNewPane: false);
        _sidebar.ConnectionEditRequested += (_, card) => RaiseEdit(card.ConnectionId);
        _sidebar.ConnectionDeleteRequested += async (_, card) => await DeleteAsync(card).ConfigureAwait(true);
        _sidebar.ConnectionMenuRequested += RowMenuRequested;

        _rowMenu = BuildRowMenu();
        _detail = new ConnectionDetailView { Dock = DockStyle.Bottom, Height = 300 };
        _detail.OpenRequested += (_, _) => WithMenuTarget(card => RaiseActivation(card, inNewPane: false));
        _detail.EditRequested += (_, tab) => WithSelection(id => EditRequested?.Invoke(this, new ConnectionEditRequest(id, tab)));
        _detail.DeleteRequested += async (_, _) =>
        {
            if (SelectedCard() is { } card)
            {
                await DeleteAsync(card).ConfigureAwait(true);
            }
        };
        _detail.TestRequested += async (_, _) => await TestSelectedAsync().ConfigureAwait(true);
        _detail.FavoriteToggleRequested += async (_, _) => await ToggleFavoriteAsync().ConfigureAwait(true);

        _status = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 0,
            AutoSize = false,
            Padding = new Padding(12, 4, 12, 4),
            ForeColor = StorageHubTheme.Warning,
            Visible = false,
            AccessibleName = "Connections status"
        };

        _searchBox = new TextBox
        {
            Dock = DockStyle.Bottom,
            PlaceholderText = "Search connections…",
            AccessibleName = "Search connections"
        };
        _searchBox.TextChanged += (_, _) => ApplyFilter();

        Controls.Add(_sidebar);
        Controls.Add(_status);
        Controls.Add(new Splitter
        {
            Dock = DockStyle.Bottom,
            Height = 4,
            MinExtra = 120,
            MinSize = 120,
            BackColor = StorageHubTheme.Border
        });
        Controls.Add(_detail);
        Controls.Add(BuildHeader());
    }

    internal event EventHandler<ConnectionActivationEventArgs>? ConnectionActivated;

    /// <summary>Raised when the editor should open — on a saved connection, or on nothing for a new one.</summary>
    internal event EventHandler<ConnectionEditRequest>? EditRequested;

    /// <summary>Raised after this panel changes what is saved, so the rest of the shell can catch up.</summary>
    internal event EventHandler? ConnectionsChanged;

    /// <summary>Raised when the user asks for the panel to be docked to the other side.</summary>
    internal event EventHandler? MoveSideRequested;

    /// <summary>Raised when the user asks for the panel to be hidden.</summary>
    internal event EventHandler? HideRequested;

    internal Guid? SelectedConnectionId => _sidebar.SelectedConnectionId;

    /// <summary>
    /// Re-lists every saved connection, disabled ones included: the panel shows a Disabled group
    /// that the overview's own cache — which lists enabled connections only — cannot supply.
    /// </summary>
    internal async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        // Several routes can ask at once — a dialog closing, a pane finishing, the panel reappearing.
        if (Interlocked.Exchange(ref _refreshing, 1) == 1)
        {
            return;
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            var response = await _storageClient.ListConnectionsAsync(
                new ConnectionListRequest(
                    StorageIpcContract.CurrentVersion,
                    IncludeDisabled: true,
                    Limit: StorageIpcLimits.MaximumConnectionResults),
                linked.Token).ConfigureAwait(true);
            if (IsDisposed)
            {
                return;
            }

            if (response.Failure is not null)
            {
                ShowStatus(response.Failure.Message);
                return;
            }

            ShowStatus(null);
            _summaries.Clear();
            _cards.Clear();
            foreach (var connection in response.Connections)
            {
                _summaries[connection.ConnectionId] = connection;
                _cards.Add(ConnectionCardFactory.Create(connection));
            }

            ApplyFilter();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            ShowStatus("The background agent is unavailable; saved connections could not be loaded.");
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    private Panel BuildHeader()
    {
        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 92,
            Padding = new Padding(12, 10, 12, 10),
            BackColor = StorageHubTheme.Surface
        };

        var title = UiControlFactory.CreateSectionTitle("Connections");
        title.Dock = DockStyle.Left;

        var overflow = new Button
        {
            Dock = DockStyle.Right,
            Width = 32,
            Text = "⋮",
            FlatStyle = FlatStyle.Flat,
            AccessibleName = "Connections panel options",
            AccessibleDescription = "Move the panel to the other side, refresh it, or hide it."
        };
        StorageHubTheme.StyleInlineButton(overflow);
        overflow.Click += (_, _) => BuildPanelMenu().Show(overflow, new Point(0, overflow.Height));

        var add = new Button
        {
            Dock = DockStyle.Right,
            Width = 86,
            Text = "New",
            AccessibleName = "New connection",
            AccessibleDescription = "Create a saved connection."
        };
        StorageHubTheme.StylePrimaryButton(add);
        add.Click += (_, _) => EditRequested?.Invoke(this, new ConnectionEditRequest(null, ConnectionEditorTab.General));

        var row = new Panel { Dock = DockStyle.Top, Height = 34 };
        row.Controls.Add(title);
        row.Controls.Add(add);
        row.Controls.Add(overflow);

        header.Controls.Add(_searchBox);
        header.Controls.Add(row);
        return header;
    }

    private ContextMenuStrip BuildPanelMenu()
    {
        var menu = new ContextMenuStrip { Renderer = DesktopAppearanceService.MenuRenderer };
        var move = new ToolStripMenuItem("Move to the other side");
        move.Click += (_, _) => MoveSideRequested?.Invoke(this, EventArgs.Empty);
        var refresh = new ToolStripMenuItem("Refresh");
        refresh.Click += async (_, _) => await RefreshAsync(_lifetime.Token).ConfigureAwait(true);
        var hide = new ToolStripMenuItem("Hide panel");
        hide.Click += (_, _) => HideRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.AddRange([move, refresh, new ToolStripSeparator(), hide]);
        return menu;
    }

    private ContextMenuStrip BuildRowMenu()
    {
        var menu = new ContextMenuStrip { Renderer = DesktopAppearanceService.MenuRenderer };
        var open = new ToolStripMenuItem("Open");
        open.Click += (_, _) => WithMenuTarget(card => RaiseActivation(card, inNewPane: false));
        var openInNewPane = new ToolStripMenuItem("Open in new pane");
        openInNewPane.Click += (_, _) => WithMenuTarget(card => RaiseActivation(card, inNewPane: true));
        var favorite = new ToolStripMenuItem("Toggle favorite") { Name = "ToggleFavorite" };
        favorite.Click += async (_, _) => await ToggleFavoriteAsync().ConfigureAwait(true);
        var edit = new ToolStripMenuItem("Edit…");
        edit.Click += (_, _) => WithMenuTarget(card => RaiseEdit(card.ConnectionId));
        var delete = new ToolStripMenuItem("Delete…");
        delete.Click += async (_, _) =>
        {
            if (_menuTarget is { } card)
            {
                await DeleteAsync(card).ConfigureAwait(true);
            }
        };
        menu.Items.AddRange([open, openInNewPane, new ToolStripSeparator(), favorite, edit, new ToolStripSeparator(), delete]);
        return menu;
    }

    private void RowMenuRequested(object? sender, ConnectionRowMenuEventArgs args)
    {
        _menuTarget = args.Connection;
        _rowMenu.Show(args.ScreenLocation);
    }

    private void WithMenuTarget(Action<ConnectionCardModel> action)
    {
        var card = _menuTarget ?? SelectedCard();
        if (card is not null)
        {
            action(card);
        }
    }

    private void WithSelection(Action<Guid?> action) => action(SelectedConnectionId);

    private ConnectionCardModel? SelectedCard() => SelectedConnectionId is { } id
        ? _cards.FirstOrDefault(card => card.ConnectionId == id)
        : null;

    private void RaiseEdit(Guid? connectionId) =>
        EditRequested?.Invoke(this, new ConnectionEditRequest(connectionId, ConnectionEditorTab.General));

    private void RaiseActivation(ConnectionCardModel card, bool inNewPane)
    {
        if (card.ConnectionId is { } id && _summaries.TryGetValue(id, out var summary))
        {
            ConnectionActivated?.Invoke(this, new ConnectionActivationEventArgs(summary, inNewPane));
        }
    }

    private void ApplyFilter()
    {
        _sidebar.SetConnections(_cards, _searchBox.Text, SelectedConnectionId);
        ShowDetail(SelectedConnectionId);
    }

    private void ShowDetail(Guid? connectionId)
    {
        // Any load still in flight is for the previous selection.
        _detailLoad?.Cancel();
        _detailLoad?.Dispose();
        _detailLoad = null;

        if (connectionId is not { } id || !_summaries.TryGetValue(id, out var summary))
        {
            _detail.Show(null);
            return;
        }

        // Draw what the listing already knows straight away, then fill in the endpoint and
        // authentication detail, which only the full profile carries.
        _detail.Show(summary);
        var load = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _detailLoad = load;
        _ = LoadDetailAsync(id, load.Token);
    }

    private async Task LoadDetailAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _controller.GetAsync(connectionId, cancellationToken).ConfigureAwait(true);
            if (!cancellationToken.IsCancellationRequested && !IsDisposed)
            {
                _detail.ShowProfile(connectionId, response.Profile);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // The summary is already on screen; the agent being unreachable costs the extra
            // fields, not the selection.
        }
    }

    private void ShowStatus(string? message)
    {
        _status.Text = message ?? string.Empty;
        _status.Visible = message is not null;
        _status.Height = message is null ? 0 : 40;
    }

    private async Task DeleteAsync(ConnectionCardModel card)
    {
        if (card.ConnectionId is not { } id || !_summaries.TryGetValue(id, out var summary))
        {
            return;
        }

        if (MessageBox.Show(
                FindForm(),
                ConnectionCardFactory.DeleteConfirmationPrompt(summary.DisplayName),
                ConnectionCardFactory.DeleteConfirmationCaption,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            return;
        }

        await DeleteConfirmedAsync(card).ConfigureAwait(true);
    }

    /// <summary>
    /// The delete itself, past the confirmation. Separate so the agent call can be exercised
    /// without a message pump to answer a modal prompt.
    /// </summary>
    private async Task DeleteConfirmedAsync(ConnectionCardModel card)
    {
        if (card.ConnectionId is not { } id || !_summaries.TryGetValue(id, out var summary))
        {
            return;
        }

        try
        {
            // The version comes from the last listing rather than a fresh read: if the editor saved
            // in between, this fails as a conflict instead of deleting a revision nobody saw.
            var response = await _controller.DeleteAsync(id, summary.Version, _lifetime.Token).ConfigureAwait(true);
            if (response.Status != ConnectionProfileWriteStatus.Succeeded)
            {
                ShowStatus(response.Failure?.Message ?? "The connection could not be deleted.");
                await RefreshAsync(_lifetime.Token).ConfigureAwait(true);
                return;
            }

            _sidebar.ClearSelection();
            _detail.Show(null);
            await RefreshAsync(_lifetime.Token).ConfigureAwait(true);
            ConnectionsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            ShowStatus("The connection could not be deleted through the background agent.");
        }
    }

    private async Task ToggleFavoriteAsync()
    {
        var card = _menuTarget ?? SelectedCard();
        if (card?.ConnectionId is not { } id)
        {
            return;
        }

        try
        {
            // IsFavorite lives inside the profile's metadata and saving takes a whole draft, so
            // unlike delete this genuinely needs the document first.
            var current = await _controller.GetAsync(id, _lifetime.Token).ConfigureAwait(true);
            if (current.Profile is not { } profile)
            {
                ShowStatus(current.Failure?.Message ?? "The connection could not be loaded.");
                return;
            }

            var draft = profile.Draft with
            {
                Metadata = profile.Draft.Metadata with { IsFavorite = !profile.Draft.Metadata.IsFavorite }
            };
            var response = await _controller.SaveAsync(draft, profile, _lifetime.Token).ConfigureAwait(true);
            if (response.Status != ConnectionProfileWriteStatus.Succeeded)
            {
                ShowStatus(response.Failure?.Message ?? "The connection could not be updated.");
                return;
            }

            await RefreshAsync(_lifetime.Token).ConfigureAwait(true);
            ConnectionsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            ShowStatus("The connection could not be updated through the background agent.");
        }
    }

    private async Task TestSelectedAsync()
    {
        if (SelectedConnectionId is not { } id)
        {
            return;
        }

        try
        {
            _detail.ShowTesting();
            _ = await _storageClient.TestConnectionAsync(
                new ConnectionTestRequest(StorageIpcContract.CurrentVersion, id),
                _lifetime.Token).ConfigureAwait(true);

            // The agent records the outcome on the profile, so re-listing is what refreshes the
            // health shown here rather than the response itself.
            await RefreshAsync(_lifetime.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            ShowStatus("The connection could not be tested through the background agent.");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _lifetime.Cancel();
            _detailLoad?.Cancel();
            _detailLoad?.Dispose();
        }

        base.Dispose(disposing);

        if (!disposing)
        {
            return;
        }

        _rowMenu.Dispose();
        _lifetime.Dispose();
        if (!_ownsClients)
        {
            return;
        }

        _ = _storageClient.DisposeAsync().AsTask();
        _ = _profileClient.DisposeAsync().AsTask();
        _ = _secretClient.DisposeAsync().AsTask();
    }
}
