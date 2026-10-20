using ArgumentMap.Rules;
using ArgumentMap.Contracts;
using ArgumentMap.Storage;

namespace ArgumentMap.Business;

public sealed class MapAreaMoveService(ILineRepository repository)
{
    public ServiceResult<MapAreaSelection> Select(SelectMapAreaRequest request)=>repository.Transaction(state=>Select(state,request.Owner,request.Bounds));
    public ServiceResult<MapAreaSnapshot> Move(MoveMapAreaRequest request)=>repository.Transaction(state=>
    {
        var selection=ReadSelection(state,request.Owner,request.Selection);
        if(!selection.Success)return Fail(selection.Code,selection.Message);
        if(request.DeltaX is < -500000 or > 500000 || request.DeltaY is < -500000 or > 500000)
            return Fail("OutOfBounds","移动距离超出地图范围。");
        // Reload every object inside the original bounds while holding the map transaction
        // lock. Detect newly inserted objects, deletions, rights/locks and Cross state edits.
        var current=selection.Data!;
        if(request.DeltaX==0 && request.DeltaY==0)return ServiceResult<MapAreaSnapshot>.Ok(Snapshot(state,request.Owner),"位置未改变。");
        var movedRoads=current.Lines.Select(l=>l.Code).ToHashSet(StringComparer.Ordinal);
        var movedNodes=current.LogicalNodes.Select(n=>n.Code).ToHashSet(StringComparer.Ordinal);
        var working=LineTopologyService.Clone(state);
        foreach(var line in current.Lines)
        {
            var points=line.Nodes.Select(n=>n with{X=n.X+request.DeltaX,Y=n.Y+request.DeltaY}).ToArray();
            if(points.Any(n=>n.X<0||n.X>500000||n.Y<0||n.Y>500000))return Fail("OutOfBounds",$"线路 {line.Code} 移动后超出地图边界。");
            working.Lines[line.Code]=line with{Nodes=points,Version=line.Version+1};
        }
        foreach(var node in current.LogicalNodes)
        {
            var changed=state.LogicalNodes[node.Code] with{X=node.X+request.DeltaX,Y=node.Y+request.DeltaY,Version=node.Version+1};
            if(LogicalNodeRules.ValidateShape(changed.X,changed.Y,changed.SideLength) is {} shape)return Fail(shape.Code,shape.Message);
            working.LogicalNodes[node.Code]=changed;
        }
        // Reuse F6/K's mark -> recalculate/restore -> cleanup lifecycle. Keep obsolete
        // coordinates until cleanup, and seed translated identities as stale too. A seed
        // is kept only if CalculateSegment actually finds it in the resulting geometry.
        CrossTopology.MarkChanged(working,movedRoads,recordSqlMarks:true);
        foreach(var cross in state.Crosses.Values.Where(c=>movedRoads.Contains(c.Key.RoadCode1)||movedRoads.Contains(c.Key.RoadCode2)))
        {
            var both=movedRoads.Contains(cross.Key.RoadCode1)&&movedRoads.Contains(cross.Key.RoadCode2);
            var position=both ? new CrossPosition(cross.Key.Position.X2+2*request.DeltaX,cross.Key.Position.Y2+2*request.DeltaY):cross.Key.Position;
            if(LineGeometry.FindPointOnLine(working.Lines[cross.Key.RoadCode1].Nodes,position) is null ||
                LineGeometry.FindPointOnLine(working.Lines[cross.Key.RoadCode2].Nodes,position) is null)continue;
            var key=CrossKey.Create(cross.Key.RoadCode1,cross.Key.RoadCode2,position);
            working.Crosses[key]=cross with{Key=key};
            working.StaleCrosses.Add(key);
        }
        // All geometry has already moved in the working snapshot: no validation ever
        // observes an intermediate state with only one road/LN translated.
        foreach(var code in movedRoads)
        {
            var line=working.Lines[code];
            if(LineSaveService.ValidateShape(working,new(code,request.Owner,line.Version,line.Nodes),line.Nodes) is {} invalid)return Fail(invalid.Code,invalid.Message);
        }
        if(CrossPassageValidator.FindInCurrentGeometry(working) is {} passage)return Fail("LinePassesCross",passage.Message("框选移动校验失败"));
        foreach(var code in movedRoads)
        {
            var line=working.Lines[code];
            for(var i=0;i<line.Nodes.Length-1;i++)CrossTopology.CalculateSegment(working,code,i);
            working.PendingSegments.RemoveWhere(p=>p.LineCode==code);working.DirtyLines.Remove(code);
        }
        // Only clean this group's records. Another user's pending K work and markers
        // are independent, and must survive this atomic move unchanged.
        CrossTopology.Cleanup(working,movedRoads);
        // A moved LN cannot silently cover an existing unset/connected or uncomputed
        // crossing. Newly created road crossings inside LNs are calculated as disconnected.
        foreach(var code in movedNodes)
            if(LogicalNodeRules.ValidateNew(working.LogicalNodes[code],working) is {} invalid)return Fail(invalid.Code,invalid.Message);
        if(LogicalNodeRules.ValidateMap(working) is {} ln)return Fail(ln.Code,ln.Message);
        if(CrossPassageValidator.FindThroughCalculatedCrosses(working) is {} after)return Fail("LinePassesCross",after.Message("框选移动重算后校验失败"));
        LogicalNodePositions.RebuildAffected(working,movedRoads,movedNodes);
        LineTopologyService.Publish(state,working);
        return ServiceResult<MapAreaSnapshot>.Ok(Snapshot(state,request.Owner),$"已整体移动 {current.Lines.Length} 条线路、{current.LogicalNodes.Length} 个 LN；Cross 设置和全部线路位置已在同一事务更新。");
    });

