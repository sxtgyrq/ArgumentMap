using ArgumentMap.Contracts;
using ArgumentMap.Storage;

namespace ArgumentMap.Business;

public sealed class LinePasteService(ILineRepository repository)
{
    public ServiceResult<LinePasteResult> Paste(PasteLineRequest request)=>repository.Transaction(state=>
    {
        ServiceResult<LinePasteResult> Fail(string code,string message)=>ServiceResult<LinePasteResult>.Fail(code,message);
        if(string.IsNullOrWhiteSpace(request.Owner))return Fail("InvalidOwner","缺少登录用户。");
        var token=request.Source;
        if(token is null||!LineCode.IsValid(token.Code)||token.Nodes is null||token.Properties is null)
            return Fail("InvalidLineCopy","线路副本格式无效，请重新复制。");
        if(!state.Lines.TryGetValue(token.Code,out var source)||source.Nodes.Length<2)
            return Fail("LineNotFound","原线路已不存在，请刷新后重新复制。");
        if(source.Version!=token.Version||source.IsLocked!=token.IsLocked||source.Properties!=token.Properties||!source.Nodes.SequenceEqual(token.Nodes))
            return Fail("VersionConflict","原线路已发生变化，请刷新后重新复制，避免粘贴过期的形状或属性。");
        long dx=(long)request.X-source.Nodes[0].X,dy=(long)request.Y-source.Nodes[0].Y;
        if(source.Nodes.Any(n=>n.X+dx is <0 or >MapLimits.CoordinateMaximum||n.Y+dy is <0 or >MapLimits.CoordinateMaximum))
            return Fail("OutOfBounds","粘贴后部分线路超出地图范围，请重新选择落点。");
        var working=LineTopologyService.Clone(state);var scope=new Scope(working);var roads=new LineSaveService(scope);
        var created=roads.Create(new(request.Owner));if(!created.Success)return Fail(created.Code,created.Message);
        var line=created.Data!;
        var nodes=source.Nodes.Select((n,i)=>new LineNode(line.Code,i,(int)(n.X+dx),(int)(n.Y+dy))).ToArray();
        var saved=roads.CheckShapeIsOKThenSave(new(line.Code,request.Owner,line.Version,nodes));
        if(!saved.Success)return Fail(saved.Code,saved.Message);
        var properties=roads.SaveProperties(new(line.Code,request.Owner,saved.Data!.Version,source.Properties));
        if(!properties.Success)return Fail(properties.Code,properties.Message);
        line=working.Lines[line.Code];
        for(var i=0;i<line.Nodes.Length-1;i++)CrossTopology.CalculateSegment(working,line.Code,i);
        working.PendingSegments.RemoveWhere(p=>p.LineCode==line.Code);working.DirtyLines.Remove(line.Code);
        if(LogicalNodeRules.ValidateMap(working) is {} invalid)return Fail(invalid.Code,invalid.Message);
        if(CrossPassageValidator.FindInCurrentGeometry(working) is {} cross)return Fail("LinePassesCross",cross.Message("粘贴校验失败"));
        if(CrossPassageValidator.FindThroughCalculatedCrosses(working) is {} after)return Fail("LinePassesCross",after.Message("粘贴重算后校验失败"));
        LogicalNodePositions.RebuildAffected(working,new HashSet<string>(StringComparer.Ordinal){line.Code},new HashSet<string>(StringComparer.Ordinal));
        // Lock state belongs to the copied road. Apply it only after the normal
        // creation/save/calculation pipeline has validated the complete geometry.
        line=working.Lines[line.Code] with{IsLocked=source.IsLocked};working.Lines[line.Code]=line;
        var map=new MapAreaSnapshot(working.Lines.Values.Where(l=>l.Nodes.Length>=2).OrderBy(l=>l.Code).Select(l=>l with{Nodes=l.Nodes.ToArray()}).ToArray(),
            new LogicalNodeService(scope).GetAll(request.Owner).Data!,CrossTopology.Visible(working));
        LineTopologyService.Publish(state,working);
        return ServiceResult<LinePasteResult>.Ok(new(line with{Nodes=line.Nodes.ToArray()},map),$"已粘贴线路 {line.Code}：形状、名称、说明、速度、方向和锁定状态已保留；Cross、LN 关联已重算。");
    });
    private sealed class Scope(LineStoreState state):ILineRepository{public T Transaction<T>(Func<LineStoreState,T> operation)=>operation(state);}
}
