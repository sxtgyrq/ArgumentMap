using ArgumentMap.Rules;
using System.Collections.ObjectModel;
using System.Windows;
using ArgumentMap.Contracts;
using LineGeometry = ArgumentMap.Rules.LineGeometry;

namespace WpfArgumentMap;

internal readonly record struct GridPoint(int X, int Y)
{
    internal Point ToPoint() => new(X, Y);
}

internal static class EightDirectionSnap
{
    internal const int Limit = 500_000;
    private static readonly (int X, int Y)[] Directions =
        [(1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1), (0, -1), (1, -1)];

    internal static bool IsInside(Point point) => double.IsFinite(point.X) && double.IsFinite(point.Y) &&
        point.X >= 0 && point.X <= Limit && point.Y >= 0 && point.Y <= Limit;

    internal static GridPoint Round(Point point) => new(
        (int)Math.Clamp(Math.Round(point.X, MidpointRounding.AwayFromZero), 0, Limit),
        (int)Math.Clamp(Math.Round(point.Y, MidpointRounding.AwayFromZero), 0, Limit));

    internal static bool IsValidSegment(GridPoint start, GridPoint end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        return start != end && (dx == 0 || dy == 0 || Math.Abs(dx) == Math.Abs(dy));
    }

    internal static GridPoint Snap(GridPoint start, Point cursor)
    {
        var best = start;
        var bestDistance = double.PositiveInfinity;
        foreach (var (dx, dy) in Directions)
        {
            var projectedLength = ((cursor.X - start.X) * dx + (cursor.Y - start.Y) * dy) / (dx * dx + dy * dy);
            // Clamp the ray length, rather than X/Y independently, to preserve diagonal directions at edges.
            var maxLength = Math.Min(dx == 0 ? Limit : dx > 0 ? Limit - start.X : start.X,
                dy == 0 ? Limit : dy > 0 ? Limit - start.Y : start.Y);
            var length = (int)Math.Clamp(Math.Round(projectedLength, MidpointRounding.AwayFromZero), 0, maxLength);
            var candidate = new GridPoint(start.X + dx * length, start.Y + dy * length);
            var distance = Math.Pow(candidate.X - cursor.X, 2) + Math.Pow(candidate.Y - cursor.Y, 2);
            if (distance < bestDistance)
            {
                best = candidate;
                bestDistance = distance;
            }
        }
        return best;
    }
}

internal enum LineEditorMode { Idle, Ready, Drawing, Selecting }

internal sealed class MapLine(string id, IEnumerable<GridPoint> nodes, long version = 0, bool isLocked = false, LineProperties? properties = null, string owner = "")
{
    internal string Code { get; } = id;
    internal IReadOnlyList<GridPoint> Nodes { get; } = Array.AsReadOnly(nodes.ToArray());
    internal LineProperties Properties { get; } = properties ?? new();
    internal bool IsLocked { get; } = isLocked;
    internal string Owner { get; } = owner;
    internal string Name => string.IsNullOrEmpty(Properties.RoadName) ? $"线 {Code}" : $"线 {Code} · {Properties.RoadName}";
    internal long Version { get; } = version;
}

/// <summary>Committed lines and editing drafts are separate, so cancelling edits preserves the original.</summary>
internal sealed class LineEditor
{
    private readonly List<MapLine> lines = [];
    private readonly List<GridPoint> draft = [];
    private ILineClient client;
    private long draftVersion;
    private MapLine? mergeFirst;
    internal string? MergeFirstCode => mergeFirst?.Code;

    internal LineEditor(ILineClient? client = null)
    {
        this.client = client ?? new UnavailableLineClient();
        Lines = lines.AsReadOnly();
        DraftNodes = draft.AsReadOnly();
    }

