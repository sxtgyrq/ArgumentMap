namespace ArgumentMap.Contracts;

/// <summary>Exact logical coordinates; each passing axis-aligned segment owns one projection.</summary>
public static class LogicalNodePositionCalculator
{
    public static LogicalNodePosition[] Calculate(LogicalNodeSnapshot node, IEnumerable<LineSnapshot> roads)
    {
        var positions = new List<LogicalNodePosition>();
        foreach (var road in roads.OrderBy(r => r.Code))
        for (var i = 0; i < road.Nodes.Length - 1; i++)
        {
            var a = road.Nodes[i]; var b = road.Nodes[i + 1];
            decimal x, y, percent;
            if (a.Y == b.Y && a.X != b.X && a.Y > node.Bottom && a.Y < node.Top &&
                node.X >= Math.Min(a.X,b.X) && node.X <= Math.Max(a.X,b.X))
            { x=node.X; y=a.Y; percent=(x-a.X)/(b.X-a.X); }
            else if (a.X == b.X && a.Y != b.Y && a.X > node.Left && a.X < node.Right &&
                node.Y >= Math.Min(a.Y,b.Y) && node.Y <= Math.Max(a.Y,b.Y))
            { x=a.X; y=node.Y; percent=(y-a.Y)/(b.Y-a.Y); }
            else continue;
            positions.Add(new(node.Code,road.Code,a.Order,PercentPrecision.Normalize((double)percent),x,y,road.Version));
        }
        return positions.ToArray();
    }
}
