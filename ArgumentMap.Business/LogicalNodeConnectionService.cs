using ArgumentMap.Contracts;
using ArgumentMap.Storage;

namespace ArgumentMap.Business;

public sealed record LogicalNodeConnectionPlanningInput(LineStoreState Map, LogicalNodeSnapshot Source, LogicalNodeSnapshot Target, string Code,LogicalNodeConnectionKind Kind=LogicalNodeConnectionKind.Direct);

public sealed class LogicalNodeConnectionService(ILineRepository repository)
{
    public ServiceResult<LogicalNodeConnectionPlanningInput> Prepare(PreviewLogicalNodeConnectionRequest request)=>repository.Transaction(state=>
    {
        ServiceResult<LogicalNodeConnectionPlanningInput> Fail(string code,string message)=>ServiceResult<LogicalNodeConnectionPlanningInput>.Fail(code,message);
        if(string.IsNullOrWhiteSpace(request.Owner))return Fail("InvalidOwner","缺少登录用户。");
        if(!Enum.IsDefined(request.Kind))return Fail("InvalidConnectionKind","连接方式只能是直连、前往或寻找。");
        if(request.Source is null||request.Target is null||!LineCode.IsValid(request.Source.Code)||!LineCode.IsValid(request.Target.Code))return Fail("InvalidConnectionNodes","请选择两个有效 LN。");
        if(request.Source.Code==request.Target.Code)return Fail("SameConnectionNode","请选择两个不同的 LN。");
        if(!state.LogicalNodes.TryGetValue(request.Source.Code,out var source)||!state.LogicalNodes.TryGetValue(request.Target.Code,out var target))return Fail("ConnectionNodeMissing","目标 LN 已删除，请重新选择。");
        if(!request.Source.Matches(source)||!request.Target.Matches(target))return Fail("ConnectionNodeChanged","目标 LN 的位置或边长已变化，请刷新后重新选择。");
        // Copy only geometry. No credentials, history loader or live transaction escapes this call.
        var snapshot=new LineStoreState();
        foreach(var l in state.Lines.Values)snapshot.Lines.Add(l.Code,l with{Nodes=l.Nodes.ToArray()});
        foreach(var n in state.LogicalNodes.Values)snapshot.LogicalNodes.Add(n.Code,n);
        foreach(var c in state.Crosses.Values)snapshot.Crosses.Add(c.Key,c);
        var code=LineCode.Generate();while(snapshot.Lines.ContainsKey(code))code=LineCode.Generate();
        return ServiceResult<LogicalNodeConnectionPlanningInput>.Ok(new(snapshot,source,target,code,request.Kind),"");
    });
    public static ServiceResult<LogicalNodeConnectionPlan> Plan(LogicalNodeConnectionPlanningInput input,CancellationToken cancellation=default)=>
        new LogicalNodeConnectionPlanner(input.Map,input.Source,input.Target,input.Code,cancellation,input.Kind).Find();
    public ServiceResult<LogicalNodeConnectionPlan> Preview(PreviewLogicalNodeConnectionRequest request,CancellationToken cancellation=default)
    {
        var prepared=Prepare(request);
        return prepared.Success?Plan(prepared.Data!,cancellation):ServiceResult<LogicalNodeConnectionPlan>.Fail(prepared.Code,prepared.Message);
    }
    public ServiceResult<LogicalNodeConnectionResult> Save(SaveLogicalNodeConnectionRequest request)=>repository.Transaction(state=>
    {
        ServiceResult<LogicalNodeConnectionResult> Fail(string code,string message)=>ServiceResult<LogicalNodeConnectionResult>.Fail(code,message);
        if(string.IsNullOrWhiteSpace(request.Owner))return Fail("InvalidOwner","缺少登录用户。");
        var plan=request.Plan;
        var targets=LogicalNodeConnectionGeometry.ValidateTargets(state,plan);
        if(!targets.Success)return Fail(targets.Code,targets.Message);
        var joinCheck=LogicalNodeConnectionGeometry.ValidateJoin(state,plan);
        if(!joinCheck.Success)return Fail(joinCheck.Code,joinCheck.Message);
        var usesExistingPath=plan.Kind!=LogicalNodeConnectionKind.Direct;
        if(usesExistingPath)
        {
            // Never trust the client-supplied continuation. A moved LN, reversed road, or edited
            // upstream Cross can invalidate a preview while its two selected squares stay unchanged.
            LogicalNodeContinuationPoint[]? path;
            var clock=System.Diagnostics.Stopwatch.StartNew();
            try{path=plan.Kind==LogicalNodeConnectionKind.Find
                ?LogicalNodeReachability.ForwardPath(state,state.LogicalNodes[plan.Source.Code],plan.Join!,plan.LineCode,expired:()=>clock.Elapsed.TotalSeconds>5)
                :LogicalNodeReachability.Build(state,state.LogicalNodes[plan.Target.Code],plan.LineCode,expired:()=>clock.Elapsed.TotalSeconds>5).Path(plan.Join!);}
            catch(TimeoutException){return Fail("ConnectionSearchLimit","当前通路规模过大，未保存连接线路，请重新规划。");}
            if(path is null)return Fail("ConnectionPathChanged",plan.Kind==LogicalNodeConnectionKind.Find
                ?"LN1 已不能沿允许方向无障碍地到达接出点，请刷新后重新规划。"
                :"接入点已不能沿允许方向无障碍地到达 LN2，请刷新后重新规划。");
        }
        CrossState Desired(CrossSnapshot cross)=>usesExistingPath&&cross.Key.RoadCode1!=cross.Key.RoadCode2&&
            cross.Key.Contains(plan.Join!.RoadCode)&&cross.Key.Position==plan.Join.Position
                ?CrossState.Connected:CrossState.Disconnected;
        var working=LineTopologyService.Clone(state);var scope=new Scope(working);
        if(working.Lines.TryGetValue(plan.LineCode,out var existing))
        {
            // A lost successful reply can be retried without creating another line or history entry.
            if(existing.Owner!=request.Owner||!existing.Nodes.SequenceEqual(plan.Nodes)||existing.Properties!=new LineProperties()||
                working.Crosses.Values.Any(c=>c.Key.Contains(plan.LineCode)&&c.State!=Desired(c))||
                usesExistingPath&&!working.Crosses.ContainsKey(CrossKey.Create(plan.LineCode,plan.Join!.RoadCode,plan.Join.Position)))
                return Fail("ConnectionAlreadyChanged","该连接编码已存在或线路已变化，请刷新地图，勿重复新增。");
            return Result(state,existing,request.Owner,"连接已保存，已重新读取结果。",plan.Kind);
        }
        working.Lines.Add(plan.LineCode,new(plan.LineCode,request.Owner,false,0,[]));
        // Stage 1: regular F6 save, including subdivision, LN projections and pending IsChanged marks.
        var saved=new LineSaveService(scope).CheckShapeIsOKThenSave(new(plan.LineCode,request.Owner,0,plan.Nodes.ToArray()));
        if(!saved.Success)return Fail(saved.Code,saved.Message);
        var savedPlan=plan with{Nodes=saved.Data!.Nodes};
        var checkedTargets=LogicalNodeConnectionGeometry.ValidateTargets(working,savedPlan);
        if(!checkedTargets.Success)return Fail(checkedTargets.Code,checkedTargets.Message);
        // Stage 2: K Calculate -> restore -> scoped cleanup, rather than fabricating Cross records.
        var crossService=new CrossService(scope);
        for(var unit=0;unit<=saved.Data.Nodes.Length;unit++)
        {
            var calculated=crossService.CalculateForLine(plan.LineCode);
            if(!calculated.Success)return Fail(calculated.Code,"K 计算失败，连接未保存："+calculated.Message);
            if(!calculated.Data!.CanContinue)break;
            if(unit==saved.Data.Nodes.Length)return Fail("ConnectionCalculationIncomplete","K 计算未完成，连接未保存。");
        }
        // Stage 3: real Cross edits after K, scoped to the new road. GoTo/Find have ONE ordinary I join.
        // Include the endpoint's <=1-unit adjustment: every additional crossing on
        // that extension is O; only the selected join road AND coordinate receive I.
        foreach(var cross in working.Crosses.Values.Where(c=>c.Key.Contains(plan.LineCode)).ToArray())
        {
            var desired=Desired(cross);if(cross.State==desired)continue;
            var edited=crossService.Edit(new(cross.Key,request.Owner,cross.Version,desired));
            if(!edited.Success)return Fail(edited.Code,"设置连接 Cross 失败，连接未保存："+edited.Message);
        }
        if(usesExistingPath&&!working.Crosses.ContainsKey(CrossKey.Create(plan.LineCode,plan.Join!.RoadCode,plan.Join.Position)))
            return Fail("ConnectionJoinNotCalculated","K 未生成预期连接 Cross，线路未保存。");
        if(LogicalNodeRules.ValidateMap(working) is {} invalid)return Fail(invalid.Code,invalid.Message);
        if(CrossPassageValidator.FindInCurrentGeometry(working) is {} crossed)return Fail("LinePassesCross",crossed.Message("连接校验失败"));
        LineTopologyService.Publish(state,working);
        var message=plan.Kind switch
        {
            LogicalNodeConnectionKind.Find=>"寻找完成：线路已保存，K 计算完成，接出 Cross 已设为普通连通（I），其余新线 Cross 不连通。",
            LogicalNodeConnectionKind.GoTo=>"前往完成：线路已保存，K 计算完成，接入 Cross 已设为普通连通（I），其余新线 Cross 不连通。",
            _=>"连接完成：线路已保存，K 计算完成，相关 Cross 已全部设置为不连通。"
        };
        return Result(state,working.Lines[plan.LineCode],request.Owner,message,plan.Kind);
    });
    private static ServiceResult<LogicalNodeConnectionResult> Result(LineStoreState state,LineSnapshot road,string owner,string message,LogicalNodeConnectionKind kind)
    {
        var count=state.Crosses.Values.Count(c=>c.Key.Contains(road.Code)&&c.State==CrossState.Disconnected);
        var map=new MapAreaSnapshot(state.Lines.Values.Where(l=>l.Nodes.Length>=2).OrderBy(l=>l.Code).Select(l=>l with{Nodes=l.Nodes.ToArray()}).ToArray(),
            state.LogicalNodes.Values.OrderBy(n=>n.Code).Select(n=>LogicalNodePositions.Snapshot(state,n) with{CanEdit=LogicalNodeService.CanEdit(state,n,owner)}).ToArray(),CrossTopology.Visible(state));
        return ServiceResult<LogicalNodeConnectionResult>.Ok(new(road,map,count),message+$" 共 {count} 个不连通 Cross；"+
            (kind switch
            {
                LogicalNodeConnectionKind.Find=>"起点位于接出处（合法化最多延伸 1），穿过 LN2 后延伸 5.5；",
                LogicalNodeConnectionKind.GoTo=>"LN1 后侧延伸 5.5，末端止于接入处（合法化最多延伸 1）；",
                _=>"两端延伸 5.5，"
            })+"默认速度 100。Ctrl+Z 可撤销整次连接。");
    }
    private sealed class Scope(LineStoreState state):ILineRepository{public T Transaction<T>(Func<LineStoreState,T> operation)=>operation(state);}
}