    internal ReadOnlyCollection<MapLine> Lines { get; }
    internal ReadOnlyCollection<GridPoint> DraftNodes { get; }
    internal LineEditorMode Mode { get; private set; }
    internal string? DraftCode { get; private set; }
    internal string? SelectedCode { get; private set; }
    internal MapLine? Selected => lines.Find(line => line.Code == SelectedCode);
    internal bool IsBusy { get; private set; }
    internal bool AddFromStart { get; private set; }
    internal int? MovingNodeOrder { get; private set; }
    internal GridPoint? AdditionAnchor => draft.Count == 0 ? null : AddFromStart ? draft[0] : draft[^1];
    internal void ConfigureClient(ILineClient value) => client = value;
    internal string Message { get; private set; } = "3 新建线 → 4 开始绘制 → 移动鼠标，A 加点 → F6 校验保存。";
    internal string ModeText => Mode switch
    {
        LineEditorMode.Ready => $"线 {DraftCode} 待绘制",
        LineEditorMode.Drawing => MovingNodeOrder is int order ? $"线 {DraftCode} · 节点 {order} 移动中 · E / Enter 确认，Esc 取消" : $"线 {DraftCode} 绘制中 · {(AddFromStart ? "前端" : "末端")}加点 · {draft.Count} 个节点",
        LineEditorMode.Selecting => Selected is { } line ? $"已选中{line.Name} · {(line.IsLocked ? "已锁定" : "未锁定")} · 负责人 {line.Owner}" : "选择线：移动鼠标靠近线条",
        _ => "浏览"
    };

    internal async Task<bool> NewLineAsync()
    {
        if (IsBusy || HasDraft()) return false;
        IsBusy = true;
        Message = "正在新建线路……";
        try
        {
            var result = await client.CreateAsync();
            if (!Accept(result)) return false;
            LoadDraft(result.Data!);
            SelectedCode = null;
            Mode = LineEditorMode.Ready;
            Message = $"已新建线 {DraftCode}，按 4 开始绘制。";
            return true;
        }
        finally { IsBusy = false; }
    }

    internal async Task<bool> BeginDrawingAsync()
    {
        if (IsBusy) return false;
        if (Mode != LineEditorMode.Ready)
        {
            Message = Mode == LineEditorMode.Drawing ? "正在绘制，移动鼠标后按 A 加点。" : "先按 3 新建线，或按 F8 选择线、F9 编辑。";
            return false;
        }
        return await StartDrawingAsync(DraftCode!);
    }

    internal GridPoint? Candidate(Point? cursor)
    {
        if (Mode != LineEditorMode.Drawing || MovingNodeOrder.HasValue || !cursor.HasValue || !EightDirectionSnap.IsInside(cursor.Value)) return null;
        return AdditionAnchor is {} anchor ? EightDirectionSnap.Snap(anchor, cursor.Value) : EightDirectionSnap.Round(cursor.Value);
    }

    internal bool ToggleAdditionEnd()
    {
        if (IsBusy) return false;
        if (PendingNodeMove()) return false;
        if (Mode != LineEditorMode.Drawing) { Message = "先按 4 开始绘制，或 F8 选线、F9 编辑，再按 5 切换加点端。"; return false; }
        AddFromStart = !AddFromStart;
        Message = AddFromStart ? "前端加点：A 在第一个节点之前补点，可连续添加；5 切回末端，F6 校验保存。" : "末端加点：A 在最后一个节点之后补点；5 切回前端，F6 校验保存。";
        return true;
    }

    internal bool AddPoint(Point? cursor)
    {
        if (IsBusy) return false;
        if (PendingNodeMove()) return false;
        var candidate = Candidate(cursor);
        if (!candidate.HasValue)
        {
            Message = Mode != LineEditorMode.Drawing ? "先按 3 新建、4 开始绘制。" : "把鼠标移到坐标范围内，再按 A 加点。";
            return false;
        }
        if (candidate.Value == AdditionAnchor)
        {
            Message = "候选节点与上一节点重合，请移动鼠标。";
            return false;
        }
        if (AddFromStart) draft.Insert(0, candidate.Value); else draft.Add(candidate.Value);
        Message = $"已在{(AddFromStart ? "前端" : "末端")}添加节点 ({candidate.Value.X}, {candidate.Value.Y})，共 {draft.Count} 个节点。";
        return true;
    }