    public ServiceResult<MapAreaSnapshot> Delete(DeleteMapAreaRequest request)=>repository.Transaction(state=>
    {
        var selection=ReadSelection(state,request.Owner,request.Selection);
        if(!selection.Success)return Fail(selection.Code,selection.Message);
        var current=selection.Data!;
        var roads=current.Lines.Select(l=>l.Code).ToHashSet(StringComparer.Ordinal);
        var nodes=current.LogicalNodes.Select(n=>n.Code).ToHashSet(StringComparer.Ordinal);
        if(LogicalNodeInstanceRules.ValidateDeletion(state.LogicalNodes.Values,nodes) is {} instance)return Fail(instance.Code,instance.Message);
        var working=LineTopologyService.Clone(state);
        LogicalNodeService.ClearBranchReferences(working,nodes);
        foreach(var code in nodes)working.LogicalNodes.Remove(code);
        foreach(var key in working.LogicalNodePositions.Keys.Where(k=>nodes.Contains(k.LogicalNodeCode)).ToArray())working.LogicalNodePositions.Remove(key);
        working.LogicalNodePositionRecalculationMarks.RemoveWhere(k=>nodes.Contains(k.LogicalNodeCode));
        foreach(var code in roads)working.Lines.Remove(code);
        foreach(var key in working.Crosses.Keys.Where(k=>roads.Contains(k.RoadCode1)||roads.Contains(k.RoadCode2)).ToArray())working.Crosses.Remove(key);
        working.StaleCrosses.RemoveWhere(k=>roads.Contains(k.RoadCode1)||roads.Contains(k.RoadCode2));
        working.CrossRecalculationMarks.RemoveWhere(k=>roads.Contains(k.RoadCode1)||roads.Contains(k.RoadCode2));
        working.DirtyLines.ExceptWith(roads);working.PendingSegments.RemoveWhere(p=>roads.Contains(p.LineCode));
        // Retained LNs can also reference deleted roads. Reuse J's scoped cleanup and
        // increment their versions without touching unrelated pending positions.
        LogicalNodePositions.RebuildAffected(working,roads,new HashSet<string>(StringComparer.Ordinal));
        LineTopologyService.Publish(state,working);
        return ServiceResult<MapAreaSnapshot>.Ok(Snapshot(state,request.Owner),$"已删除框选的 {roads.Count} 条完整线路、{nodes.Count} 个 LN，关联 Cross 和 LN 位置已在同一事务清理。");
    });

