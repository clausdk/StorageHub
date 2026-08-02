namespace StorageHub.Sync;

public enum SyncSideDelta
{
    Absent = 0,
    Unchanged = 1,
    Created = 2,
    Modified = 3,
    Deleted = 4,
    Indeterminate = 5,
}

public enum SyncChangeKind
{
    Unchanged = 0,
    LeftCreated = 1,
    RightCreated = 2,
    BothCreatedIdentical = 3,
    LeftModified = 4,
    RightModified = 5,
    BothModifiedIdentical = 6,
    LeftDeleted = 7,
    RightDeleted = 8,
    BothDeleted = 9,
    ConflictBothCreated = 10,
    ConflictBothModified = 11,
    ConflictDeleteModify = 12,
    ConflictIndeterminate = 13,
}

public readonly record struct SyncChangeClassification(
    SyncChangeKind Kind,
    SyncSideDelta LeftDelta,
    SyncSideDelta RightDelta)
{
    public bool IsConflict => Kind is
        SyncChangeKind.ConflictBothCreated or
        SyncChangeKind.ConflictBothModified or
        SyncChangeKind.ConflictDeleteModify or
        SyncChangeKind.ConflictIndeterminate;
}

public static class ThreeWayChangeClassifier
{
    public static SyncChangeClassification Classify(
        SyncBaselineObservation baseline,
        SyncItemObservation left,
        SyncItemObservation right)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        var leftDelta = ClassifySide(baseline, left, baseline.LeftVersionId);
        var rightDelta = ClassifySide(baseline, right, baseline.RightVersionId);

        var kind = ClassifyPair(baseline, left, right, leftDelta, rightDelta);
        return new SyncChangeClassification(kind, leftDelta, rightDelta);
    }

    private static SyncSideDelta ClassifySide(
        SyncBaselineObservation baseline,
        SyncItemObservation current,
        string? baselineVersionId)
    {
        if (!baseline.Exists)
        {
            return current.Exists ? SyncSideDelta.Created : SyncSideDelta.Absent;
        }

        if (!current.Exists)
        {
            return SyncSideDelta.Deleted;
        }

        if (baselineVersionId is not null &&
            current.VersionId is not null &&
            StringComparer.Ordinal.Equals(baselineVersionId, current.VersionId))
        {
            return SyncSideDelta.Unchanged;
        }

        if (baseline.Digest is not null && current.Digest is not null)
        {
            return baseline.Digest == current.Digest
                ? SyncSideDelta.Unchanged
                : SyncSideDelta.Modified;
        }

        if (baseline.Length != current.Length)
        {
            return SyncSideDelta.Modified;
        }

        return SyncSideDelta.Indeterminate;
    }

    private static SyncChangeKind ClassifyPair(
        SyncBaselineObservation baseline,
        SyncItemObservation left,
        SyncItemObservation right,
        SyncSideDelta leftDelta,
        SyncSideDelta rightDelta)
    {
        if (leftDelta == SyncSideDelta.Indeterminate || rightDelta == SyncSideDelta.Indeterminate)
        {
            return SyncChangeKind.ConflictIndeterminate;
        }

        if (!baseline.Exists)
        {
            return (leftDelta, rightDelta) switch
            {
                (SyncSideDelta.Absent, SyncSideDelta.Absent) => SyncChangeKind.Unchanged,
                (SyncSideDelta.Created, SyncSideDelta.Absent) => SyncChangeKind.LeftCreated,
                (SyncSideDelta.Absent, SyncSideDelta.Created) => SyncChangeKind.RightCreated,
                (SyncSideDelta.Created, SyncSideDelta.Created) =>
                    CompareCurrentContent(left, right) switch
                    {
                        ContentComparison.Same => SyncChangeKind.BothCreatedIdentical,
                        ContentComparison.Different => SyncChangeKind.ConflictBothCreated,
                        _ => SyncChangeKind.ConflictIndeterminate,
                    },
                _ => SyncChangeKind.ConflictIndeterminate,
            };
        }

        return (leftDelta, rightDelta) switch
        {
            (SyncSideDelta.Unchanged, SyncSideDelta.Unchanged) => SyncChangeKind.Unchanged,
            (SyncSideDelta.Modified, SyncSideDelta.Unchanged) => SyncChangeKind.LeftModified,
            (SyncSideDelta.Unchanged, SyncSideDelta.Modified) => SyncChangeKind.RightModified,
            (SyncSideDelta.Deleted, SyncSideDelta.Unchanged) => SyncChangeKind.LeftDeleted,
            (SyncSideDelta.Unchanged, SyncSideDelta.Deleted) => SyncChangeKind.RightDeleted,
            (SyncSideDelta.Deleted, SyncSideDelta.Deleted) => SyncChangeKind.BothDeleted,
            (SyncSideDelta.Deleted, SyncSideDelta.Modified) or
            (SyncSideDelta.Modified, SyncSideDelta.Deleted) => SyncChangeKind.ConflictDeleteModify,
            (SyncSideDelta.Modified, SyncSideDelta.Modified) =>
                CompareCurrentContent(left, right) switch
                {
                    ContentComparison.Same => SyncChangeKind.BothModifiedIdentical,
                    ContentComparison.Different => SyncChangeKind.ConflictBothModified,
                    _ => SyncChangeKind.ConflictIndeterminate,
                },
            _ => SyncChangeKind.ConflictIndeterminate,
        };
    }

    private static ContentComparison CompareCurrentContent(
        SyncItemObservation left,
        SyncItemObservation right)
    {
        if (!left.Exists || !right.Exists)
        {
            return ContentComparison.Different;
        }

        if (left.Digest is not null && right.Digest is not null)
        {
            return left.Digest == right.Digest
                ? ContentComparison.Same
                : ContentComparison.Different;
        }

        if (left.Length != right.Length)
        {
            return ContentComparison.Different;
        }

        return ContentComparison.Indeterminate;
    }

    private enum ContentComparison
    {
        Same,
        Different,
        Indeterminate,
    }
}
