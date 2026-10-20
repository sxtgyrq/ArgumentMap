using ArgumentMap.Rules;
using ArgumentMap.Contracts;
using ArgumentMap.Storage;

namespace ArgumentMap.Business;

/// <summary>Topology, Cross identity/settings and LN positions are published by one data-layer transaction.</summary>
public sealed class LineTopologyService(ILineRepository repository)
{
    public ServiceResult<LineTopologyResult> Split(SplitLineRequest request) => repository.Transaction(state =>
    {
        if(Access(state,request.LineCode,request.Owner,request.Version,out var original) is {} failure)return failure;
        var cut=request.CutBeforeOrder;
        if(cut<2||cut>original!.Nodes.Length-2)return Fail("InvalidSplit","拆分后两条线路都必须至少保留两个节点。请选择中间节点，在它之前断开。");
        var code=LineCode.Generate();while(state.Lines.ContainsKey(code))code=LineCode.Generate();
        var first=original with{Version=original.Version+1,Nodes=Renumber(original.Code,original.Nodes.Take(cut))};
        var second=original with{Code=code,Version=1,Nodes=Renumber(code,original.Nodes.Skip(cut))};
        var working=Clone(state);working.Lines[first.Code]=first;working.Lines[second.Code]=second;
        var mapping=new Dictionary<string,string[]>{{original.Code,[first.Code,second.Code]}};
        if(Prepare(state,working,mapping,[first,second]) is {} invalid)return invalid;
        first=working.Lines[first.Code];second=working.Lines[second.Code];
        Publish(state,working);
        return ServiceResult<LineTopologyResult>.Ok(new([CopySnapshot(first),CopySnapshot(second)],[]),$"已拆分为 {first.Code} 与 {second.Code}，节点之间的连接段已移除；Cross 设置及 LN 位置已在同一事务同步。");
    });

    public ServiceResult<MergePreview> GetMergePlans(MergeLinesRequest request) => repository.Transaction(state =>
    {
        ServiceResult<MergePreview> Error(ServiceResult<LineTopologyResult> e)=>ServiceResult<MergePreview>.Fail(e.Code,e.Message);
        if(request.FirstCode==request.SecondCode)return Error(Fail("SameLine","请选择两条不同线路进行合并。"));
        if(Access(state,request.FirstCode,request.Owner,request.FirstVersion,out var first) is {} firstFailure)return Error(firstFailure);
        if(Access(state,request.SecondCode,request.Owner,request.SecondVersion,out var second) is {} secondFailure)return Error(secondFailure);
        if(first!.Nodes.Length<2||second!.Nodes.Length<2)return Error(Fail("TooFewNodes","待合并的两条线路都必须至少有两个节点。"));
        if(first.Nodes.Zip(first.Nodes.Skip(1)).Any(p=>!LineGeometry.IsEightDirection(p.First,p.Second))||
            second.Nodes.Zip(second.Nodes.Skip(1)).Any(p=>!LineGeometry.IsEightDirection(p.First,p.Second)))
            return Error(Fail("InvalidDirection","原线路存在不符合八方向的线段，请先修正再合并。"));
        var working=Clone(state);working.Lines.Remove(second.Code);
        var mapping=new Dictionary<string,string[]>{{first.Code,[first.Code]},{second.Code,[first.Code]}};
        ServiceResult<LineTopologyResult>? lastFailure=null;
        var plans=new Dictionary<(int X,int Y),MergePlan>();
        foreach(var move in LineMergeCandidates.Enumerate(first,second,state))
        {
            var points=first.Nodes.Concat(second.Nodes).ToArray();var last=first.Nodes[^1];var previous=first.Nodes[^2];var head=second.Nodes[0];var next=second.Nodes[1];
            points[first.Nodes.Length-1]=last with{X=last.X-move.First*Math.Sign(last.X-previous.X),Y=last.Y-move.First*Math.Sign(last.Y-previous.Y)};
            points[first.Nodes.Length]=head with{X=head.X+move.Second*Math.Sign(next.X-head.X),Y=head.Y+move.Second*Math.Sign(next.Y-head.Y)};
            var joinedFirst=points[first.Nodes.Length-1];var joinedSecond=points[first.Nodes.Length];
            var direction=(Math.Sign(joinedSecond.X-joinedFirst.X),Math.Sign(joinedSecond.Y-joinedFirst.Y));
            if(plans.ContainsKey(direction))continue;
            var merged=first with{Version=first.Version+1,Nodes=Renumber(first.Code,points)};working.Lines[first.Code]=merged;
            if(Prepare(state,working,mapping,[merged],rebuildPositions:false) is {} invalid){lastFailure=invalid;continue;}
            static int Corner(LineNode a,LineNode b,LineNode c)
            {
                long x=b.X-a.X,y=b.Y-a.Y,u=c.X-b.X,v=c.Y-b.Y;
                return x*u+y*v==0?90:x*v-y*u==0?180:135;
            }
            plans.Add(direction,new(joinedFirst.X,joinedFirst.Y,joinedSecond.X,joinedSecond.Y,direction.Item1,direction.Item2,
                Corner(previous,joinedFirst,joinedSecond),Corner(joinedFirst,joinedSecond,next),Math.Abs(move.First)+Math.Abs(move.Second)));
        }
        if(plans.Count>0)return ServiceResult<MergePreview>.Ok(new(CopySnapshot(first),CopySnapshot(second),plans.Values.ToArray()),$"找到 {plans.Count} 个合法连接方向；每个方向列出移动最少的方案，优先 135°，请选择并预览。");
        return Error(Fail("CannotMerge","两条线路无法合并：在原末段、首段的方向上收缩或延长后，仍不能满足八方向、135°／90°、LN、Cross 和重叠约束。"+
            (lastFailure is null?"请调整线路或用 C 反转方向后重试。":$" 校验原因：{lastFailure.Message}")));
    });