    internal static ServiceResult<MapAreaSelection> ReadSelection(LineStoreState state,string owner,MapAreaSelection? expected,bool sourceObjectsOnly=false)
    {
        if(expected is null || expected.Lines is null || expected.LogicalNodes is null || expected.Crosses is null)
            return ServiceResult<MapAreaSelection>.Fail("InvalidSelection","框选数据不完整，请重新框选。");
        // Clipboard membership freezes at Copy. Newly inserted objects in the old
        // rectangle must neither join the copy nor invalidate an otherwise current
        // source. Move/Delete keep their stricter whole-rectangle concurrency check.
        var selection=Select(state,owner,expected.Bounds,
            sourceObjectsOnly?expected.Lines.Select(l=>l.Code).ToHashSet(StringComparer.Ordinal):null,
            sourceObjectsOnly?expected.LogicalNodes.Select(n=>n.Code).ToHashSet(StringComparer.Ordinal):null);if(!selection.Success)return selection;
        var current=selection.Data!;
        static bool Same<T,TKey>(T[] old,T[] now,Func<T,TKey> key,Func<T,long> version) where T:class where TKey:notnull
        {
            if(old.Length!=now.Length || old.Any(x=>x is null))return false;
            var map=new Dictionary<TKey,long>();foreach(var x in old)if(key(x) is not {} k || !map.TryAdd(k,version(x)))return false;
            return now.All(x=>map.TryGetValue(key(x),out var v)&&v==version(x));
        }
        return Same(expected.Lines,current.Lines,l=>l.Code,l=>l.Version)&&Same(expected.LogicalNodes,current.LogicalNodes,n=>n.Code,n=>n.Version)&&
            Same(expected.Crosses,current.Crosses,c=>c.Key,c=>c.Version) ? selection :
            ServiceResult<MapAreaSelection>.Fail("SelectionChanged","框选对象、线路锁定或 Cross 设置已被其他用户修改，请重新框选；未提交操作。");
    }

