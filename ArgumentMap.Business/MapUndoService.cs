using ArgumentMap.Contracts;
using ArgumentMap.Rules;
using ArgumentMap.Storage;

namespace ArgumentMap.Business;

/// <summary>Compensating map transactions. History and mutation stamps commit with the edit.</summary>
public sealed class MapUndoService(ILineRepository repository)
{
    public ServiceResult<T> Record<T>(string owner, string operation, Func<ServiceResult<T>> edit) => repository.Transaction(state =>
    {
        var before = LineTopologyService.Clone(state);
        var result = edit();
        if (!result.Success) return result;
        var lines = before.Lines.Keys.Union(state.Lines.Keys).Order(StringComparer.Ordinal)
            .Select(k => new UndoLineChange(before.Lines.GetValueOrDefault(k), state.Lines.GetValueOrDefault(k)))
            .Where(c => !Same(c.Before, c.After)).ToArray();
        var nodes = before.LogicalNodes.Keys.Union(state.LogicalNodes.Keys).Order(StringComparer.Ordinal)
            .Select(k => new UndoNodeChange(Core(before.LogicalNodes.GetValueOrDefault(k)), Core(state.LogicalNodes.GetValueOrDefault(k))))
            .Where(c => !Same(c.Before, c.After)).ToArray();
        var geometry = lines.Where(c => !SameGeometry(c.Before, c.After)).Select(LineCodeOf).ToHashSet(StringComparer.Ordinal);
        // Compare canonical crossings so a K unit that finishes later neither changes
        // history nor creates a false conflict. K/J versions and positions are projections.
        var oldMap = Project(before, geometry); var newMap = Project(state, geometry);
        var crossKeys = oldMap.Crosses.Keys.Union(newMap.Crosses.Keys)
            .Where(k => geometry.Contains(k.RoadCode1) || geometry.Contains(k.RoadCode2) ||
                oldMap.Crosses.GetValueOrDefault(k)?.State != newMap.Crosses.GetValueOrDefault(k)?.State)
            .Order().ToArray();
        if (lines.Length == 0 && nodes.Length == 0 && crossKeys.Length == 0) return result;
        var delta = new MapUndoDelta(lines, nodes, geometry.Order(StringComparer.Ordinal).ToArray(),
            crossKeys.Where(oldMap.Crosses.ContainsKey).Select(k => oldMap.Crosses[k]).ToArray(),
            crossKeys.Where(newMap.Crosses.ContainsKey).Select(k => newMap.Crosses[k]).ToArray());
        var keys = lines.Select(c => "L:" + LineCodeOf(c)).Concat(nodes.Select(c => "N:" + NodeCodeOf(c)))
            .Concat(crossKeys.Select(CrossStamp)).Distinct(StringComparer.Ordinal).ToArray();
        state.UndoHistory.Append(new(Guid.NewGuid().ToString("N"), owner, operation, 0, delta,
            keys.Select(k => new UndoStamp(k, state.UndoHistory.Stamp(k))).ToArray()));
        return result;
    });

