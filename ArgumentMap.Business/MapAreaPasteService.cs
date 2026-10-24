using ArgumentMap.Contracts;
using ArgumentMap.Storage;

namespace ArgumentMap.Business;

public sealed class MapAreaPasteService(ILineRepository repository)
{
    public ServiceResult<MapAreaPasteResult> Paste(PasteMapAreaRequest request)=>repository.Transaction(state=>
    {
        static ServiceResult<MapAreaPasteResult> Fail(string code,string message)=>ServiceResult<MapAreaPasteResult>.Fail(code,message);
        if(request.Selection?.LogicalNodes?.Any(n=>n?.SideLength==18)==true)return Fail("StartLogicalNodeNotCopyable","选区包含 LN18 开始节点，不能复制；请重新框选，排除全图唯一的开始节点。");
        if(!MapAreaCopyRules.Valid(request.Selection))return Fail("InvalidAreaCopy","框选副本无效、含不支持的 LN 或超过单批上限（256 条线路、256 个 LN、4096 个线路节点／Cross）；请重新分批框选复制。");
        if(request.DeltaX is < -500000 or >500000||request.DeltaY is < -500000 or >500000)return Fail("OutOfBounds","粘贴偏移超出地图范围。");
        // Same canonical selection as F7 Move/Delete: K/J progress alone is not a
        // source edit. Clipboard membership stays frozen while properties, rights
        // and internal Cross state are rechecked against authoritative objects.
        var selected=MapAreaMoveService.ReadSelection(state,request.Owner,request.Selection,sourceObjectsOnly:true);
        if(!selected.Success)return Fail(selected.Code,selected.Message);
        var source=selected.Data!;var dx=request.DeltaX;var dy=request.DeltaY;
        if(source.Lines.Any(l=>l.Nodes.Any(n=>n.X+dx is <0 or >500000||n.Y+dy is <0 or >500000)))return Fail("OutOfBounds","粘贴后部分线路超出地图范围，请更换落点。");
        var working=LineTopologyService.Clone(state);var scope=new Scope(working);var roads=new LineSaveService(scope);
        var batch=LogicalNodeBatch.Create(working,request.Owner,source.LogicalNodes.Select(n=>new FlowLogicalNode
            {Id=n.Code,At=[n.X+dx,n.Y+dy],Size=n.SideLength,Value=n.Properties.Value,Logic=n.Properties.LogicLanguage,Info=n.Properties.Info,OperandKind=n.Properties.OperandKind}).ToArray());
        if(!batch.Success)return Fail(batch.Code,batch.Message);
        var nodeCodes=batch.Data!.Codes;var roadCodes=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(var original in source.Lines)
        {
            var created=roads.Create(new(request.Owner));if(!created.Success)return Fail(created.Code,created.Message);var road=created.Data!;
            var saved=roads.CheckShapeIsOKThenSave(new(road.Code,request.Owner,road.Version,original.Nodes.Select((n,i)=>new LineNode(road.Code,i,n.X+dx,n.Y+dy)).ToArray()));
            if(!saved.Success)return Fail(saved.Code,saved.Message);
            var properties=roads.SaveProperties(new(road.Code,request.Owner,saved.Data!.Version,original.Properties));if(!properties.Success)return Fail(properties.Code,properties.Message);
            roadCodes.Add(original.Code,road.Code);
        }
        var addedRoads=roadCodes.Values.ToHashSet(StringComparer.Ordinal);var addedNodes=nodeCodes.Values.ToHashSet(StringComparer.Ordinal);
        foreach(var code in addedRoads)
        {
            var road=working.Lines[code];for(var i=0;i<road.Nodes.Length-1;i++)CrossTopology.CalculateSegment(working,code,i);
            working.PendingSegments.RemoveWhere(p=>p.LineCode==code);working.DirtyLines.Remove(code);
        }
        var crossService=new CrossService(scope);
        foreach(var original in source.Crosses)
        {
            var first=roadCodes[original.Key.RoadCode1];var second=roadCodes[original.Key.RoadCode2];
            var key=CrossKey.Create(first,second,new(original.Key.Position.X2+2*dx,original.Key.Position.Y2+2*dy));
            if(!working.Crosses.TryGetValue(key,out var cross))return Fail("CrossNotFound","副本内部 Cross 未能重建，未保存任何对象。");
            var desired=original.State;
            if(key.RoadCode1!=first&&desired is CrossState.Road1ToRoad2 or CrossState.Road2ToRoad1)
                desired=desired==CrossState.Road1ToRoad2?CrossState.Road2ToRoad1:CrossState.Road1ToRoad2;
            var edited=crossService.Edit(new(key,request.Owner,cross.Version,desired));if(!edited.Success)return Fail(edited.Code,edited.Message);
        }
        if(LogicalNodeRules.ValidateMap(working) is {} invalid)return Fail(invalid.Code,invalid.Message);
        if(CrossPassageValidator.FindInCurrentGeometry(working) is {} before)return Fail("LinePassesCross",before.Message("整组粘贴校验失败"));
        if(CrossPassageValidator.FindThroughCalculatedCrosses(working) is {} after)return Fail("LinePassesCross",after.Message("整组粘贴重算后校验失败"));
        LogicalNodePositions.RebuildAffected(working,addedRoads,addedNodes);
        // Preserve all source road attributes. Apply locks after calculations/edits.
        foreach(var original in source.Lines)working.Lines[roadCodes[original.Code]]=working.Lines[roadCodes[original.Code]] with{IsLocked=original.IsLocked};
        var added=new MapAreaSnapshot(roadCodes.Values.Select(c=>working.Lines[c]).ToArray(),
            nodeCodes.Values.Select(c=>LogicalNodePositions.Snapshot(working,working.LogicalNodes[c]) with{CanEdit=true}).ToArray(),
            working.Crosses.Values.Where(c=>addedRoads.Contains(c.Key.RoadCode1)||addedRoads.Contains(c.Key.RoadCode2)).OrderBy(c=>c.Key).ToArray());
        var map=new MapAreaSnapshot(working.Lines.Values.Where(l=>l.Nodes.Length>=2).OrderBy(l=>l.Code).Select(l=>l with{Nodes=l.Nodes.ToArray()}).ToArray(),
            new LogicalNodeService(scope).GetAll(request.Owner).Data!,CrossTopology.Visible(working));
        LineTopologyService.Publish(state,working);
        // Keep the status bar usable for large groups; full allocation details stay
        // in Notes and the returned snapshots instead of expanding the map footer.
        return ServiceResult<MapAreaPasteResult>.Ok(new(added,map,nodeCodes,roadCodes,batch.Data.Notes.ToArray()),
            $"已按新增整组粘贴 {added.Lines.Length} 条线路、{added.LogicalNodes.Length} 个 LN；内部引用与 Cross 设置已同步，位置和关联已重算。"+
            (batch.Data.Notes.Count>0?" 重复项已自动编号，同组 LN4／LN10 已同步；请在新节点属性中核对实际值与自由公式。":""));
    });
    private sealed class Scope(LineStoreState state):ILineRepository{public T Transaction<T>(Func<LineStoreState,T> operation)=>operation(state);}
}
