using System.Windows;
using ArgumentMap.Contracts;

namespace WpfArgumentMap;

internal enum LogicalNodeMode { Idle, Ready, Selecting }

internal sealed class LogicalNodeEditor
{
    private ILogicalNodeClient? client;
    private readonly List<LogicalNodeSnapshot> nodes = [];
    internal IReadOnlyList<LogicalNodeSnapshot> Nodes => nodes;
    internal LogicalNodeMode Mode { get; private set; }
    internal bool IsActive => Mode != LogicalNodeMode.Idle;
    internal bool IsBusy { get; private set; }
    internal bool IsCalculating { get; private set; }
    internal string CalculationMessage { get; private set; } = "";
    private CancellationTokenSource? calculationCancellation;
    private long contentRevision;
    internal string? SelectedCode { get; private set; }
    internal LogicalNodeSnapshot? Selected => nodes.Find(n => n.Code == SelectedCode);
    internal LogicalNodeSnapshot? Moving { get; private set; }
    internal string Message { get; private set; } = "F5 选择边长并落点，A 新增，7 选择，E 编辑，J 重算位置，F4 刷新。";
    internal void Configure(ILogicalNodeClient? value) { CancelCalculation(); client = value; }
    internal void CancelCalculation() => calculationCancellation?.Cancel();
    internal void ReplaceNodes(LogicalNodeSnapshot[] values)
    {contentRevision++;nodes.Clear();nodes.AddRange(values);SelectedCode=null;}
    internal void Activate() { if (IsBusy) return; Moving = null; Mode = LogicalNodeMode.Ready; SelectedCode = null; Message = "LN 模式：A 新增正方形，7 选择节点，E 编辑，J 重算全部线段位置。"; }
    internal void Leave() { if (IsBusy) return; Moving = null; Mode = LogicalNodeMode.Idle; SelectedCode = null; }
    internal void Notify(string message) => Message = message;
    internal void ToggleSelection() { if (IsBusy) return; Moving = null; Mode = Mode == LogicalNodeMode.Selecting ? LogicalNodeMode.Ready : LogicalNodeMode.Selecting; SelectedCode = null; }
    internal bool BeginMove(LogicalNodeSnapshot node)
    {
        if (IsBusy) return false;
        if (client is not ILogicalNodeMoveClient) { Message = "当前连接不支持移动逻辑节点。"; return false; }
        if (node.CanEdit == false) { Message = "没有移动该逻辑节点的权限。"; return false; }
        // The properties window loads a fresh authenticated snapshot. Another user may
        // have changed this node since the map was last refreshed.
        contentRevision++;nodes.RemoveAll(n => n.Code == node.Code); nodes.Add(node);
        Moving = node; SelectedCode = node.Code; Mode = LogicalNodeMode.Ready;
        Message = $"移动 LN {node.Code}：鼠标预览，左键 / A / Enter 校验并落点，Esc 取消；边长不变。";
        return true;
    }
    internal bool CancelMove()
    {
        if (IsBusy || Moving is null) return false;
        Moving = null; Mode = LogicalNodeMode.Selecting; Message = "已取消移动，原位置保持不变。"; return true;
    }
    internal async Task<bool> MoveAsync(decimal x, decimal y)
    {
        if (IsBusy || Moving is not { } source || client is not ILogicalNodeMoveClient mover) return false;
        IsBusy = true; Message = "正在校验并移动逻辑节点…";
        contentRevision++;
        try
        {
            var result = await mover.MoveLogicalNodeAsync(source.Code, source.Version, x, y);
            Message = result.Message;
            if (!result.Success || result.Data is null) return false;
            nodes.RemoveAll(n => n.Code == source.Code); nodes.Add(result.Data);
            Moving = null; SelectedCode = source.Code; Mode = LogicalNodeMode.Selecting;
            return true;
        }
        finally { IsBusy = false; }
    }
    internal static Point? Candidate(Point? pointer) => pointer is { } p && double.IsFinite(p.X) && double.IsFinite(p.Y) ?
        new Point(Math.Floor(p.X) + 0.5, Math.Floor(p.Y) + 0.5) : null;
    internal void Select(string code)
    {
        if(IsBusy || Moving is not null || nodes.All(n=>n.Code!=code))return;
        Mode=LogicalNodeMode.Selecting;SelectedCode=code;Message="已选择逻辑节点，E 编辑，或双击打开编辑窗口。";
    }
    internal void SelectClosest(Point? pointer, double tolerance,string? labelCode=null)
    {
        if (Mode != LogicalNodeMode.Selecting || IsBusy || pointer is null) return;
        if(labelCode is not null && nodes.Any(n=>n.Code==labelCode)){SelectedCode=labelCode;return;}
        SelectedCode = nodes.Where(n =>
            pointer.Value.X >= (double)n.Left - tolerance && pointer.Value.X <= (double)n.Right + tolerance &&
            pointer.Value.Y >= (double)n.Bottom - tolerance && pointer.Value.Y <= (double)n.Top + tolerance)
            .OrderBy(n => (new Point((double)n.X,(double)n.Y) - pointer.Value).LengthSquared).ThenBy(n => n.Code).FirstOrDefault()?.Code;
    }
    internal async Task<bool> RefreshAsync()
    {
        if (IsBusy) return false;
        if (client is null) return true;
        IsBusy = true;
        contentRevision++;
        try
        {
            var result = await client.GetLogicalNodesAsync();
            if (!result.Success || result.Data is null) { Message = result.Message; return false; }
            nodes.Clear(); nodes.AddRange(result.Data);
            Message = result.Message;
            if (Moving is not null) { Moving = null; Mode = LogicalNodeMode.Selecting; Message += " 已取消移动，请重新打开节点后移动。"; }
            if (Selected is null) SelectedCode = null;
            return true;
        }
        finally { IsBusy = false; }
    }
    internal async Task<bool> CreateAsync(decimal x,decimal y,int size,LogicalNodeProperties? properties=null)
    {
        if (IsBusy || Moving is not null) return false;
        if (client is null) { Message = "未连接服务，无法新增逻辑节点。"; return false; }
        IsBusy = true;
        contentRevision++;
        Message = "正在校验并保存逻辑节点…";
        try
        {
            var value = size == 18 ? "开始" : size==4?LogicalNodeBranchRules.False:size==8?LogicalNodeRelationRules.Equality:"逻辑节点";
            var result = await client.CreateLogicalNodeAsync(x,y,size,properties??new(){Value=value});
            Message = result.Message;
            if (!result.Success || result.Data is null) return false;
            // Only show a committed server response, including its calculated positions.
            nodes.RemoveAll(n=>n.Code==result.Data.Code);
            nodes.Add(result.Data);
            SelectedCode = result.Data.Code;
            Mode = LogicalNodeMode.Ready;
            return true;
        }
        finally { IsBusy = false; }
    }
    internal async Task<bool> CalculateAsync(Action progress, Func<bool>? foregroundBusy = null)
    {
        if (IsBusy || IsCalculating || client is null) return false;
        var activeClient=client;
        using var cancellation=new CancellationTokenSource();calculationCancellation=cancellation;
        var token=cancellation.Token;
        IsCalculating=true;CalculationMessage="J 后台计算中，可继续操作 LN 和线路。";
        try
        {
            string? cursor=null;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                while(IsBusy||foregroundBusy?.Invoke()==true)await Task.Delay(25,token);
                var step = activeClient is ILogicalNodeStepClient incremental ?
                    await incremental.CalculateLogicalNodeStepAsync(cursor) : await activeClient.CalculateLogicalNodeAsync();
                token.ThrowIfCancellationRequested();
                CalculationMessage=step.Success?step.Message:"J 已停止："+step.Message;progress();
                if (!step.Success || step.Data is null) return false;
                if (!step.Data.CanContinue) break;
                if(activeClient is ILogicalNodeStepClient && (step.Data.NextCode is not {} next ||
                    cursor is not null&&StringComparer.Ordinal.Compare(next,cursor)<=0))
                {CalculationMessage="J 已停止：Server 未返回有效的下一节点，请更新 Server 后重试。";return false;}
                cursor=step.Data.NextCode;
                await Task.Delay(10,token);
            }
            while(IsBusy||foregroundBusy?.Invoke()==true)await Task.Delay(25,token);
            var revision=contentRevision;
            var result = await activeClient.GetLogicalNodesAsync();
            token.ThrowIfCancellationRequested();
            if (!result.Success || result.Data is null)
            { CalculationMessage = $"逻辑节点位置已计算并提交，但刷新失败：{result.Message} 请按 F4 刷新。"; return true; }
            if(revision==contentRevision&&!IsBusy&&Moving is null)
            {
                contentRevision++;nodes.Clear();nodes.AddRange(result.Data);
                if (Selected is null) SelectedCode = null;
            }
            return true;
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested)
        {CalculationMessage="J 后台计算已停止；已提交的结果保留，下次 J 按最新数据重新检查。";return false;}
        finally { IsCalculating=false;calculationCancellation=null; }
    }
}
