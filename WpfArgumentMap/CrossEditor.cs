using System.Windows;
using ArgumentMap.Contracts;

namespace WpfArgumentMap;

internal sealed class CrossEditor
{
    private ICrossClient? client;
    private readonly List<CrossSnapshot> crosses = [];
    internal IReadOnlyList<CrossSnapshot> Crosses => crosses;
    internal bool IsSelecting { get; private set; }
    internal bool IsBusy { get; private set; }
    internal bool IsCalculating {get;private set;}
    internal string CalculationMessage {get;private set;}="";
    private CancellationTokenSource? calculationCancellation;
    private long contentRevision;
    internal bool ShowConnected { get; private set; } = true;
    internal bool ShowDisconnected { get; private set; } = true;
    internal bool ShowUnset { get; private set; } = true;
    internal CrossKey? SelectedKey { get; private set; }
    internal string? ExitLineCode { get; private set; }
    internal CrossSnapshot? Selected => crosses.FirstOrDefault(cross => cross.Key == SelectedKey && IsVisible(cross.State));
    internal string Message { get; private set; } = "K 计算交点，U 选择交点，I 连通，O 不连通，P 重置；F8 选线、Enter 记住出口线、【 单向换路。";

    internal void ConfigureClient(ICrossClient? value) {CancelCalculation();client = value;}
    internal void CancelCalculation()=>calculationCancellation?.Cancel();
    internal bool IsVisible(CrossState state) => state switch
    {
        CrossState.Connected or CrossState.Road1ToRoad2 or CrossState.Road2ToRoad1 => ShowConnected,
        CrossState.Disconnected => ShowDisconnected,
        CrossState.Unset => ShowUnset,
        _ => false
    };
    internal void SetVisibility(bool connected,bool disconnected,bool unset)
    {
        ShowConnected=connected;ShowDisconnected=disconnected;ShowUnset=unset;
        if(Selected is null)SelectedKey=null;
    }
    internal void LeaveSelection() { IsSelecting = false; SelectedKey = null; }
    internal void Notify(string message) => Message = message;
    internal bool RememberExitLine(string? lineCode)
    {
        if (IsBusy) return false;
        if (!LineCode.IsValid(lineCode)) { Message = "先按 F8 选择一条线路，再按 Enter 记住出口线路。"; return false; }
        ExitLineCode = lineCode;
        Message = $"已记住出口线路 {lineCode}。按 U 选择交点，再按【设置该线路 → 另一条线路的单向换路。";
        return true;
    }
    internal void ToggleSelection()
    {
        if (IsBusy) return;
        IsSelecting = !IsSelecting;
        SelectedKey = null;
        Message = IsSelecting ? "移动鼠标靠近交点：I 双向连通，O 不连通，P 重置，【 单向换路；Tab 切换同位置的交点关系。" : "已退出交点选择模式。";
    }

    internal void SelectClosest(Point? cursor, double tolerance)
    {
        if (!IsSelecting || IsBusy) return;
        var nearby = Nearby(cursor, tolerance);
        var nearest = nearby.FirstOrDefault();
        // Preserve a Tab-selected pair when three or more lines cross at the same coordinate.
        if (nearest is not null && Selected is { } selected && selected.Key.Position == nearest.Key.Position) return;
        SelectedKey = nearest?.Key;
    }

    internal void CycleSelection(Point? cursor, double tolerance)
    {
        if (!IsSelecting || IsBusy || Selected is not { } selected) return;
        // A menu action retains the selected relation after the pointer has left the map.
        var position = cursor ?? new Point(selected.Key.Position.X, selected.Key.Position.Y);
        var candidates = Nearby(position, tolerance).Where(cross => cross.Key.Position == selected.Key.Position).ToArray();
        if (candidates.Length < 2) return;
        var index = Array.FindIndex(candidates, cross => cross.Key == selected.Key);
        SelectedKey = candidates[(index + 1) % candidates.Length].Key;
    }

    private CrossSnapshot[] Nearby(Point? cursor, double tolerance) => !cursor.HasValue ? [] : crosses
        .Where(cross => IsVisible(cross.State) && (new Point(cross.Key.Position.X, cross.Key.Position.Y) - cursor.Value).LengthSquared <= tolerance * tolerance)
        .OrderBy(cross => (new Point(cross.Key.Position.X, cross.Key.Position.Y) - cursor.Value).LengthSquared).ThenBy(cross => cross.Key).ToArray();

    internal async Task<bool> RefreshAsync()
    {
        if (IsBusy) return false;
        if (client is null) { Message = "交点服务尚未连接。"; return false; }
        IsBusy = true;
        contentRevision++;
        try
        {
            var result = await client.GetCrossesAsync();
            Message = result.Message;
            if (!result.Success || result.Data is null) return false;
            crosses.Clear();
            crosses.AddRange(result.Data.Where(c=>c is not null&&c.Key.IsValid));
            if(crosses.Count!=result.Data.Length)Message="已排除无效或非整数 Cross，请更新 Server 后刷新地图。";
            if (Selected is null) SelectedKey = null;
            return true;
        }
        finally { IsBusy = false; }
    }
    internal void ReplaceCrosses(CrossSnapshot[] values)
    {contentRevision++;crosses.Clear();crosses.AddRange(values.Where(c=>c is not null&&c.Key.IsValid));SelectedKey=null;
        if(crosses.Count!=values.Length)Message="已排除无效或非整数 Cross，请更新 Server 后刷新地图。";}

