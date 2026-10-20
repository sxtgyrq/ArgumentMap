using ArgumentMap.Contracts;
using ArgumentMap.Storage;

using ArgumentMap.Business;

namespace ArgumentMap.Tools;

/// <summary>The example is ordinary, editable map data. All creation shares one transaction.</summary>
internal sealed class EquipmentDiagramData(ILineRepository repository)
{
    public const string Marker = "设备停机排查图/v1/";
    public ServiceResult<LogicalNodeSnapshot[]> Import(string owner, int x, int topY) => repository.Transaction(state =>
    {
        var existing=state.LogicalNodes.Values.Where(n=>n.Properties.Info.StartsWith(Marker,StringComparison.Ordinal)).ToArray();
        if(existing.Length>0)return Fail("DiagramAlreadyExists","设备停机排查图已存在，不会重复新增。");
        var scope=new Scope(state);var ln=new LogicalNodeService(scope);var roads=new LineSaveService(scope);var codes=new List<string>();
        var definitions=new (string Key,int X,int Y,int Size,string Value,string Logic)[]
        {
            ("S",x,topY,12,"设备停止运行","S"),
            ("P",x,topY-40,6,"供电是否正常？","P"),
            ("A",x-90,topY-80,12,"检查电源与线路","(S ∧ ¬P) → A"),
            ("Q",x,topY-80,6,"是否收到停机指令？","Q"),
            ("B",x-60,topY-120,12,"检查指令来源","(S ∧ P ∧ Q) → B"),
            ("C",x,topY-120,12,"检查保护装置与机械故障","(S ∧ P ∧ ¬Q) → C"),
            ("PNo",x-45,topY-40,4,"False","¬P"),
            ("PYes",x,topY-60,4,"True","P"),
            ("QYes",x-30,topY-80,4,"True","Q"),
            ("QNo",x,topY-100,4,"False","¬Q")
        };
        foreach(var d in definitions)
        {
            var result=ln.Create(new(owner,d.X+0.5m,d.Y+0.5m,d.Size,new(){Value=d.Value,LogicLanguage=d.Logic,Info=Marker+d.Key}));
            if(!result.Success)return Fail(result.Code,result.Message);
        }
        // A road must pass through a square; endpoints and corners stay outside it.
        // The vertical road serves S → P → Q → C, with orthogonal branches at P/Q.
        foreach(var path in new (string Name,int[] XY)[]
        {
            ("是 P；否 ¬Q",[x,topY+12,x,topY-132]),
            ("否 ¬P",[x+12,topY-40,x-90,topY-40,x-90,topY-92]),
            ("是 Q",[x+12,topY-80,x-60,topY-80,x-60,topY-132])
        })
        {
            var created=roads.Create(new(owner));if(!created.Success)return Fail(created.Code,created.Message);
            var road=created.Data!;
            var saved=roads.CheckShapeIsOKThenSave(new(road.Code,owner,road.Version,
                Enumerable.Range(0,path.XY.Length/2).Select(i=>new LineNode(road.Code,i,path.XY[i*2],path.XY[i*2+1])).ToArray()));
            if(!saved.Success)return Fail(saved.Code,saved.Message);
            var properties=roads.SaveProperties(new(road.Code,owner,saved.Data!.Version,new(){RoadName=path.Name,RoadDetail=Marker+path.Name,PassInOpposeDirection=0,Speed=5}));
            if(!properties.Success)return Fail(properties.Code,properties.Message);
            codes.Add(road.Code);
        }
        // Use the existing Cross service on a calculation snapshot, so old pending work
        // is not cleared when this fixture's three roads finish calculation.
        var calculated=new LineStoreState();
        foreach(var (code,road) in state.Lines)calculated.Lines.Add(code,road);
        foreach(var (code,node) in state.LogicalNodes)calculated.LogicalNodes.Add(code,node);
        foreach(var (key,cross) in state.Crosses)calculated.Crosses.Add(key,cross);
        foreach(var code in codes)
        {
            var road=state.Lines[code];
            for(var i=0;i<road.Nodes.Length-1;i++)calculated.PendingSegments.Add((code,i));
        }
        var crossService=new CrossService(new Scope(calculated));
        while(true)
        {
            var step=crossService.Calculate();if(!step.Success)return Fail(step.Code,step.Message);
            if(!step.Data!.CanContinue)break;
        }
        foreach(var cross in calculated.Crosses.Values.Where(c=>codes.Contains(c.Key.RoadCode1)||codes.Contains(c.Key.RoadCode2)))state.Crosses[cross.Key]=cross;
        foreach(var code in codes)
        {
            state.PendingSegments.RemoveWhere(p=>p.LineCode==code);
            state.DirtyLines.Remove(code);
        }
        if(LogicalNodeRules.ValidateMap(state) is {} violation)return Fail(violation.Code,violation.Message);
        if(CrossPassageValidator.FindInCurrentGeometry(state) is {} passage)return Fail("LinePassesCross",passage.Message("示例图校验失败"));
        var nodes=ln.GetAll(owner).Data!.Where(n=>n.Properties.Info.StartsWith(Marker,StringComparison.Ordinal)).ToArray();
        return ServiceResult<LogicalNodeSnapshot[]>.Ok(nodes,"已保存 10 个普通 LN（含四个是／否分支）、3 条线路及关联位置。");
    });
    public ServiceResult<LogicalNodeSnapshot[]> AddBranches(string owner) => repository.Transaction(state=>
    {
        var ln=new LogicalNodeService(new Scope(state));
        LogicalNodeSnapshot? Find(string key)=>state.LogicalNodes.Values.SingleOrDefault(n=>n.Properties.Info==Marker+key);
        if(Find("P") is not {} p || Find("Q") is not {} q)return Fail("MissingDecisionNode","找不到现有 P、Q 节点，未修改数据。");
        foreach(var (key,x,y,value,logic) in new[]{("PNo",p.X-45,p.Y,"False","¬P"),("PYes",p.X,p.Y-20,"True","P"),
            ("QYes",q.X-30,q.Y,"True","Q"),("QNo",q.X,q.Y-20,"False","¬Q")})
        {
            if(Find(key) is {} existing)
            {
                if(existing.SideLength!=4 || state.LogicalNodePositions.Values.Count(pos=>pos.LogicalNodeCode==existing.Code)!=1)return Fail("BranchChanged","分支已被修改，未覆盖。");
                continue;
            }
            var created=ln.Create(new(owner,x,y,4,new(){Value=value,LogicLanguage=logic,Info=Marker+key}));
            if(!created.Success)return Fail(created.Code,created.Message);
            if(created.Data!.Positions.Length!=1)return Fail("BranchNotOnRoad","分支节点必须落在现有的一条线路上，整次回滚。");
        }
        return ServiceResult<LogicalNodeSnapshot[]>.Ok(ln.GetAll(owner).Data!.Where(n=>n.Properties.Info.StartsWith(Marker,StringComparison.Ordinal)).ToArray(),"四个是／否分支已保存为普通 LN。");
    });
    public ServiceResult<LogicalNodeSnapshot[]> ResizeBranchesToFour(string owner)=>repository.Transaction(state=>
    {
        var keys=new[]{"PNo","PYes","QYes","QNo"};
        foreach(var key in keys)
        {
            var matches=state.LogicalNodes.Values.Where(n=>n.Properties.Info==Marker+key).ToArray();
            if(matches.Length!=1)return Fail("BranchNotFound","必须存在四个已保存的分支 LN，未修改数据。");
            var node=matches[0];
            if(node.Owner!=owner && (!state.Accounts.TryGetValue(owner,out var user)||!user.EditAllLogicalNodes))return Fail("NotOwner","没有修改分支 LN 的权限。");
            if(node.SideLength is not (4 or 6))return Fail("BranchChanged","分支尺寸已被修改，未覆盖。");
            var resized=node with{SideLength=4,Version=node.Version+(node.SideLength==4?0:1)};
            if(node.SideLength!=4)
            {
                var decision=state.LogicalNodes.Values.Single(n=>n.Properties.Info==Marker+(key.StartsWith("P",StringComparison.Ordinal)?"P":"Q"));
                var value=key.EndsWith("No",StringComparison.Ordinal)?"False":"True";
                resized=resized with{Properties=resized.Properties with{Value=value,LogicLanguage=LogicalNodeBranchRules.Expression(value,decision.Properties.LogicLanguage)}};
            }
            if(LogicalNodeRules.ValidateShape(resized.X,resized.Y,4) is {} shape)return Fail(shape.Code,shape.Message);
            if(LogicalNodeRules.ValidateNew(resized,state) is {} violation)return Fail(violation.Code,violation.Message);
            var positions=LogicalNodePositionCalculator.Calculate(resized,state.Lines.Values);
            if(positions.Length!=1)return Fail("BranchNotOnRoad","路径必须穿过每个是／否方块，整次回滚。");
            var position=positions[0];var road=state.Lines[position.RoadCode];var a=road.Nodes[position.RoadOrder];var b=road.Nodes[position.RoadOrder+1];
            var passes=a.Y==b.Y ? Math.Min(a.X,b.X)<resized.Left && Math.Max(a.X,b.X)>resized.Right && a.Y>resized.Bottom && a.Y<resized.Top :
                a.X==b.X && Math.Min(a.Y,b.Y)<resized.Bottom && Math.Max(a.Y,b.Y)>resized.Top && a.X>resized.Left && a.X<resized.Right;
            if(!passes)return Fail("BranchNotOnRoad","路径必须从方块一侧进入、另一侧穿出，整次回滚。");
            state.LogicalNodes[node.Code]=resized;
            foreach(var oldKey in state.LogicalNodePositions.Keys.Where(k=>k.LogicalNodeCode==node.Code).ToArray())state.LogicalNodePositions.Remove(oldKey);
            foreach(var p in positions)state.LogicalNodePositions.Add(p.Key,p);
        }
        return ServiceResult<LogicalNodeSnapshot[]>.Ok(new LogicalNodeService(new Scope(state)).GetAll(owner).Data!.Where(n=>n.Properties.Info.StartsWith(Marker,StringComparison.Ordinal)).ToArray(),"四个是／否 LN 已改为边长 4，路径均穿过方块内部。");
    });
    private static ServiceResult<LogicalNodeSnapshot[]> Fail(string code,string message)=>ServiceResult<LogicalNodeSnapshot[]>.Fail(code,message);
    private sealed class Scope(LineStoreState state):ILineRepository
    {public T Transaction<T>(Func<LineStoreState,T> operation)=>operation(state);}
}
