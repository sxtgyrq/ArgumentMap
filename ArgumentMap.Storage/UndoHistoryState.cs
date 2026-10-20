using ArgumentMap.Contracts;

namespace ArgumentMap.Storage;

// Journal payloads contain map data only. Never store credentials or login codes.
public sealed record UndoLineChange(LineSnapshot? Before, LineSnapshot? After);
public sealed record UndoNodeChange(LogicalNodeSnapshot? Before, LogicalNodeSnapshot? After);
public sealed record UndoStamp(string Key, string? Before);
public sealed record MapUndoDelta(UndoLineChange[] Lines, UndoNodeChange[] Nodes,
    string[] GeometryRoads, CrossSnapshot[] BeforeCrosses, CrossSnapshot[] AfterCrosses);
public sealed record MapUndoEntry(string Id, string Owner, string Operation, long Sequence,
    MapUndoDelta Delta, UndoStamp[] Stamps, bool IsUndone = false, string? UndoRequestId = null);

/// <summary>Lazy per-transaction journal: K/J and read requests never load history.</summary>
public sealed class UndoHistoryState
{
    public const int MaximumEntriesPerUser = 100;
    private readonly Func<string, MapUndoEntry?>? loadLatest;
    private readonly Func<string, string?>? loadStamp;
    private readonly Func<string, string, MapUndoEntry?>? loadRequest;
    private readonly Dictionary<string, MapUndoEntry> entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> stamps = new(StringComparer.Ordinal);
    private long sequence;
    public List<MapUndoEntry> Added { get; } = [];
    public HashSet<string> Consumed { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> ConsumedRequests { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string?> StampWrites { get; } = new(StringComparer.Ordinal);

    public UndoHistoryState(Func<string, MapUndoEntry?>? loadLatest = null, Func<string, string?>? loadStamp = null,
        Func<string, string, MapUndoEntry?>? loadRequest = null)
    { this.loadLatest = loadLatest; this.loadStamp = loadStamp; this.loadRequest = loadRequest; }

    public MapUndoEntry? FindRequest(string owner, string requestId) => entries.Values.FirstOrDefault(e =>
        e.Owner == owner && e.IsUndone && e.UndoRequestId == requestId) ?? loadRequest?.Invoke(owner, requestId);

    public MapUndoEntry? Latest(string owner) => entries.Values.Where(e => e.Owner == owner && !e.IsUndone)
        .OrderByDescending(e => e.Sequence).FirstOrDefault() ?? loadLatest?.Invoke(owner);

    public string? Stamp(string key)
    {
        if (!stamps.TryGetValue(key, out var stamp)) stamps[key] = stamp = loadStamp?.Invoke(key);
        return stamp;
    }

    public void Append(MapUndoEntry entry)
    {
        entry = entry with { Sequence = ++sequence };
        entries.Add(entry.Id, entry); Added.Add(entry);
        foreach (var stamp in entry.Stamps) WriteStamp(stamp.Key, entry.Id);
        foreach (var old in entries.Values.Where(e => e.Owner == entry.Owner).OrderByDescending(e => e.Sequence)
                     .Skip(MaximumEntriesPerUser).ToArray()) entries.Remove(old.Id);
    }

    public void Consume(MapUndoEntry entry, string requestId)
    {
        entries[entry.Id] = entry with { IsUndone = true, UndoRequestId = requestId }; Consumed.Add(entry.Id);
        ConsumedRequests[entry.Id] = requestId;
        foreach (var stamp in entry.Stamps) WriteStamp(stamp.Key, stamp.Before);
    }

    private void WriteStamp(string key, string? value) { stamps[key] = value; StampWrites[key] = value; }

    public UndoHistoryState Clone()
    {
        var copy = new UndoHistoryState { sequence = sequence };
        foreach (var pair in entries) copy.entries.Add(pair.Key, pair.Value);
        foreach (var pair in stamps) copy.stamps.Add(pair.Key, pair.Value);
        return copy;
    }

    public LogicalNodeInstanceViolation? ValidateDeletion(IEnumerable<LogicalNodeSnapshot> before, IEnumerable<LogicalNodeSnapshot> after,
        MapUndoEntry? restoring = null)
    {
        var remaining = after.Select(n => n.Code).ToHashSet(StringComparer.Ordinal);
        // Reversing an atomic addition removes its newly created instances first.
        // This is not the ordinary Delete API: surviving or pre-existing instances
        // still block removal of their parent, including instances added by others.
        var compensations = restoring is null ? entries.Values.Where(e => Consumed.Contains(e.Id)) : [restoring];
        var addedInstances = compensations.SelectMany(e => e.Delta.Nodes)
            .Where(c => c.Before is null && c.After?.SideLength == 10 && !remaining.Contains(c.After.Code))
            .Select(c => c.After!.Code).ToHashSet(StringComparer.Ordinal);
        var previous = before.ToArray();
        return LogicalNodeInstanceRules.ValidateDeletion(previous.Where(n => !addedInstances.Contains(n.Code)), previous.Select(n => n.Code).Except(remaining));
    }
}