    // Confirmation always reloads locked map state and revalidates LN/Cross in this transaction.
    // Publish only follows successful validation; the repository commits all dependent rows together
    // and rolls back a failed ServiceResult or any persistence exception.
    public ServiceResult<LineTopologyResult> Merge(ApplyMergeRequest request)=>repository.Transaction(state=>
    {
        if(request.FirstCode==request.SecondCode)return Fail("SameLine","请选择两条不同线路进行合并。");
        if(Access(state,request.FirstCode,request.Owner,request.FirstVersion,out var first) is {} firstFailure)return firstFailure;
        if(Access(state,request.SecondCode,request.Owner,request.SecondVersion,out var second) is {} secondFailure)return secondFailure;
        if(first!.Nodes.Length<2||second!.Nodes.Length<2)return Fail("TooFewNodes","两条线路都必须至少有两个节点。");
        var firstEnd=first.Nodes[^1] with{X=request.FirstX,Y=request.FirstY};var secondStart=second.Nodes[0] with{X=request.SecondX,Y=request.SecondY};
        static bool OnRay(LineNode anchor,LineNode original,LineNode adjusted)
        {
            long dx=original.X-anchor.X,dy=original.Y-anchor.Y,x=adjusted.X-anchor.X,y=adjusted.Y-anchor.Y;
            return adjusted.X is >=0 and <=MapLimits.CoordinateMaximum && adjusted.Y is >=0 and <=MapLimits.CoordinateMaximum &&
                LineGeometry.IsEightDirection(anchor,original)&&dx*y-dy*x==0&&dx*x+dy*y>0;
        }
        if(!OnRay(first.Nodes[^2],first.Nodes[^1],firstEnd)||!OnRay(second.Nodes[1],second.Nodes[0],secondStart))
            return Fail("InvalidEndpointAdjustment","调整点必须沿原末段、首段收缩或延长，不能到达／越过相邻节点，且不能超出地图。");
        var points=first.Nodes.Concat(second.Nodes).ToArray();points[first.Nodes.Length-1]=firstEnd;points[first.Nodes.Length]=secondStart;
        var merged=first with{Version=first.Version+1,Nodes=Renumber(first.Code,points)};
        var working=Clone(state);working.Lines[first.Code]=merged;working.Lines.Remove(second.Code);
        var mapping=new Dictionary<string,string[]>{{first.Code,[first.Code]},{second.Code,[first.Code]}};
        if(Prepare(state,working,mapping,[merged]) is {} invalid)return invalid;
        merged=working.Lines[merged.Code];
        Publish(state,working);var steps=Math.Max(Math.Abs(firstEnd.X-first.Nodes[^1].X),Math.Abs(firstEnd.Y-first.Nodes[^1].Y))+
            Math.Max(Math.Abs(secondStart.X-second.Nodes[0].X),Math.Abs(secondStart.Y-second.Nodes[0].Y));
        return ServiceResult<LineTopologyResult>.Ok(new([CopySnapshot(merged)],[second.Code],steps),$"已合并到 {first.Code}，沿原线段调整共 {steps} 格；第一条属性、Cross 设置及 LN 位置已在同一事务同步。");
    });

