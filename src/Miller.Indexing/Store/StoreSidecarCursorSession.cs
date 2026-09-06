using Miller.Indexing.Reads;

namespace Miller.Indexing.Store;

internal delegate StoreConsumerCursorOutcome StoreSidecarCursorAdvance(StoreSidecarCursorKey cursor, long sequence);

internal delegate StoreConsumerCursorOutcome StoreSidecarCursorRelease(StoreSidecarCursorKey cursor);

internal readonly record struct StoreSidecarCursorCompletion(bool Succeeded, bool DidWork, string? Error);

internal interface IStoreSidecarCursorSession
{
    bool TryProtectBaseline(StoreSidecarStamp baseline);

    void PrepareTarget(long sequence);

    StoreSidecarCursorCompletion CompleteCommitted();
}

internal sealed class StoreSidecarCursorSession : IStoreSidecarCursorSession
{
    private const int OperationAttempts = 3;
    private readonly string _storeRoot;
    private readonly WorkspaceReadSnapshot _snapshot;
    private readonly StoreSidecarCursorKey _key;
    private readonly StoreSidecarCursorJournal _journal;
    private readonly StoreSidecarCursorAdvance _advance;
    private readonly StoreSidecarCursorRelease _release;

    internal StoreSidecarCursorSession(
        string storeRoot,
        WorkspaceReadSnapshot snapshot,
        StoreSidecarKind kind,
        StoreSidecarCursorAdvance advance,
        StoreSidecarCursorRelease release,
        Action? afterJournalWrite = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeRoot);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(advance);
        ArgumentNullException.ThrowIfNull(release);
        _storeRoot = storeRoot;
        _snapshot = snapshot;
        _key = StoreSidecarCursorIdentity.Create(snapshot, kind);
        _journal = new(storeRoot, _key.FamilyId, _key.ViewId, afterJournalWrite);
        _advance = advance;
        _release = release;
    }

    public bool TryProtectBaseline(StoreSidecarStamp baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        try
        {
            StoreSidecarCursorKey baselineKey = KeyFor(baseline);
            StoreSidecarCursorEntry entry = _journal.UpsertDesired(baselineKey, baseline.StoreLogSequence);
            if (entry.AcknowledgedSequence == baseline.StoreLogSequence)
                return true;
            if (entry.AcknowledgedSequence > baseline.StoreLogSequence)
                return false;
            if (!TryAdvance(baselineKey, baseline.StoreLogSequence, out _))
                return false;
            _journal.Acknowledge(baselineKey, baseline.StoreLogSequence);
            return true;
        }
        catch (Exception error) when (IsExpectedFailure(error))
        {
            return false;
        }
    }

    public void PrepareTarget(long sequence) => _journal.UpsertDesired(_key, sequence);

    public StoreSidecarCursorCompletion CompleteCommitted()
    {
        try
        {
            if (!_journal.Exists)
                return new(true, false, null);

            StoreSidecarCursorState state = _journal.Read();
            StoreSidecarCursorEntry? current = state.Entries.SingleOrDefault(
                entry => string.Equals(entry.ConsumerId, _key.ConsumerId, StringComparison.Ordinal));
            if (current is null)
                return new(true, false, null);

            StoreSidecarStamp expected = StoreSidecarStamp.FromSnapshot(_key.Kind, _snapshot);
            string databasePath = StoreSidecarCatalog.PathFor(_storeRoot, _key.Kind, _key.ViewId);
            if (!StoreSidecarCatalog.IsCurrent(databasePath, expected))
                return new(false, false, "matching sidecar stamp is not committed");

            bool didWork = false;
            long target = expected.StoreLogSequence;
            if (current.DesiredSequence < target)
            {
                current = _journal.UpsertDesired(_key, target);
                didWork = true;
            }
            if (current.AcknowledgedSequence is not long acknowledged || acknowledged < target)
            {
                if (!TryAdvance(_key, target, out string? error))
                    return new(false, didWork, error);
                _journal.Acknowledge(_key, target);
                didWork = true;
            }

            state = _journal.Read();
            foreach (StoreSidecarCursorEntry obsolete in state.Entries.Where(entry =>
                entry.Kind == _key.Kind &&
                !string.Equals(entry.ConsumerId, _key.ConsumerId, StringComparison.Ordinal)).ToArray())
            {
                StoreSidecarCursorKey obsoleteKey = KeyFor(obsolete);
                if (!TryRelease(obsoleteKey, out string? error))
                    return new(false, didWork, error);
                _journal.Remove(obsolete.ConsumerId);
                didWork = true;
            }

            return new(true, didWork, null);
        }
        catch (Exception error) when (IsExpectedFailure(error))
        {
            return new(false, false, error.Message);
        }
    }

    private bool TryAdvance(StoreSidecarCursorKey key, long sequence, out string? error)
    {
        error = null;
        for (int attempt = 0; attempt < OperationAttempts; attempt++)
        {
            StoreConsumerCursorOutcome outcome;
            try
            {
                outcome = _advance(key, sequence);
            }
            catch (Exception exception) when (IsExpectedFailure(exception))
            {
                error = exception.Message;
                continue;
            }
            if (outcome.Succeeded &&
                outcome.ConsumerId == key.ConsumerId &&
                outcome.ConsumerSequence == sequence &&
                outcome.SourceGeneration == key.GenerationName)
            {
                return true;
            }
            error = outcome.Error ?? "cursor advance report did not match the request";
        }
        return false;
    }

    private bool TryRelease(StoreSidecarCursorKey key, out string? error)
    {
        error = null;
        for (int attempt = 0; attempt < OperationAttempts; attempt++)
        {
            StoreConsumerCursorOutcome outcome;
            try
            {
                outcome = _release(key);
            }
            catch (Exception exception) when (IsExpectedFailure(exception))
            {
                error = exception.Message;
                continue;
            }
            if (outcome.Succeeded && outcome.ConsumerId == key.ConsumerId)
                return true;
            error = outcome.Error ?? "cursor release report did not match the request";
        }
        return false;
    }

    private StoreSidecarCursorKey KeyFor(StoreSidecarCursorEntry entry) => new(
        _key.FamilyId,
        entry.StoreInstanceId,
        _key.ViewId,
        entry.Kind,
        entry.GenerationName,
        entry.ConsumerId);

    private StoreSidecarCursorKey KeyFor(StoreSidecarStamp stamp)
    {
        if (stamp.FamilyId != _key.FamilyId || stamp.ViewId != _key.ViewId || stamp.Kind != _key.Kind)
            throw new ArgumentException("Baseline stamp does not belong to this cursor session.", nameof(stamp));
        string consumerId = StoreSidecarCursorIdentity.CursorId(
            stamp.FamilyId,
            stamp.StoreInstanceId,
            stamp.ViewId,
            stamp.Kind,
            stamp.GenerationName);
        return new(
            stamp.FamilyId,
            stamp.StoreInstanceId,
            stamp.ViewId,
            stamp.Kind,
            stamp.GenerationName,
            consumerId);
    }

    private static bool IsExpectedFailure(Exception error) =>
        error is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or InvalidOperationException;
}