    internal bool InsertPoint(Point? cursor)
    {
        if (IsBusy) return false;
        if (PendingNodeMove()) return false;
        if (Mode != LineEditorMode.Drawing || draft.Count < 2 || !cursor.HasValue || !EightDirectionSnap.IsInside(cursor.Value))
        { Message = "绘制或编辑线路时，将鼠标移到插点位置，按 T 插入节点。"; return false; }
        var point = EightDirectionSnap.Round(cursor.Value);
        if (draft.Contains(point)) { Message = "插入位置与已有节点重复，请移动鼠标。"; return false; }
        var bestIndex = -1;
        var bestLengthChange = double.PositiveInfinity;
        // As in the old InsertIntoLine, choose the insertion producing the shortest polyline.
        // Restrict candidate connections to the logical map's eight directions.
        for (var i = 0; i <= draft.Count; i++)
        {
            if (i > 0 && !EightDirectionSnap.IsValidSegment(draft[i - 1], point) ||
                i < draft.Count && !EightDirectionSnap.IsValidSegment(point, draft[i])) continue;
            var change = (i > 0 ? (point.ToPoint() - draft[i - 1].ToPoint()).Length : 0) +
                (i < draft.Count ? (draft[i].ToPoint() - point.ToPoint()).Length : 0) -
                (i > 0 && i < draft.Count ? (draft[i].ToPoint() - draft[i - 1].ToPoint()).Length : 0);
            if (change < bestLengthChange) { bestLengthChange = change; bestIndex = i; }
        }
        if (bestIndex < 0) { Message = "插入后相邻线段不符合八方向约束，无法插入这个节点。"; return false; }
        draft.Insert(bestIndex, point);
        Message = $"已插入节点 ({point.X}, {point.Y})，序号 {bestIndex}；F6 保存后按 K 重新计算交点。";
        return true;
    }

    internal async Task<bool> CompleteAsync()
    {
        if (IsBusy) return false;
        if (PendingNodeMove()) return false;
        if (Mode != LineEditorMode.Drawing || draft.Count < 2)
        {
            Message = "至少添加两个不同节点后，按 F6 校验保存线条。";
            return false;
        }
        // Rebuild node orders from the current list, just before HTTP submission.
        var nodes = draft.Select((node, index) => new LineNode(DraftCode!, index, node.X, node.Y)).ToArray();
        if (LineGeometry.FindAcuteCorner(nodes) is int corner)
        {
            Message = $"第 {corner + 1} 个节点（序号 {corner}，坐标 {nodes[corner].X}, {nodes[corner].Y}）形成 45° 锐角，无法保存。请调整节点，允许 90° 直角、135° 钝角或直线延伸。";
            return false;
        }
        IsBusy = true;
        Message = "正在校验并保存线路……";
        try
        {
            var result = await client.SaveAsync(DraftCode!, draftVersion, nodes);
            if (!Accept(result)) return false;
            ApplySnapshot(result.Data!);
            ClearDraft();
            SelectedCode = null;
            Mode = LineEditorMode.Idle;
            // Commit already succeeded; a refresh failure must not suggest resubmitting this draft.
            var savedMessage = result.Message;
            var refreshed = await client.GetAllAsync();
            if (refreshed.Success && refreshed.Data is not null) ReplaceLines(refreshed.Data);
            Message = refreshed.Success ? savedMessage : $"{savedMessage} 刷新失败，请按 F2 重试。";
            return true;
        }
        finally { IsBusy = false; }
    }

    internal void Cancel()
    {
        if (IsBusy) return;
        ClearDraft();
        SelectedCode = null;
        Mode = LineEditorMode.Idle;
        Message = "已取消当前操作。";
    }

    internal bool ToggleSelection()
    {
        if (IsBusy || HasDraft()) return false;
        Mode = Mode == LineEditorMode.Selecting ? LineEditorMode.Idle : LineEditorMode.Selecting;
        SelectedCode = null;
        mergeFirst = null;
        Message = Mode == LineEditorMode.Selecting ? "移动鼠标靠近线条，按 F9 编辑；R 删除选中的线。" : "已退出选择模式。";
        return true;
    }