    private static ServiceResult<LineTopologyResult>? Access(LineStoreState state,string code,string owner,long version,out LineSnapshot? line)
    {
        line=null;
        if(!LineCode.IsValid(code))return Fail("InvalidLineCode","线路编码必须是 10 位大写英文字母。");
        if(!state.Lines.TryGetValue(code,out line))return Fail("LineNotFound",$"线路 {code} 已不存在，请刷新。");
        if(string.IsNullOrWhiteSpace(owner)||owner!=line.Owner)return Fail("NotOwner",$"不能修改其他用户的线路 {code}。");
        if(line.IsLocked)return Fail("LineIsLocked",$"线路 {code} 已锁定，不能拆分或合并。");
        if(line.Version!=version)return Fail("VersionConflict",$"线路 {code} 已发生变化，请刷新后重试。");
        return null;
    }

    private static ServiceResult<LineTopologyResult>? Prepare(LineStoreState original,LineStoreState working,Dictionary<string,string[]> mapping,LineSnapshot[] changed,bool rebuildPositions=true)
    {
        // Failed candidates never modify the transaction's state, including within WithSession.
        if(SeedCrosses(original,working,mapping) is {} seedFailure)return seedFailure;
        foreach(var line in changed)
            if(LineSaveService.ValidateShape(working,new(line.Code,line.Owner,line.Version,line.Nodes),line.Nodes) is {} invalid)
                return Fail(invalid.Code,invalid.Message);
        // Apply the storage length invariant to every resulting road, including a long merge
        // connector or an existing terminal segment extended by the chosen plan. Preview uses
        // the same refinement, so it cannot offer an impossible-to-subdivide result.
        for(var i=0;i<changed.Length;i++)
        {
            var divided=LineSubdivision.Divide(working,changed[i].Nodes);
            if(!divided.Success)return Fail(divided.Code,divided.Message);
            changed[i]=changed[i] with{Nodes=divided.Data!};working.Lines[changed[i].Code]=changed[i];
        }
        if(CrossPassageValidator.FindInCurrentGeometry(working) is {} passage)return Fail("LinePassesCross",passage.Message("拆分／合并校验失败"));
        var affected=mapping.Keys.Concat(changed.Select(l=>l.Code)).ToHashSet(StringComparer.Ordinal);
        working.PendingSegments.RemoveWhere(p=>affected.Contains(p.LineCode));working.DirtyLines.RemoveWhere(affected.Contains);
        working.StaleCrosses.RemoveWhere(k=>affected.Contains(k.RoadCode1)||affected.Contains(k.RoadCode2));
        foreach(var line in changed)for(var i=0;i<line.Nodes.Length-1;i++)CrossTopology.CalculateSegment(working,line.Code,i);
        if(LogicalNodeRules.ValidateMap(working) is {} ln)return Fail(ln.Code,ln.Message);
        if(CrossPassageValidator.FindThroughCalculatedCrosses(working) is {} after)return Fail("LinePassesCross",after.Message("拆分／合并计算后校验失败"));
        if(rebuildPositions)LogicalNodePositions.RebuildAffected(working,affected,new HashSet<string>(StringComparer.Ordinal));
        return null;
    }

