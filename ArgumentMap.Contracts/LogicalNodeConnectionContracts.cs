namespace ArgumentMap.Contracts;

public enum LogicalNodeConnectionKind { Direct, GoTo, Find }
public sealed record LogicalNodeConnectionJoin(string RoadCode, CrossPosition Position);
public sealed record LogicalNodeContinuationPoint(string RoadCode, decimal X, decimal Y);

// Clicks identify squares, not line vertices. Only geometry is relevant to stale-preview checks;
// a concurrent J calculation or metadata edit must not invalidate unchanged squares.
public sealed record LogicalNodeConnectionAnchor(string Code, decimal X, decimal Y, int SideLength)
{
    public static LogicalNodeConnectionAnchor From(LogicalNodeSnapshot node) => new(node.Code,node.X,node.Y,node.SideLength);
    public bool Matches(LogicalNodeSnapshot node) => Code==node.Code&&X==node.X&&Y==node.Y&&SideLength==node.SideLength;
}
public sealed record PreviewLogicalNodeConnectionRequest(string Owner, LogicalNodeConnectionAnchor Source,
    LogicalNodeConnectionAnchor Target, string LoginCode = "", LogicalNodeConnectionKind Kind = LogicalNodeConnectionKind.Direct);
public sealed record LogicalNodeConnectionPlan(LogicalNodeConnectionAnchor Source, LogicalNodeConnectionAnchor Target,
    string LineCode, LineNode[] Nodes, LogicalNodeConnectionKind Kind = LogicalNodeConnectionKind.Direct, LogicalNodeConnectionJoin? Join = null)
{
    // Display only. Confirmation derives reachability again from current server geometry/settings.
    public LogicalNodeContinuationPoint[] Continuation { get; init; } = [];
}
public sealed record SaveLogicalNodeConnectionRequest(string Owner, LogicalNodeConnectionPlan Plan, string LoginCode = "");
public sealed record LogicalNodeConnectionResult(LineSnapshot Line, MapAreaSnapshot Map, int DisconnectedCrosses);
public interface ILogicalNodeConnectionClient
{
    Task<ServiceResult<LogicalNodeConnectionPlan>> PreviewLogicalNodeConnectionAsync(LogicalNodeConnectionAnchor source, LogicalNodeConnectionAnchor target, LogicalNodeConnectionKind kind = LogicalNodeConnectionKind.Direct);
    Task<ServiceResult<LogicalNodeConnectionResult>> SaveLogicalNodeConnectionAsync(LogicalNodeConnectionPlan plan);
}
