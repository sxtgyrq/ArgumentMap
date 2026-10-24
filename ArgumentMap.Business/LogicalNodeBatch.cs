using ArgumentMap.Contracts;
using ArgumentMap.Storage;

namespace ArgumentMap.Business;

// Import and group paste must reserve names and resolve local references in the
// same order, against the same transaction-local snapshot.
internal sealed record LogicalNodeBatchResult(Dictionary<string,string> Codes,List<string> Notes);
internal static class LogicalNodeBatch
{
    internal static ServiceResult<LogicalNodeBatchResult> Create(LineStoreState working,string owner,FlowLogicalNode[] input)
    {
        static ServiceResult<LogicalNodeBatchResult> Fail(string code,string message)=>ServiceResult<LogicalNodeBatchResult>.Fail(code,message);
        var nodes=new LogicalNodeService(new Scope(working));var codes=new Dictionary<string,string>(StringComparer.Ordinal);var notes=new List<string>();
        foreach(var n in input.OrderBy(n=>n.Size==12?0:n.Size==6?1:2))
        {
            var logic=n.Logic;
            var value=n.Value;
            if(n.Size==10)
            {
                var parentValue=LogicalNodeInstanceRules.ParentValue(value);
                var local=input.Where(l=>l.Size==12&&LogicalNodeValueUniqueness.Normalize(l.Value)==parentValue).ToArray();
                if(local.Length>1)return Fail("AmbiguousInstanceParent",$"LN {n.Id}：本批有多个值为“{parentValue}”的 LN12，无法确定实例所属；请使用不同的 LN12 值。");
                if(local.Length==1)value=LogicalNodeInstanceRules.Reparent(value,working.LogicalNodes[codes[local[0].Id]].Properties.Value);
                logic=LogicalNodeInstanceRules.Expression(value);
            }
            if(n.Size==4&&!string.IsNullOrWhiteSpace(logic))
            {
                var branch=new LogicalNodeSnapshot(n.Id,owner,0,0,0,4,new(){Value=n.Value,LogicLanguage=logic});
                if(LogicalNodeBranchRules.SourceLogic(branch) is {} source)
                {
                    var local=input.Where(l=>l.Size==6&&(l.Logic==source||LogicalNodeOperandRules.Normalize(l.Logic,l.OperandKind)==source)).ToArray();
                    if(local.Length>1)return Fail("AmbiguousBranchSource",$"LN {n.Id}：本次导入有多个逻辑语言为“{source}”的 LN6，无法确定分支对应哪个节点，请先使用不同的 LN6 逻辑语言。");
                    if(local.Length==1)logic=LogicalNodeBranchRules.Expression(n.Value,working.LogicalNodes[codes[local[0].Id]].Properties.LogicLanguage);
                }
            }
            var create=nodes.Create(new(owner,n.At[0],n.At[1],n.Size,new(){Value=value,Info=n.Info,LogicLanguage=logic,OperandKind=n.OperandKind}));
            if(!create.Success)return Fail(create.Code,$"LN {n.Id}：{create.Message}");codes.Add(n.Id,create.Data!.Code);
            if(create.Data.Properties.Value!=n.Value)notes.Add($"LN {n.Id}：值“{n.Value}”已按大写与唯一性规则保存为“{create.Data.Properties.Value}”。");
            if(create.Data.Properties.LogicLanguage!=n.Logic)
            {
                var detail=n.Size==10?"已按所属 LN12 最终保存的值生成隶属表达式。":n.Size==6?"本批引用该 LN6 的 LN4 将同步使用新表达式；已有 LN4 保持原关联，其他边长节点的命题引用未自动改写，请核对。":
                    string.IsNullOrWhiteSpace(n.Logic)?"空白逻辑语言已规范为空字符串。":"已同步为本批对应 LN6 实际保存的表达式。";
                notes.Add($"LN {n.Id}：逻辑语言“{n.Logic}”已保存为“{create.Data.Properties.LogicLanguage}”。{detail}");
            }
        }
        return ServiceResult<LogicalNodeBatchResult>.Ok(new(codes,notes),"节点批次已建立。");
    }
    private sealed class Scope(LineStoreState state):ILineRepository{public T Transaction<T>(Func<LineStoreState,T> operation)=>operation(state);}
}