    private static ServiceResult<LineTopologyResult>? SeedCrosses(LineStoreState original,LineStoreState working,Dictionary<string,string[]> mapping)
    {
        working.Crosses=[];
        foreach(var cross in original.Crosses.Values.OrderBy(c=>c.Key))
        {
            if(!mapping.ContainsKey(cross.Key.RoadCode1)&&!mapping.ContainsKey(cross.Key.RoadCode2)){working.Crosses.Add(cross.Key,cross);continue;}
            var codes1=mapping.GetValueOrDefault(cross.Key.RoadCode1)??[cross.Key.RoadCode1];var codes2=mapping.GetValueOrDefault(cross.Key.RoadCode2)??[cross.Key.RoadCode2];
            foreach(var a in codes1)foreach(var b in codes2)
            {
                if(a==b||!working.Lines.TryGetValue(a,out var first)||!working.Lines.TryGetValue(b,out var second)||
                    LineGeometry.FindPointOnLine(first.Nodes,cross.Key.Position) is null||LineGeometry.FindPointOnLine(second.Nodes,cross.Key.Position) is null)continue;
                var key=CrossKey.Create(a,b,cross.Key.Position);var sameSides=key.RoadCode1==a;
                var state=cross.State;
                if(!sameSides&&state is CrossState.Road1ToRoad2 or CrossState.Road2ToRoad1)
                    state=state==CrossState.Road1ToRoad2?CrossState.Road2ToRoad1:CrossState.Road1ToRoad2;
                var seed=new CrossSnapshot(key,sameSides?cross.RoadOrder1:cross.RoadOrder2,sameSides?cross.RoadOrder2:cross.RoadOrder1,
                    sameSides?cross.RoadVersion1:cross.RoadVersion2,sameSides?cross.RoadVersion2:cross.RoadVersion1,state,cross.Version+(key==cross.Key?0:1),
                    sameSides?cross.Percent1:cross.Percent2,sameSides?cross.Percent2:cross.Percent1);
                if(working.Crosses.TryGetValue(key,out var existing)&&existing.State!=seed.State)
                    return Fail("CrossStateConflict",$"Cross {key} 的原通行设置冲突，无法合并，请先统一设置。");
                working.Crosses[key]=seed;
            }
        }
        return null;
    }

    private static LineNode[] Renumber(string code,IEnumerable<LineNode> nodes)=>nodes.Select((p,i)=>p with{LineCode=code,Order=i}).ToArray();
    private static LineSnapshot CopySnapshot(LineSnapshot line)=>line with{Nodes=line.Nodes.ToArray()};
    internal static LineStoreState Clone(LineStoreState state)
    {
        var clone=new LineStoreState();Copy(state.Lines,clone.Lines);Copy(state.Crosses,clone.Crosses);Copy(state.Accounts,clone.Accounts);
        Copy(state.LogicalNodes,clone.LogicalNodes);Copy(state.LogicalNodePositions,clone.LogicalNodePositions);
        clone.PendingSegments.UnionWith(state.PendingSegments);clone.StaleCrosses.UnionWith(state.StaleCrosses);clone.DirtyLines.UnionWith(state.DirtyLines);
        clone.CrossRecalculationMarks.UnionWith(state.CrossRecalculationMarks);
        clone.LogicalNodePositionRecalculationMarks.UnionWith(state.LogicalNodePositionRecalculationMarks);return clone;
    }
    internal static void Publish(LineStoreState state,LineStoreState working)
    {
        Copy(working.Lines,state.Lines);Copy(working.Crosses,state.Crosses);Copy(working.LogicalNodes,state.LogicalNodes);Copy(working.LogicalNodePositions,state.LogicalNodePositions);
        state.PendingSegments.Clear();state.PendingSegments.UnionWith(working.PendingSegments);state.StaleCrosses.Clear();state.StaleCrosses.UnionWith(working.StaleCrosses);
        state.DirtyLines.Clear();state.DirtyLines.UnionWith(working.DirtyLines);
        state.CrossRecalculationMarks.Clear();state.CrossRecalculationMarks.UnionWith(working.CrossRecalculationMarks);
        state.LogicalNodePositionRecalculationMarks.Clear();state.LogicalNodePositionRecalculationMarks.UnionWith(working.LogicalNodePositionRecalculationMarks);
    }
    private static void Copy<TKey,TValue>(Dictionary<TKey,TValue> source,Dictionary<TKey,TValue> destination) where TKey:notnull
    {destination.Clear();foreach(var pair in source)destination.Add(pair.Key,pair.Value);}
    private static ServiceResult<LineTopologyResult> Fail(string code,string message)=>ServiceResult<LineTopologyResult>.Fail(code,message);
}
