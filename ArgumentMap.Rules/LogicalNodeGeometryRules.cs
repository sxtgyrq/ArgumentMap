using ArgumentMap.Contracts;

namespace ArgumentMap.Rules;

public sealed record LogicalNodeViolation(string Code, string Message);

public static class LogicalNodeGeometryRules
{
    public static bool IsHalfInteger(decimal value) => LogicalNodeCoordinateRules.IsHalfInteger(value);

    public static LogicalNodeViolation? ValidateShape(decimal x, decimal y, int size)
    {
        if (size is not (4 or 6 or 8 or 10 or 12 or 18)) return new("InvalidLogicalNodeSize", "逻辑节点的边长只能是 4、6、8、10（实例）、12 或 18（开始）。");
        if (!IsHalfInteger(x) || !IsHalfInteger(y)) return new("InvalidLogicalNodeCoordinate", "中心 X、Y 都必须是整数＋0.5，不能是整数或其他小数。");
        var radius = size / 2m;
        if (x - radius < 0 || y - radius < 0 || x + radius > MapLimits.CoordinateMaximum || y + radius > MapLimits.CoordinateMaximum)
            return new("LogicalNodeOutOfBounds", "正方形的四条边和四个顶点必须全部位于 0–500000 范围内。");
        return null;
    }

    public static LogicalNodeViolation? ValidateNew(LogicalNodeSnapshot node, IEnumerable<LineSnapshot> lines, IReadOnlyDictionary<CrossKey,CrossSnapshot> crosses, IEnumerable<LogicalNodeSnapshot> nodes)
    {
        if (node.IsStart && nodes.Any(n => n.IsStart && n.Code != node.Code))
            return new("StartLogicalNodeExists", "全图只能有一个边长 18 的开始节点，请先删除已有开始节点。");
        if (ValidateSeparation(node,nodes.Where(n=>n.Code!=node.Code).OrderBy(n=>n.Code)) is {} separation) return separation;
        var ordered=lines.OrderBy(l=>l.Code).ToArray();var geometry=new Lazy<IntegerCrossIndex>(()=>new(ordered));
        return ValidateArea(node, ordered, crosses, allowMissingCross: false, geometry);
    }

    public static LogicalNodeViolation? ValidateMap(IEnumerable<LineSnapshot> existingLines, IReadOnlyDictionary<CrossKey,CrossSnapshot> crosses, IEnumerable<LogicalNodeSnapshot> existingNodes, LineSnapshot? replacement = null, Func<IntegerCrossIndex>? geometryFactory = null)
    {
        if (!existingNodes.Any()) return null;
        var nodes=existingNodes.OrderBy(n=>n.Code).ToArray();
        for(var i=0;i<nodes.Length;i++)
            if(ValidateSeparation(nodes[i],nodes.Skip(i+1)) is {} separation)return separation;
        var lines = existingLines.Where(l => l.Code != replacement?.Code).Concat(replacement is null ? [] : new[] { replacement }).OrderBy(l => l.Code).ToArray();
        var geometry=new Lazy<IntegerCrossIndex>(()=>replacement is null&&geometryFactory is not null?geometryFactory():new(lines));
        foreach (var node in existingNodes.OrderBy(n => n.Code))
            if (ValidateArea(node, lines, crosses, allowMissingCross: true, geometry) is { } violation) return violation;
        return null;
    }

    private static LogicalNodeViolation? ValidateSeparation(LogicalNodeSnapshot node,IEnumerable<LogicalNodeSnapshot> others)
    {
        foreach(var other in others)
            if(node.TouchesOrOverlaps(other))
                return new("LogicalNodesTouchOrOverlap", $"LN {node.Code}（中心 {node.X:0.0}, {node.Y:0.0}，边长 {node.SideLength}）与 LN {other.Code}（中心 {other.X:0.0}, {other.Y:0.0}，边长 {other.SideLength}）的正方形重叠或接触。两个 LN 之间必须留出间隔，边和顶点也不能接触。");
        return null;
    }

    private static LogicalNodeViolation? ValidateArea(LogicalNodeSnapshot square, LineSnapshot[] lines, IReadOnlyDictionary<CrossKey,CrossSnapshot> crosses, bool allowMissingCross, Lazy<IntegerCrossIndex> geometry)
    {
        foreach (var line in lines)
            foreach (var point in line.Nodes)
                if (square.Contains(point.X, point.Y))
                    return new("LineNodeInsideLogicalNode", $"线路 {line.Code} 的节点序号 {point.Order} ({point.X}, {point.Y}) 位于逻辑节点 {square.Code} 的正方形内，无法保存。");
        foreach (var line in lines)
            for (var i = 0; i < line.Nodes.Length - 1; i++)
                if (DiagonalEntersInterior(square, line.Nodes[i], line.Nodes[i + 1]))
                    return new("DiagonalInsideLogicalNode", $"线路 {line.Code} 的线段序号 {i} 斜穿逻辑节点 {square.Code} 的正方形内部。只有横线、竖线可以穿过 LN；45° 斜线仅允许擦过顶点。");
        foreach (var key in geometry.Value.Intersections)
        {
            if (!square.Contains(key.Position)) continue;
            if (!crosses.TryGetValue(key, out var cross))
            {
                if (allowMissingCross) continue; // K will create this crossing as disconnected inside an existing square.
                return new("LogicalNodeCrossNotDisconnected", $"正方形内有尚未计算或设置的 Cross {key}，请先按 K 计算并用 O 设为不连通，再新增逻辑节点。");
            }
            if (cross.State != CrossState.Disconnected)
                return new("LogicalNodeCrossNotDisconnected", $"正方形内的 Cross {key} 当前为{cross.StateLabel}。范围内的路口必须全部设为不连通（O）。");
        }
        return null;
    }

    private static bool DiagonalEntersInterior(LogicalNodeSnapshot square, LineNode start, LineNode end)
    {
        decimal dx = end.X - start.X, dy = end.Y - start.Y;
        if (dx == 0 || dy == 0) return false;
        // Clip the finite segment against the OPEN square. A single corner contact has
        // equal entry and exit parameters, so it is allowed without an epsilon tolerance.
        decimal enter = 0, exit = 1;
        bool Clip(decimal origin, decimal delta, decimal lower, decimal upper)
        {
            var a = (lower - origin) / delta;
            var b = (upper - origin) / delta;
            enter = Math.Max(enter, Math.Min(a, b));
            exit = Math.Min(exit, Math.Max(a, b));
            return enter < exit;
        }
        return Clip(start.X, dx, square.Left, square.Right) && Clip(start.Y, dy, square.Bottom, square.Top);
    }

}
