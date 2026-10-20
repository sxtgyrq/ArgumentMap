using ArgumentMap.Contracts;
using ArgumentMap.Storage;

namespace ArgumentMap.Business;

public sealed class CrossService(ILineRepository repository)
{
    public ServiceResult<CrossCalculationProgress> Recalculate()
    {
        var preparation = repository.Transaction(state =>
        {
            if (LogicalNodeRules.ValidateMap(state) is { } logicalNodeViolation)
                return ServiceResult<CrossCalculationProgress>.Fail(logicalNodeViolation.Code, logicalNodeViolation.Message);
            if (CrossPassageValidator.FindInCurrentGeometry(state) is { } violation)
                return PassageFailure(violation, "计算前校验失败");
            foreach (var line in state.Lines.Values)
            {
                if (line.Nodes.Length >= 2) state.DirtyLines.Add(line.Code);
                for (var i = 0; i < line.Nodes.Length - 1; i++) state.PendingSegments.Add((line.Code, i));
            }
            foreach (var cross in state.Crosses.Values) state.StaleCrosses.Add(cross.Key);
            return ServiceResult<CrossCalculationProgress>.Ok(new(true, state.PendingSegments.Count, state.Crosses.Count), "准备重新计算交点。");
        });
        if (!preparation.Success) return preparation;
        while (true)
        {
            var result = Calculate();
            if (!result.Success || result.Data is null || !result.Data.CanContinue) return result;
        }
    }

    // As in the old K workflow, one transaction processes one changed segment.
    public ServiceResult<CrossCalculationProgress> Calculate() => repository.Transaction(state =>
    {
        if (LogicalNodeRules.ValidateMap(state) is { } logicalNodeViolation)
            return ServiceResult<CrossCalculationProgress>.Fail(logicalNodeViolation.Code, logicalNodeViolation.Message);
        // Every request checks the current geometry under the existing shared database lock.
        if (CrossPassageValidator.FindInCurrentGeometry(state) is { } before)
            return PassageFailure(before, "计算前校验失败");
        if (state.PendingSegments.Count > 0)
        {
            var segment = state.PendingSegments.OrderBy(item => item.LineCode).ThenBy(item => item.Segment).First();
            CrossTopology.CalculateSegment(state, segment.LineCode, segment.Segment);
            state.PendingSegments.Remove(segment);
            if (LogicalNodeRules.ValidateMap(state) is { } afterLogicalNode)
                return ServiceResult<CrossCalculationProgress>.Fail(afterLogicalNode.Code, afterLogicalNode.Message);
            if (CrossPassageValidator.FindThroughCalculatedCrosses(state) is { } afterSegment)
                return PassageFailure(afterSegment, "线段计算后校验失败");
            return ServiceResult<CrossCalculationProgress>.Ok(new(true, state.PendingSegments.Count, state.Crosses.Count),
                $"已计算线 {segment.LineCode} 第 {segment.Segment + 1} 段，剩余 {state.PendingSegments.Count} 段。");
        }
        CrossTopology.Cleanup(state);
        state.DirtyLines.Clear();
        if (LogicalNodeRules.ValidateMap(state) is { } afterLogicalNodeCleanup)
            return ServiceResult<CrossCalculationProgress>.Fail(afterLogicalNodeCleanup.Code, afterLogicalNodeCleanup.Message);
        if (CrossPassageValidator.FindThroughCalculatedCrosses(state) is { } afterCleanup)
            return PassageFailure(afterCleanup, "计算完成后校验失败");
        return ServiceResult<CrossCalculationProgress>.Ok(new(false, 0, state.Crosses.Count), $"交点计算完成，共 {state.Crosses.Count} 个交点关系。");
    });

    public ServiceResult<CrossSnapshot[]> GetAll() => repository.Transaction(state =>
        ServiceResult<CrossSnapshot[]>.Ok(state.Crosses.Values.OrderBy(cross => cross.Key).ToArray(), "交点已刷新。"));

    public ServiceResult<CrossSnapshot> Edit(EditCrossRequest request) => repository.Transaction(state =>
    {
        if (!Enum.IsDefined(request.State)) return Fail("InvalidCrossState", "交点状态只能是未设置、双向连通、不连通或两个方向的单向换路。");
        if (!request.Cross.IsValid) return Fail("InvalidCrossKey", "交点须使用两个 10 位大写字母线路编码及坐标识别。");
        state.Crosses.TryGetValue(request.Cross, out var current);
        if (current is null) return Fail("CrossNotFound", "交点已不存在，请按 F3 刷新交点。");
        if (current.Version != request.Version) return Fail("VersionConflict", "交点已发生变化，请按 F3 刷新后再设置。");
        var first = state.Lines[current.Key.RoadCode1];
        var second = state.Lines[current.Key.RoadCode2];
        // As in the old CrossData.SetCrossState, either road controller can configure the crossing,
        // including while geometry awaits recalculation. Recalculation retains this state by coordinate.
        if (string.IsNullOrWhiteSpace(request.Owner) || first.Owner != request.Owner && second.Owner != request.Owner)
            return Fail("NotOwner", "只有参与交点的线路负责人可以修改这个交点。");
        if (first.IsLocked && second.IsLocked) return Fail("LineIsLocked", "参与这个交点的两条线路均已锁定。");
        if (request.State != CrossState.Disconnected && LogicalNodeRules.ContainsAny(state, current.Key.Position))
            return Fail("LogicalNodeCrossNotDisconnected", "该 Cross 位于逻辑节点正方形内，只能设置为不连通（O），不能设为连通、单向换路或未设置。");
        var changed = current with { State = request.State, Version = current.Version + 1 };
        state.Crosses[current.Key] = changed;
        return ServiceResult<CrossSnapshot>.Ok(changed, $"交点 ({changed.Key.Position.X:0.0}, {changed.Key.Position.Y:0.0}) 已设为{changed.StateLabel}。");
    });

    private static ServiceResult<CrossSnapshot> Fail(string code, string message) => ServiceResult<CrossSnapshot>.Fail(code, message);
    private static ServiceResult<CrossCalculationProgress> PassageFailure(CrossPassageViolation violation, string stage) =>
        ServiceResult<CrossCalculationProgress>.Fail("LinePassesCross", violation.Message(stage));
}
