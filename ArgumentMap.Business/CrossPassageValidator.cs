using ArgumentMap.Rules;
using ArgumentMap.Contracts;
using ArgumentMap.Storage;

namespace ArgumentMap.Business;

public sealed record CrossPassageViolation(string LineCode, CrossKey Cross, bool IsCalculated, int? NodeOrder, int Segment)
{
    public string Message(string stage)
    {
        var location = NodeOrder is int order ? $"节点序号 {order}" : $"第 {Segment + 1} 段";
        var identity = IsCalculated ? $"Cross {Cross.RoadCode1}/{Cross.RoadCode2}" : "待计算的 Cross";
        return $"{stage}：线 {LineCode} 的{location}经过{identity} ({Cross.Position.X:0.0}, {Cross.Position.Y:0.0})，" +
            $"该交点属于线 {Cross.RoadCode1} 与线 {Cross.RoadCode2}。禁止第三条线路的节点或线段经过该交点，请调整线路后重试。";
    }
}

public static class CrossPassageValidator
{
    // Check projected geometry, rather than obsolete Cross coordinates awaiting the old cleanup transaction.
    public static CrossPassageViolation? FindForLine(LineSnapshot candidate, LineStoreState state)
    {
        var others = state.Lines.Values.Where(line => line.Code != candidate.Code && line.Nodes.Length >= 2).OrderBy(line => line.Code).ToArray();
        foreach (var key in Intersections(others))
            if (Violation(candidate, key, state) is { } violation) return violation;
        return null;
    }

    public static CrossPassageViolation? FindInCurrentGeometry(LineStoreState state)
    {
        var lines = state.Lines.Values.Where(line => line.Nodes.Length >= 2).OrderBy(line => line.Code).ToArray();
        var firstPairAtPosition = new Dictionary<CrossPosition, CrossKey>();
        foreach (var key in Intersections(lines))
        {
            if (!firstPairAtPosition.TryGetValue(key.Position, out var first))
            {
                firstPairAtPosition.Add(key.Position, key);
                continue;
            }
            var thirdId = key.RoadCode1 != first.RoadCode1 && key.RoadCode1 != first.RoadCode2 ? key.RoadCode1 :
                key.RoadCode2 != first.RoadCode1 && key.RoadCode2 != first.RoadCode2 ? key.RoadCode2 : null;
            if (thirdId is string id && Violation(state.Lines[id], first, state) is { } violation) return violation;
        }
        return null;
    }

    public static CrossPassageViolation? FindThroughCalculatedCrosses(LineStoreState state)
    {
        foreach (var cross in state.Crosses.Values.OrderBy(cross => cross.Key))
        {
            // During incremental K, obsolete records remain until cleanup for settings recovery.
            if (!state.Lines.TryGetValue(cross.Key.RoadCode1, out var first) || !state.Lines.TryGetValue(cross.Key.RoadCode2, out var second) ||
                LineGeometry.FindPointOnLine(first.Nodes, cross.Key.Position) is null ||
                LineGeometry.FindPointOnLine(second.Nodes, cross.Key.Position) is null) continue;
            foreach (var line in state.Lines.Values.OrderBy(line => line.Code))
                if (line.Code != first.Code && line.Code != second.Code && line.Nodes.Length >= 2 &&
                    Violation(line, cross.Key, state) is { } violation) return violation;
        }
        return null;
    }

    private static CrossPassageViolation? Violation(LineSnapshot line, CrossKey key, LineStoreState state)
    {
        if (LineGeometry.FindPointOnLine(line.Nodes, key.Position) is not { } location) return null;
        return new(line.Code, key, state.Crosses.ContainsKey(key), location.NodeOrder, location.Segment);
    }

    private static IEnumerable<CrossKey> Intersections(LineSnapshot[] lines)
    {
        for (var i = 0; i < lines.Length; i++)
            for (var j = i + 1; j < lines.Length; j++)
            {
                var positions = new HashSet<CrossPosition>();
                for (var a = 0; a < lines[i].Nodes.Length - 1; a++)
                    for (var b = 0; b < lines[j].Nodes.Length - 1; b++)
                        if (LineGeometry.TryIntersection(lines[i].Nodes[a], lines[i].Nodes[a + 1],
                            lines[j].Nodes[b], lines[j].Nodes[b + 1], out var position) && positions.Add(position))
                            yield return CrossKey.Create(lines[i].Code, lines[j].Code, position);
            }
    }
}