    private static ServiceResult<MapAreaSelection> Select(LineStoreState state,string owner,MapAreaBounds bounds,HashSet<string>? roadFilter=null,HashSet<string>? nodeFilter=null)
    {
        ServiceResult<MapAreaSelection> Error(string code,string message)=>ServiceResult<MapAreaSelection>.Fail(code,message);
        if(bounds is null || bounds.Left<0||bounds.Bottom<0||bounds.Right>500000||bounds.Top>500000)
            return Error("InvalidBounds","请在地图范围内框选有效矩形。");
        if(bounds.Left>=bounds.Right||bounds.Bottom>=bounds.Top)
            return Error("InvalidBounds","选框没有宽度或高度。请按住鼠标左键拖出矩形，再松开；单击不会完成框选。");
        if(string.IsNullOrWhiteSpace(owner))return Error("InvalidOwner","缺少登录用户。");
        var lines=state.Lines.Values.Where(l=>(roadFilter is null||roadFilter.Contains(l.Code))&&bounds.ContainsEntireLine(l)).OrderBy(l=>l.Code).ToArray();
        var nodes=state.LogicalNodes.Values.Where(n=>(nodeFilter is null||nodeFilter.Contains(n.Code))&&bounds.ContainsEntireSquare(n)).OrderBy(n=>n.Code).ToArray();
        // K may be between any two segment transactions. Build the same canonical
        // Cross metadata on a private snapshot, without consuming pending work or
        // writing to the database. The selection then remains comparable before
        // and after K restores a stale Cross or creates a previously missing one.
        var projected=LineTopologyService.Clone(state);
        foreach(var line in lines)
            for(var i=0;i<line.Nodes.Length-1;i++)CrossTopology.CalculateSegment(projected,line.Code,i);
        // J can advance a node's derived version between preview and drop. Compare
        // canonical positions/versions without writing the preview to the database.
        foreach(var node in nodes)LogicalNodePositions.RebuildNode(projected,node.Code);
        var insideCrosses=projected.Crosses.Values.Where(c=>bounds.Contains(c.Key.Position.X2/2m,c.Key.Position.Y2/2m)&&
            state.Lines.TryGetValue(c.Key.RoadCode1,out var a)&&state.Lines.TryGetValue(c.Key.RoadCode2,out var b)&&
            LineGeometry.FindPointOnLine(a.Nodes,c.Key.Position) is not null&&LineGeometry.FindPointOnLine(b.Nodes,c.Key.Position) is not null).OrderBy(c=>c.Key).ToArray();
        var codes=lines.Select(l=>l.Code).ToHashSet(StringComparer.Ordinal);
        // A Cross is derived from its two roads, so it is selected only with both
        // complete roads. Other visible crossings must not block independent LNs
        // or complete roads; Move/Delete still recalculate all affected relations.
        var crosses=insideCrosses.Where(c=>codes.Contains(c.Key.RoadCode1)&&codes.Contains(c.Key.RoadCode2)).ToArray();
        var skippedCrosses=insideCrosses.Length-crosses.Length;
        if(lines.Length==0&&nodes.Length==0)return Error("EmptySelection","框内没有完整线路或完整 LN；部分框入的对象及缺少完整所属线路的 Cross 不会选中。");
        foreach(var line in lines)
        {
            if(LineSaveService.CheckAccess(state,line.Code,owner,out _) is {} access)return Error(access.Code,access.Message);
            if(line.Nodes.Zip(line.Nodes.Skip(1)).Any(p=>Math.Pow(p.Second.X-p.First.X,2)+Math.Pow(p.Second.Y-p.First.Y,2)>250000))
                return Error("SegmentTooLong",$"线路 {line.Code} 有长度超过 500 的段，请先按 F6 校验保存。");
        }
        foreach(var node in nodes)
            if(!LogicalNodeService.CanEdit(state,node,owner))return Error("NotOwner",$"没有操作 LN {node.Code} 的权限。");
        return ServiceResult<MapAreaSelection>.Ok(new(bounds,lines.Select(l=>l with{Nodes=l.Nodes.ToArray()}).ToArray(),
            nodes.Select(n=>LogicalNodePositions.Snapshot(projected,projected.LogicalNodes[n.Code]) with{CanEdit=true}).ToArray(),crosses),
            $"框选 {lines.Length} 条完整线路、{nodes.Length} 个完整 LN、{crosses.Length} 个 Cross；部分框入的线路和 LN 不选。"+
            (skippedCrosses>0?$"已跳过 {skippedCrosses} 个缺少完整所属线路的 Cross，仍可移动或删除选中对象。":"")+
            "左键 / Enter 移动落点，R 删除原选中对象，Ctrl+C 复制整组 → Ctrl+V 鼠标落点新增，Esc 取消。");
    }
    private static MapAreaSnapshot Snapshot(LineStoreState state,string owner)=>new(
        state.Lines.Values.Where(l=>l.Nodes.Length>=2).OrderBy(l=>l.Code).Select(l=>l with{Nodes=l.Nodes.ToArray()}).ToArray(),
        state.LogicalNodes.Values.OrderBy(n=>n.Code).Select(n=>LogicalNodePositions.Snapshot(state,n) with{CanEdit=LogicalNodeService.CanEdit(state,n,owner)}).ToArray(),
        state.Crosses.Values.OrderBy(c=>c.Key).ToArray());
    private static ServiceResult<MapAreaSnapshot> Fail(string code,string message)=>ServiceResult<MapAreaSnapshot>.Fail(code,message);
}
