using ArgumentMap.Rules;
using ArgumentMap.Contracts;
using ArgumentMap.Storage;

namespace ArgumentMap.Business;

public sealed class LineSaveService(ILineRepository repository)
{
    public ServiceResult<LineSnapshot> Create(CreateLineRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Owner)) return Failure("InvalidOwner", "缺少线路所属用户。");
        return repository.Transaction(state =>
        {
            var code = LineCode.Generate();
            while (state.Lines.ContainsKey(code)) code = LineCode.Generate();
            var line = new LineSnapshot(code, request.Owner, false, 0, []);
            state.Lines.Add(line.Code, line);
            return ServiceResult<LineSnapshot>.Ok(Copy(line), $"已新建线 {line.Code}。");
        });
    }

    public ServiceResult<LineSnapshot> StartDrawing(LineAccessRequest request) => repository.Transaction(state =>
    {
        var failure = CheckAccess(state, request.LineCode, request.Owner, out var line);
        return failure ?? ServiceResult<LineSnapshot>.Ok(Copy(line!), $"开始编辑线 {line!.Code}。");
    });

    public ServiceResult<LineSnapshot[]> GetAll() => repository.Transaction(state =>
        ServiceResult<LineSnapshot[]>.Ok(state.Lines.Values.Where(line => line.Nodes.Length >= 2)
            .OrderBy(line => line.Code).Select(Copy).ToArray(), "已刷新全部线路。"));

    public ServiceResult<LineSnapshot> GetDetail(LineAccessRequest request) => repository.Transaction(state =>
        !LineCode.IsValid(request.LineCode) ? Failure("InvalidLineCode", "线路编码必须是 10 位大写英文字母。") :
        state.Lines.TryGetValue(request.LineCode, out var line)
            ? ServiceResult<LineSnapshot>.Ok(Copy(line), "线路属性已加载。")
            : Failure("LineNotFound", "线路已不存在，请刷新。"));

    public ServiceResult<LineSnapshot> SaveProperties(SaveLinePropertiesRequest request) => repository.Transaction(state =>
    {
        var failure = CheckAccess(state, request.LineCode, request.Owner, out var original);
        if (failure is not null) return failure;
        if (original!.Version != request.Version) return Failure("VersionConflict", "线路属性已发生变化，请重新打开属性窗口。");
        var properties = request.Properties;
        if (properties is null || properties.RoadName is null || properties.RoadDetail is null ||
            properties.RoadName.Length >= 16 || properties.RoadDetail.Length >= 128)
            return Failure("InvalidProperties", "线路名称最多 15 个字符，说明最多 127 个字符。");
        if (properties.Speed is < MapLimits.SpeedMinimum or > MapLimits.SpeedMaximum)
            return Failure("InvalidSpeed", "速度须为 5–500 的整数。");
        if (properties.PassInDirection != 1 || properties.PassInOpposeDirection is < 0 or > 1)
            return Failure("InvalidPassDirection", "正向通行固定为 1；反向通行只能为 0（单向）或 1（双向）。");
        if (original.Properties == properties) return ServiceResult<LineSnapshot>.Ok(Copy(original), "线路属性未发生变化。");
        var changed = original with { Properties = properties, Version = original.Version + 1 };
        state.Lines[changed.Code] = changed;
        LogicalNodePositions.RebuildRoad(state, changed.Code);
        // Property changes do not change geometry or discard/recalculate Cross settings.
        return ServiceResult<LineSnapshot>.Ok(Copy(changed), "线路属性已保存。");
    });

    public ServiceResult<LineSnapshot> ToggleLock(ToggleLineLockRequest request) => repository.Transaction(state =>
    {
        if (!LineCode.IsValid(request.LineCode)) return Failure("InvalidLineCode", "线路编码必须是 10 位大写英文字母。");
        if (!state.Accounts.TryGetValue(request.Owner, out var account) || !account.EditAllRoad)
            return Failure("InsufficientRights", "需要“编辑全部线路”权限才能锁定或解锁线路。");
        if (!state.Lines.TryGetValue(request.LineCode, out var line)) return Failure("LineNotFound", "线路已不存在，请刷新。");
        if (line.Version != request.Version) return Failure("VersionConflict", "线路已发生变化，请刷新后再锁定或解锁。");
        if (line.Nodes.Length < 2 || state.DirtyLines.Contains(line.Code) || state.PendingSegments.Any(segment => segment.LineCode == line.Code))
            return Failure("LineNotCalculated", "该线路还未完成计算，请先按 K 完成交点计算及清理。");
        var changed = line with { IsLocked = !line.IsLocked, Version = line.Version + 1 };
        state.Lines[changed.Code] = changed;
        LogicalNodePositions.RebuildRoad(state, changed.Code);
        return ServiceResult<LineSnapshot>.Ok(Copy(changed), changed.IsLocked ? "线路已锁定，显示为绿色。" : "线路已解锁。");
    });

    public ServiceResult<LineSnapshot> CheckShapeIsOKThenSave(SaveLineRequest request)
    {
        // Snapshot mutable transport arrays before starting the transaction.
        var nodes = request.Nodes?.ToArray();
        return repository.Transaction(state => ValidateAndSave(state, request, nodes));
    }

    public ServiceResult<LineSnapshot> Reverse(ReverseLineRequest request) => repository.Transaction(state =>
    {
        var failure = CheckAccess(state, request.LineCode, request.Owner, out var original);
        if (failure is not null) return failure;
        if (original!.Version != request.Version) return Failure("VersionConflict", "线路已经发生变化，请刷新后再反转方向。");
        var nodes = original.Nodes.Reverse().Select((node, index) => node with { Order = index }).ToArray();
        // Read, reverse, validate, replace and mark Crosses inside the SAME existing data-layer transaction.
        var result = ValidateAndSave(state, new(request.LineCode, request.Owner, request.Version, nodes), nodes);
        return result.Success ? result with { Message = $"线 {request.LineCode} 方向已反转，节点重新编号。请按 K 重算交点，原通行设置会按坐标保留。" } : result;
    });

    internal static ServiceResult<LineSnapshot>? ValidateShape(LineStoreState state, SaveLineRequest request, LineNode[]? nodes)
    {
            var failure = CheckAccess(state, request.LineCode, request.Owner, out var original);
            if (failure is not null) return failure;
            if (original!.Version != request.Version) return Failure("VersionConflict", "线路已经发生变化，请取消当前修改并重新选择编辑。");
            if (nodes is null || nodes.Length < 2) return Failure("TooFewNodes", "至少添加两个不同节点后才能保存线路。");
            var nodeIndices = new Dictionary<(int X, int Y), int>();
            for (var i = 0; i < nodes.Length; i++)
            {
                var node = nodes[i];
                if (node is null) return Failure("InvalidNode", $"第 {i + 1} 个节点数据为空。");
                if (node.LineCode != request.LineCode) return Failure("LineCodeMismatch", $"第 {i + 1} 个节点的线路编码不一致。");
                if (node.Order != i) return Failure("InvalidOrder", "节点编号必须从 0 开始，按当前节点顺序连续递增。");
                if (node.X < 0 || node.X > MapLimits.CoordinateMaximum || node.Y < 0 || node.Y > MapLimits.CoordinateMaximum)
                    return Failure("OutOfBounds", $"第 {i + 1} 个节点 ({node.X}, {node.Y}) 超出 0–500000 坐标范围。");
                if (nodeIndices.TryGetValue((node.X, node.Y), out var first))
                    return Failure("DuplicateNode", $"第 {first + 1} 个与第 {i + 1} 个节点坐标重复 ({node.X}, {node.Y})，请修改后再按 F6。");
                nodeIndices.Add((node.X, node.Y), i);
            }
            for (var i = 1; i < nodes.Length; i++)
                if (!LineGeometry.IsEightDirection(nodes[i - 1], nodes[i]))
                    return Failure("InvalidDirection", $"第 {i} 段不符合八方向约束，只允许水平、竖直和 45° 斜线。");

            // The previous version of this same line is excluded from global collision checks.
            var otherLines = state.Lines.Values.Where(line => line.Code != request.LineCode).OrderBy(line => line.Code).ToArray();
            var occupied = new Dictionary<(int X, int Y), (string LineCode, int Order)>();
            foreach (var line in otherLines)
                foreach (var node in line.Nodes) occupied[(node.X, node.Y)] = (line.Code, node.Order);
            foreach (var node in nodes)
                if (occupied.TryGetValue((node.X, node.Y), out var other))
                    return Failure("PointIsOccupied", $"节点 ({node.X}, {node.Y}) 已被线 {other.LineCode} 的第 {other.Order + 1} 个节点占用。");

            for (var i = 1; i < nodes.Length; i++)
            {
                for (var j = 1; j < i; j++)
                    if (LineGeometry.Overlaps(nodes[i - 1], nodes[i], nodes[j - 1], nodes[j]))
                        return Failure("SegmentOverlap", $"当前线的第 {j} 段与第 {i} 段重叠，无法保存。");
                foreach (var line in otherLines)
                    for (var j = 1; j < line.Nodes.Length; j++)
                        if (LineGeometry.Overlaps(nodes[i - 1], nodes[i], line.Nodes[j - 1], line.Nodes[j]))
                            return Failure("SegmentOverlap", $"当前线第 {i} 段与线 {line.Code} 的第 {j} 段重叠，无法保存。");
            }

            if (LineGeometry.FindAcuteCorner(nodes) is int corner)
                return Failure("AcuteCorner", $"第 {corner + 1} 个节点（序号 {corner}，坐标 {nodes[corner].X}, {nodes[corner].Y}）形成 45° 锐角，无法保存。请调整节点，允许 90° 直角、135° 钝角或直线延伸。");

            if (CrossPassageValidator.FindForLine(original with { Nodes = nodes }, state) is { } passage)
                return Failure("LinePassesCross", passage.Message("保存校验失败"));
            if (LogicalNodeRules.ValidateMap(state, original with { Nodes = nodes }) is { } logicalNodeViolation)
                return Failure(logicalNodeViolation.Code, logicalNodeViolation.Message);

            // Check every intersecting segment, including both sides of a bend. Cross's canonical
            // outgoing segment cannot represent all contact angles. Read the other roads from this
            // same data-layer transaction, before replacing nodes or changing Cross/LN markers.
            for (var i = 1; i < nodes.Length; i++)
                foreach (var line in otherLines)
                    for (var j = 1; j < line.Nodes.Length; j++)
                        if (LineGeometry.TryIntersection(nodes[i - 1], nodes[i], line.Nodes[j - 1], line.Nodes[j], out var position) &&
                            !LineGeometry.IsPerpendicular(nodes[i - 1], nodes[i], line.Nodes[j - 1], line.Nodes[j]))
                            return Failure("NonOrthogonalIntersection", $"当前线 {request.LineCode} 第 {i} 段与线 {line.Code} 第 {j} 段在 ({position.X}, {position.Y}) 斜交，无法保存。不同线路相交必须为 90°：横线与竖线，或两条相反斜率的 45° 斜线。请调整后再按 F6。");
            return null;
    }

    private static ServiceResult<LineSnapshot> ValidateAndSave(LineStoreState state, SaveLineRequest request, LineNode[]? nodes)
    {
            if (ValidateShape(state, request, nodes) is { } failure) return failure;
            var divided=LineSubdivision.Divide(state,nodes!);
            if(!divided.Success)return Failure(divided.Code,divided.Message);
            var added=divided.Data!.Length-nodes!.Length;nodes=divided.Data;
            var original = state.Lines[request.LineCode];
            if (original.Nodes.SequenceEqual(nodes!))
                return ServiceResult<LineSnapshot>.Ok(Copy(original), $"线 {original.Code} 保存成功，节点未发生变化。");
            var saved = original with { Nodes = nodes!, Version = original.Version + 1 };
            // A single replacement is the commit point: no failure above changes stored geometry.
            state.Lines[saved.Code] = saved;
            LogicalNodePositions.RebuildRoad(state, saved.Code);
            state.DirtyLines.Add(saved.Code);
            state.PendingSegments.RemoveWhere(segment => segment.LineCode == saved.Code);
            for (var i = 0; i < nodes!.Length - 1; i++) state.PendingSegments.Add((saved.Code, i));
            CrossTopology.MarkChanged(state,new HashSet<string>(StringComparer.Ordinal){saved.Code});
            return ServiceResult<LineSnapshot>.Ok(Copy(saved), $"线 {saved.Code} 保存成功，共 {saved.Nodes.Length} 个节点。"+
                (added>0?$"超过 500 的线段已自动细分，新增 {added} 个整数节点。":"")+"请按 K 计算交点。");
    }

    public ServiceResult<bool> Delete(DeleteLineRequest request) => repository.Transaction(state =>
    {
        var failure = CheckAccess(state, request.LineCode, request.Owner, out var line);
        if (failure is not null) return ServiceResult<bool>.Fail(failure.Code, failure.Message);
        if (line!.Version != request.Version) return ServiceResult<bool>.Fail("VersionConflict", "线路已经发生变化，请刷新后再删除。");
        state.Lines.Remove(line.Code);
        LogicalNodePositions.RebuildRoad(state, line.Code);
        state.DirtyLines.Remove(line.Code);
        state.PendingSegments.RemoveWhere(segment => segment.LineCode == line.Code);
        foreach (var cross in state.Crosses.Values.Where(cross => cross.Key.RoadCode1 == line.Code || cross.Key.RoadCode2 == line.Code).ToArray())
        {
            state.Crosses.Remove(cross.Key);
            state.StaleCrosses.Remove(cross.Key);
        }
        return ServiceResult<bool>.Ok(true, $"已删除线 {line.Code}。");
    });

    internal static ServiceResult<LineSnapshot>? CheckAccess(LineStoreState state, string id, string owner, out LineSnapshot? line)
    {
        line = null;
        if (!LineCode.IsValid(id)) return Failure("InvalidLineCode", "线路编码必须是 10 位大写英文字母。");
        if (!state.Lines.TryGetValue(id, out line)) return Failure("LineNotFound", $"线 {id} 不存在。");
        if (string.IsNullOrWhiteSpace(owner) || line.Owner != owner) return Failure("NotOwner", "当前用户不能修改这条线路。");
        if (line.IsLocked) return Failure("LineIsLocked", "线路已锁定，不能修改。");
        return null;
    }

    private static LineSnapshot Copy(LineSnapshot line) => line with { Nodes = line.Nodes.ToArray() };
    private static ServiceResult<LineSnapshot> Failure(string code, string message) => ServiceResult<LineSnapshot>.Fail(code, message);
}
