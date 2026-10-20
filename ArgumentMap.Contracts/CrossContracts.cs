namespace ArgumentMap.Contracts;

public enum CrossState { Disconnected = -1, Unset = 0, Connected = 1, Road1ToRoad2 = 2, Road2ToRoad1 = 3 }

// Eight-direction integer-node segments can intersect on integers or half-integers.
// Doubled coordinates represent both exactly, without floating-point identity comparisons.
public readonly record struct CrossPosition(int X2, int Y2)
{
    public double X => X2 / 2.0;
    public double Y => Y2 / 2.0;
}

// Segment orders are mutable location fields, not the identity of a crossing.
public readonly record struct CrossKey(string RoadCode1, string RoadCode2, CrossPosition Position) : IComparable<CrossKey>
{
    // The old project puts the lexically greater road code first.
    public static CrossKey Create(string first, string second, CrossPosition position) =>
        string.CompareOrdinal(first, second) > 0 ? new(first, second, position) : new(second, first, position);
    public bool IsValid => LineCode.IsValid(RoadCode1) && LineCode.IsValid(RoadCode2) &&
        string.CompareOrdinal(RoadCode1, RoadCode2) > 0 && Position.X2 is >= 0 and <= 1000000 && Position.Y2 is >= 0 and <= 1000000;
    public bool Contains(string code) => RoadCode1 == code || RoadCode2 == code;
    public int CompareTo(CrossKey other)
    {
        var comparison = string.CompareOrdinal(RoadCode1, other.RoadCode1);
        if (comparison == 0) comparison = string.CompareOrdinal(RoadCode2, other.RoadCode2);
        if (comparison == 0) comparison = Position.X2.CompareTo(other.Position.X2);
        return comparison != 0 ? comparison : Position.Y2.CompareTo(other.Position.Y2);
    }
    public override string ToString() => $"{RoadCode1}/{RoadCode2} ({Position.X:0.0}, {Position.Y:0.0})";
}
public sealed record CrossSnapshot(CrossKey Key, int RoadOrder1, int RoadOrder2,
    long RoadVersion1, long RoadVersion2, CrossState State, long Version, double Percent1 = 0, double Percent2 = 0)
{
    public bool CanTransfer => State is CrossState.Connected or CrossState.Road1ToRoad2 or CrossState.Road2ToRoad1;
    public bool AllowsTransfer(string fromLine, string toLine) => fromLine != toLine && Key.Contains(fromLine) && Key.Contains(toLine) &&
        (State == CrossState.Connected || State == CrossState.Road1ToRoad2 && fromLine == Key.RoadCode1 || State == CrossState.Road2ToRoad1 && fromLine == Key.RoadCode2);
    public string StateLabel => State switch
    {
        CrossState.Unset => "未设置", CrossState.Connected => "双向连通", CrossState.Disconnected => "不连通",
        CrossState.Road1ToRoad2 => $"单向：{Key.RoadCode1} → {Key.RoadCode2}",
        CrossState.Road2ToRoad1 => $"单向：{Key.RoadCode2} → {Key.RoadCode1}", _ => "未知状态"
    };
    public (int RoadOrder, double Percent) LocationOn(string lineCode) => lineCode == Key.RoadCode1 ? (RoadOrder1, Percent1) :
        lineCode == Key.RoadCode2 ? (RoadOrder2, Percent2) : throw new ArgumentException("线路不属于该交点。", nameof(lineCode));
}

public sealed record EditCrossRequest(CrossKey Cross, string Owner, long Version, CrossState State, string LoginCode = "");
public sealed record CrossCalculationProgress(bool CanContinue, int RemainingSegments, int CrossCount);

public interface ICrossClient
{
    Task<ServiceResult<CrossSnapshot[]>> GetCrossesAsync();
    Task<ServiceResult<CrossCalculationProgress>> CalculateCrossAsync();
    Task<ServiceResult<CrossSnapshot>> SetCrossStateAsync(CrossKey key, long version, CrossState state);
}
