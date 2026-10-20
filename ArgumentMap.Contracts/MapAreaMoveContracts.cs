namespace ArgumentMap.Contracts;

public sealed record MapAreaBounds(decimal Left, decimal Bottom, decimal Right, decimal Top)
{
    public bool Contains(decimal x,decimal y)=>x>=Left && x<=Right && y>=Bottom && y<=Top;
    // A rectangle is convex: containing every vertex contains every straight segment.
    public bool ContainsEntireLine(LineSnapshot line)=>line.Nodes.Length>=2&&line.Nodes.All(n=>Contains(n.X,n.Y));
    // Use the full square even for LN8's chamfered outline, never just its centre.
    public bool ContainsEntireSquare(LogicalNodeSnapshot node)=>Contains(node.Left,node.Bottom)&&Contains(node.Right,node.Top);
}
// These are snapshots of existing objects for optimistic concurrency, not new map entities.
public sealed record MapAreaSelection(MapAreaBounds Bounds,LineSnapshot[] Lines,LogicalNodeSnapshot[] LogicalNodes,CrossSnapshot[] Crosses);
public sealed record MapAreaSnapshot(LineSnapshot[] Lines,LogicalNodeSnapshot[] LogicalNodes,CrossSnapshot[] Crosses);
public sealed record SelectMapAreaRequest(string Owner,MapAreaBounds Bounds,string LoginCode="");
public sealed record MoveMapAreaRequest(string Owner,MapAreaSelection Selection,int DeltaX,int DeltaY,string LoginCode="");
public sealed record DeleteMapAreaRequest(string Owner,MapAreaSelection Selection,string LoginCode="");
public interface IMapAreaMoveClient
{
    Task<ServiceResult<MapAreaSelection>> SelectMapAreaAsync(MapAreaBounds bounds);
    Task<ServiceResult<MapAreaSnapshot>> MoveMapAreaAsync(MapAreaSelection selection,int deltaX,int deltaY);
    Task<ServiceResult<MapAreaSnapshot>> DeleteMapAreaAsync(MapAreaSelection selection);
}
