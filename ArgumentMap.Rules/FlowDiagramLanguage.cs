using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ArgumentMap.Contracts;

namespace ArgumentMap.Rules;

public static class FlowDiagramLanguage
{
    public const string Format="argumentmap.flow/1";
    public const int MaximumTextLength=1_000_000;
    private static readonly JsonSerializerOptions Options=new()
    {PropertyNamingPolicy=JsonNamingPolicy.CamelCase,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,ReadCommentHandling=JsonCommentHandling.Skip,AllowTrailingCommas=true,MaxDepth=32};
    public static ServiceResult<FlowDiagramDocument> Parse(string? text)
    {
        ServiceResult<FlowDiagramDocument> Fail(string message)=>ServiceResult<FlowDiagramDocument>.Fail("InvalidFlowText",message);
        if(string.IsNullOrWhiteSpace(text))return Fail("请粘贴 ArgumentMap Flow JSON 文本。");
        if(text.Length>MaximumTextLength)return Fail("流程图文本不能超过 100 万字符。");
        text=text.Trim();
        if(text.StartsWith("```",StringComparison.Ordinal)&&text.EndsWith("```",StringComparison.Ordinal)&&text.IndexOf('\n') is var line&&line>=0)
            text=text[(line+1)..^3].Trim();
        try
        {
            using var json=JsonDocument.Parse(text,new(){AllowTrailingCommas=true,CommentHandling=JsonCommentHandling.Skip,MaxDepth=32});
            if(DuplicateProperty(json.RootElement) is {} duplicate)return Fail($"JSON 字段 {duplicate} 重复，请删除重复字段。");
            var d=json.RootElement.Deserialize<FlowDiagramDocument>(Options);
            if(d is null||d.Format!=Format)return Fail($"format 必须为 {Format}。");
            if(d.Ln is null||d.Roads is null||d.Crosses is null||d.Ln.Length==0)return Fail("ln 至少需要一个 LN，roads、crosses 必须是数组。");
            if(d.Ln.Length>256||d.Roads.Length>256||d.Crosses.Length>4096)return Fail("一次最多导入 256 个 LN、256 条 Road、4096 个 Cross 设置，请分批导入。");
            bool Id(string? id)=>id is not null&&Regex.IsMatch(id,"^[A-Za-z][A-Za-z0-9_-]{0,63}$",RegexOptions.CultureInvariant);
            bool Point(decimal[]? p)=>p is {Length:2}&&p.All(v=>v>=-500000&&v<=500000);
            bool Half(decimal v)=>Math.Abs(v%1)==.5m;
            var lnIds=new HashSet<string>(StringComparer.Ordinal);var roadIds=new HashSet<string>(StringComparer.Ordinal);
            foreach(var n in d.Ln)
            {
                if(n is null||!Id(n.Id)||!lnIds.Add(n.Id))return Fail("LN 的 id 必须唯一，以字母开头，最多 64 位字母、数字、下划线或连字符。");
                if(!Point(n.At)||!Half(n.At[0])||!Half(n.At[1]))return Fail($"LN {n.Id}：at 必须是两个半整数相对坐标，例如 [0.5, -39.5]。");
                if(n.Size is not(4 or 6 or 8 or 10 or 12 or 18))return Fail($"LN {n.Id}：size 只能为 4、6、8、10、12 或 18。");
                if(!LogicalNodeOperandRules.Valid(n.Size,n.OperandKind))return Fail($"LN {n.Id}：operandKind 只能为 Class、Instance、Custom；非 LN6 只能省略或使用 Custom。");
                if(n.Size is not (4 or 8 or 10)&&string.IsNullOrWhiteSpace(n.Logic))return Fail($"LN {n.Id}：逻辑语言 logic 必填，不能省略、为 null、空字符串或仅空白。LN4 允许留空；LN8 和 LN10 漏填或空串时自动生成。");
                if(n.Value is null||n.Info is null||n.Logic is null||n.Value.Length>(n.Size==10?LogicalNodeInstanceRules.ValueMaximum:32)||n.Info.Length>255||n.Logic.Length>(n.Size==10?LogicalNodeInstanceRules.LogicMaximum:255))return Fail($"LN {n.Id}：LN10 的 value 最多 220 字符，其他 LN 最多 32 字符；LN10 的 logic 最多 4096 字符，其他 LN 的 logic 及所有 info 最多 255 字符，不能为 null。");
                if(LogicalNodeTextRules.HasLineBreak(n.Value)||LogicalNodeTextRules.HasLineBreak(n.Logic))return Fail($"LN {n.Id}：value 和 logic 不能包含换行，只有 info 允许换行。");
                if(n.Size==4&&!LogicalNodeBranchRules.ValidValue(n.Value))return Fail($"LN {n.Id}：LN4 的 value 只能为 True 或 False（字符串），逻辑语言可以为空。");
                if(n.Size==8&&!LogicalNodeRelationRules.ValidValue(n.Value))return Fail($"LN {n.Id}：LN8 的 value 只能为 {LogicalNodeRelationRules.OptionsDescription}（字符串）；等同及集合关系用于 LN12，蕴含和等价用于 LN6，为绘图约定，不自动检查端点。");
                if(n.Size==8&&n.Logic.Length>0&&n.Logic!=n.Value)return Fail($"LN {n.Id}：LN8 的 logic 必须与 value 完全一致（{LogicalNodeRelationRules.OptionsDescription}），省略或空串时自动补齐。");
                if(n.Size==10)
                {
                    if(!LogicalNodeInstanceRules.ValidFormat(n.Value))return Fail($"LN {n.Id}：LN10 的 value 必须为 类名小写() 或 类名小写(属性=属性值,属性=属性值)，例如 human() a、human(id=1,name=要瑞卿) c；别名可省略，括号后用空白分隔，属性和别名保留原文。");
                    if(!LogicalNodeInstanceRules.HasEnglishLetter(LogicalNodeInstanceRules.ParentValue(n.Value)))return Fail($"LN {n.Id}：LN10 的类名前缀必须至少包含一个英文字母（A–Z／a–z），不能引用全中文、纯数字或符号的 LN12。");
                    var value=LogicalNodeInstanceRules.Normalize(n.Value);
                    if(n.Logic.Length>0&&n.Logic!=LogicalNodeInstanceRules.Expression(value))
                        return Fail($"LN {n.Id}：LN10 的 logic 应为“{LogicalNodeInstanceRules.Expression(value)}”，不可填写全称量词或其他表达式；也可省略让系统生成。");
                }
            }
            d=d with{Ln=d.Ln.Select(n=>n.Size==8?n with{Logic=n.Value}:n.Size==10?
                n with{Value=LogicalNodeInstanceRules.Normalize(n.Value),Logic=LogicalNodeInstanceRules.Expression(LogicalNodeInstanceRules.Normalize(n.Value))}:n).ToArray()};
            if(d.Origin is null||!lnIds.Contains(d.Origin))return Fail("origin 必须引用 ln 中的一个 id，插入点对应这个 LN 的中心。");
            var points=0;
            foreach(var r in d.Roads)
            {
                if(r is null||!Id(r.Id)||!roadIds.Add(r.Id))return Fail("Road 的 id 必须唯一，以字母开头，最多 64 位字母、数字、下划线或连字符。");
                if(r.Points is null||r.Points.Length<2||r.Points.Any(p=>!Point(p)||p.Any(v=>v%1!=0)))return Fail($"Road {r.Id}：points 至少两个 [整数 X, 整数 Y] 节点。");
                points+=r.Points.Length;if(points>4096)return Fail("一次最多导入 4096 个线路节点，请分批导入。");
                if(r.Direction is not("forward" or "both"))return Fail($"Road {r.Id}：direction 只能为 forward（单向）或 both（双向）。");
                if(r.Speed is <5 or >500)return Fail($"Road {r.Id}：speed 必须为 5–500 的整数。");
                if(r.Name is null||r.Info is null||r.Name.Length>=16||r.Info.Length>=128)return Fail($"Road {r.Id}：name 最多 15 字符，info 最多 127 字符。");
            }
            foreach(var c in d.Crosses)
            {
                if(c is null||c.Road1 is null||c.Road2 is null||c.Road1==c.Road2||!roadIds.Contains(c.Road1)||!roadIds.Contains(c.Road2))return Fail("Cross 的 road1、road2 必须引用两条不同的导入 Road id。");
                if(!Point(c.At)||c.At.Any(v=>v*2%1!=0))return Fail($"Cross {c.Road1}/{c.Road2}：at 必须为整数或半整数坐标。");
                if(c.State is not("unset" or "connected" or "disconnected" or "road1ToRoad2" or "road2ToRoad1"))return Fail($"Cross {c.Road1}/{c.Road2}：state 无效。");
            }
            return ServiceResult<FlowDiagramDocument>.Ok(d,"流程图文本格式正确。");
        }
        catch(JsonException e){return Fail($"JSON 格式或字段错误：第 {(e.LineNumber??0)+1} 行，第 {(e.BytePositionInLine??0)+1} 字节。{e.Message}");}
    }
    private static string? DuplicateProperty(JsonElement value)
    {
        if(value.ValueKind==JsonValueKind.Object)
        {
            var names=new HashSet<string>(StringComparer.Ordinal);
            foreach(var p in value.EnumerateObject())
            {if(!names.Add(p.Name))return p.Name;if(DuplicateProperty(p.Value) is {} found)return found;}
        }
        else if(value.ValueKind==JsonValueKind.Array)foreach(var item in value.EnumerateArray())if(DuplicateProperty(item) is {} found)return found;
        return null;
    }
}
