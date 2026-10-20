namespace ArgumentMap.Contracts;

public sealed record LogicalNodeProperties
{
    public string Value { get; init; } = "逻辑节点";
    public string Info { get; init; } = "";
    public string LogicLanguage { get; init; } = "";
    public string OperandKind { get; init; } = LogicalNodeOperandRules.Custom;
}

public readonly record struct LogicalNodePositionKey(string LogicalNodeCode, string RoadCode, int RoadOrder);
public sealed record LogicalNodePosition(string LogicalNodeCode, string RoadCode, int RoadOrder, double RoadPercent,
    decimal X, decimal Y, long RoadVersion, bool IsChanged = false)
{
    public LogicalNodePositionKey Key => new(LogicalNodeCode, RoadCode, RoadOrder);
}

public sealed record LogicalNodeSnapshot(string Code, string Owner, long Version, decimal X, decimal Y,
    int SideLength, LogicalNodeProperties Properties)
{
    public LogicalNodePosition[] Positions { get; init; } = [];
    // Session-specific permission, supplied by the authenticated API; never stored in the node table.
    public bool? CanEdit { get; init; }
    public bool IsStart => SideLength == 18;
    public decimal Left => X - SideLength / 2m;
    public decimal Right => X + SideLength / 2m;
    public decimal Bottom => Y - SideLength / 2m;
    public decimal Top => Y + SideLength / 2m;
    public bool Contains(decimal x, decimal y) => x >= Left && x <= Right && y >= Bottom && y <= Top;
    public bool Contains(CrossPosition position) => Contains(position.X2 / 2m, position.Y2 / 2m);
    // Closed square ranges: sharing even a single corner is prohibited for two LNs.
    public bool TouchesOrOverlaps(LogicalNodeSnapshot other) =>
        Left <= other.Right && Right >= other.Left && Bottom <= other.Top && Top >= other.Bottom;
}

public sealed record CreateLogicalNodeRequest(string Owner, decimal X, decimal Y, int SideLength,
    LogicalNodeProperties Properties, string LoginCode = "");
public sealed record UpdateLogicalNodeRequest(string Code, string Owner, long Version, string Value, string Info, string LoginCode = "", string? LogicLanguage = null, string? OperandKind = null);
public sealed record DeleteLogicalNodeRequest(string Code, string Owner, long Version, string LoginCode = "");
public sealed record MoveLogicalNodeRequest(string Code, string Owner, long Version, decimal X, decimal Y, string LoginCode = "");
public sealed record LogicalNodeCalculationProgress(bool CanContinue, int RemainingNodes, int NodeCount, string? NextCode = null);
public sealed record CalculateLogicalNodeRequest(string UserName, string LoginCode, bool Incremental = false, string? AfterCode = null);

public interface ILogicalNodeStepClient
{
    Task<ServiceResult<LogicalNodeCalculationProgress>> CalculateLogicalNodeStepAsync(string? afterCode);
}

public interface ILogicalNodeMoveClient
{
    Task<ServiceResult<LogicalNodeSnapshot>> MoveLogicalNodeAsync(string code, long version, decimal x, decimal y);
}

public interface ILogicalNodeLogicClient
{
    Task<ServiceResult<LogicalNodeSnapshot>> UpdateLogicalNodeAsync(string code, long version, string value, string info, string logicLanguage);
}

public interface ILogicalNodeClient
{
    Task<ServiceResult<LogicalNodeSnapshot[]>> GetLogicalNodesAsync();
    Task<ServiceResult<LogicalNodeSnapshot>> CreateLogicalNodeAsync(decimal x, decimal y, int sideLength, LogicalNodeProperties properties);
    Task<ServiceResult<LogicalNodeSnapshot>> UpdateLogicalNodeAsync(string code, long version, string value, string info);
    Task<ServiceResult<bool>> DeleteLogicalNodeAsync(string code, long version);
    Task<ServiceResult<LogicalNodeCalculationProgress>> CalculateLogicalNodeAsync();
}

public interface ILogicalNodeOperandClient
{
    Task<ServiceResult<LogicalNodeSnapshot>> UpdateLogicalNodeAsync(string code, long version, string value, string info, string logicLanguage, string operandKind);
}
