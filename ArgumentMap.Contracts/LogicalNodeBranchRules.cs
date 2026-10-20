namespace ArgumentMap.Contracts;

public sealed record LogicalNodeBranchViolation(string Code,string Message);
public sealed class InvalidLogicalNodeBranchException(LogicalNodeBranchViolation violation):InvalidOperationException(violation.Message)
{public string ErrorCode {get;}=violation.Code;}

public static class LogicalNodeBranchRules
{
    public const string True="True",False="False";
    public static bool ValidValue(string value)=>value is True or False;
    // P denotes the complete, case-sensitive logic of one LN6, not a fixed letter.
    public static string? SourceLogic(LogicalNodeSnapshot branch)=>branch.SideLength!=4 || string.IsNullOrWhiteSpace(branch.Properties.LogicLanguage)?null:
        branch.Properties.Value==True?branch.Properties.LogicLanguage:
        branch.Properties.LogicLanguage.StartsWith("¬",StringComparison.Ordinal)&&branch.Properties.LogicLanguage.Length>1?branch.Properties.LogicLanguage[1..]:null;
    public static string Expression(string value,string source)=>value==True?source:"¬"+source;
    public static IReadOnlyDictionary<string,int> SourceCounts(IEnumerable<LogicalNodeSnapshot> nodes)=>nodes.Where(n=>n.SideLength==6)
        .GroupBy(n=>n.Properties.LogicLanguage,StringComparer.Ordinal).ToDictionary(g=>g.Key,g=>g.Count(),StringComparer.Ordinal);
    public static LogicalNodeBranchViolation? Validate(LogicalNodeSnapshot node,IEnumerable<LogicalNodeSnapshot> nodes)=>node.SideLength==4?Validate(node,SourceCounts(nodes)):null;
    public static LogicalNodeBranchViolation? Validate(LogicalNodeSnapshot node,IReadOnlyDictionary<string,int> sources)
    {
        if(node.SideLength!=4)return null;
        if(!ValidValue(node.Properties.Value))return new("InvalidBranchValue",$"LN4 {node.Code} 的值只能为 True 或 False。");
        if(string.IsNullOrWhiteSpace(node.Properties.LogicLanguage))return null;
        var source=SourceLogic(node);
        if(source is null)return new("InvalidBranchLogic",$"LN4 {node.Code}：True 的逻辑语言必须是 P，False 必须是 ¬P；也可以留空。P 为对应 LN6 的完整逻辑语言。");
        if(!sources.TryGetValue(source,out var count)||count!=1)
            return new("BranchSourceNotFound",$"LN4 {node.Code}：找不到逻辑语言为“{source}”的唯一 LN6，请选择现有 LN6 的逻辑语言，或清空此项。True 对应 P，False 对应 ¬P，区分大小写。");
        return null;
    }
    public static LogicalNodeBranchViolation? Validate(IEnumerable<LogicalNodeSnapshot> nodes)
    {
        var all=nodes.ToArray();
        var sources=SourceCounts(all);
        return all.Where(n=>n.SideLength==4).Select(n=>Validate(n,sources)).FirstOrDefault(v=>v is not null);
    }
    public static void EnsureValid(IEnumerable<LogicalNodeSnapshot> nodes)
    {if(Validate(nodes) is {} violation)throw new InvalidLogicalNodeBranchException(violation);}
}
