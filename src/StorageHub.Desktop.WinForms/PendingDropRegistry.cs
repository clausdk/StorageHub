using System.Globalization;

namespace StorageHub.Desktop;

/// <summary>
/// How far a dragged-out selection has got before it becomes durable agent work.
/// </summary>
public enum PendingDropState
{
    /// <summary>The drag has started; Explorer has not yet reported where it was dropped.</summary>
    AwaitingDestination = 1,

    /// <summary>The destination is known and the agent has accepted the export.</summary>
    Queued = 2,

    /// <summary>The gesture ended without a usable drop.</summary>
    Cancelled = 3,

    /// <summary>The drop could not be turned into agent work.</summary>
    Failed = 4
}

public sealed record PendingDropEntry(
    string Token,
    string Source,
    int ItemCount,
    PendingDropState State,
    string? Destination,
    string? Detail,
    DateTimeOffset StartedUtc,
    DateTimeOffset UpdatedUtc)
{
    public bool IsTerminal => State is PendingDropState.Cancelled or PendingDropState.Queued
        or PendingDropState.Failed;

    public string Describe() => State switch
    {
        PendingDropState.AwaitingDestination => "Waiting for destination",
        PendingDropState.Queued => "Queued",
        PendingDropState.Cancelled => Detail is null ? "Cancelled" : $"Cancelled: {Detail}",
        _ => Detail is null ? "Failed" : $"Failed: {Detail}"
    };

    public string DescribeSource() => ItemCount == 1
        ? Source
        : string.Create(CultureInfo.CurrentCulture, $"{ItemCount:N0} items from {Source}");
}

/// <summary>
/// Tracks drags out to File Explorer between the moment the gesture starts and the moment the agent
/// turns them into durable transfers.
///
/// These entries are deliberately desktop-local and never durable: until Explorer reports a
/// destination there is no transfer intent to record, which is why the agent cannot create a real
/// job yet. Surfacing them anyway closes the window where a drag looked like it had done nothing,
/// but they are always labelled as pending so they cannot be mistaken for committed work.
/// </summary>
public sealed class PendingDropRegistry
{
    /// <summary>Terminal entries linger briefly so an outcome can be read, then clear themselves.</summary>
    public static readonly TimeSpan TerminalLifetime = TimeSpan.FromMinutes(1);

    private const int MaximumEntries = 32;

    private readonly object _gate = new();
    private readonly Dictionary<string, PendingDropEntry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    public PendingDropRegistry(TimeProvider? timeProvider = null) =>
        _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>Raised whenever an entry is added or changes state, so views can refresh promptly.</summary>
    public event EventHandler? Changed;

    public void Begin(string token, string source, int itemCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            PruneExpired(now);
            if (_entries.Count >= MaximumEntries && !_entries.ContainsKey(token))
            {
                // Drop the oldest terminal entry rather than refuse to record a live gesture.
                var stale = _entries.Values
                    .Where(static entry => entry.IsTerminal)
                    .OrderBy(static entry => entry.UpdatedUtc)
                    .FirstOrDefault();
                if (stale is not null)
                {
                    _entries.Remove(stale.Token);
                }
                else
                {
                    return;
                }
            }

            _entries[token] = new PendingDropEntry(
                token,
                string.IsNullOrWhiteSpace(source) ? "selection" : source,
                itemCount < 1 ? 1 : itemCount,
                PendingDropState.AwaitingDestination,
                Destination: null,
                Detail: null,
                now,
                now);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void MarkQueued(string token, string? destination) =>
        Transition(token, PendingDropState.Queued, destination, detail: null);

    public void MarkCancelled(string token, string? reason) =>
        Transition(token, PendingDropState.Cancelled, destination: null, reason);

    public void MarkFailed(string token, string? reason) =>
        Transition(token, PendingDropState.Failed, destination: null, reason);

    /// <summary>
    /// Returns live entries newest first, pruning any terminal entry whose display window has
    /// passed. Reading is what expires them, so no timer is needed.
    /// </summary>
    public IReadOnlyList<PendingDropEntry> Snapshot()
    {
        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            PruneExpired(now);
            return [.. _entries.Values.OrderByDescending(static entry => entry.StartedUtc)];
        }
    }

    private void Transition(string token, PendingDropState state, string? destination, string? detail)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            if (!_entries.TryGetValue(token, out var existing))
            {
                return;
            }

            // A gesture only settles once. Reporting cancellation after a successful queue would
            // otherwise overwrite the outcome the user needs to see.
            if (existing.IsTerminal)
            {
                return;
            }

            _entries[token] = existing with
            {
                State = state,
                Destination = destination ?? existing.Destination,
                Detail = detail,
                UpdatedUtc = now
            };
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void PruneExpired(DateTimeOffset now)
    {
        List<string>? expired = null;
        foreach (var entry in _entries.Values)
        {
            if (entry.IsTerminal && now - entry.UpdatedUtc >= TerminalLifetime)
            {
                (expired ??= []).Add(entry.Token);
            }
        }

        if (expired is null)
        {
            return;
        }

        foreach (var token in expired)
        {
            _entries.Remove(token);
        }
    }
}
