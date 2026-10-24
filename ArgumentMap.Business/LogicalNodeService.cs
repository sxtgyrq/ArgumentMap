using ArgumentMap.Contracts;
using ArgumentMap.Storage;

namespace ArgumentMap.Business;

public sealed class LogicalNodeService(ILineRepository repository)
{
    public ServiceResult<LogicalNodeSnapshot[]> GetAll(string? viewer = null) => repository.Transaction(state =>
        ServiceResult<LogicalNodeSnapshot[]>.Ok(state.LogicalNodes.Values.OrderBy(n => n.Code).Select(n=>LogicalNodePositions.Snapshot(state,n) with
            { CanEdit = viewer is null ? null : CanEdit(state,n,viewer) }).ToArray(), "逻辑节点已刷新。"));

    public ServiceResult<LogicalNodeSnapshot> Create(CreateLogicalNodeRequest request) => repository.Transaction(state =>
    {
        if (string.IsNullOrWhiteSpace(request.Owner)) return Fail("InvalidOwner", "缺少逻辑节点所属用户。");
        if (LogicalNodeRules.ValidateShape(request.X, request.Y, request.SideLength) is { } shape) return Fail(shape.Code, shape.Message);
        var p = request.Properties;
        if (!ValidProperties(p,request.SideLength)) return Fail("InvalidLogicalNodeProperties", "LN10 值最多 220 字符，其他 LN 的值最多 32 字符；LN10 逻辑语言最多 4096 字符，其他 LN 逻辑语言及所有说明最多 255 字符，不能为 null。");
        var code = LineCode.Generate();
        while (state.LogicalNodes.ContainsKey(code)) code = LineCode.Generate();
        if(request.InstanceParentCode is not null)
        {
            if(request.SideLength!=10||!state.LogicalNodes.TryGetValue(request.InstanceParentCode,out var parent)||!LogicalNodeInstanceRules.CanInstantiate(parent))
                return Fail("InstanceParentNotFound","所选 LN12 已不存在或不能创建实例，请刷新后重新选择。");
            p=p with{Value=LogicalNodeInstanceRules.DefaultValue(parent.Properties.Value,code)};
        }
        if(LogicalNodeTextRules.HasLineBreak(p))return Fail(LogicalNodeTextRules.ErrorCode,LogicalNodeTextRules.Message);
        if(!LogicalNodeOperandRules.Valid(request.SideLength,p.OperandKind))return Fail("InvalidOperandKind","OperandKind 只能为 Class、Instance、Custom，且只适用于 LN6。");
        if(request.SideLength==6)p=p with{LogicLanguage=LogicalNodeOperandRules.Normalize(p.LogicLanguage,p.OperandKind)};
        if(request.SideLength==4)p=p with{Value=p.Value=="逻辑节点"?LogicalNodeBranchRules.False:p.Value,LogicLanguage=string.IsNullOrWhiteSpace(p.LogicLanguage)?"":p.LogicLanguage};
        if(request.SideLength==8)
        {
            p=p with{Value=p.Value=="逻辑节点"?LogicalNodeRelationRules.Equality:p.Value};
            if(!LogicalNodeRelationRules.ValidValue(p.Value))return Fail("InvalidRelationValue",$"LN8 的值只能为 {LogicalNodeRelationRules.OptionsDescription}。");
            p=p with{LogicLanguage=p.Value};
        }
        if(request.SideLength==10)
        {
            var value=LogicalNodeInstanceRules.Normalize(p.Value);
            p=p with{Value=value,LogicLanguage=LogicalNodeInstanceRules.Expression(value)};
        }
        var node = new LogicalNodeSnapshot(code, request.Owner, 0, request.X, request.Y, request.SideLength, p);
        if (LogicalNodeRules.ValidateNew(node, state) is { } area) return Fail(area.Code, area.Message);
        if (node.SideLength == 12)
        {
            var value=LogicalNodeValueUniqueness.AvailableValue(p.Value,state.LogicalNodes.Values);
            node=node with{Properties=p with{Value=value,LogicLanguage=string.IsNullOrWhiteSpace(p.LogicLanguage)?value:p.LogicLanguage}};
            if(string.IsNullOrWhiteSpace(node.Properties.LogicLanguage))return Fail("LogicalNodeLogicRequired",new LogicalNodeLogicRequiredException(code,12).Message);
        }
        var generatedLogic=node.SideLength==6&&string.IsNullOrWhiteSpace(p.LogicLanguage);
        if(node.SideLength==6)
            node=node with{Properties=p with{LogicLanguage=LogicalNodeLogicRules.ReserveLogic(LogicalNodeOperandRules.Normalize(generatedLogic?"LN_"+code:p.LogicLanguage,p.OperandKind),
                state.LogicalNodes.Values.Where(n=>n.SideLength==6).Select(n=>n.Properties.LogicLanguage).ToHashSet(StringComparer.Ordinal))}};
        if(LogicalNodeBranchRules.Validate(node,state.LogicalNodes.Values) is {} branch)return Fail(branch.Code,branch.Message);
        if(LogicalNodeInstanceRules.Validate(node,state.LogicalNodes.Values) is {} instance)return Fail(instance.Code,instance.Message);
        if(LogicalNodeInstanceRules.ValidateAssignment(node,state.LogicalNodes.Values) is {} assignment)return Fail(assignment.Code,assignment.Message);
        state.LogicalNodes.Add(code, node);
        LogicalNodePositions.RebuildNode(state,code,incrementVersion:false);
        var renamed = node.Properties.Value != p.Value ? $"值已按大写与唯一性规则保存为“{node.Properties.Value}”。" : "";
        if(generatedLogic)renamed+=$"逻辑语言已自动补为唯一临时命题“{node.Properties.LogicLanguage}”，可在编辑窗口修改。";
        else if(node.SideLength==12&&node.Properties.LogicLanguage!=p.LogicLanguage)renamed+=$"逻辑语言默认采用值“{node.Properties.LogicLanguage}”，可单独修改。";
        else if(node.SideLength==6&&node.Properties.LogicLanguage!=p.LogicLanguage)renamed+=$"逻辑语言重复，已追加数字序号保存为“{node.Properties.LogicLanguage}”，请核对相关命题引用。";
        return ServiceResult<LogicalNodeSnapshot>.Ok(LogicalNodePositions.Snapshot(state,node) with { CanEdit=true }, $"逻辑节点 {code} 及所有线路位置已保存。" + renamed);
    });

