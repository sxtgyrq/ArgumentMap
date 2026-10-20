using System.Globalization;

namespace ArgumentMap.Contracts;

public sealed class InvalidLogicalNodeOperandException(string message) : InvalidOperationException(message);

public static class LogicalNodeOperandRules
{
    public const string Class = "Class", Instance = "Instance", Custom = "Custom";
    public static bool Valid(int size, string? kind) => kind == Custom || size == 6 && kind is Class or Instance;
    public static string Normalize(string logic, string kind) => kind switch
    {
        Class => logic.ToUpper(CultureInfo.GetCultureInfo("en-US")),
        Instance => logic.ToLower(CultureInfo.GetCultureInfo("en-US")),
        _ => logic
    };
    public static void EnsureValid(IEnumerable<LogicalNodeSnapshot> nodes)
    {
        foreach (var n in nodes)
        {
            if (!Valid(n.SideLength, n.Properties.OperandKind))
                throw new InvalidLogicalNodeOperandException($"LN {n.Code}：OperandKind 仅限 LN6 使用 Class、Instance、Custom，其他 LN 必须为 Custom。");
            if (n.SideLength == 6 && n.Properties.LogicLanguage != Normalize(n.Properties.LogicLanguage, n.Properties.OperandKind))
                throw new InvalidLogicalNodeOperandException($"LN6 {n.Code}：Class 的逻辑语言必须大写，Instance 必须小写，Custom 保留原文。");
        }
    }
}