    public ServiceResult<MapUndoResult> Undo(string owner, string? requestId = null) => repository.Transaction(state =>
    {
        requestId ??= Guid.NewGuid().ToString("N");
        if (!Guid.TryParseExact(requestId, "N", out _)) return Fail("InvalidUndoRequest", "撤销请求标识无效，请更新客户端后重试。");
        if (state.UndoHistory.FindRequest(owner, requestId) is {} completed)
            return Result(state, owner, completed.Operation, repeated: true);
        var entry = state.UndoHistory.Latest(owner);
        if (entry is null) return Fail("NothingToUndo", "没有可撤销的数据库操作；仅记录新版启用后本账号最近 100 次操作。");
        var delta = entry.Delta;
        if (entry.Stamps.Any(s => state.UndoHistory.Stamp(s.Key) != entry.Id))
            return Conflict("相关对象或 Cross 设置已被后续操作修改");
        foreach (var change in delta.Lines)
        {
            var actual = state.Lines.GetValueOrDefault(LineCodeOf(change));
            if (!Same(actual, change.After)) return Conflict("线路已变化");
            var target = actual ?? change.Before!;
            var lockChange = change.Before is not null && change.After is not null && change.Before.IsLocked != change.After.IsLocked;
            var admin = state.Accounts.TryGetValue(owner, out var account) && account.EditAllRoad;
            if (lockChange ? !admin : target.Owner != owner || target.IsLocked && change.Before is not null)
                return Fail("UndoNotAllowed", "当前账号没有撤销相关线路的权限，或线路已锁定。");
        }
        foreach (var change in delta.Nodes)
        {
            var actual = state.LogicalNodes.GetValueOrDefault(NodeCodeOf(change));
            if (!Same(actual, change.After)) return Conflict("LN 或关联引用已变化");
            if (!LogicalNodeService.CanEdit(state, actual ?? change.Before!, owner) && !IsCascade(change, delta))
                return Fail("UndoNotAllowed", "当前账号没有撤销相关 LN 的权限。");
        }
        var roads = delta.GeometryRoads.ToHashSet(StringComparer.Ordinal);
        var crossKeys = delta.BeforeCrosses.Select(c => c.Key).Union(delta.AfterCrosses.Select(c => c.Key)).ToHashSet();
        bool Affected(CrossKey key) => roads.Contains(key.RoadCode1) || roads.Contains(key.RoadCode2) || crossKeys.Contains(key);
        var projected = Project(state, roads);
        var currentCrosses = projected.Crosses.Values.Where(c => Affected(c.Key)).ToDictionary(c => c.Key);
        if (currentCrosses.Count != delta.AfterCrosses.Length || delta.AfterCrosses.Any(c =>
                !currentCrosses.TryGetValue(c.Key, out var now) || now.State != c.State))
            return Conflict("受影响线路的交点或通行设置已变化");
        foreach (var cross in delta.BeforeCrosses.Concat(delta.AfterCrosses))
        {
            // Cross-only edits need the same permission checks as EditCross. Crosses
            // removed/recreated with a road follow that road's original edit permission.
            if (roads.Contains(cross.Key.RoadCode1) || roads.Contains(cross.Key.RoadCode2)) continue;
            if (!state.Lines.TryGetValue(cross.Key.RoadCode1, out var a) || !state.Lines.TryGetValue(cross.Key.RoadCode2, out var b) ||
                a.Owner != owner && b.Owner != owner || a.IsLocked && b.IsLocked)
                return Fail("UndoNotAllowed", "当前账号没有撤销相关 Cross 的权限，或两条线路均已锁定。");
        }
        var working = LineTopologyService.Clone(state);
        // Apply all source changes together; never expose an intermediate broken reference.
        foreach (var change in delta.Lines)
        {
            var code = LineCodeOf(change);
            if (change.Before is null) working.Lines.Remove(code);
            else working.Lines[code] = change.Before with
            { Nodes = change.Before.Nodes.ToArray(), Version = NextVersion(change.Before.Version, change.After?.Version, state.Lines.GetValueOrDefault(code)?.Version) };
        }
        foreach (var change in delta.Nodes)
        {
            var code = NodeCodeOf(change);
            if (change.Before is null) working.LogicalNodes.Remove(code);
            else working.LogicalNodes[code] = change.Before with
            { Version = NextVersion(change.Before.Version, change.After?.Version, state.LogicalNodes.GetValueOrDefault(code)?.Version) };
        }
        if (state.UndoHistory.ValidateDeletion(state.LogicalNodes.Values, working.LogicalNodes.Values, entry) is {} deletion)
            return Fail(deletion.Code, "不能撤销：" + deletion.Message);
        // Reuse K/J mark -> rebuild -> scoped cleanup. Preserve unrelated pending work.
        CrossTopology.MarkChanged(working, roads, recordSqlMarks: true);
        bool GeometryCross(CrossKey key) => roads.Contains(key.RoadCode1) || roads.Contains(key.RoadCode2);
        foreach (var key in working.Crosses.Keys.Where(GeometryCross).ToArray()) working.Crosses.Remove(key);
        working.StaleCrosses.RemoveWhere(GeometryCross);
        foreach (var cross in delta.BeforeCrosses)
            if (working.Lines.ContainsKey(cross.Key.RoadCode1) && working.Lines.ContainsKey(cross.Key.RoadCode2))
            {
                var current = working.Crosses.GetValueOrDefault(cross.Key);
                working.Crosses[cross.Key] = (GeometryCross(cross.Key) ? cross : current ?? cross) with { State = cross.State, Version = NextVersion(cross.Version,
                    delta.AfterCrosses.FirstOrDefault(c => c.Key == cross.Key)?.Version, state.Crosses.GetValueOrDefault(cross.Key)?.Version) };
                if (roads.Contains(cross.Key.RoadCode1) || roads.Contains(cross.Key.RoadCode2)) working.StaleCrosses.Add(cross.Key);
            }
        foreach (var code in roads)
        {
            if (working.Lines.TryGetValue(code, out var line))
                for (var i = 0; i + 1 < line.Nodes.Length; i++) CrossTopology.CalculateSegment(working, code, i);
            working.PendingSegments.RemoveWhere(p => p.LineCode == code); working.DirtyLines.Remove(code);
        }
        CrossTopology.Cleanup(working, roads);
        // Geometry is revalidated against everyone else's CURRENT objects, including
        // nodes/roads added after deletion. Restoring a conflicting old shape must fail.
        foreach (var code in roads)
            if (working.Lines.TryGetValue(code, out var line) && line.Nodes.Length >= 2)
            {
                // ValidateShape also checks ownership/lock. They were checked above;
                // use the stored owner and temporarily unlock for a shape-only check.
                working.Lines[code] = line with { IsLocked = false };
                var invalid = LineSaveService.ValidateShape(working, new(code, line.Owner, line.Version, line.Nodes), line.Nodes);
                working.Lines[code] = line;
                if (invalid is not null) return Fail(invalid.Code, "不能撤销：" + invalid.Message);
            }
        if (LogicalNodeRules.ValidateMap(working) is {} geometryError) return Fail(geometryError.Code, "不能撤销：" + geometryError.Message);
        if (CrossPassageValidator.FindInCurrentGeometry(working) is {} passage) return Fail("LinePassesCross", passage.Message("不能撤销"));
        var codes = delta.Nodes.Select(NodeCodeOf).ToHashSet(StringComparer.Ordinal);
        foreach (var key in working.LogicalNodePositions.Keys.Where(k => !working.LogicalNodes.ContainsKey(k.LogicalNodeCode)).ToArray())
            working.LogicalNodePositions.Remove(key);
        LogicalNodePositions.RebuildAffected(working, delta.Lines.Select(LineCodeOf).ToHashSet(StringComparer.Ordinal), codes);
        // Keep every LN12/10/8/6/4 restriction and the DAL's deletion guard in force.
        try
        {
            LogicalNodeTextRules.EnsureChangesValid(state.LogicalNodes.Values, working.LogicalNodes.Values);
            LogicalNodeCoordinateRules.EnsureValid(working.LogicalNodes.Values);
            LogicalNodeValueUniqueness.EnsureValid(working.LogicalNodes.Values);
            LogicalNodeLogicRules.EnsureValid(working.LogicalNodes.Values);
            LogicalNodeOperandRules.EnsureValid(working.LogicalNodes.Values);
            LogicalNodeBranchRules.EnsureValid(working.LogicalNodes.Values);
            LogicalNodeRelationRules.EnsureValid(working.LogicalNodes.Values);
            LogicalNodeInstanceRules.EnsureValid(working.LogicalNodes.Values);
            LogicalNodeInstanceRules.EnsureChangesValid(state.LogicalNodes.Values, working.LogicalNodes.Values);
        }
        catch (InvalidOperationException error) when (error is InvalidLogicalNodeTextException or InvalidLogicalNodeCoordinateException or
            DuplicateLogicalNodeValueException or DuplicateLogicalNodeLogicException or LogicalNodeLogicRequiredException or
            InvalidLogicalNodeBranchException or InvalidLogicalNodeRelationException or InvalidLogicalNodeInstanceException or InvalidLogicalNodeOperandException)
        { return Fail("UndoRuleConflict", "不能撤销：" + error.Message + " 撤销记录已保留。"); }
        LineTopologyService.Publish(state, working);
        state.UndoHistory.Consume(entry, requestId);
        return Result(state, owner, entry.Operation);
    });