    internal SplitLineRequest? SplitSelection(Point? cursor)
    {
        if(IsBusy||Mode!=LineEditorMode.Selecting||Selected is not {} line||cursor is null)
        {Message="先按 F8 选择线路，将鼠标移到要断开的节点，再按 D。";return null;}
        var cut=Enumerable.Range(0,line.Nodes.Count).OrderBy(i=>(line.Nodes[i].ToPoint()-cursor.Value).LengthSquared).First();
        if(cut<2||cut>line.Nodes.Count-2){Message="拆分后两条线路至少各有两个节点，请选择靠中间的节点。";return null;}
        return new(line.Code,line.Owner,line.Version,cut);
    }
    internal async Task<bool> SplitAsync(SplitLineRequest request)
    {
        if(IsBusy||HasDraft())return false;
        if(client is not ILineTopologyClient topology){Message="当前服务不支持拆分线路。";return false;}
        IsBusy=true;
        try
        {
            var result=await topology.SplitLineAsync(request.LineCode,request.Version,request.CutBeforeOrder);
            if(!Accept(result))return false;
            foreach(var line in result.Data!.Lines)ApplySnapshot(line);mergeFirst=null;SelectedCode=request.LineCode;Mode=LineEditorMode.Selecting;return true;
        }
        finally{IsBusy=false;}
    }
    internal async Task<ServiceResult<MergePreview>?> GetMergePlansAsync()
    {
        if(IsBusy||HasDraft())return ServiceResult<MergePreview>.Fail("EditorBusy",Message);
        if(Mode!=LineEditorMode.Selecting||Selected is not {} selected||client is not ILineTopologyClient topology)
        {Message="先按 F8 选择第一条线路，再按 B；然后选择第二条线路，再按 B。";return ServiceResult<MergePreview>.Fail("NoSelection",Message);}
        if(mergeFirst is null){mergeFirst=selected;Message=$"第一条线路 {selected.Code} 已记住；选择第二条线路后按 B 查看方案。";return null;}
        if(mergeFirst.Code==selected.Code){Message="请移动鼠标选择另一条线路，再按 B。";return ServiceResult<MergePreview>.Fail("SameLine",Message);}
        IsBusy=true;
        try
        {
            Message="正在计算合法合并方案…";
            var result=await topology.GetMergePlansAsync(mergeFirst.Code,mergeFirst.Version,selected.Code,selected.Version);Message=result.Message;
            if(!result.Success&&result.Code is "VersionConflict" or "LineNotFound")mergeFirst=null;
            return result;
        }
        finally{IsBusy=false;}
    }
    internal async Task<bool> ApplyMergeAsync(MergePreview preview,MergePlan plan)
    {
        if(IsBusy||HasDraft())return false;
        if(client is not ILineTopologyClient topology){Message="当前服务不支持合并线路。";return false;}
        IsBusy=true;
        try
        {
            var result=await topology.ApplyMergePlanAsync(preview,plan);if(!Accept(result))return false;
            lines.RemoveAll(l=>result.Data!.RemovedCodes.Contains(l.Code));foreach(var line in result.Data!.Lines)ApplySnapshot(line);
            mergeFirst=null;SelectedCode=preview.First.Code;Mode=LineEditorMode.Selecting;return true;
        }
        finally{IsBusy=false;}
    }
    internal void CancelMergeChoice()=>mergeFirst=null;

    internal void SelectClosest(Point? cursor, double tolerance)
    {
        if (IsBusy || Mode != LineEditorMode.Selecting) return;
        SelectedCode = null;
        if (!cursor.HasValue) return;
        var bestDistance = tolerance * tolerance;
        foreach (var line in lines)
            for (var i = 1; i < line.Nodes.Count; i++)
            {
                var a = line.Nodes[i - 1].ToPoint();
                var segment = line.Nodes[i].ToPoint() - a;
                var offset = cursor.Value - a;
                var fraction = Math.Clamp(Vector.Multiply(offset, segment) / segment.LengthSquared, 0, 1);
                var distance = (cursor.Value - (a + segment * fraction)).LengthSquared;
                if (distance <= bestDistance)
                {
                    SelectedCode = line.Code;
                    bestDistance = distance;
                }
            }
    }

    internal async Task<bool> EditSelectedAsync()
    {
        if (IsBusy || HasDraft()) return false;
        var selected = lines.Find(line => line.Code == SelectedCode);
        if (selected is null)
        {
            Message = "按 F8 进入选择模式，移动鼠标靠近线条，再按 F9 编辑。";
            return false;
        }
        return await StartDrawingAsync(selected.Code);
    }

    internal async Task<LineSnapshot?> GetSelectedDetailAsync()
    {
        if (IsBusy) return null;
        if (Mode != LineEditorMode.Selecting || Selected is not { } selected || client is not ILinePropertiesClient details)
        { Message = "先按 F8 选择线路，再按 E 打开属性。"; return null; }
        IsBusy = true;
        try
        {
            var result = await details.GetLineDetailAsync(selected.Code);
            if (!Accept(result)) return null;
            ApplySnapshot(result.Data!);
            return result.Data;
        }
        finally { IsBusy = false; }
    }

