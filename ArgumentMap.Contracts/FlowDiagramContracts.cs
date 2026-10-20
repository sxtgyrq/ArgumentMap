namespace ArgumentMap.Contracts;

// Import language DTOs only. Saved objects remain ordinary LN, Road and Cross records.
public sealed record FlowDiagramDocument
{
    public string Format { get; init; } = "";
    public string Origin { get; init; } = "";
    public FlowLogicalNode[] Ln { get; init; } = [];
    public FlowRoad[] Roads { get; init; } = [];
    public FlowCross[] Crosses { get; init; } = [];
}
public sealed record FlowLogicalNode
{
    public string Id { get; init; } = "";
    public decimal[] At { get; init; } = [];
    public int Size { get; init; } = 12;
    public string Value { get; init; } = "逻辑节点";
    // Required and nonblank for LN6/12/18. LN4 may be empty; LN8 is always its Value
    // (the parser fills an omitted/empty LN8 Logic). All sizes reject null.
    public string Logic { get; init; } = "";
    public string OperandKind { get; init; } = LogicalNodeOperandRules.Custom;
    public string Info { get; init; } = "";
}
public sealed record FlowRoad
{
    public string Id { get; init; } = "";
    public decimal[][] Points { get; init; } = [];
    public string Direction { get; init; } = "forward";
    public int Speed { get; init; } = MapLimits.SpeedDefault;
    public string Name { get; init; } = "";
    public string Info { get; init; } = "";
}
public sealed record FlowCross
{
    public string Road1 { get; init; } = "";
    public string Road2 { get; init; } = "";
    public decimal[] At { get; init; } = [];
    public string State { get; init; } = "unset";
}
public sealed record FlowDiagramRequest(string Owner,string Text,decimal X,decimal Y,string LoginCode="");
public sealed record FlowDiagramResult(MapAreaSnapshot Added,MapAreaSnapshot? Map,
    Dictionary<string,string> LogicalNodeCodes,Dictionary<string,string> RoadCodes,string[] Notes);
public interface IFlowDiagramClient
{
    Task<ServiceResult<FlowDiagramResult>> PreviewFlowDiagramAsync(string text,decimal x,decimal y);
    Task<ServiceResult<FlowDiagramResult>> ImportFlowDiagramAsync(string text,decimal x,decimal y);
}
