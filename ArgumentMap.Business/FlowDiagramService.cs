using ArgumentMap.Rules;
using ArgumentMap.Contracts;
using ArgumentMap.Storage;

namespace ArgumentMap.Business;

public sealed class FlowDiagramService(ILineRepository repository)
{
    public ServiceResult<FlowDiagramResult> Preview(FlowDiagramRequest request)=>Execute(request,commit:false);
    public ServiceResult<FlowDiagramResult> Import(FlowDiagramRequest request)=>Execute(request,commit:true);
    private ServiceResult<FlowDiagramResult> Execute(FlowDiagramRequest request,bool commit)=>repository.Transaction(state=>
    {
        ServiceResult<FlowDiagramResult> Fail(string code,string message)=>ServiceResult<FlowDiagramResult>.Fail(code,message);
        if(string.IsNullOrWhiteSpace(request.Owner))return Fail("InvalidOwner","缺少登录用户。");
        var parsed=FlowDiagramLanguage.Parse(request.Text);if(!parsed.Success)return Fail(parsed.Code,parsed.Message);
        if(!LogicalNodeRules.IsHalfInteger(request.X)||!LogicalNodeRules.IsHalfInteger(request.Y)||request.X>500000||request.Y>500000)
            return Fail("InvalidFlowOrigin","插入点必须在地图范围内，X、Y 均为整数＋0.5。");
        var d=parsed.Data!;var anchor=d.Ln.Single(n=>n.Id==d.Origin);var dx=request.X-anchor.At[0];var dy=request.Y-anchor.At[1];
        // Report coordinate failures in document order before creating forward-referenced LN6s.
        foreach(var node in d.Ln)
            if(LogicalNodeRules.ValidateShape(node.At[0]+dx,node.At[1]+dy,node.Size) is {} shapeError)return Fail(shapeError.Code,$"LN {node.Id}：{shapeError.Message}");
        var working=LineTopologyService.Clone(state);var scope=new Scope(working);var nodes=new LogicalNodeService(scope);var roads=new LineSaveService(scope);
        var batch=LogicalNodeBatch.Create(working,request.Owner,d.Ln.Select(n=>n with{At=[n.At[0]+dx,n.At[1]+dy]}).ToArray());
        if(!batch.Success)return Fail(batch.Code,batch.Message);
        var lnCodes=batch.Data!.Codes;var renames=batch.Data.Notes;var roadCodes=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(var r in d.Roads)
        {
            var created=roads.Create(new(request.Owner));if(!created.Success)return Fail(created.Code,$"Road {r.Id}：{created.Message}");var line=created.Data!;
            var started=roads.StartDrawing(new(line.Code,request.Owner));if(!started.Success)return Fail(started.Code,$"Road {r.Id}：{started.Message}");
            var shape=r.Points.Select((p,i)=>new LineNode(line.Code,i,(int)(p[0]+dx),(int)(p[1]+dy))).ToArray();
            var saved=roads.CheckShapeIsOKThenSave(new(line.Code,request.Owner,line.Version,shape));if(!saved.Success)return Fail(saved.Code,$"Road {r.Id}：{saved.Message}");
            var properties=roads.SaveProperties(new(line.Code,request.Owner,saved.Data!.Version,new(){RoadName=r.Name,RoadDetail=r.Info,Speed=r.Speed,PassInOpposeDirection=(short)(r.Direction=="both"?1:0)}));
            if(!properties.Success)return Fail(properties.Code,$"Road {r.Id}：{properties.Message}");roadCodes.Add(r.Id,line.Code);
        }
        var addedRoads=roadCodes.Values.ToHashSet(StringComparer.Ordinal);
        foreach(var code in addedRoads)
        {
            var line=working.Lines[code];for(var i=0;i<line.Nodes.Length-1;i++)CrossTopology.CalculateSegment(working,code,i);
            working.PendingSegments.RemoveWhere(p=>p.LineCode==code);working.DirtyLines.Remove(code);
        }
        var crossService=new CrossService(scope);var specified=new HashSet<CrossKey>();
        foreach(var c in d.Crosses)
        {
            var x=c.At[0]+dx;var y=c.At[1]+dy;
            if(x<0||y<0||x>500000||y>500000)return Fail("OutOfBounds",$"Cross {c.Road1}/{c.Road2}：交点坐标超出地图范围。");
            var key=CrossKey.Create(roadCodes[c.Road1],roadCodes[c.Road2],new((int)(x*2),(int)(y*2)));
            if(!specified.Add(key))return Fail("DuplicateFlowCross",$"Cross {c.Road1}/{c.Road2} ({x}, {y}) 重复设置。");
            if(!working.Crosses.TryGetValue(key,out var old))return Fail("CrossNotFound",$"Cross {c.Road1}/{c.Road2}：两条线路没有在 ({x}, {y}) 相交，请检查 points 和 at。");
            var same=key.RoadCode1==roadCodes[c.Road1];
            var desired=c.State switch{"connected"=>CrossState.Connected,"disconnected"=>CrossState.Disconnected,
                "road1ToRoad2"=>same?CrossState.Road1ToRoad2:CrossState.Road2ToRoad1,"road2ToRoad1"=>same?CrossState.Road2ToRoad1:CrossState.Road1ToRoad2,_=>CrossState.Unset};
            var edited=crossService.Edit(new(key,request.Owner,old.Version,desired));if(!edited.Success)return Fail(edited.Code,$"Cross {c.Road1}/{c.Road2}：{edited.Message}");
        }
        if(LogicalNodeRules.ValidateMap(working) is {} invalid)return Fail(invalid.Code,invalid.Message);
        if(CrossPassageValidator.FindInCurrentGeometry(working) is {} cross)return Fail("LinePassesCross",cross.Message("导入校验失败"));
        if(CrossPassageValidator.FindThroughCalculatedCrosses(working) is {} after)return Fail("LinePassesCross",after.Message("导入重算后校验失败"));
        // Use J's projection pipeline, limited to imported geometry so unrelated pending
        // work is left untouched. Every created object is still inside this one transaction.
        LogicalNodePositions.RebuildAffected(working,addedRoads,lnCodes.Values.ToHashSet(StringComparer.Ordinal));
        var addedCrosses=working.Crosses.Values.Where(c=>addedRoads.Contains(c.Key.RoadCode1)||addedRoads.Contains(c.Key.RoadCode2)).OrderBy(c=>c.Key).ToArray();
        var notes=new List<string>(renames);var unset=addedCrosses.Count(c=>c.State==CrossState.Unset);
        if(unset>0)notes.Add($"有 {unset} 个未指定通行设置的 Cross，已按默认规则保留为未设置，可用 U 选择后设置。");
        var added=new MapAreaSnapshot(roadCodes.Values.Select(code=>working.Lines[code] with{Nodes=working.Lines[code].Nodes.ToArray()}).ToArray(),lnCodes.Values.Select(code=>LogicalNodePositions.Snapshot(working,working.LogicalNodes[code]) with{CanEdit=true}).ToArray(),addedCrosses);
        MapAreaSnapshot? map=null;
        if(commit)
        {
            LineTopologyService.Publish(state,working);
            map=new(state.Lines.Values.Where(l=>l.Nodes.Length>=2).OrderBy(l=>l.Code).Select(l=>l with{Nodes=l.Nodes.ToArray()}).ToArray(),nodes.GetAll(request.Owner).Data!,CrossTopology.Visible(state));
        }
        return ServiceResult<FlowDiagramResult>.Ok(new(added,map,lnCodes,roadCodes,notes.ToArray()),$"{(commit?"已导入":"校验通过，尚未保存")}：{added.Lines.Length} 条线路、{added.LogicalNodes.Length} 个 LN、{added.Crosses.Length} 个 Cross。"+(notes.Count>0?" "+string.Join(" ",notes):""));
    });
    private sealed class Scope(LineStoreState state):ILineRepository{public T Transaction<T>(Func<LineStoreState,T> operation)=>operation(state);}
}