    public ServiceResult<LogicalNodeSnapshot> Update(UpdateLogicalNodeRequest request) => repository.Transaction(state =>
    {
        if (!state.LogicalNodes.TryGetValue(request.Code ?? "", out var node)) return Fail("LogicalNodeNotFound", "逻辑节点已不存在，请按 F4 刷新。");
        if (node.Version != request.Version) return Fail("VersionConflict", "逻辑节点已发生变化，请刷新后重新打开。");
        if (!CanEdit(state, node, request.Owner)) return Fail("NotOwner", "只有负责人或具备管理全部逻辑节点权限的用户可以修改。");
        var properties=new LogicalNodeProperties{Value=request.Value,Info=request.Info,LogicLanguage=request.LogicLanguage ?? node.Properties.LogicLanguage,OperandKind=request.OperandKind??node.Properties.OperandKind};
        if (!ValidProperties(properties,node.SideLength)) return Fail("InvalidLogicalNodeProperties", "LN10 值最多 220 字符，其他 LN 的值最多 32 字符；LN10 逻辑语言最多 4096 字符，其他 LN 逻辑语言及所有说明最多 255 字符，不能为 null。");
        if(LogicalNodeTextRules.HasLineBreak(properties))return Fail(LogicalNodeTextRules.ErrorCode,LogicalNodeTextRules.Message);
        if(!LogicalNodeOperandRules.Valid(node.SideLength,properties.OperandKind))return Fail("InvalidOperandKind","OperandKind 只能为 Class、Instance、Custom，且只适用于 LN6。");
        if(node.SideLength==6)properties=properties with{LogicLanguage=LogicalNodeOperandRules.Normalize(properties.LogicLanguage,properties.OperandKind)};
        if(node.SideLength==8&&!LogicalNodeRelationRules.ValidValue(properties.Value))return Fail("InvalidRelationValue",$"LN8 的值只能为 {LogicalNodeRelationRules.OptionsDescription}。");
        if(node.SideLength==8)properties=properties with{LogicLanguage=properties.Value};
        if(node.SideLength==10)
        {
            var value=LogicalNodeInstanceRules.Normalize(properties.Value);
            properties=properties with{Value=value,LogicLanguage=LogicalNodeInstanceRules.Expression(value)};
        }
        if(node.SideLength==12)properties=properties with{Value=LogicalNodeValueUniqueness.Normalize(properties.Value)};
        if(node.SideLength==4&&string.IsNullOrWhiteSpace(properties.LogicLanguage))properties=properties with{LogicLanguage=""};
        // Geometry is absent from the update contract. Preserve all positions and ownership rules.
        var changed = node with { Owner = request.Owner, Version = node.Version + 1, Properties = properties };
        var instances=node.SideLength==12&&properties.Value!=node.Properties.Value?
            state.LogicalNodes.Values.Where(n=>LogicalNodeInstanceRules.References(n,node)).ToArray():[];
        if(instances.Length>0&&!LogicalNodeInstanceRules.CanInstantiate(changed))
            return Fail("InvalidInstanceParentValue","该 LN12 仍有关联 LN10 实例，新值必须至少包含一个英文字母（A–Z／a–z），不能含括号，并可与小写实例对应。请先删除或改属实例。");
        if(instances.Any(child=>LogicalNodeInstanceRules.Reparent(child.Properties.Value,properties.Value).Length>LogicalNodeInstanceRules.ValueMaximum))
            return Fail("InvalidInstanceValue","父类改名后，关联 LN10 的值将超过 220 字符，修改未保存。");
        if (LogicalNodeValueUniqueness.FindConflict(changed, state.LogicalNodes.Values) is { } duplicate)
            return Fail("DuplicateLogicalNodeValue", new DuplicateLogicalNodeValueException(duplicate).Message);
        if(node.SideLength is 6 or 12&&string.IsNullOrWhiteSpace(properties.LogicLanguage))
            return Fail("LogicalNodeLogicRequired",new LogicalNodeLogicRequiredException(node.Code,node.SideLength).Message);
        if(LogicalNodeLogicRules.FindConflict(changed,state.LogicalNodes.Values) is {} logicDuplicate)
            return Fail("DuplicateLogicalNodeLogic",new DuplicateLogicalNodeLogicException(logicDuplicate).Message);
        if(LogicalNodeBranchRules.Validate(changed,state.LogicalNodes.Values) is {} branch)return Fail(branch.Code,branch.Message);
        if(LogicalNodeInstanceRules.Validate(changed,state.LogicalNodes.Values) is {} instance)return Fail(instance.Code,instance.Message);
        if(node.Properties.Value!=changed.Properties.Value&&LogicalNodeInstanceRules.ValidateAssignment(changed,state.LogicalNodes.Values) is {} assignment)
            return Fail(assignment.Code,assignment.Message);
        var references=node.SideLength==6?state.LogicalNodes.Values.Where(n=>n.SideLength==4&&LogicalNodeBranchRules.SourceLogic(n)==node.Properties.LogicLanguage).ToArray():[];
        if(properties.LogicLanguage!=node.Properties.LogicLanguage&&references.Any(n=>LogicalNodeBranchRules.Expression(n.Properties.Value,properties.LogicLanguage).Length>255))
            return Fail("InvalidBranchLogic","LN6 修改后，关联 LN4 的逻辑语言将超过 255 字符，修改未保存。");
        state.LogicalNodes[node.Code] = changed;
        foreach(var child in instances)
        {
            var value=LogicalNodeInstanceRules.Reparent(child.Properties.Value,properties.Value);
            state.LogicalNodes[child.Code]=child with{Version=child.Version+1,Properties=child.Properties with{Value=value,LogicLanguage=LogicalNodeInstanceRules.Expression(value)}};
        }
        if(properties.LogicLanguage!=node.Properties.LogicLanguage)
            foreach(var related in references)state.LogicalNodes[related.Code]=related with{Version=related.Version+1,Properties=related.Properties with{LogicLanguage=LogicalNodeBranchRules.Expression(related.Properties.Value,properties.LogicLanguage)}};
        return ServiceResult<LogicalNodeSnapshot>.Ok(LogicalNodePositions.Snapshot(state,changed) with { CanEdit=true }, "逻辑节点的值、逻辑语言和说明已保存。"+(instances.Length>0?$"已同步 {instances.Length} 个 LN10 的值和隶属表达式。":"")+(references.Length>0&&properties.LogicLanguage!=node.Properties.LogicLanguage?$"已同步 {references.Length} 个关联 LN4 的逻辑语言。":""));
    });

