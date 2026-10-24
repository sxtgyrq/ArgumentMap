using ArgumentMap.Contracts;

namespace ArgumentMap.Business;

internal sealed partial class LogicalNodeConnectionPlanner
{
    private ServiceResult<LogicalNodeConnectionPlan> FindFromSource()
    {
        // Reversing the travel graph and swapping the selected LNs turns the same
        // shortest-new-road problem into GoTo. Reverse only the returned new road
        // and display path; all validation/commit work uses the original map.
        var result=new LogicalNodeConnectionPlanner(LogicalNodeReachability.ReverseDirections(map),target,source,
            code,cancellation,LogicalNodeConnectionKind.GoTo).Find();
        if(!result.Success||result.Data is null)
        {
            var message=result.Message.Replace("LN1","{source}").Replace("LN2","LN1").Replace("{source}","LN2")
                .Replace("前往","寻找");
            if(result.Code=="NoReachableTargetRoad")
                return ServiceResult<LogicalNodeConnectionPlan>.Fail("NoReachableSourceRoad","未找到从 LN1 顺向出发且途中不经过其他 LN 的已有线路，请检查线路方向和 Cross 通行设置。");
            return ServiceResult<LogicalNodeConnectionPlan>.Fail(result.Code,message);
        }
        var original=result.Data;
        var plan=new LogicalNodeConnectionPlan(LogicalNodeConnectionAnchor.From(source),LogicalNodeConnectionAnchor.From(target),code,
            original.Nodes.Reverse().Select((n,i)=>n with{Order=i}).ToArray(),LogicalNodeConnectionKind.Find,original.Join)
            {Continuation=original.Continuation.Reverse().ToArray()};
        return ServiceResult<LogicalNodeConnectionPlan>.Ok(plan,
            "寻找路线已规划：从 LN1 顺着已有通路到接出点，再沿最短新增线路穿过 LN2；尚未保存。");
    }
}