    internal async Task<bool> ReverseDirectionAsync()
    {
        if (IsBusy) return false;
        if (PendingNodeMove()) return false;
        if (Mode == LineEditorMode.Drawing)
        {
            if (draft.Count < 2) { Message = "至少两个节点后才能反转方向。"; return false; }
            draft.Reverse();
            Message = "草稿方向已反转；按 F6 保存后，再按 K 重算交点。";
            return true;
        }
        if (Mode != LineEditorMode.Selecting || Selected is not { } selected || client is not ILineDirectionClient directionClient)
        { Message = "先按 F8 选择线路，再按 C 反转方向；绘制时可直接反转草稿。"; return false; }
        IsBusy = true;
        try
        {
            var result = await directionClient.ReverseLineAsync(selected.Code, selected.Version);
            if (!Accept(result)) return false;
            ApplySnapshot(result.Data!);
            return true;
        }
        finally { IsBusy = false; }
    }

    internal async Task<bool> ToggleSelectedLockAsync()
    {
        if (IsBusy) return false;
        if (Mode != LineEditorMode.Selecting || Selected is not { } selected || client is not ILinePropertiesClient details)
        { Message = "先按 F8 选择线路，再按 L 锁定或解锁。"; return false; }
        IsBusy = true;
        try
        {
            var result = await details.ToggleLineLockAsync(selected.Code, selected.Version);
            if (!Accept(result)) return false;
            ApplySnapshot(result.Data!);
            return true;
        }
        finally { IsBusy = false; }
    }

    internal async Task<bool> MoveNodeAsync(Point? cursor)
    {
        if (IsBusy) return false;
        if (Mode != LineEditorMode.Drawing || draft.Count < 2) { Message = "先 F8 选线、F9 编辑；鼠标靠近节点按 E，移动鼠标后 E / Enter 确认，Esc 取消。至少需要两个节点。"; return false; }
        if (cursor is not {} target || !EightDirectionSnap.IsInside(target)) { Message = "把鼠标移到坐标范围内，再按 E 编辑节点。"; return false; }
        if (MovingNodeOrder is not int index)
        {
            MovingNodeOrder = Enumerable.Range(0,draft.Count).MinBy(i=>(draft[i].ToPoint()-target).LengthSquared);
            var node=draft[MovingNodeOrder.Value];Message=$"已选节点 {MovingNodeOrder} ({node.X}, {node.Y})。移动鼠标设置目标，再 E / Enter 查找最近合法整数点；Esc 取消。";return true;
        }
        if (client is not ILineNodeEditClient nodeClient) { Message = "当前服务不支持编辑节点位置，请更新服务端。Esc 可取消。"; return false; }
        IsBusy=true;
        try
        {
            Message="正在查找满足线路、Cross、LN 规则的最近整数点……";
            var nodes=draft.Select((p,i)=>new LineNode(DraftCode!,i,p.X,p.Y)).ToArray();
            var result=await nodeClient.FindNodePositionAsync(DraftCode!,draftVersion,nodes,index,target.X,target.Y);
            if(!Accept(result))return false;
            var p=result.Data!;var proposed=new GridPoint(p.X,p.Y);
            if(p.NodeOrder!=index||!EightDirectionSnap.IsInside(proposed.ToPoint())||
                index>0&&!EightDirectionSnap.IsValidSegment(draft[index-1],proposed)||index+1<draft.Count&&!EightDirectionSnap.IsValidSegment(proposed,draft[index+1])||
                draft.Where((_,i)=>i!=index).Contains(proposed))
            {Message="服务返回的候选节点不合法，草稿保持不变。";return false;}
            var unchanged=draft[index]==proposed;draft[index]=proposed;MovingNodeOrder=null;
            Message=unchanged?$"最接近目标的合法位置仍为原节点 ({p.X}, {p.Y})，未移动。可按 E 重新选择目标。":result.Message;return true;
        }
        finally{IsBusy=false;}
    }

    internal bool CancelNodeMove()
    {
        if(IsBusy||MovingNodeOrder is null)return false;
        MovingNodeOrder=null;Message="已取消本次节点移动，线路草稿保持不变。";return true;
    }
    private bool PendingNodeMove()
    {
        if(MovingNodeOrder is null)return false;
        Message="请先按 E / Enter 确认节点位置，或 Esc 取消本次移动。";return true;
    }