    public ServiceResult<LogicalNodeSnapshot> Move(MoveLogicalNodeRequest request) => repository.Transaction(state =>
    {
        if (!state.LogicalNodes.TryGetValue(request.Code ?? "", out var node)) return Fail("LogicalNodeNotFound", "逻辑节点已不存在，请按 F4 刷新。");
        if (node.Version != request.Version) return Fail("VersionConflict", "逻辑节点已发生变化，请刷新后重新打开再移动。");
        if (!CanEdit(state, node, request.Owner)) return Fail("NotOwner", "只有负责人或具备管理全部逻辑节点权限的用户可以移动。");
        if (LogicalNodeRules.ValidateShape(request.X, request.Y, node.SideLength) is { } shape) return Fail(shape.Code, shape.Message);
        var moved = node with { X = request.X, Y = request.Y };
        // Validate the destination against the complete, locked map snapshot. Exclude this
        // node from square separation; moving never changes existing Cross states.
        if (LogicalNodeRules.ValidateNew(moved, state) is { } area) return Fail(area.Code, area.Message);
        if (moved.X == node.X && moved.Y == node.Y)
            return ServiceResult<LogicalNodeSnapshot>.Ok(LogicalNodePositions.Snapshot(state, node) with { CanEdit = true }, "位置未改变。");
        moved = moved with { Version = node.Version + 1 };
        state.LogicalNodes[node.Code] = moved;
        // Replace all projections, including deletion of old associations, in this transaction.
        LogicalNodePositions.RebuildNode(state, node.Code, incrementVersion: false);
        return ServiceResult<LogicalNodeSnapshot>.Ok(LogicalNodePositions.Snapshot(state, moved) with { CanEdit = true }, "逻辑节点已移动，全部线路位置已更新。");
    });

