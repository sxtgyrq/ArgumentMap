using ArgumentMap.Contracts;
using ArgumentMap.Rules;
using ArgumentMap.Storage;

namespace ArgumentMap.Business;

internal static class LogicalNodeConnectionGeometry
{
    internal const int TailLength=5;
    internal static bool Touches(LogicalNodeSnapshot square,LineNode a,LineNode b)
    {
        decimal low=0,high=1;
        bool Clip(decimal origin,decimal delta,decimal min,decimal max)
        {
            if(delta==0)return origin>=min&&origin<=max;
            var x=(min-origin)/delta;var y=(max-origin)/delta;
            low=Math.Max(low,Math.Min(x,y));high=Math.Min(high,Math.Max(x,y));return low<=high;
        }
        return Clip(a.X,b.X-a.X,square.Left,square.Right)&&Clip(a.Y,b.Y-a.Y,square.Bottom,square.Top);
    }
    internal static bool Tail(LogicalNodeSnapshot square,LineNode a,LineNode b,bool start)
    {
        var end=start?a:b;var inner=start?b:a;
        if(a.Y==b.Y&&a.Y>square.Bottom&&a.Y<square.Top)
            return end.X==(int)decimal.Floor(square.Left-TailLength)&&inner.X>square.Right ||
                end.X==(int)decimal.Ceiling(square.Right+TailLength)&&inner.X<square.Left;
        if(a.X==b.X&&a.X>square.Left&&a.X<square.Right)
            return end.Y==(int)decimal.Floor(square.Bottom-TailLength)&&inner.Y>square.Top ||
                end.Y==(int)decimal.Ceiling(square.Top+TailLength)&&inner.Y<square.Bottom;
        return false;
    }
    internal static ServiceResult<bool> ValidateTargets(LineStoreState state,LogicalNodeConnectionPlan plan)
    {
        if(plan?.Source is null||plan.Target is null||!Enum.IsDefined(plan.Kind)||!LineCode.IsValid(plan.Source.Code)||!LineCode.IsValid(plan.Target.Code)||plan.Nodes is not {Length:>=2 and <=4096}||!LineCode.IsValid(plan.LineCode)||plan.Nodes.Any(n=>n is null))
            return ServiceResult<bool>.Fail("InvalidConnectionPlan","连接预览格式无效，请重新选择两个 LN。");
        if(plan.Nodes.Where((n,i)=>n.LineCode!=plan.LineCode||n.Order!=i||n.X<0||n.Y<0||n.X>500000||n.Y>500000).Any())
            return ServiceResult<bool>.Fail("InvalidConnectionPlan","线路节点的编码、序号或坐标无效，请重新规划。");
        if(plan.Source.Code==plan.Target.Code)return ServiceResult<bool>.Fail("SameConnectionNode","请选择两个不同的 LN。");
        if(!state.LogicalNodes.TryGetValue(plan.Source.Code,out var source)||!state.LogicalNodes.TryGetValue(plan.Target.Code,out var target))
            return ServiceResult<bool>.Fail("ConnectionNodeMissing","目标 LN 已删除，请重新选择。");
        if(!plan.Source.Matches(source)||!plan.Target.Matches(target))
            return ServiceResult<bool>.Fail("ConnectionNodeChanged","目标 LN 的位置或边长已变化，请重新规划连接。");
        var nodes=plan.Nodes;
        var crossesSource=plan.Kind!=LogicalNodeConnectionKind.Find;
        var crossesTarget=plan.Kind!=LogicalNodeConnectionKind.GoTo;
        if(crossesSource&&!Tail(source,nodes[0],nodes[1],true)||crossesTarget&&!Tail(target,nodes[^2],nodes[^1],false))
            return ServiceResult<bool>.Fail("InvalidConnectionTail","新线必须横向或纵向穿越指定 LN，并在 LN 外侧延伸 5.5 个单位。");
        var road=new LineSnapshot(plan.LineCode,"",false,0,nodes);
        foreach(var node in state.LogicalNodes.Values)
        {
            if(crossesSource&&node.Code==source.Code||crossesTarget&&node.Code==target.Code)
            {
                if(LogicalNodePositionCalculator.Calculate(node,[road]).Length!=1)
                    return ServiceResult<bool>.Fail("InvalidConnectionPassage","线路必须恰好穿越每个目标 LN 一次。");
            }
            else if(nodes.Zip(nodes.Skip(1)).Any(p=>Touches(node,p.First,p.Second)))
                return ServiceResult<bool>.Fail("ConnectionTouchesOtherNode",$"连接线路经过其他 LN {node.Code}，请重新规划。");
        }
        return ServiceResult<bool>.Ok(true,"");
    }

    internal static ServiceResult<bool> ValidateJoin(LineStoreState state,LogicalNodeConnectionPlan plan)
    {
        ServiceResult<bool> Fail()=>ServiceResult<bool>.Fail("InvalidConnectionJoin","连接 Cross、相邻线段方向或已有通路已无效，请重新规划。");
        if(plan.Kind==LogicalNodeConnectionKind.Direct)return plan.Join is null?ServiceResult<bool>.Ok(true,""):Fail();
        var join=plan.Join;
        if(join is null||!LineCode.IsValid(join.RoadCode)||join.RoadCode==plan.LineCode||join.Position.X2 is <0 or >1000000||join.Position.Y2 is <0 or >1000000||
            !state.Lines.TryGetValue(join.RoadCode,out var road)||LogicalNodeRules.ContainsAny(state,join.Position))return Fail();
        var a=plan.Kind==LogicalNodeConnectionKind.Find?plan.Nodes[1]:plan.Nodes[^2];
        var b=plan.Kind==LogicalNodeConnectionKind.Find?plan.Nodes[0]:plan.Nodes[^1];
        // End at an integer Cross. A half-integer diagonal Cross may extend to the next integer (<=1 unit).
        var dx=b.X-join.Position.X;var dy=b.Y-join.Position.Y;
        if(dx*dx+dy*dy>1.000000001||!road.Nodes.Zip(road.Nodes.Skip(1)).Any(p=>
            ArgumentMap.Rules.LineGeometry.IsPerpendicular(a,b,p.First,p.Second)&&
            ArgumentMap.Rules.LineGeometry.TryIntersection(a,b,p.First,p.Second,out var at)&&at==join.Position))return Fail();
        return ServiceResult<bool>.Ok(true,"");
    }
}