    internal async Task<bool> CalculateAsync(Action progress,Func<bool>? foregroundBusy=null)
    {
        if (IsBusy||IsCalculating) return false;
        if (client is null) { Message = "交点服务尚未连接。"; return false; }
        var activeClient=client;
        using var cancellation=new CancellationTokenSource();calculationCancellation=cancellation;
        var token=cancellation.Token;
        IsCalculating = true;CalculationMessage="K 后台计算中，可继续操作 LN 和线路。";
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                while(IsBusy||foregroundBusy?.Invoke()==true)await Task.Delay(25,token);
                var result = await activeClient.CalculateCrossAsync();
                token.ThrowIfCancellationRequested();
                CalculationMessage = result.Success?result.Message:"K 已停止："+result.Message;
                progress();
                if (!result.Success || result.Data is null) return false;
                if (!result.Data.CanContinue) break;
                // Yield between transactions so WPF input and foreground saves can run,
                // even with an immediately completed local/test transport.
                await Task.Delay(10,token);
            }
            while(IsBusy||foregroundBusy?.Invoke()==true)await Task.Delay(25,token);
            var revision=contentRevision;
            var loaded = await activeClient.GetCrossesAsync();
            token.ThrowIfCancellationRequested();
            if (!loaded.Success || loaded.Data is null) { CalculationMessage = "K 已完成，但刷新交点失败："+loaded.Message; return false; }
            // A foreground save/import/move/refresh may have published newer Crosses
            // while this read was in flight. Never replace them with an older read.
            if(revision==contentRevision&&!IsBusy)
            {
                contentRevision++;crosses.Clear();crosses.AddRange(loaded.Data.Where(c=>c is not null&&c.Key.IsValid));
                if(crosses.Count!=loaded.Data.Length)CalculationMessage="K 刷新排除了无效或非整数 Cross，请更新 Server 后刷新地图。";
                if (Selected is null) SelectedKey = null;
            }
            return true;
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested)
        {CalculationMessage="K 后台计算已停止；已提交的结果保留，待计算标记可由下次 K 继续。";return false;}
        finally {IsCalculating=false;calculationCancellation=null;}
    }

    internal Task<bool> SetOneWayAsync(bool refreshAfterSave = true)
    {
        if (IsBusy) return Task.FromResult(false);
        if (!IsSelecting || Selected is not { } selected) { Message = "先按 U 选择交点，再按【设置单向换路。"; return Task.FromResult(false); }
        if (ExitLineCode is null) { Message = "尚未记住出口线路：先按 F8 选线，再按 Enter，然后 U 选交点、【设置单向换路。"; return Task.FromResult(false); }
        if (!selected.Key.Contains(ExitLineCode)) { Message = $"出口线路 {ExitLineCode} 不属于当前 Cross，请重新选择线路并按 Enter。"; return Task.FromResult(false); }
        return SetStateAsync(ExitLineCode == selected.Key.RoadCode1 ? CrossState.Road1ToRoad2 : CrossState.Road2ToRoad1, refreshAfterSave);
    }

    internal async Task<bool> SetStateAsync(CrossState state, bool refreshAfterSave = true)
    {
        if (IsBusy) return false;
        if (!IsSelecting || Selected is not { } selected || client is null)
        { Message = "先按 U 进入交点选择模式，将鼠标移到交点附近。"; return false; }
        IsBusy = true;
        contentRevision++;
        try
        {
            var result = await client.SetCrossStateAsync(selected.Key, selected.Version, state);
            Message = result.Message;
            if (!result.Success || result.Data is null) return false;
            if(!result.Data.Key.IsValid){Message="服务端返回了无效或非整数 Cross，请更新 Server 后刷新确认保存结果。";return false;}
            var index = crosses.FindIndex(cross => cross.Key == selected.Key);
            crosses[index] = result.Data;
            // Shift still commits through HTTP, as in the old project; only the full reload is skipped.
            if (refreshAfterSave)
            {
                var refreshed = await client.GetCrossesAsync();
                if (refreshed.Success && refreshed.Data is not null)
                {
                    crosses.Clear(); crosses.AddRange(refreshed.Data.Where(c=>c is not null&&c.Key.IsValid));
                    if(crosses.Count!=refreshed.Data.Length)Message="状态已保存，刷新排除了无效或非整数 Cross；请更新 Server 后刷新地图。";
                    if (Selected is null) SelectedKey = null;
                }
                else Message = result.Message + " 状态已保存，但刷新交点失败，请按 F3 重试。";
            }
            if(Selected is null)SelectedKey=null;
            return true;
        }
        finally { IsBusy = false; }
    }
}
