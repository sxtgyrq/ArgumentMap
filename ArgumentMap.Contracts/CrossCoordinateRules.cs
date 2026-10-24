namespace ArgumentMap.Contracts;

public sealed class InvalidCrossCoordinateException(CrossKey key)
    : InvalidOperationException($"Cross {key} 的坐标必须是 0–500000 内的整数；半整数交叉不形成 Cross。");

public static class CrossCoordinateRules
{
    public static void EnsureValid(IEnumerable<CrossSnapshot> crosses)
    {
        foreach(var cross in crosses)
            if(!cross.Key.Position.IsInteger || cross.Key.Position.X2 is <0 or >1000000 || cross.Key.Position.Y2 is <0 or >1000000)
                throw new InvalidCrossCoordinateException(cross.Key);
    }
}
