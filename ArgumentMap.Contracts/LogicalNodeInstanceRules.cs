using System.Globalization;

namespace ArgumentMap.Contracts;

public sealed record LogicalNodeInstanceViolation(string Code,string Message);
public sealed class InvalidLogicalNodeInstanceException(LogicalNodeInstanceViolation violation):InvalidOperationException(violation.Message)
{public string ErrorCode{get;}=violation.Code;}

public static class LogicalNodeInstanceRules
{
    public const int ValueMaximum=220;
    public const int LogicMaximum=4096;
    private static string Prefix(string value)=>value.Split('(',2)[0];
    public static string Normalize(string value)
    {var at=value.IndexOf('(');return at<0?value.ToLower(CultureInfo.GetCultureInfo("en-US")):value[..at].ToLower(CultureInfo.GetCultureInfo("en-US"))+value[at..];}
    public static string ParentValue(string value)=>LogicalNodeValueUniqueness.Normalize(Prefix(value));
    public static string Expression(string value)
    {
        var parent=ParentValue(value);
        var expression=value+"∈"+parent;
        if(!ValidFormat(value))return expression; // Incomplete editor drafts remain displayable.
        var close=value.IndexOf(')');
        var alias=value[(close+1)..].Trim();
        expression=(alias.Length==0?value:alias)+"∈"+parent;
        var content=value[(value.IndexOf('(')+1)..close];
        if(content.Length==0)return expression;
        foreach(var attribute in content.Split(','))
        {
            var equals=attribute.IndexOf('=');
            expression+="∧"+attribute[(equals+1)..]+"∈"+parent+"."+LogicalNodeValueUniqueness.Normalize(attribute[..equals]);
        }
        return expression;
    }
    public static string DefaultValue(string parentValue,string identifier)=>Normalize(parentValue)+"(id="+identifier+")";
    public static string Reparent(string value,string parentValue)
    {
        var at=value.IndexOf('(');
        return Normalize(parentValue)+(at>=0?value[at..]:"");
    }
    public static bool ValidFormat(string value)
    {
        var open=value.IndexOf('(');
        var close=value.IndexOf(')');
        if(open<=0||value.Length>ValueMaximum||close<open||value.IndexOf('(',open+1)>=0||value.IndexOf(')',close+1)>=0)return false;
        if(string.IsNullOrWhiteSpace(value[..open]))return false;
        if(close<value.Length-1&&(!char.IsWhiteSpace(value[close+1])||string.IsNullOrWhiteSpace(value[(close+1)..])))return false;
        var content=value[(open+1)..close];
        if(content.Length==0)return true;
        return content.Split(',').All(attribute=>attribute.IndexOf('=') is var equals&&equals>0&&
            !string.IsNullOrWhiteSpace(attribute[..equals])&&!string.IsNullOrWhiteSpace(attribute[(equals+1)..]));
    }
    public static bool HasEnglishLetter(string value)=>value.Any(c=>c is >= 'A' and <= 'Z' or >= 'a' and <= 'z');
    public static bool CanInstantiate(LogicalNodeSnapshot parent)=>parent.SideLength==12&&HasEnglishLetter(parent.Properties.Value)&&parent.Properties.Value.IndexOfAny(['(',')'])<0&&
        ParentValue(Normalize(parent.Properties.Value))==parent.Properties.Value;
    public static bool References(LogicalNodeSnapshot instance,LogicalNodeSnapshot parent)=>instance.SideLength==10&&parent.SideLength==12&&
        ParentValue(instance.Properties.Value)==parent.Properties.Value;
    public static LogicalNodeInstanceViolation? Validate(LogicalNodeSnapshot node,IEnumerable<LogicalNodeSnapshot> nodes)
    {
        if(node.SideLength!=10)return null;
        var value=node.Properties.Value;
        if(!ValidFormat(value)||value!=Normalize(value))
            return new("InvalidInstanceValue","LN10 的值必须为 类名小写() 或 类名小写(属性=属性值,属性=属性值)，括号后可用空白分隔别名，例如 human() a、human(id=1,name=要瑞卿) c；属性和别名保留原文，最多 220 字符。");
        if(node.Properties.LogicLanguage!=Expression(value))
            return new("InvalidInstanceLogic",$"LN10 的逻辑语言必须为“{Expression(value)}”，由值自动生成。");
        if(!nodes.Any(n=>References(node,n)))
            return new("InstanceParentNotFound",$"LN10 值“{value}”需要对应值为“{ParentValue(value)}”的 LN12，请先创建或选择所属 LN12。");
        return null;
    }
    public static LogicalNodeInstanceViolation? ValidateAssignment(LogicalNodeSnapshot node,IEnumerable<LogicalNodeSnapshot> nodes)
    {
        if(node.SideLength!=10)return null;
        if(!HasEnglishLetter(Prefix(node.Properties.Value)))
            return new("InvalidInstanceValue","LN10 的值和所属 LN12 的值必须至少包含一个英文字母（A–Z／a–z）；全中文、纯数字或符号的 LN12 不能新增实例。");
        if(!nodes.Any(n=>References(node,n)&&CanInstantiate(n)))
            return new("InstanceParentNotFound",$"找不到可作为实例父节点的 LN12“{ParentValue(node.Properties.Value)}”；父节点的值必须含英文字母，并与实例大小写对应。");
        return null;
    }
    // Old Chinese-only instances remain readable/movable/removable. Eligibility
    // applies to new assignments and parent renames, not unrelated transactions.
    public static void EnsureChangesValid(IEnumerable<LogicalNodeSnapshot> before,IEnumerable<LogicalNodeSnapshot> after)
    {
        var old=before.ToDictionary(n=>n.Code,StringComparer.Ordinal);var current=after.ToArray();
        foreach(var node in current.Where(n=>n.SideLength==10))
            if(!old.TryGetValue(node.Code,out var previous)||previous.SideLength!=10||previous.Properties.Value!=node.Properties.Value)
                if(ValidateAssignment(node,current) is {} invalid)throw new InvalidLogicalNodeInstanceException(invalid);
        foreach(var parent in current.Where(n=>n.SideLength==12))
            if(old.TryGetValue(parent.Code,out var previous)&&parent.Properties.Value!=previous.Properties.Value&&
                old.Values.Any(n=>References(n,previous))&&!CanInstantiate(parent))
                throw new InvalidLogicalNodeInstanceException(new("InvalidInstanceParentValue","该 LN12 仍有关联 LN10 实例，新值必须至少包含一个英文字母（A–Z／a–z），不能含括号，并保持大小写对应。请先删除或改属实例。"));
    }
    public static LogicalNodeInstanceViolation? ValidateDeletion(IEnumerable<LogicalNodeSnapshot> nodes,IEnumerable<string> codes)
    {
        var all=nodes.ToArray();var deleted=codes.ToHashSet(StringComparer.Ordinal);
        foreach(var parent in all.Where(n=>n.SideLength==12&&deleted.Contains(n.Code)))
            if(all.FirstOrDefault(n=>References(n,parent)) is {} child)
                return new("LogicalNodeHasInstances",$"LN12 {parent.Code}（值“{parent.Properties.Value}”）仍有 LN10 实例 {child.Code}，不能删除；请先删除实例，再删除 LN12。");
        return null;
    }
    public static void EnsureValid(IEnumerable<LogicalNodeSnapshot> nodes)
    {
        var all=nodes.ToArray();foreach(var node in all)
            if(Validate(node,all) is {} invalid)throw new InvalidLogicalNodeInstanceException(invalid);
    }
}