    internal async Task<bool> RemoveAsync(Point? cursor,bool nodesOnly=false)
    {
        if (IsBusy) return false;
        if (PendingNodeMove()) return false;
        if (Mode == LineEditorMode.Selecting && !nodesOnly)
        {
            var selected = lines.Find(line => line.Code == SelectedCode);
            if (selected is null) { Message = "先将鼠标移到要删除的线上。"; return false; }
            IsBusy = true;
            Message = "正在删除线路……";
            try
            {
                var result = await client.DeleteAsync(selected.Code, selected.Version);
                if (!Accept(result)) return false;
                lines.Remove(selected);
                SelectedCode = null;
                Message = result.Message;
                return true;
            }
            finally { IsBusy = false; }
        }
        if (Mode != LineEditorMode.Drawing || draft.Count == 0 || !cursor.HasValue || !EightDirectionSnap.IsInside(cursor.Value))
        {
            Message = "绘制时将鼠标移到节点附近，按 Delete 或 R 删除。";
            return false;
        }
        var index = Enumerable.Range(0, draft.Count).MinBy(i => (draft[i].ToPoint() - cursor.Value).LengthSquared);
        if (index > 0 && index < draft.Count - 1 && !EightDirectionSnap.IsValidSegment(draft[index - 1], draft[index + 1]))
        {
            Message = "删除后相邻线段不符合八方向约束，无法删除这个节点。";
            return false;
        }
        draft.RemoveAt(index);
        Message = $"已删除节点，剩余 {draft.Count} 个节点。";
        return true;
    }

    internal async Task<bool> RefreshLinesAsync()
    {
        if (IsBusy) return false;
        IsBusy = true;
        try
        {
            var result = await client.GetAllAsync();
            if (!Accept(result)) return false;
            ReplaceLines(result.Data!);
            return true;
        }
        finally { IsBusy = false; }
    }

    private async Task<bool> StartDrawingAsync(string id)
    {
        IsBusy = true;
        Message = "正在获取线路编辑数据……";
        try
        {
            var result = await client.StartDrawingAsync(id);
            if (!Accept(result)) return false;
            LoadDraft(result.Data!);
            ApplySnapshot(result.Data!);
            SelectedCode = null;
            Mode = LineEditorMode.Drawing;
            Message = $"正在编辑线 {id}：A 加点，5 切换前端／末端，R 删点，E 改点，F6 校验保存，Backspace 取消。";
            return true;
        }
        finally { IsBusy = false; }
    }

    private bool Accept<T>(ServiceResult<T> result)
    {
        Message = result.Message;
        return result.Success && result.Data is not null;
    }

    private void LoadDraft(LineSnapshot line)
    {
        MovingNodeOrder = null;
        AddFromStart = false;
        DraftCode = line.Code;
        draftVersion = line.Version;
        draft.Clear();
        draft.AddRange(line.Nodes.Select(node => new GridPoint(node.X, node.Y)));
    }

    private void ApplySnapshot(LineSnapshot line)
    {
        if (line.Nodes.Length < 2) return;
        var mapped = new MapLine(line.Code, line.Nodes.Select(node => new GridPoint(node.X, node.Y)), line.Version, line.IsLocked, line.Properties, line.Owner);
        var index = lines.FindIndex(item => item.Code == line.Code);
        if (index < 0) lines.Add(mapped);
        else lines[index] = mapped;
    }

    internal void ReplaceLines(IEnumerable<LineSnapshot> snapshots)
    {
        lines.Clear();
        foreach (var snapshot in snapshots) ApplySnapshot(snapshot);
    }

    internal void SelectLine(string code)
    {if(IsBusy||!lines.Any(l=>l.Code==code))return;ClearDraft();Mode=LineEditorMode.Selecting;SelectedCode=code;}

    private bool HasDraft()
    {
        if (Mode is not (LineEditorMode.Ready or LineEditorMode.Drawing)) return false;
        Message = "请先按 F6 完成当前线条，或按 Backspace 取消。";
        return true;
    }

    private void ClearDraft()
    {
        MovingNodeOrder = null;
        AddFromStart = false;
        draft.Clear();
        DraftCode = null;
        mergeFirst = null;
    }
}