    public ServiceResult<bool> Delete(DeleteLogicalNodeRequest request) => repository.Transaction(state =>
    {
        if (!state.LogicalNodes.TryGetValue(request.Code ?? "", out var node)) return ServiceResult<bool>.Fail("LogicalNodeNotFound", "逻辑节点已不存在，请按 F4 刷新。");
        if (node.Version != request.Version) return ServiceResult<bool>.Fail("VersionConflict", "逻辑节点已发生变化，请刷新后再删除。");
        if (!CanEdit(state, node, request.Owner)) return ServiceResult<bool>.Fail("NotOwner", "只有负责人或管理员可以删除该逻辑节点。");
        if(LogicalNodeInstanceRules.ValidateDeletion(state.LogicalNodes.Values,[node.Code]) is {} instance)
            return ServiceResult<bool>.Fail(instance.Code,instance.Message);
        ClearBranchReferences(state,[node.Code]);
        state.LogicalNodes.Remove(node.Code);
        foreach(var key in state.LogicalNodePositions.Keys.Where(k=>k.LogicalNodeCode==node.Code).ToArray())state.LogicalNodePositions.Remove(key);
        return ServiceResult<bool>.Ok(true, "逻辑节点已删除。");
    });

    public ServiceResult<LogicalNodeCalculationProgress> Calculate() => repository.Transaction(state =>
    {
        if (LogicalNodeRules.ValidateMap(state) is { } violation) return ServiceResult<LogicalNodeCalculationProgress>.Fail(violation.Code, violation.Message);
        foreach(var code in state.LogicalNodes.Keys.Order().ToArray()) LogicalNodePositions.RebuildNode(state,code);
        return ServiceResult<LogicalNodeCalculationProgress>.Ok(new(false,0,state.LogicalNodes.Count),
            $"逻辑节点位置计算完成，共 {state.LogicalNodePositions.Count} 个位置；全部在同一事务提交。");
    });