    private static ServiceResult<MapUndoResult> Result(LineStoreState state, string owner, string operation, bool repeated = false)
    {
        var map = new MapAreaSnapshot(state.Lines.Values.Where(l => l.Nodes.Length >= 2).OrderBy(l => l.Code).ToArray(),
            state.LogicalNodes.Values.OrderBy(n => n.Code).Select(n => LogicalNodePositions.Snapshot(state, n) with
                { CanEdit = LogicalNodeService.CanEdit(state, n, owner) }).ToArray(), state.Crosses.Values.OrderBy(c => c.Key).ToArray());
        return ServiceResult<MapUndoResult>.Ok(new(operation, map), repeated ? $"上次撤销已完成：{operation}。已刷新最新数据，本次重试没有再次撤销。" :
            $"已从数据库撤销：{operation}。相关对象和关联数据已在同一事务恢复。");
    }

    private static bool IsCascade(UndoNodeChange change, MapUndoDelta delta)
    {
        if (change.Before is not {} a || change.After is not {} b || a.Owner != b.Owner || a.X != b.X || a.Y != b.Y ||
            a.SideLength != b.SideLength || a.Properties.Info != b.Properties.Info) return false;
        return a.SideLength == 4 && a.Properties.Value == b.Properties.Value && delta.Nodes.Any(c =>
            c.Before?.SideLength == 6 && LogicalNodeBranchRules.SourceLogic(a) == c.Before.Properties.LogicLanguage &&
            (c.After is null ? b.Properties.LogicLanguage == "" : LogicalNodeBranchRules.SourceLogic(b) == c.After.Properties.LogicLanguage)) ||
            a.SideLength == 10 && delta.Nodes.Any(c => c.Before?.SideLength == 12 && c.After?.SideLength == 12 &&
                LogicalNodeInstanceRules.References(a, c.Before) && LogicalNodeInstanceRules.References(b, c.After));
    }
    private static long NextVersion(long a, long? b, long? c) => checked(Math.Max(a, Math.Max(b ?? 0, c ?? 0)) + 1);
    private static LogicalNodeSnapshot? Core(LogicalNodeSnapshot? n) => n is null ? null : n with { Positions = [], CanEdit = null };
    private static bool Same(LogicalNodeSnapshot? a, LogicalNodeSnapshot? b) => a is null || b is null ? a is null && b is null :
        a.Code == b.Code && a.Owner == b.Owner && a.X == b.X && a.Y == b.Y && a.SideLength == b.SideLength && a.Properties == b.Properties;
    private static bool Same(LineSnapshot? a, LineSnapshot? b) => a is null || b is null ? a is null && b is null :
        a.Code == b.Code && a.Owner == b.Owner && a.IsLocked == b.IsLocked && a.Properties == b.Properties && a.Nodes.SequenceEqual(b.Nodes);
    private static bool SameGeometry(LineSnapshot? a, LineSnapshot? b) => a is null || b is null ? a is null && b is null : a.Nodes.SequenceEqual(b.Nodes);
    private static string LineCodeOf(UndoLineChange c) => (c.Before ?? c.After)!.Code;
    private static string NodeCodeOf(UndoNodeChange c) => (c.Before ?? c.After)!.Code;
    private static string CrossStamp(CrossKey k) => $"C:{k.RoadCode1}:{k.RoadCode2}:{k.Position.X2}:{k.Position.Y2}";
    private static LineStoreState Project(LineStoreState state, IReadOnlySet<string> roads)
    {
        var projected = LineTopologyService.Clone(state);
        CrossTopology.MarkChanged(projected, roads);
        foreach (var code in roads)
            if (projected.Lines.TryGetValue(code, out var line))
                for (var i = 0; i + 1 < line.Nodes.Length; i++) CrossTopology.CalculateSegment(projected, code, i);
        CrossTopology.Cleanup(projected, roads);
        return projected;
    }
    private static ServiceResult<MapUndoResult> Conflict(string reason) => Fail("UndoConflict", $"不能撤销：{reason}，未修改数据库，也未消耗撤销记录；请先核对最新数据。");
    private static ServiceResult<MapUndoResult> Fail(string code, string message) => ServiceResult<MapUndoResult>.Fail(code, message);
}
