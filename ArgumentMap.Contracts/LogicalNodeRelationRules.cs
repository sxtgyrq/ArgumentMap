namespace ArgumentMap.Contracts;

public sealed class InvalidLogicalNodeRelationException(string code):InvalidOperationException($"LN8 {code} 的值只能为 {LogicalNodeRelationRules.OptionsDescription}，逻辑语言必须与值完全一致。");

// LN8 describes a relation drawn by the user. It does not discover endpoints,
// execute implication or change Cross passage settings.
public static class LogicalNodeRelationRules
{
    public const string Equality="=",Implication="→",Equivalence="↔",Superset="⊇",ProperSuperset="⊋",Subset="⊆",ProperSubset="⊊";
    public const string OptionsDescription="=（等同）、→（蕴含）、↔（等价）、⊇（包含）、⊋（真包含）、⊆（包含于）或 ⊊（真包含于）";
    public static bool ValidValue(string? value)=>value is Equality or Implication or Equivalence or Superset or ProperSuperset or Subset or ProperSubset;
    public static void EnsureValid(IEnumerable<LogicalNodeSnapshot> nodes)
    {
        foreach(var node in nodes)
            if(node.SideLength==8&&(!ValidValue(node.Properties.Value)||node.Properties.LogicLanguage!=node.Properties.Value))throw new InvalidLogicalNodeRelationException(node.Code);
    }
}
