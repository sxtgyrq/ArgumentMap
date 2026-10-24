using ArgumentMap.Contracts;

namespace ArgumentMap.Rules;

public static class LineGeometry
{
    public static bool TryCrossIntersection(LineNode a, LineNode b, LineNode c, LineNode d, out CrossPosition position)
    {
        if(TryIntersection(a,b,c,d,out position) && position.IsInteger)return true;
        position=default;return false;
    }

    public static (int Segment, double Percent) LocateCross(IReadOnlyList<LineNode> nodes, int segment, CrossPosition position)
    {
        // Prefer the node's outgoing segment even when another segment of the same line passes through it.
        for (var i = 0; i < nodes.Count; i++)
            if (position.X2 == 2 * nodes[i].X && position.Y2 == 2 * nodes[i].Y)
                return i < nodes.Count - 1 ? (i, 0) : (i - 1, 1);
        var end = nodes[segment + 1];
        var start = nodes[segment];
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var percent = dx != 0
            ? (position.X2 - 2 * start.X) / (2.0 * dx)
            : (position.Y2 - 2 * start.Y) / (2.0 * dy);
        return (segment, PercentPrecision.Normalize(percent));
    }

    public static bool TryIntersection(LineNode a, LineNode b, LineNode c, LineNode d, out CrossPosition position)
    {
        position = default;
        long rx = (long)b.X - a.X, ry = (long)b.Y - a.Y;
        long sx = (long)d.X - c.X, sy = (long)d.Y - c.Y;
        long qx = (long)c.X - a.X, qy = (long)c.Y - a.Y;
        long denominator = rx * sy - ry * sx;
        if (denominator == 0) return false; // Parallel/collinear overlaps are rejected by save validation.
        long t = qx * sy - qy * sx, u = qx * ry - qy * rx;
        if (denominator < 0) { denominator = -denominator; t = -t; u = -u; }
        if (t < 0 || t > denominator || u < 0 || u > denominator) return false;
        long x2 = 2 * (a.X * denominator + rx * t);
        long y2 = 2 * (a.Y * denominator + ry * t);
        if (x2 % denominator != 0 || y2 % denominator != 0)
            throw new InvalidOperationException("Cross calculation requires eight-direction segments.");
        position = new CrossPosition(checked((int)(x2 / denominator)), checked((int)(y2 / denominator)));
        return true;
    }

    public static bool IsEightDirection(LineNode a, LineNode b)
    {
        long dx = (long)b.X - a.X;
        long dy = (long)b.Y - a.Y;
        return (dx != 0 || dy != 0) && (dx == 0 || dy == 0 || Math.Abs(dx) == Math.Abs(dy));
    }

    /// <summary>Exact 90-degree test; containment/intersection is checked separately.</summary>
    public static bool IsPerpendicular(LineNode a, LineNode b, LineNode c, LineNode d)
    {
        long rx = (long)b.X - a.X, ry = (long)b.Y - a.Y;
        long sx = (long)d.X - c.X, sy = (long)d.Y - c.Y;
        return (rx != 0 || ry != 0) && (sx != 0 || sy != 0) && rx * sx + ry * sy == 0;
    }

    /// <summary>Exact containment, including integer nodes and half-integer points inside segments.</summary>
    public static (int? NodeOrder, int Segment)? FindPointOnLine(IReadOnlyList<LineNode> nodes, CrossPosition position)
    {
        for (var i = 0; i < nodes.Count; i++)
            if (2L * nodes[i].X == position.X2 && 2L * nodes[i].Y == position.Y2)
                return (nodes[i].Order, Math.Min(i, Math.Max(0, nodes.Count - 2)));
        for (var i = 0; i < nodes.Count - 1; i++)
        {
            long ax = 2L * nodes[i].X, ay = 2L * nodes[i].Y;
            long bx = 2L * nodes[i + 1].X, by = 2L * nodes[i + 1].Y;
            if ((bx - ax) * (position.Y2 - ay) != (by - ay) * (position.X2 - ax)) continue;
            if (position.X2 >= Math.Min(ax, bx) && position.X2 <= Math.Max(ax, bx) &&
                position.Y2 >= Math.Min(ay, by) && position.Y2 <= Math.Max(ay, by)) return (null, i);
        }
        return null;
    }

    /// <summary>
    /// Returns the first acute interior corner's zero-based node index.
    /// For eight-direction segments, a non-collinear acute corner is exactly 45 degrees.
    /// Collinear reversal is handled by the existing positive-length overlap validation.
    /// </summary>
    public static int? FindAcuteCorner(IReadOnlyList<LineNode> nodes)
    {
        for (var i = 1; i < nodes.Count - 1; i++)
        {
            // Both rays point away from the shared node: interior angle, not travel-direction turn.
            long ax = (long)nodes[i - 1].X - nodes[i].X, ay = (long)nodes[i - 1].Y - nodes[i].Y;
            long bx = (long)nodes[i + 1].X - nodes[i].X, by = (long)nodes[i + 1].Y - nodes[i].Y;
            if (ax * bx + ay * by > 0 && ax * by - ay * bx != 0) return i;
        }
        return null;
    }

    /// <summary>A positive-length common interval is overlap; a single intersection is not.</summary>
    public static bool Overlaps(LineNode a, LineNode b, LineNode c, LineNode d)
    {
        long dx = (long)b.X - a.X;
        long dy = (long)b.Y - a.Y;
        if (dx == 0 && dy == 0) return false;
        if (dx * ((long)c.Y - a.Y) - dy * ((long)c.X - a.X) != 0 ||
            dx * ((long)d.Y - a.Y) - dy * ((long)d.X - a.X) != 0) return false;
        return dx != 0
            ? Math.Max(Math.Min(a.X, b.X), Math.Min(c.X, d.X)) < Math.Min(Math.Max(a.X, b.X), Math.Max(c.X, d.X))
            : Math.Max(Math.Min(a.Y, b.Y), Math.Min(c.Y, d.Y)) < Math.Min(Math.Max(a.Y, b.Y), Math.Max(c.Y, d.Y));
    }
}
