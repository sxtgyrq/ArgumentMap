using ArgumentMap.Contracts;

namespace ArgumentMap.Storage;

public sealed record LoginAccount(string UserName, string Md5Password, string? LoginCode, bool EditAllRoad = false, bool EditAllLogicalNodes = false);

public sealed class LineStoreState
{
    public UndoHistoryState UndoHistory { get; set; } = new();
    public Dictionary<string, LineSnapshot> Lines { get; } = new(StringComparer.Ordinal);
    public Dictionary<CrossKey, CrossSnapshot> Crosses { get; set; } = [];
    public HashSet<(string LineCode, int Segment)> PendingSegments { get; } = [];
    public HashSet<CrossKey> StaleCrosses { get; } = [];
    // Transaction-local mark intents. Atomic workflows may restore/clean stale Crosses
    // before persistence; SQL must still mark the original rows before replacing them.
    public HashSet<CrossKey> CrossRecalculationMarks { get; } = [];
    public HashSet<LogicalNodePositionKey> LogicalNodePositionRecalculationMarks { get; } = [];
    public HashSet<string> DirtyLines { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, LoginAccount> Accounts { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, LogicalNodeSnapshot> LogicalNodes { get; } = new(StringComparer.Ordinal);
    public Dictionary<LogicalNodePositionKey, LogicalNodePosition> LogicalNodePositions { get; } = [];
}

public interface ILineRepository
{
    T Transaction<T>(Func<LineStoreState, T> operation);
}

/// <summary>Validation and replacement share one lock, preventing concurrent conflicting saves.</summary>
public sealed class InMemoryLineRepository : ILineRepository
{
    private readonly object gate = new();
    private LineStoreState state = new();

    public T Transaction<T>(Func<LineStoreState, T> operation)
    {
        lock (gate)
        {
            // Match MySQL transaction semantics: failed callbacks never mutate committed state.
            var working = new LineStoreState();
            working.UndoHistory = state.UndoHistory.Clone();
            foreach (var (id, line) in state.Lines) working.Lines.Add(id, line with { Nodes = line.Nodes.ToArray() });
            foreach (var (key, cross) in state.Crosses) working.Crosses.Add(key, cross);
            working.PendingSegments.UnionWith(state.PendingSegments);
            working.StaleCrosses.UnionWith(state.StaleCrosses);
            working.DirtyLines.UnionWith(state.DirtyLines);
            foreach (var (name, account) in state.Accounts) working.Accounts.Add(name, account);
            foreach (var (code, node) in state.LogicalNodes) working.LogicalNodes.Add(code, node);
            foreach (var (key, position) in state.LogicalNodePositions) working.LogicalNodePositions.Add(key, position);
            var result = operation(working);
            if (result is not IServiceResult { Success: false })
            {
                LogicalNodeTextRules.EnsureChangesValid(state.LogicalNodes.Values,working.LogicalNodes.Values);
                LogicalNodeCoordinateRules.EnsureValid(working.LogicalNodes.Values);
                LogicalNodeValueUniqueness.EnsureValid(working.LogicalNodes.Values);
                LogicalNodeLogicRules.EnsureValid(working.LogicalNodes.Values);
                LogicalNodeOperandRules.EnsureValid(working.LogicalNodes.Values);
                LogicalNodeBranchRules.EnsureValid(working.LogicalNodes.Values);
                LogicalNodeRelationRules.EnsureValid(working.LogicalNodes.Values);
                LogicalNodeInstanceRules.EnsureValid(working.LogicalNodes.Values);
                LogicalNodeInstanceRules.EnsureChangesValid(state.LogicalNodes.Values,working.LogicalNodes.Values);
                if(working.UndoHistory.ValidateDeletion(state.LogicalNodes.Values,working.LogicalNodes.Values) is {} instanceDeletion)
                    throw new InvalidLogicalNodeInstanceException(instanceDeletion);
                state = working;
            }
            return result;
        }
    }
}
