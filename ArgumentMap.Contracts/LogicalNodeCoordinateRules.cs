namespace ArgumentMap.Contracts;

public sealed class InvalidLogicalNodeCoordinateException(string code):InvalidOperationException($"LN {code} 的中心 X、Y 都必须是整数＋0.5，不能是整数或其他小数。");

public static class LogicalNodeCoordinateRules
{
    public static bool IsHalfInteger(decimal value)=>value%1m==0.5m;
    public static void EnsureValid(IEnumerable<LogicalNodeSnapshot> nodes)
    {
        foreach(var node in nodes)
            if(!IsHalfInteger(node.X)||!IsHalfInteger(node.Y))throw new InvalidLogicalNodeCoordinateException(node.Code);
    }
}
