using ArgumentMap.Contracts;
using ArgumentMap.Rules;

namespace ArgumentMap.Business;

internal sealed partial class LogicalNodeConnectionPlanner
{
    private readonly record struct GoToState(Point Point,int Heading);
    private sealed record GoToStep(double Length,int Turns,GoToState? Previous,int Root);
    private ServiceResult<LogicalNodeConnectionPlan> FindGoTo(Port[] starts)
    {
        ServiceResult<LogicalNodeConnectionPlan> Fail(string code,string message)=>ServiceResult<LogicalNodeConnectionPlan>.Fail(code,message);
        if(starts.Length==0)return NotFound();
        LogicalNodeReachability graph;
        try{graph=LogicalNodeReachability.Build(map,target,code,cancellation,Expired);}
        catch(TimeoutException){return Fail("ConnectionSearchLimit","前往搜索达到时间或规模上限，尚未确认最短线路；未新增任何线路。");}
        if(graph.Pieces.Count==0)return Fail("NoReachableTargetRoad","未找到可顺向到达 LN2 且中途不经过其他 LN 的已有线路，请检查线路方向和 Cross 通行设置。");
        var pieces=graph.Pieces.ToArray();
        double Estimate(Point p)
        {
            var best=double.PositiveInfinity;
            foreach(var piece in pieces)
            {
                double ax=(double)piece.A.X,ay=(double)piece.A.Y,dx=(double)(piece.B.X-piece.A.X),dy=(double)(piece.B.Y-piece.A.Y);
                var t=Math.Clamp(((p.X-ax)*dx+(p.Y-ay)*dy)/(dx*dx+dy*dy),0,1);
                var ex=p.X-ax-dx*t;var ey=p.Y-ay-dy*t;best=Math.Min(best,Math.Sqrt(ex*ex+ey*ey));
            }
            return best;
        }
        var steps=new Dictionary<GoToState,GoToStep>();var visited=new HashSet<GoToState>();
        var queue=new PriorityQueue<GoToState,(double Length,int Turns,long Tie)>();long sequence=0;
        LogicalNodeConnectionPlan? bestPlan=null;var bestLength=double.PositiveInfinity;
        for(var i=0;i<starts.Length;i++)
        {
            var port=starts[i];var state=new GoToState(port.Gate,port.Heading);var length=Length(port.Tail,port.Gate);
            if(steps.TryGetValue(state,out var prior)&&prior.Length<=length)continue;
            // A shifted first gate can already be just past a valid join on the source stem.
            // Its lower bound is the stem itself, not another trip back toward the old road.
            steps[state]=new(length,0,null,i);queue.Enqueue(state,(length,0,sequence++));
        }
        LogicalNodeConnectionPlan? Candidate(GoToState state,Point end,LogicalNodeConnectionJoin join)
        {
            var points=new List<Point>{end};var at=state;
            while(true)
            {
                points.Add(at.Point);var step=steps[at];
                if(step.Previous is not {} previous){points.Add(starts[step.Root].Tail);break;}
                at=previous;
            }
            points.Reverse();var reduced=new List<Point>();
            foreach(var p in points)
            {
                if(reduced.Count>0&&reduced[^1]==p)continue;
                if(reduced.Count>=2)
                {
                    var a=reduced[^2];var b=reduced[^1];
                    if((long)(b.X-a.X)*(p.Y-b.Y)==(long)(b.Y-a.Y)*(p.X-b.X)&&
                        (long)(b.X-a.X)*(p.X-b.X)+(long)(b.Y-a.Y)*(p.Y-b.Y)>0)reduced.RemoveAt(reduced.Count-1);
                }
                reduced.Add(p);
            }
            if(reduced.Count is <2 or >128)return null;
            var plan=new LogicalNodeConnectionPlan(LogicalNodeConnectionAnchor.From(source),LogicalNodeConnectionAnchor.From(target),code,
                reduced.Select((p,i)=>new LineNode(code,i,p.X,p.Y)).ToArray(),LogicalNodeConnectionKind.GoTo,join);
            if(!LogicalNodeConnectionGeometry.ValidateTargets(map,plan).Success||!LogicalNodeConnectionGeometry.ValidateJoin(map,plan).Success)return null;
            var working=LineTopologyService.Clone(map);working.Lines[code]=new(code,"route",false,0,[]);
            if(LineSaveService.ValidateShape(working,new(code,"route",0,plan.Nodes),plan.Nodes) is not null)return null;
            var divided=LineSubdivision.Divide(working,plan.Nodes);var path=graph.Path(join);
            return divided.Success&&divided.Data!.Length<=4096&&path is not null?plan with{Nodes=divided.Data,Continuation=path}:null;
        }
        var expanded=0;
        while(queue.TryDequeue(out var state,out var priority))
        {
            if(visited.Contains(state))continue;
            // No turn penalty in the objective: actual NEW line length alone determines the winner.
            // Do not accept an incumbent after a timeout while a shorter candidate remains unsearched.
            if(bestPlan is not null&&priority.Length>=bestLength-1e-9)return Success(bestPlan);
            if(++expanded%32==0&&(Expired()||steps.Count>160000))
                return Fail("ConnectionSearchLimit","前往搜索达到时间或规模上限，尚未确认最短线路；未新增任何线路。请调整 LN1 或附近线路后重试。");
            visited.Add(state);var current=steps[state];var at=state.Point;
            foreach(var piece in pieces)
            {
                var oldA=piece.Road.Nodes[piece.Segment];var oldB=piece.Road.Nodes[piece.Segment+1];
                decimal ux=Math.Sign(oldB.X-oldA.X),uy=Math.Sign(oldB.Y-oldA.Y);
                var t=((at.X-piece.A.X)*ux+(at.Y-piece.A.Y)*uy)/(ux*ux+uy*uy);
                var foot=new LogicalNodeReachability.Position(piece.A.X+t*ux,piece.A.Y+t*uy);
                if(!LogicalNodeReachability.Contains(piece,foot))continue;
                var join=new LogicalNodeConnectionJoin(piece.Road.Code,new(checked((int)(foot.X*2)),checked((int)(foot.Y*2))));
                // The perpendicular from this search vertex has exactly one foot.
                // Rounding a half foot along the old road would break the angle.
                // Keep searching the neighboring integer vertices below: moving
                // one horizontal/vertical grid step reaches the opposite parity
                // and can yield a shorter legal integer join than an extra stem.
                if(!join.Position.IsInteger)continue;
                var dx=Math.Sign(foot.X-at.X);var dy=Math.Sign(foot.Y-at.Y);
                if(current.Previous is null&&(foot.X-at.X)*(foot.X-at.X)+(foot.Y-at.Y)*(foot.Y-at.Y)<=1&&
                    Directions[state.Heading].X*ux+Directions[state.Heading].Y*uy==0&&
                    (foot.X-at.X)*Directions[state.Heading].X+(foot.Y-at.Y)*Directions[state.Heading].Y<0)
                {
                    // An occupied first gate may have moved one step beyond the junction.
                    // That Cross is already on the source stem; do not reverse to reach it.
                    if(current.Length<bestLength-1e-9&&Candidate(state,at,join) is {} stem)
                    {bestPlan=stem;bestLength=current.Length;}
                    continue;
                }
                var heading=dx==0&&dy==0?state.Heading:Array.IndexOf(Directions,(dx,dy));
                if(heading<0||!Turn(state.Heading,heading)||Directions[heading].X*ux+Directions[heading].Y*uy!=0)continue;
                var direction=Directions[heading];
                var end=new Point(checked((int)foot.X),checked((int)foot.Y));
                // The old road's own vertex cannot also be our endpoint. One grid step past it
                // is allowed, provided the full segment remains legal and no third-road Cross is touched.
                if(occupied.Contains(end))end=new(end.X+direction.X,end.Y+direction.Y);
                if(!InRange(end)||current.Length+Length(at,end)>=bestLength-1e-9||
                    end!=at&&!Clear(at,end))continue;
                var candidate=Candidate(state,end,join);
                if(candidate is null)continue;
                bestPlan=candidate;bestLength=current.Length+Length(at,end);
            }
            for(var heading=0;heading<8;heading++)
            {
                if(!Turn(state.Heading,heading))continue;
                var (dx,dy)=Directions[heading];var point=new Point(at.X+dx,at.Y+dy);
                // Search vertices are prospective bends, not saved collinear subdivision points.
                // Jump over an occupied old vertex so a legal perpendicular pass is still possible.
                while(InRange(point)&&occupied.Contains(point))point=new(point.X+dx,point.Y+dy);
                if(!InRange(point)||!Clear(at,point))continue;
                var length=current.Length+Length(at,point);var estimate=length+Estimate(point);
                if(estimate>=bestLength-1e-9)continue;
                var following=new GoToState(point,heading);var turns=current.Turns+(heading==state.Heading?0:1);
                if(steps.TryGetValue(following,out var previous)&&(previous.Length<length-1e-9||
                    Math.Abs(previous.Length-length)<1e-9&&previous.Turns<=turns))continue;
                steps[following]=new(length,turns,state,current.Root);queue.Enqueue(following,(estimate,turns,sequence++));
            }
        }
        return bestPlan is not null?Success(bestPlan):NotFound();
        static ServiceResult<LogicalNodeConnectionPlan> Success(LogicalNodeConnectionPlan plan)=>
            ServiceResult<LogicalNodeConnectionPlan>.Ok(plan,"前往路线已规划：按新增线路长度选择接入点，随后沿已有通路到达 LN2；尚未保存。");
    }
}