    // The caller holds only a code cursor, never a map snapshot or a database lock.
    // CRUD/import/move already rebuild affected positions atomically, including newly
    // inserted nodes whose generated code sorts before this cursor.
    public ServiceResult<LogicalNodeCalculationProgress> CalculateStep(string? afterCode) => repository.Transaction(state =>
    {
        if(afterCode is not null&&!LineCode.IsValid(afterCode))
            return ServiceResult<LogicalNodeCalculationProgress>.Fail("InvalidCalculationCursor","LN 计算游标无效，请重新按 J 开始。");
        if(LogicalNodeRules.ValidateMap(state) is {} violation)
            return ServiceResult<LogicalNodeCalculationProgress>.Fail(violation.Code,violation.Message);
        var codes=state.LogicalNodes.Keys.Where(code=>afterCode is null||StringComparer.Ordinal.Compare(code,afterCode)>0)
            .Order(StringComparer.Ordinal).ToArray();
        if(codes.Length==0)return ServiceResult<LogicalNodeCalculationProgress>.Ok(new(false,0,state.LogicalNodes.Count),
            $"J 计算完成，共 {state.LogicalNodePositions.Count} 个位置。");
        LogicalNodePositions.RebuildNode(state,codes[0]);
        return ServiceResult<LogicalNodeCalculationProgress>.Ok(new(true,codes.Length-1,state.LogicalNodes.Count,codes[0]),
            $"J 已提交 LN {codes[0]}，尚余 {codes.Length-1} 个；可继续编辑。");
    });

    internal static bool CanEdit(LineStoreState state, LogicalNodeSnapshot node, string owner) => !string.IsNullOrWhiteSpace(owner) &&
        (owner == node.Owner || state.Accounts.TryGetValue(owner, out var account) && account.EditAllLogicalNodes);
    internal static void ClearBranchReferences(LineStoreState state,IEnumerable<string> deletedCodes)
    {
        var codes=deletedCodes.ToHashSet(StringComparer.Ordinal);
        var sources=state.LogicalNodes.Values.Where(n=>codes.Contains(n.Code)&&n.SideLength==6).Select(n=>n.Properties.LogicLanguage).ToHashSet(StringComparer.Ordinal);
        foreach(var branch in state.LogicalNodes.Values.Where(n=>n.SideLength==4&&!codes.Contains(n.Code)&&LogicalNodeBranchRules.SourceLogic(n) is {} logic&&sources.Contains(logic)).ToArray())
            state.LogicalNodes[branch.Code]=branch with{Version=branch.Version+1,Properties=branch.Properties with{LogicLanguage=""}};
    }
    private static bool ValidProperties(LogicalNodeProperties? p,int size) => p is not null && p.Value is not null && p.Info is not null && p.LogicLanguage is not null && p.Value.Length<=(size==10?LogicalNodeInstanceRules.ValueMaximum:32) && p.Info.Length<=255 && p.LogicLanguage.Length<=(size==10?LogicalNodeInstanceRules.LogicMaximum:255);
    private static ServiceResult<LogicalNodeSnapshot> Fail(string code, string message) => ServiceResult<LogicalNodeSnapshot>.Fail(code, message);
}
