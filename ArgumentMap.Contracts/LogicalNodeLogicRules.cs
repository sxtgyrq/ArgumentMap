using System.Globalization;
using System.Text;

namespace ArgumentMap.Contracts;

public sealed record LogicalNodeLogicConflict(string LogicLanguage,string ExistingCode,string ConflictingCode,decimal ExistingX,decimal ExistingY);
public sealed class DuplicateLogicalNodeLogicException(LogicalNodeLogicConflict conflict):InvalidOperationException(
    FormattableString.Invariant($"边长为 6 的 LN 逻辑语言“{conflict.LogicLanguage}”已被 LN {conflict.ExistingCode}（中心坐标 X={conflict.ExistingX:0.0}, Y={conflict.ExistingY:0.0}）使用，请使用不同的逻辑语言。"))
{public LogicalNodeLogicConflict Conflict {get;}=conflict;}
public sealed class LogicalNodeLogicRequiredException(string code,int sideLength=6):InvalidOperationException($"边长为 {sideLength} 的 LN {code} 的逻辑语言不能为空或仅含空白。");

public static class LogicalNodeLogicRules
{
    public static string ReservePlaceholder(string code,ISet<string> used)
        => ReserveLogic("LN_"+code,used);

    public static string ReserveLogic(string requested,ISet<string> used)
    {
        if(used.Add(requested))return requested;
        for(long suffix=1;;suffix++)
        {
            var number=suffix.ToString(CultureInfo.InvariantCulture);
            var prefix=new StringBuilder();
            foreach(var rune in requested.EnumerateRunes())
            {
                if(prefix.Length+rune.Utf16SequenceLength+number.Length>255)break;
                prefix.Append(rune.ToString());
            }
            var candidate=prefix+number;
            if(used.Add(candidate))return candidate;
        }
    }
    public static LogicalNodeLogicConflict? FindConflict(LogicalNodeSnapshot node,IEnumerable<LogicalNodeSnapshot> nodes)
    {
        if(node.SideLength!=6)return null;
        var existing=nodes.FirstOrDefault(n=>n.SideLength==6&&n.Code!=node.Code&&
            string.Equals(n.Properties.LogicLanguage,node.Properties.LogicLanguage,StringComparison.Ordinal));
        return existing is null?null:new(node.Properties.LogicLanguage,existing.Code,node.Code,existing.X,existing.Y);
    }
    public static LogicalNodeLogicConflict? FindConflict(IEnumerable<LogicalNodeSnapshot> nodes)
    {
        var used=new Dictionary<string,LogicalNodeSnapshot>(StringComparer.Ordinal);
        foreach(var node in nodes.Where(n=>n.SideLength==6&&!string.IsNullOrWhiteSpace(n.Properties.LogicLanguage)).OrderBy(n=>n.Code,StringComparer.Ordinal))
            if(!used.TryAdd(node.Properties.LogicLanguage,node))
            {var existing=used[node.Properties.LogicLanguage];return new(node.Properties.LogicLanguage,existing.Code,node.Code,existing.X,existing.Y);}
        return null;
    }
    public static void EnsureValid(IEnumerable<LogicalNodeSnapshot> nodes)
    {
        if(nodes.FirstOrDefault(n=>n.SideLength is 6 or 12&&string.IsNullOrWhiteSpace(n.Properties.LogicLanguage)) is {} empty)
            throw new LogicalNodeLogicRequiredException(empty.Code,empty.SideLength);
        if(FindConflict(nodes) is {} duplicate)throw new DuplicateLogicalNodeLogicException(duplicate);
    }
}
