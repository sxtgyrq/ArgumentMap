using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ArgumentMap.Contracts;

namespace WpfArgumentMap;

public sealed class LogicalNodeWindowRequestEventArgs(Point center, LogicalNodeSnapshot? node, int sideLength = 6) : EventArgs
{
    public Point Center { get; } = center;
    public LogicalNodeSnapshot? Node { get; } = node;
    public int SideLength { get; } = sideLength;
}

/// <summary>Draws only visible grid lines; never allocates a 500000-pixel canvas.</summary>
public sealed partial class CoordinateMapView : FrameworkElement
{
    private const double RulerWidth = 72;
    private const double RulerHeight = 30;
    private readonly CoordinateViewport viewport = new();
    private readonly LineEditor editor = new();
    private readonly CrossEditor crossEditor = new();
    private readonly LogicalNodeEditor logicalNodes = new();
    private readonly MapAreaMoveEditor areaMove=new();
    private bool areaMoveAction;
    private Point? areaBoxStart,areaBoxEnd,areaAnchor;
    private bool logicalNodeAction;
    private bool crossAction;
    private readonly Typeface typeface = new("Segoe UI");
    private static readonly Brush OutsideBrush = MakeBrush("#E9EDF2");
    private static readonly Brush RulerBrush = MakeBrush("#F7F9FC");
    // 70% transparent background; keep the text itself fully readable.
    private static readonly Brush LogicalNodeLabelBackground = MakeBrush("#F7F9FC",0.3);
    private static readonly Brush TextBrush = MakeBrush("#596579");
    private static readonly Pen MinorPen = MakePen("#EDF1F6");
    private static readonly Pen MajorPen = MakePen("#D7DFE9");
    private static readonly Pen EdgePen = MakePen("#8B9DB3", 1.5);
    private static readonly Pen TickPen = MakePen("#9AA8BA");
    private static readonly Pen LinePen = MakePen("#3478C8", 2);
    private static readonly Pen LockedLinePen = MakePen("#238257", 3);
    private static readonly Pen SelectionHaloPen = MakePen("#F3BD65", 6);
    private static readonly Pen SelectedPen = MakePen("#DB8218", 3);
    private static readonly Pen DraftPen = MakePen("#228459", 2);
    private static readonly Pen PreviewPen = MakePreviewPen();
    private static readonly Pen UnsetCrossPen = MakePen("#D94A49", 2);
    private static readonly Pen ConnectedCrossPen = MakePen("#228459", 2);
    private static readonly Pen DisconnectedCrossPen = MakePen("#7C8798", 2);
    private static readonly Pen Road1ExitCrossPen = MakePen("#1E40AF", 2);
    private static readonly Pen Road2ExitCrossPen = MakePen("#38A6CF", 2);
    private static readonly Pen SelectedCrossPen = MakePen("#DFA51B", 2);
    private static readonly Pen LogicalNodePen = MakePen("#7C3AED", 1.5);
    private static readonly Pen PendingLogicalNodePen = MakePen("#B7791F", 1.5);
    private static readonly Brush LogicalNodeFill = MakeBrush("#167C3AED");
    private Point? dragPosition;
    private Point? lastPointer;
    private Point? lastLogicalNodePointer;
    private GridPoint[] mergePreview=[];
    private bool fitToWindow;

    public event EventHandler? ViewChanged;
    public event EventHandler? LinePropertiesRequested;
    public event EventHandler? SplitLineRequested;
    public event EventHandler? MergeLinesRequested;
    public event EventHandler<LogicalNodeWindowRequestEventArgs>? LogicalNodeWindowRequested;
    public event EventHandler? LogicalNodeSizeRequested;
    public int LogicalNodePlacementSize { get; private set; } = 6;
    private bool isPlacingLogicalNode;
    public bool IsPlacingLogicalNode { get=>isPlacingLogicalNode;private set{isPlacingLogicalNode=value;if(!value){pastedLogicalNodeProperties=null;instancePlacementProperties=null;}} }
    private LogicalNodeProperties? instancePlacementProperties;
    private string? instancePlacementParentCode;
    public LogicalNodeSnapshot[] InstanceParents=>logicalNodes.Nodes.Where(LogicalNodeInstanceRules.CanInstantiate).OrderBy(n=>n.Properties.Value,StringComparer.Ordinal).ToArray();
    public bool IsMovingLogicalNode => logicalNodes.Moving is not null;
    public bool IsAreaMoveMode=>areaMove.IsActive;
    public bool IsLogicalNodeMode => logicalNodes.IsActive;
    public int LogicalNodeCount => logicalNodes.Nodes.Count;
    public bool HasStartLogicalNode => logicalNodes.Nodes.Any(n => n.IsStart);
    public bool ShowArrows { get; private set; }
    public bool ShowLogicalNodeLabels { get; private set; } = true;
    public bool ShowLineSpeeds { get; private set; }
    public bool ShowConnectedCrosses => crossEditor.ShowConnected;
    public bool ShowDisconnectedCrosses => crossEditor.ShowDisconnected;
    public bool ShowUnsetCrosses => crossEditor.ShowUnset;
    private readonly List<Rect> speedLabelBounds=[];
    public bool SelectedLineIsLocked => editor.Selected?.IsLocked == true;
    public bool AddFromStart => editor.AddFromStart;
    public bool IsEditingLineNode => editor.MovingNodeOrder.HasValue;
    public Point Center => viewport.Center;
    public double Scale => viewport.Scale;
    public double GridStep => CoordinateViewport.GetGridStep(Scale);
    public Point? PointerPosition { get; private set; }
    public Point? CandidatePosition => editor.Candidate(PointerPosition)?.ToPoint();
    public int LineCount => editor.Lines.Count;
    public int CrossCount => crossEditor.Crosses.Count;
    public string? CrossExitLineCode => crossEditor.ExitLineCode;
    public string EditingStatus => IsConnectingNodes ? "LN 连接" : IsPastingArea ? "整组粘贴" : IsPastingLine ? "粘贴线路" : IsFlowDiagramImportActive ? "导入流程图" : IsAreaMoveMode ? "框选移动" : IsMovingLogicalNode ? "LN 移动模式" : logicalNodes.IsActive ? logicalNodes.Mode == LogicalNodeMode.Selecting ? "LN 选择模式" : "LN 模式" : crossEditor.IsSelecting ? "交点选择模式" : editor.ModeText;
    public string OperationMessage => IsConnectingNodes ? connectionMessage : flowImportAction || IsFlowDiagramImportActive ? flowImportMessage : areaMoveAction || IsAreaMoveMode ? areaMove.Message : logicalNodeAction || logicalNodes.IsActive ? logicalNodes.Message : crossAction ? crossEditor.Message : editor.Message;
    public bool IsSaving => connectionBusy || undoBusy || editor.IsBusy || crossEditor.IsBusy || logicalNodes.IsBusy || areaMove.IsBusy || IsFlowDiagramImportBusy || linePasteBusy || areaPasteBusy;
    public bool IsCalculatingCrosses=>crossEditor.IsCalculating;
    public string CrossCalculationMessage=>crossEditor.CalculationMessage;
    internal void StopCrossCalculation()=>crossEditor.CancelCalculation();
    public bool IsCalculatingLogicalNodes=>logicalNodes.IsCalculating;
    public string LogicalNodeCalculationMessage=>logicalNodes.CalculationMessage;
    internal void StopLogicalNodeCalculation()=>logicalNodes.CancelCalculation();
    private Rect Plot => new(RulerWidth, RulerHeight,
        Math.Max(0, ActualWidth - RulerWidth), Math.Max(0, ActualHeight - RulerHeight));

    public CoordinateMapView()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = Cursors.Cross;
    }

    public void ResetView()
    {
        fitToWindow = false;
        viewport.Reset();
        Refresh();
    }

    public void FitView()
    {
        fitToWindow = true;
        viewport.Reset(fit: true);
        Refresh();
    }

    public void ZoomAtCenter(double factor) => Zoom(factor,
        new Point(Plot.Width / 2, Plot.Height / 2));

    public void ConfigureClient(ILineClient client)
    {
        editor.ConfigureClient(client);
        crossEditor.ConfigureClient(client as ICrossClient);
        logicalNodes.Configure(client as ILogicalNodeClient);
        areaMove.Configure(client as IMapAreaMoveClient);
        linePasteClient=client as ILinePasteClient;
        areaPasteClient=client as IMapAreaPasteClient;
        undoClient=client as IMapUndoClient;
        connectionClient=client as ILogicalNodeConnectionClient;
    }
    public bool BeginAreaMove()
    {
        if(IsSaving)return false;
        if(IsConnectingNodes||IsFlowDiagramImportActive||IsPastingLine||IsPastingArea){PendingMapOperation();return false;}
        flowImportAction=false;
        areaMoveAction=true;
        if(editor.Mode is LineEditorMode.Ready or LineEditorMode.Drawing || IsMovingLogicalNode)
        {areaMove.Notify("请先保存或取消当前线路／LN 移动，再框选。");Refresh();return false;}
        editor.Cancel();logicalNodes.Leave();crossEditor.LeaveSelection();IsPlacingLogicalNode=false;
        areaBoxStart=areaBoxEnd=areaAnchor=null;ReleaseMouseCapture();var started=areaMove.Begin();Refresh();Focus();return started;
    }
    public async Task<bool> SelectMapAreaAsync(MapAreaBounds bounds,Point anchor)
    {
        if(IsSaving||!IsAreaMoveMode)return false;
        var task=areaMove.SelectAsync(bounds);Refresh();
        try{var selected=await task;areaAnchor=selected?anchor:null;return selected;}finally{Refresh();}
    }
    internal bool BeginAreaSelection(Point screen)
    {
        if(!IsAreaMoveMode||IsSaving||areaMove.Selection is not null||!Plot.Contains(screen))return false;
        Focus();lastPointer=screen;areaBoxStart=areaBoxEnd=AreaWorld(screen);CaptureMouse();Refresh();return true;
    }
    internal void UpdateAreaSelection(Point screen)
    {
        if(!areaBoxStart.HasValue)return;
        lastPointer=screen;areaBoxEnd=AreaWorld(screen);Refresh();
    }
    internal async Task<bool> CompleteAreaSelectionAsync(Point screen)
    {
        if(areaBoxStart is not {} start)return false;
        var end=AreaWorld(screen);areaBoxStart=areaBoxEnd=null;ReleaseMouseCapture();
        return await SelectMapAreaAsync(new((decimal)Math.Min(start.X,end.X),(decimal)Math.Min(start.Y,end.Y),
            (decimal)Math.Max(start.X,end.X),(decimal)Math.Max(start.Y,end.Y)),end);
    }
    public async Task<bool> CompleteAreaMoveAsync(Point? target=null)
    {
        if(IsSaving||areaMove.Selection is null||areaAnchor is null)return false;
        var delta=AreaDelta(target);var task=areaMove.MoveAsync(delta.X,delta.Y);Refresh();
        try
        {
            var result=await task;if(!result.Success||result.Data is null)return false;
            editor.ReplaceLines(result.Data.Lines);crossEditor.ReplaceCrosses(result.Data.Crosses);logicalNodes.ReplaceNodes(result.Data.LogicalNodes);
            areaAnchor=null;return true;
        }
        finally{Refresh();}
    }
    public async Task<bool> DeleteAreaSelectionAsync()
    {
        if(IsSaving)return false;
        if(!IsAreaMoveMode||areaMove.Selection is null){areaMoveAction=true;areaMove.Notify("请先按 F7 并完成框选，再按 R 删除。");Refresh();return false;}
        var task=areaMove.DeleteAsync();Refresh();
        try
        {
            var result=await task;if(!result.Success||result.Data is null)return false;
            editor.ReplaceLines(result.Data.Lines);crossEditor.ReplaceCrosses(result.Data.Crosses);logicalNodes.ReplaceNodes(result.Data.LogicalNodes);
            areaBoxStart=areaBoxEnd=areaAnchor=null;ReleaseMouseCapture();return true;
        }
        finally{Refresh();}
    }
    private (int X,int Y) AreaDelta(Point? target=null)
    {
        var from=areaAnchor??Center;var to=target??PointerPosition??lastLogicalNodePointer??from;
        return ((int)Math.Round(to.X-from.X,MidpointRounding.AwayFromZero),(int)Math.Round(to.Y-from.Y,MidpointRounding.AwayFromZero));
    }
    private bool PendingMapOperation()
    {
        if(IsConnectingNodes){connectionMessage="正在连接两个 LN：Enter / F6 确认预览，Esc 取消后再进行其他编辑。";Refresh();return true;}
        if(IsPastingArea){ClipboardNotice("正在整组粘贴：左键 / Enter 按新增保存，Esc 取消后再进行其他编辑。");return true;}
        if(IsPastingLine){ClipboardNotice("正在粘贴线路：左键校验并保存，Esc 取消后再进行其他编辑。");return true;}
        if(IsFlowDiagramImportActive){flowImportMessage="正在导入流程图，请在导入窗口选择插入点、校验预览或取消。";Refresh();return true;}
        flowImportAction=false;
        if(!IsAreaMoveMode){areaMoveAction=false;return false;}
        areaMove.Notify("正在框选移动：左键 / Enter 落点，R 删除原选中对象，F7 重新框选，Esc 取消。");Refresh();return true;
    }
    public bool LocateLogicalNode(string code,Point center,LogicalNodeSnapshot? refreshedNode=null)
    {
        if(IsSaving || IsPlacingLogicalNode || editor.Mode is LineEditorMode.Ready or LineEditorMode.Drawing ||
            PendingMapOperation() || PendingLogicalNodeMove())return false;
        if(!double.IsFinite(center.X)||!double.IsFinite(center.Y)||center.X<0||center.X>500000||center.Y<0||center.Y>500000)return false;
        if(refreshedNode is not null&&(refreshedNode.Code!=code||(double)refreshedNode.X!=center.X||(double)refreshedNode.Y!=center.Y))return false;
        editor.Cancel();crossEditor.LeaveSelection();crossAction=false;logicalNodeAction=true;
        if(refreshedNode is not null)logicalNodes.ReplaceNodes(logicalNodes.Nodes.Where(n=>n.Code!=code).Append(refreshedNode).ToArray());
        logicalNodes.Leave();logicalNodes.Select(code);
        fitToWindow=false;
        viewport.FitBounds(new Rect(center.X-40,center.Y-30,80,60));
        logicalNodes.Notify(FormattableString.Invariant($"已定位 LN {code}，中心坐标 X={center.X:0.0}, Y={center.Y:0.0}；按 E 返回原编辑窗口，未保存的内容已保留。"));
        Refresh();return true;
    }
    public bool FocusLogicalNodes(IEnumerable<string>? codes=null)
    {
        var selectedCodes=codes?.ToHashSet() ?? (logicalNodes.Selected is {} selected ? new HashSet<string>{selected.Code} : []);
        var nodes=logicalNodes.Nodes.Where(n=>selectedCodes.Contains(n.Code)).ToArray();
        if(nodes.Length==0){logicalNodes.Notify("先按 7 选择一个逻辑节点，再定位。F4 刷新节点。");logicalNodeAction=true;Refresh();return false;}
        fitToWindow=false;
        viewport.FitBounds(new Rect(new Point((double)nodes.Min(n=>n.Left)-18,(double)nodes.Min(n=>n.Bottom)-18),
            new Point((double)nodes.Max(n=>n.Right)+68,(double)nodes.Max(n=>n.Top)+20)));
        SetShowArrows(true);Refresh();return true;
    }
    public Task<bool> NewLineAsync() => RunCommandAsync(editor.NewLineAsync);
    public Task<bool> BeginDrawingAsync() => RunCommandAsync(editor.BeginDrawingAsync);
    public Task<bool> CompleteLineAsync() => IsConnectingNodes?SaveLogicalNodeConnectionAsync():RunCommandAsync(editor.CompleteAsync);
    public Task<bool> RefreshLinesAsync() => RunCommandAsync(editor.RefreshLinesAsync);
    public void CancelEditing()
    {
        if(IsConnectingNodes){CancelLogicalNodeConnection();return;}
        if (IsSaving) return;
        if(IsPastingArea){CancelAreaPaste();return;}
        if(IsPastingLine){CancelLinePaste();return;}
        if(IsFlowDiagramImportActive){CancelFlowDiagramInteraction();return;}
        if(IsAreaMoveMode){areaMove.Cancel();areaBoxStart=areaBoxEnd=areaAnchor=null;ReleaseMouseCapture();Refresh();return;}
        IsPlacingLogicalNode = false;
        logicalNodes.Leave(); logicalNodeAction = false;
        crossEditor.LeaveSelection();
        crossAction = false;
        editor.Cancel();
        Refresh();
    }
    public void ToggleLineSelection()
    {
        if (IsSaving || PendingMapOperation()) return;
        logicalNodes.Leave(); logicalNodeAction = false;
        crossEditor.LeaveSelection();
        crossAction = false;
        editor.ToggleSelection();
        Refresh();
    }
    public Task<bool> EditSelectedLineAsync() => RunCommandAsync(editor.EditSelectedAsync);
    public Task<bool> ToggleSelectedLineLockAsync() => RunCommandAsync(editor.ToggleSelectedLockAsync);
    public Task<bool> ReverseLineDirectionAsync() => RunCommandAsync(editor.ReverseDirectionAsync);
    public SplitLineRequest? GetSplitSelection()=>editor.SplitSelection(PointerPosition??lastLogicalNodePointer);
    public Task<bool> SplitLineAsync(SplitLineRequest request)=>RunCommandAsync(()=>editor.SplitAsync(request));
    public async Task<bool> EditLineNodeAsync()
    {
        if(IsSaving || PendingMapOperation())return false;
        logicalNodes.Leave();logicalNodeAction=false;crossEditor.LeaveSelection();crossAction=false;
        var task=editor.MoveNodeAsync(PointerPosition??lastLogicalNodePointer);Refresh();
        try{return await task;}finally{Refresh();}
    }
    public Task<bool> RemoveLineNodeAsync()=>RunCommandAsync(()=>editor.RemoveAsync(PointerPosition??lastLogicalNodePointer,nodesOnly:true));
    public async Task<ServiceResult<MergePreview>?> GetMergePlansAsync()
    {
        if(IsSaving || PendingMapOperation())return ServiceResult<MergePreview>.Fail("EditorBusy","操作尚未完成。");
        logicalNodes.Leave();logicalNodeAction=false;crossEditor.LeaveSelection();crossAction=false;
        var task=editor.GetMergePlansAsync();Refresh();try{return await task;}finally{Refresh();}
    }
    public Task<bool> ApplyMergePlanAsync(MergePreview preview,MergePlan plan)=>RunCommandAsync(()=>editor.ApplyMergeAsync(preview,plan));
    public void SetMergePreview(MergePreview? preview,MergePlan? plan)
    {
        mergePreview=[];
        if(preview is not null&&plan is not null)
        {
            mergePreview=preview.First.Nodes.Concat(preview.Second.Nodes).Select(n=>new GridPoint(n.X,n.Y)).ToArray();
            mergePreview[preview.First.Nodes.Length-1]=new(plan.FirstX,plan.FirstY);mergePreview[preview.First.Nodes.Length]=new(plan.SecondX,plan.SecondY);
        }
        Refresh();
    }
    public void CancelMergeChoice(){editor.CancelMergeChoice();SetMergePreview(null,null);}
    public void SetShowArrows(bool value) { if (ShowArrows == value) return; ShowArrows = value; Refresh(); }
    public void SetShowLogicalNodeLabels(bool value) { if(ShowLogicalNodeLabels==value)return;ShowLogicalNodeLabels=value;Refresh(); }
    public void SetShowLineSpeeds(bool value)
    {
        if(ShowLineSpeeds==value)return;
        ShowLineSpeeds=value;
        if(value)ShowArrows=true;
        Refresh();
    }
    public void SetCrossVisibility(bool connected,bool disconnected,bool unset)
    {
        if(ShowConnectedCrosses==connected && ShowDisconnectedCrosses==disconnected && ShowUnsetCrosses==unset)return;
        crossEditor.SetVisibility(connected,disconnected,unset);Refresh();
    }
    public void RequestLineProperties() { if (!IsSaving && !PendingMapOperation()) LinePropertiesRequested?.Invoke(this, EventArgs.Empty); }
    public async Task<LineSnapshot?> GetSelectedLineDetailAsync()
    {
        if (IsSaving || PendingMapOperation()) return null;
        crossAction = false;
        var task = editor.GetSelectedDetailAsync();
        Refresh();
        try { return await task; }
        finally { Refresh(); }
    }

    public void ToggleCrossSelection()
    {
        if (IsSaving || PendingMapOperation()) return;
        logicalNodes.Leave(); logicalNodeAction = false;
        crossAction = true;
        if (editor.Mode is LineEditorMode.Ready or LineEditorMode.Drawing)
        { crossEditor.Notify("请先完成或取消当前线路绘制，再选择交点。"); Refresh(); return; }
        editor.Cancel();
        crossEditor.ToggleSelection();
        Refresh();
    }

    public async Task<bool> RefreshCrossesAsync()
    {
        if (IsSaving || PendingMapOperation()) return false;
        BeginCrossAction();
        crossAction = true;
        var task = crossEditor.RefreshAsync();
        Refresh();
        try { return await task; }
        finally { Refresh(); }
    }

    public bool RememberCrossExitLine()
    {
        if (IsSaving || PendingMapOperation()) return false;
        BeginCrossAction();
        crossAction = true;
        var success = crossEditor.RememberExitLine(editor.Mode == LineEditorMode.Selecting ? editor.SelectedCode : null);
        Refresh();
        return success;
    }

    public async Task<bool> SetCrossOneWayAsync(bool refreshAfterSave = true)
    {
        if (IsSaving || PendingMapOperation()) return false;
        BeginCrossAction();
        crossAction = true;
        var task = crossEditor.SetOneWayAsync(refreshAfterSave);
        Refresh();
        try { return await task; }
        finally { Refresh(); }
    }

    public async Task<bool> SetCrossStateAsync(CrossState state, bool refreshAfterSave = true)
    {
        if (IsSaving || PendingMapOperation()) return false;
        BeginCrossAction();
        logicalNodeAction = false;
        crossAction = true;
        var task = crossEditor.SetStateAsync(state, refreshAfterSave);
        Refresh();
        try { return await task; }
        finally { Refresh(); }
    }

    public Task<bool> CalculateCrossesAsync()
    {
        if(!Dispatcher.CheckAccess())return Dispatcher.InvokeAsync(CalculateCrossesAsync).Task.Unwrap();
        if (IsCalculatingCrosses || IsSaving || PendingMapOperation()) return Task.FromResult(false);
        // A shortcut/test host can invoke a WPF control without installing a
        // DispatcherSynchronizationContext. Establish it before starting the loop,
        // so every progress/final refresh remains on this control's UI thread.
        if(SynchronizationContext.Current is not DispatcherSynchronizationContext)
            return Dispatcher.InvokeAsync(CalculateCrossesAsync).Task.Unwrap();
        return CalculateCrossesOnDispatcherAsync();
    }
    private async Task<bool> CalculateCrossesOnDispatcherAsync()
    {
        // K operates on saved server geometry. It never changes the current editor,
        // selection, preview or draft, including when another K is already running.
        var task = crossEditor.CalculateAsync(Refresh,()=>IsSaving);
        Refresh();
        try { return await task; }
        finally { Refresh(); }
    }

    private void BeginCrossAction()
    {
        logicalNodes.Leave(); logicalNodeAction = false;
    }

    public void ToggleLogicalNodeMode()
    {
        if (IsSaving || PendingMapOperation()) return;
        IsPlacingLogicalNode = false;
        logicalNodeAction = true;
        if (editor.Mode is LineEditorMode.Ready or LineEditorMode.Drawing)
        { logicalNodes.Notify("请先保存或取消当前线路绘制，再切换 LN 模式。"); Refresh(); return; }
        editor.Cancel(); crossEditor.LeaveSelection();
        if (logicalNodes.IsActive) logicalNodes.Leave(); else logicalNodes.Activate();
        Refresh();
    }
    public void ToggleLogicalNodeSelection()
    {
        if (IsSaving || PendingMapOperation() || PendingLogicalNodeMove()) return;
        IsPlacingLogicalNode = false;
        if (!logicalNodes.IsActive) ToggleLogicalNodeMode();
        if (!logicalNodes.IsActive) return;
        logicalNodes.ToggleSelection(); Refresh();
    }
    public void RequestLogicalNodeEdit()
    {
        if (IsSaving || PendingMapOperation() || PendingLogicalNodeMove()) return;
        logicalNodeAction = true;
        if (!logicalNodes.IsActive) ToggleLogicalNodeMode();
        if (!logicalNodes.IsActive) return;
        if (logicalNodes.Selected is not { } selected) { logicalNodes.Notify("先按 7 选择逻辑节点，再按 E 编辑。"); Refresh(); return; }
        LogicalNodeWindowRequested?.Invoke(this, new(new Point((double)selected.X,(double)selected.Y),selected));
    }
    public void RequestLogicalNodePlacement()
    {
        if (IsSaving || PendingMapOperation() || PendingLogicalNodeMove()) return;
        if (!logicalNodes.IsActive) ToggleLogicalNodeMode();
        if (!logicalNodes.IsActive) return;
        IsPlacingLogicalNode = false;
        LogicalNodeSizeRequested?.Invoke(this, EventArgs.Empty);
        Refresh();
    }
    public bool BeginLogicalNodePlacement(int size)
    {
        if (IsSaving || PendingMapOperation() || PendingLogicalNodeMove() || size is not (4 or 6 or 8 or 10 or 12 or 18)) return false;
        if (!logicalNodes.IsActive) ToggleLogicalNodeMode();
        if (!logicalNodes.IsActive) return false;
        if (size == 18 && logicalNodes.Nodes.Any(n => n.IsStart))
        { logicalNodes.Notify("全图只能有一个开始节点，请先删除已有开始节点。"); Refresh(); return false; }
        logicalNodes.Activate();
        pastedLogicalNodeProperties=null;
        instancePlacementProperties=null;
        LogicalNodePlacementSize = size;
        IsPlacingLogicalNode = true;
        logicalNodes.Notify($"边长 {size}{(size == 18 ? " · 开始" : "")}：A 或左键连续新增，Esc 结束。中心自动吸附为整数＋0.5。");
        Refresh(); return true;
    }
    public bool BeginInstancePlacement(LogicalNodeSnapshot parent)
    {
        if(!LogicalNodeInstanceRules.CanInstantiate(parent)||!BeginLogicalNodePlacement(10))return false;
        var value=LogicalNodeInstanceRules.DefaultValue(parent.Properties.Value,"新节点编码");
        instancePlacementParentCode=parent.Code;
        instancePlacementProperties=new(){Value=value,LogicLanguage=LogicalNodeInstanceRules.Expression(value)};
        logicalNodes.Notify($"新增 LN10：{LogicalNodeInstanceRules.Expression(value)}。保存时用新节点编码补临时 id，可编辑为 id=1 或 name=名称；鼠标选落点，左键 / A 连续新增，Esc 取消。");
        Refresh();Focus();return true;
    }
    public bool BeginLogicalNodeMove(LogicalNodeSnapshot node)
    {
        if (IsSaving || PendingMapOperation()) return false;
        if (!IsLogicalNodeMode) ToggleLogicalNodeMode();
        if (!IsLogicalNodeMode) return false;
        IsPlacingLogicalNode = false; logicalNodeAction = true;
        var started = logicalNodes.BeginMove(node); Refresh(); Focus(); return started;
    }
    public async Task<bool> MoveLogicalNodeAsync(Point? world = null)
    {
        if (IsSaving || PendingMapOperation() || !IsMovingLogicalNode) return false;
        if (LogicalNodeEditor.Candidate(world ?? PointerPosition ?? lastLogicalNodePointer) is not { } candidate)
        { logicalNodes.Notify("请先将鼠标移到地图上选择新位置。Esc 取消移动。"); Refresh(); return false; }
        var task = logicalNodes.MoveAsync((decimal)candidate.X, (decimal)candidate.Y); Refresh();
        try { return await task; } finally { Refresh(); }
    }
    private bool PendingLogicalNodeMove()
    {
        if (!IsMovingLogicalNode) return false;
        logicalNodes.Notify("正在移动 LN：左键 / A / Enter 校验并落点，Esc 取消。" ); Refresh(); return true;
    }
    public Task<bool> PlaceLogicalNodeAsync(Point world) => IsMovingLogicalNode ? MoveLogicalNodeAsync(world) :
        !IsPlacingLogicalNode ? Task.FromResult(false) : AddLogicalNodeAsync(world);
    public async Task<bool> AddLogicalNodeAsync(Point? world = null)
    {
        if (IsMovingLogicalNode) return await MoveLogicalNodeAsync(world);
        if (IsSaving || PendingMapOperation()) return false;
        if (!IsLogicalNodeMode) ToggleLogicalNodeMode();
        if (!IsLogicalNodeMode) return false;
        logicalNodeAction = true;
        if (LogicalNodeEditor.Candidate(world ?? PointerPosition ?? lastLogicalNodePointer ?? Center) is not { } candidate) return false;
        var x = (decimal)candidate.X; var y = (decimal)candidate.Y;
        var violation = ArgumentMap.Rules.LogicalNodeGeometryRules.ValidateShape(x,y,LogicalNodePlacementSize);
        if (violation is null)
        {
            var lines = editor.Lines.Select(line => new LineSnapshot(line.Code,line.Owner,line.IsLocked,line.Version,
                line.Nodes.Select((p,i)=>new LineNode(line.Code,i,p.X,p.Y)).ToArray()));
            violation = ArgumentMap.Rules.LogicalNodeGeometryRules.ValidateNew(new("", "", 0, x, y, LogicalNodePlacementSize, new()),
                lines,crossEditor.Crosses.ToDictionary(c=>c.Key),logicalNodes.Nodes);
        }
        if (violation is not null) { logicalNodes.Notify(violation.Message); Refresh(); return false; }
        var pasted=pastedLogicalNodeProperties;
        var task=logicalNodes.CreateAsync(x,y,LogicalNodePlacementSize,pasted??instancePlacementProperties,
            pasted is null&&instancePlacementProperties is not null?instancePlacementParentCode:null);
        Refresh();
        try
        {
            var saved=await task;
            if(saved&&pasted is not null)
            {
                var savedMessage=logicalNodes.Message;
                IsPlacingLogicalNode=false;
                if(logicalNodes.SelectedCode is {} code)logicalNodes.Select(code);
                logicalNodes.Notify($"LN{LogicalNodePlacementSize} 已粘贴；"+savedMessage+" Ctrl+V 可再次选择落点，副本具有独立编码。");
            }
            else if(saved && IsPlacingLogicalNode)
                logicalNodes.Notify(logicalNodes.Message+$" 可继续新增边长 {LogicalNodePlacementSize} 的 LN；Esc 结束，F5 更换边长。");
            return saved;
        }
        finally { Refresh(); }
    }
    public async Task<bool> RefreshLogicalNodesAsync()
    {
        if (IsSaving || PendingMapOperation()) return false;
        logicalNodeAction = true;
        var task = logicalNodes.RefreshAsync(); Refresh();
        try { return await task; } finally { Refresh(); }
    }
    public Task<bool> CalculateLogicalNodesAsync()
    {
        if(!Dispatcher.CheckAccess())return Dispatcher.InvokeAsync(CalculateLogicalNodesAsync).Task.Unwrap();
        if(IsCalculatingLogicalNodes||IsSaving||PendingMapOperation())return Task.FromResult(false);
        if(SynchronizationContext.Current is not DispatcherSynchronizationContext)
            return Dispatcher.InvokeAsync(CalculateLogicalNodesAsync).Task.Unwrap();
        return CalculateLogicalNodesOnDispatcherAsync();
    }
    private async Task<bool> CalculateLogicalNodesOnDispatcherAsync()
    {
        var task = logicalNodes.CalculateAsync(Refresh,()=>IsSaving); Refresh();
        try { return await task; } finally { Refresh(); }
    }

    public static bool IsEditorKey(Key key) => key is Key.D1 or Key.NumPad1 or Key.D3 or Key.NumPad3 or Key.D4 or Key.NumPad4 or Key.D5 or Key.NumPad5 or
        Key.D8 or Key.NumPad8 or Key.D9 or Key.NumPad9 or Key.D0 or Key.NumPad0 or
        Key.A or Key.T or Key.F2 or Key.F3 or Key.F6 or Key.Back or Key.F8 or Key.F9 or Key.Delete or Key.R or
        Key.E or Key.L or Key.C or Key.D or Key.B or Key.K or Key.U or Key.I or Key.O or Key.P or Key.Enter or Key.OemOpenBrackets or Key.Tab or
        Key.F4 or Key.F5 or Key.F7 or Key.D7 or Key.NumPad7 or Key.J or Key.G or Key.Escape;

    public async Task<bool> HandleEditorKeyAsync(Key key, bool skipCrossReload = false)
    {
        // View-only filters stay usable while drawing, moving or previewing an import.
        switch(key)
        {
            case Key.G:SetShowLogicalNodeProtection(!ShowLogicalNodeProtection);return true;
            case Key.D8:case Key.NumPad8:SetCrossVisibility(!ShowConnectedCrosses,ShowDisconnectedCrosses,ShowUnsetCrosses);return true;
            case Key.D9:case Key.NumPad9:SetCrossVisibility(ShowConnectedCrosses,!ShowDisconnectedCrosses,ShowUnsetCrosses);return true;
            case Key.D0:case Key.NumPad0:SetCrossVisibility(ShowConnectedCrosses,ShowDisconnectedCrosses,!ShowUnsetCrosses);return true;
        }
        if(IsConnectingNodes)
        {
            if(key==Key.Escape){CancelLogicalNodeConnection();return true;}
            if(key==Key.Back){ResetConnectionTarget();return true;}
            if(key is Key.Enter or Key.F6){await SaveLogicalNodeConnectionAsync();return true;}
            if(IsEditorKey(key)){PendingMapOperation();return true;}
        }
        if(IsPastingArea)
        {
            if(key is Key.Escape or Key.Back){if(!IsSaving)CancelAreaPaste();return true;}
            if(key==Key.Enter){if(PointerPosition is {} at)await PlacePastedAreaAsync(at);return true;}
            if(IsEditorKey(key)){PendingMapOperation();return true;}
        }
        if(IsPastingLine)
        {
            if(key is Key.Escape or Key.Back){if(!IsSaving)CancelLinePaste();return true;}
            if(IsEditorKey(key)){PendingMapOperation();return true;}
        }
        if(IsFlowDiagramImportActive)
        {
            if(key==Key.Escape){if(!IsSaving)CancelFlowDiagramInteraction();return true;}
            if(key==Key.Enter&&!IsPickingFlowDiagramOrigin){if(!IsSaving)ReturnToFlowDiagramWindow();return true;}
            if(IsEditorKey(key)){PendingMapOperation();return true;}
        }
        if(key==Key.F7){BeginAreaMove();return true;}
        if(IsAreaMoveMode)
        {
            if(key==Key.Escape){if(!IsSaving){areaMove.Cancel();areaBoxStart=areaBoxEnd=areaAnchor=null;ReleaseMouseCapture();Refresh();}return true;}
            if(key==Key.Enter){await CompleteAreaMoveAsync();return true;}
            if(key==Key.R){await DeleteAreaSelectionAsync();return true;}
            if(IsEditorKey(key)){PendingMapOperation();return true;}
        }
        if (IsMovingLogicalNode)
        {
            if (key is Key.A or Key.Enter) { await MoveLogicalNodeAsync(); return true; }
            if (key == Key.Escape) { logicalNodes.CancelMove(); Refresh(); return true; }
            if (key == Key.F4) { await RefreshLogicalNodesAsync(); return true; }
            if (IsEditorKey(key)) { PendingLogicalNodeMove(); return true; }
        }
        if (key == Key.F5) { RequestLogicalNodePlacement(); return true; }
        if (key == Key.F4) { await RefreshLogicalNodesAsync(); return true; }
        if (key == Key.J) { await CalculateLogicalNodesAsync(); return true; }
        if (key is Key.D7 or Key.NumPad7) { ToggleLogicalNodeSelection(); return true; }
        if (logicalNodes.IsActive)
        {
            if (key == Key.Escape) { if(IsSaving)return true; IsPlacingLogicalNode = false; logicalNodes.Notify("已取消落点；F5 重新选择边长。"); Refresh(); return true; }
            if (key == Key.A) { await AddLogicalNodeAsync(); return true; }
            if (key == Key.E) { RequestLogicalNodeEdit(); return true; }
        }
        switch (key)
        {
            case Key.D1:
            case Key.NumPad1: SetShowArrows(!ShowArrows); break;
            case Key.E: if(editor.Mode==LineEditorMode.Drawing)await EditLineNodeAsync();else RequestLineProperties();break;
            case Key.L: await ToggleSelectedLineLockAsync(); break;
            case Key.C: await ReverseLineDirectionAsync(); break;
            case Key.D: SplitLineRequested?.Invoke(this,EventArgs.Empty);break;
            case Key.B: MergeLinesRequested?.Invoke(this,EventArgs.Empty);break;
            case Key.D3:
            case Key.NumPad3: await NewLineAsync(); break;
            case Key.D4:
            case Key.NumPad4: await BeginDrawingAsync(); break;
            case Key.D5:
            case Key.NumPad5: editor.ToggleAdditionEnd(); crossAction=false; Refresh(); break;
            case Key.A: editor.AddPoint(PointerPosition); Refresh(); break;
            case Key.T: editor.InsertPoint(PointerPosition); Refresh(); break;
            case Key.F2: await RefreshLinesAsync(); break;
            case Key.F6: await CompleteLineAsync(); break;
            case Key.Back: CancelEditing(); break;
            case Key.F8: ToggleLineSelection(); break;
            case Key.F9: await EditSelectedLineAsync(); break;
            case Key.Delete:
            case Key.R: await RunCommandAsync(() => editor.RemoveAsync(PointerPosition)); break;
            case Key.F3: await RefreshCrossesAsync(); break;
            case Key.K: await CalculateCrossesAsync(); break;
            case Key.U: ToggleCrossSelection(); break;
            case Key.Enter: if(IsEditingLineNode)await EditLineNodeAsync();else RememberCrossExitLine();break;
            case Key.OemOpenBrackets: await SetCrossOneWayAsync(!skipCrossReload); break;
            case Key.I: await SetCrossStateAsync(CrossState.Connected, !skipCrossReload); break;
            case Key.O: await SetCrossStateAsync(CrossState.Disconnected, !skipCrossReload); break;
            case Key.P: await SetCrossStateAsync(CrossState.Unset, !skipCrossReload); break;
            case Key.Escape: if(editor.CancelNodeMove())Refresh();else ReleaseMouseCapture();break;
            case Key.Tab:
                if (!crossEditor.IsSelecting) return false;
                crossEditor.CycleSelection(PointerPosition, 12 / Scale);
                Refresh();
                break;
            default: return false;
        }
        return true;
    }

    private async Task<bool> RunCommandAsync(Func<Task<bool>> operation)
    {
        if (IsSaving || PendingMapOperation()) return false;
        logicalNodes.Leave(); logicalNodeAction = false;
        crossAction = false;
        crossEditor.LeaveSelection();
        var task = operation();
        Refresh();
        try
        {
            var success = await task;
            if (success) { await crossEditor.RefreshAsync(); await logicalNodes.RefreshAsync(); }
            return success;
        }
        finally { Refresh(); }
    }

    private void Zoom(double factor, Point anchor)
    {
        fitToWindow = false;
        viewport.Zoom(factor, anchor);
        Refresh();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        viewport.Resize(Plot.Size);
        if (fitToWindow) viewport.Reset(fit: true);
        Refresh();
    }

    private Point RelativeToPlot(Point point) => new(point.X - RulerWidth, point.Y - RulerHeight);
    private Point ToScreen(Point world)
    {
        var point = viewport.WorldToScreen(world);
        return new Point(point.X + RulerWidth, point.Y + RulerHeight);
    }

    private void Refresh()
    {
        PointerPosition = null;
        if (lastPointer.HasValue && Plot.Contains(lastPointer.Value))
        {
            var world = viewport.ScreenToWorld(RelativeToPlot(lastPointer.Value));
            if (world.X >= 0 && world.X <= CoordinateViewport.WorldSize &&
                world.Y >= 0 && world.Y <= CoordinateViewport.WorldSize)
                PointerPosition = world;
        }
        if (PointerPosition is { } pointer) lastLogicalNodePointer = pointer;
        // Keep the hovered selection when the pointer leaves for the toolbar's Edit/Delete actions.
        if (lastPointer.HasValue)
        {
            editor.SelectClosest(PointerPosition, 10 / Scale);
            crossEditor.SelectClosest(PointerPosition, 12 / Scale);
            logicalNodes.SelectClosest(PointerPosition, 8 / Scale, FindLogicalNodeAt(lastPointer.Value)?.Code);
        }
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override async void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton != MouseButton.Left || !Plot.Contains(e.GetPosition(this))) return;
        Focus();
        lastPointer = e.GetPosition(this);
        if(IsConnectingNodes){e.Handled=true;if(!IsSaving)PickConnectionPoint(viewport.ScreenToWorld(RelativeToPlot(e.GetPosition(this))));return;}
        if(IsPastingArea){e.Handled=true;if(!IsSaving)await PlacePastedAreaAsync(viewport.ScreenToWorld(RelativeToPlot(e.GetPosition(this))));return;}
        if(IsPastingLine){e.Handled=true;if(!IsSaving)await PlacePastedLineAsync(viewport.ScreenToWorld(RelativeToPlot(e.GetPosition(this))));return;}
        if(IsPickingFlowDiagramOrigin){e.Handled=true;if(!IsSaving)SelectFlowDiagramOrigin(AreaWorld(e.GetPosition(this)));return;}
        if(IsAreaMoveMode)
        {
            e.Handled=true;if(IsSaving)return;
            if(areaMove.Selection is not null){await CompleteAreaMoveAsync(AreaWorld(e.GetPosition(this)));return;}
            BeginAreaSelection(e.GetPosition(this));return;
        }
        if ((IsPlacingLogicalNode || IsMovingLogicalNode) && IsLogicalNodeMode)
        {
            e.Handled = true;
            await PlaceLogicalNodeAsync(viewport.ScreenToWorld(RelativeToPlot(e.GetPosition(this))));
            return;
        }
        if(SelectLogicalNodeAt(e.GetPosition(this)) && e.ClickCount==2)
        {RequestLogicalNodeEdit();e.Handled=true;return;}
        if (!CaptureMouse()) return;
        dragPosition = e.GetPosition(this);
        Cursor = Cursors.Hand;
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var current = e.GetPosition(this);
        lastPointer = current;
        if(areaBoxStart.HasValue){UpdateAreaSelection(current);return;}
        if (dragPosition.HasValue)
        {
            fitToWindow = false;
            viewport.Pan(current - dragPosition.Value);
            dragPosition = current;
        }
        Refresh();
    }

    protected override async void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if(e.ChangedButton==MouseButton.Left && areaBoxStart.HasValue)
        {
            e.Handled=true;await CompleteAreaSelectionAsync(e.GetPosition(this));return;
        }
        if (e.ChangedButton != MouseButton.Left || !dragPosition.HasValue) return;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        dragPosition = null;
        if(areaBoxStart.HasValue){areaBoxStart=areaBoxEnd=null;areaMove.Notify("框选已中断，请重新拖出矩形。");Refresh();}
        Cursor = Cursors.Cross;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        lastPointer = null;
        Refresh();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        var mouse = e.GetPosition(this);
        if (!Plot.Contains(mouse)) return;
        Zoom(Math.Pow(1.25, e.Delta / 120.0), RelativeToPlot(mouse));
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Home: ResetView(); break;
            case Key.Add:
            case Key.OemPlus: ZoomAtCenter(1.25); break;
            case Key.Subtract:
            case Key.OemMinus: ZoomAtCenter(1 / 1.25); break;
            case Key.Escape: ReleaseMouseCapture(); break;
            default: return;
        }
        e.Handled = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        speedLabelBounds.Clear();
        dc.DrawRectangle(OutsideBrush, null, new Rect(RenderSize));
        var plot = Plot;
        if (plot.Width <= 0 || plot.Height <= 0) return;
        var upperLeft = viewport.ScreenToWorld(new Point(0, 0));
        var lowerRight = viewport.ScreenToWorld(new Point(plot.Width, plot.Height));
        var left = Math.Max(0, upperLeft.X);
        var right = Math.Min(CoordinateViewport.WorldSize, lowerRight.X);
        var bottom = Math.Max(0, lowerRight.Y);
        var top = Math.Min(CoordinateViewport.WorldSize, upperLeft.Y);
        var worldRect = new Rect(ToScreen(new Point(0, CoordinateViewport.WorldSize)),
            ToScreen(new Point(CoordinateViewport.WorldSize, 0)));

        dc.PushClip(new RectangleGeometry(plot));
        dc.DrawRectangle(Brushes.White, null, worldRect);
        var step = GridStep;
        var minorStep = step / 5;
        if (minorStep * Scale >= 8) DrawGrid(dc, left, right, bottom, top, minorStep, MinorPen);
        DrawGrid(dc, left, right, bottom, top, step, MajorPen);
        dc.DrawRectangle(null, EdgePen, worldRect);
        DrawLogicalNodeProtectionAreas(dc, plot);
        DrawLogicalNodes(dc, plot);
        DrawLines(dc, plot);
        DrawCrosses(dc, plot);
        DrawLogicalNodeLabels(dc,plot);
        DrawAreaMove(dc);
        DrawFlowDiagramPreview(dc);
        DrawLinePastePreview(dc);
        DrawAreaPastePreview(dc);
        DrawLogicalNodeConnection(dc);
        dc.Pop();

        dc.DrawRectangle(RulerBrush, null, new Rect(0, 0, ActualWidth, RulerHeight));
        dc.DrawRectangle(RulerBrush, null, new Rect(0, 0, RulerWidth, ActualHeight));
        dc.DrawLine(TickPen, new Point(RulerWidth, 0), new Point(RulerWidth, ActualHeight));
        dc.DrawLine(TickPen, new Point(0, RulerHeight), new Point(ActualWidth, RulerHeight));
        DrawRulers(dc, plot, left, right, bottom, top, step, minorStep);
        DrawText(dc, "X / Y", new Point(16, 7));
    }

    private void DrawGrid(DrawingContext dc, double left, double right, double bottom,
        double top, double step, Pen pen)
    {
        if (left > right || bottom > top) return;
        for (var x = Math.Ceiling(left / step) * step; x <= right; x += step)
            dc.DrawLine(pen, ToScreen(new Point(x, bottom)), ToScreen(new Point(x, top)));
        for (var y = Math.Ceiling(bottom / step) * step; y <= top; y += step)
            dc.DrawLine(pen, ToScreen(new Point(left, y)), ToScreen(new Point(right, y)));
    }

    private void DrawRulers(DrawingContext dc, Rect plot, double left, double right,
        double bottom, double top, double step, double minorStep)
    {
        dc.PushClip(new RectangleGeometry(new Rect(plot.Left, 0, plot.Width, RulerHeight)));
        if (minorStep * Scale >= 8)
            for (var x = Math.Ceiling(left / minorStep) * minorStep; x <= right; x += minorStep)
            {
                var px = ToScreen(new Point(x, 0)).X;
                dc.DrawLine(TickPen, new Point(px, RulerHeight - 4), new Point(px, RulerHeight));
            }
        for (var x = Math.Ceiling(left / step) * step; x <= right; x += step)
        {
            var px = ToScreen(new Point(x, 0)).X;
            dc.DrawLine(TickPen, new Point(px, RulerHeight - 9), new Point(px, RulerHeight));
            var text = CreateText(x.ToString("0.##", CultureInfo.InvariantCulture));
            dc.DrawText(text, new Point(px - text.Width / 2, 3));
        }
        dc.Pop();
        dc.PushClip(new RectangleGeometry(new Rect(0, plot.Top, RulerWidth, plot.Height)));
        if (minorStep * Scale >= 8)
            for (var y = Math.Ceiling(bottom / minorStep) * minorStep; y <= top; y += minorStep)
            {
                var py = ToScreen(new Point(0, y)).Y;
                dc.DrawLine(TickPen, new Point(RulerWidth - 4, py), new Point(RulerWidth, py));
            }
        for (var y = Math.Ceiling(bottom / step) * step; y <= top; y += step)
        {
            var py = ToScreen(new Point(0, y)).Y;
            dc.DrawLine(TickPen, new Point(RulerWidth - 9, py), new Point(RulerWidth, py));
            var text = CreateText(y.ToString("0.##", CultureInfo.InvariantCulture));
            dc.DrawText(text, new Point(RulerWidth - 13 - text.Width, py - text.Height / 2));
        }
        dc.Pop();
    }

    private const double MapTextFontSize=11;
    private FormattedText CreateText(string text) => new(text, CultureInfo.InvariantCulture,
        FlowDirection.LeftToRight, typeface, MapTextFontSize, TextBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    private void DrawText(DrawingContext dc, string text, Point position) => dc.DrawText(CreateText(text), position);

    private void DrawLines(DrawingContext dc, Rect plot)
    {
        foreach (var line in editor.Lines)
        {
            if (line.Code == editor.DraftCode) continue;
            var selected = line.Code == editor.SelectedCode || line.Code == editor.MergeFirstCode;
            var pen = line.IsLocked ? LockedLinePen : selected ? SelectedPen : LinePen;
            if (selected && line.IsLocked) DrawPolyline(dc, line.Nodes, SelectionHaloPen);
            DrawPolyline(dc, line.Nodes, pen);
            if (ShowArrows) DrawDirectionArrows(dc, line, plot, pen);
            if (selected) DrawNodes(dc, line.Nodes, plot, pen);
        }
        DrawPolyline(dc, editor.DraftNodes, DraftPen);
        DrawNodes(dc, editor.DraftNodes, plot, DraftPen);
        if(editor.MovingNodeOrder is int moving)
        {
            var original=ToScreen(editor.DraftNodes[moving].ToPoint());dc.DrawEllipse(null,DraftPen,original,8,8);
            if(PointerPosition is {} target)
            {
                var screenTarget=ToScreen(target);
                if(moving>0)dc.DrawLine(PreviewPen,ToScreen(editor.DraftNodes[moving-1].ToPoint()),screenTarget);
                if(moving+1<editor.DraftNodes.Count)dc.DrawLine(PreviewPen,screenTarget,ToScreen(editor.DraftNodes[moving+1].ToPoint()));
                dc.DrawEllipse(Brushes.White,DraftPen,screenTarget,5,5);
                var hint=CreateText($"目标 ({target.X:0.0}, {target.Y:0.0}) · E / Enter 校验吸附，Esc 取消");
                var x=Math.Clamp(screenTarget.X+12,plot.Left+4,Math.Max(plot.Left+4,plot.Right-hint.Width-8));
                var y=Math.Clamp(screenTarget.Y+12,plot.Top+4,Math.Max(plot.Top+4,plot.Bottom-hint.Height-4));
                dc.DrawRectangle(RulerBrush,null,new Rect(x-3,y-2,hint.Width+6,hint.Height+4));dc.DrawText(hint,new(x,y));
            }
        }
        DrawPolyline(dc,mergePreview,DraftPen);DrawNodes(dc,mergePreview,plot,DraftPen);
        var candidate = editor.Candidate(PointerPosition);
        if (!candidate.HasValue || dragPosition.HasValue) return;
        var screen = ToScreen(candidate.Value.ToPoint());
        if (editor.AdditionAnchor is {} anchor)
            dc.DrawLine(PreviewPen, ToScreen(anchor.ToPoint()), screen);
        dc.DrawEllipse(Brushes.White, DraftPen, screen, 5, 5);
        var text = CreateText($"({candidate.Value.X}, {candidate.Value.Y})");
        var labelX = Math.Clamp(screen.X + 12, plot.Left + 4, Math.Max(plot.Left + 4, plot.Right - text.Width - 12));
        var labelY = Math.Clamp(screen.Y + 12, plot.Top + 4, Math.Max(plot.Top + 4, plot.Bottom - text.Height - 8));
        dc.DrawRectangle(RulerBrush, null, new Rect(labelX - 4, labelY - 2, text.Width + 8, text.Height + 4));
        dc.DrawText(text, new Point(labelX, labelY));
    }

    private void DrawPolyline(DrawingContext dc, IReadOnlyList<GridPoint> nodes, Pen pen)
    {
        if (nodes.Count < 2) return;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(ToScreen(nodes[0].ToPoint()), false, false);
            for (var i = 1; i < nodes.Count; i++)
                context.LineTo(ToScreen(nodes[i].ToPoint()), true, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    private void DrawDirectionArrows(DrawingContext dc, MapLine line, Rect plot, Pen pen)
    {
        var bidirectional = line.Properties.PassInOpposeDirection == 1;
        Point? lastSpeedLabel=null;
        for (var i = 1; i < line.Nodes.Count; i++)
        {
            var start = ToScreen(line.Nodes[i - 1].ToPoint());
            var vector = ToScreen(line.Nodes[i].ToPoint()) - start;
            var length = vector.Length;
            if (length < 24) continue;
            double first = 0, last = 1;
            if (!ClipAxis(start.X, vector.X, plot.Left, plot.Right, ref first, ref last) ||
                !ClipAxis(start.Y, vector.Y, plot.Top, plot.Bottom, ref first, ref last)) continue;
            var visibleLength = (last - first) * length;
            if (visibleLength < 24) continue;
            var unit = vector / length;
            var perpendicular = new Vector(-unit.Y, unit.X);
            for (var offset = Math.Min(55, visibleLength / 2); offset < visibleLength; offset += 110)
            {
                var center = start + vector * first + unit * offset;
                void Chevron(Vector direction, Point tip)
                {
                    var back = tip - direction * 7;
                    dc.DrawLine(pen, tip, back + perpendicular * 4);
                    dc.DrawLine(pen, tip, back - perpendicular * 4);
                }
                Chevron(unit, bidirectional ? center + unit * 9 : center);
                if (bidirectional) Chevron(-unit, center - unit * 9);
                // Keep spacing across segment boundaries, including F6's collinear subdivisions.
                if(ShowLineSpeeds && ShowArrows && (lastSpeedLabel is null || (center-lastSpeedLabel.Value).Length>=300) &&
                    DrawLineSpeed(dc,line.Properties.Speed,center,perpendicular,plot,pen.Brush))lastSpeedLabel=center;
            }
        }
    }

    private static bool ClipAxis(double start, double delta, double minimum, double maximum, ref double first, ref double last)
    {
        if (Math.Abs(delta) < 1e-9) return start >= minimum && start <= maximum;
        var a = (minimum - start) / delta;
        var b = (maximum - start) / delta;
        first = Math.Max(first, Math.Min(a, b));
        last = Math.Min(last, Math.Max(a, b));
        return first <= last;
    }

    private void DrawCrosses(DrawingContext dc, Rect plot)
    {
        foreach (var cross in crossEditor.Crosses.OrderBy(cross => cross.Key == crossEditor.SelectedKey ? 1 : 0))
        {
            if(!crossEditor.IsVisible(cross.State))continue;
            if (cross.Key.RoadCode1 == editor.DraftCode || cross.Key.RoadCode2 == editor.DraftCode) continue;
            var screen = ToScreen(new Point(cross.Key.Position.X, cross.Key.Position.Y));
            if (!plot.Contains(screen)) continue;
            var pen = cross.State switch
            {
                CrossState.Connected => ConnectedCrossPen,
                CrossState.Disconnected => DisconnectedCrossPen,
                CrossState.Road1ToRoad2 => Road1ExitCrossPen,
                CrossState.Road2ToRoad1 => Road2ExitCrossPen,
                _ => UnsetCrossPen
            };
            dc.DrawEllipse(Brushes.White, pen, screen, 6, 6);
            if (cross.State == CrossState.Connected) dc.DrawEllipse(pen.Brush, null, screen, 3, 3);
            if (cross.State == CrossState.Disconnected)
                dc.DrawLine(pen, new Point(screen.X - 3, screen.Y + 3), new Point(screen.X + 3, screen.Y - 3));
            if (cross.State is CrossState.Road1ToRoad2 or CrossState.Road2ToRoad1)
            {
                var direction = cross.State == CrossState.Road1ToRoad2 ? 1 : -1;
                var tip = new Point(screen.X + direction * 3, screen.Y);
                dc.DrawLine(pen,new Point(screen.X - direction * 3,screen.Y),tip);
                dc.DrawLine(pen,tip,new Point(screen.X,screen.Y - 2));
                dc.DrawLine(pen,tip,new Point(screen.X,screen.Y + 2));
            }
            if (cross.Key != crossEditor.SelectedKey || !crossEditor.IsSelecting) continue;
            dc.DrawEllipse(null, SelectedCrossPen, screen, 10, 10);
            var state = cross.State is CrossState.Road1ToRoad2 or CrossState.Road2ToRoad1 ? "单向换路" : cross.StateLabel;
            var relation = cross.State switch { CrossState.Road1ToRoad2 => "→", CrossState.Road2ToRoad1 => "←", CrossState.Connected => "↔", CrossState.Disconnected => "×", _ => "?" };
            var text = CreateText($"线 {cross.Key.RoadCode1}[{cross.RoadOrder1}:{cross.Percent1:0.###}] {relation} " +
                $"{cross.Key.RoadCode2}[{cross.RoadOrder2}:{cross.Percent2:0.###}]  ({cross.Key.Position.X:0.0}, {cross.Key.Position.Y:0.0})  {state}");
            var x = Math.Clamp(screen.X + 14, plot.Left + 4, Math.Max(plot.Left + 4, plot.Right - text.Width - 12));
            var y = Math.Clamp(screen.Y + 14, plot.Top + 4, Math.Max(plot.Top + 4, plot.Bottom - text.Height - 8));
            dc.DrawRectangle(RulerBrush, null, new Rect(x - 4, y - 2, text.Width + 8, text.Height + 4));
            dc.DrawText(text, new Point(x, y));
        }
    }

    private void DrawLogicalNodes(DrawingContext dc, Rect plot)
    {
        foreach (var node in logicalNodes.Nodes)
        {
            var bounds = new Rect(ToScreen(new Point((double)node.Left,(double)node.Top)),ToScreen(new Point((double)node.Right,(double)node.Bottom)));
            if (!bounds.IntersectsWith(plot)) continue;
            var pen = NodePen(node.SideLength,node.Positions.Any(p=>p.IsChanged));
            DrawNodeShape(dc,node.SideLength,NodeFill(node.SideLength),pen,bounds);
            var center = ToScreen(new Point((double)node.X,(double)node.Y));
            dc.DrawEllipse(pen.Brush,null,center,2,2);
            if (node.Code == logicalNodes.SelectedCode)
            {
                DrawNodeShape(dc,node.SideLength,null,SelectedCrossPen,new Rect(bounds.Left-2,bounds.Top-2,bounds.Width+4,bounds.Height+4),0.5 * Scale + 2);
                if(ShowLogicalNodeLabels)
                {
                    var text = CreateText($"LN {node.Code} · 值 {node.Properties.Value} · 中心 ({node.X:0.0}, {node.Y:0.0}) · 边长 {node.SideLength} · {node.Positions.Length} 个位置");
                    var x = Math.Clamp(center.X + 12,plot.Left+4,Math.Max(plot.Left+4,plot.Right-text.Width-8));
                    var y = Math.Clamp(bounds.Top-text.Height-5,plot.Top+4,Math.Max(plot.Top+4,plot.Bottom-text.Height-4));
                    dc.DrawRectangle(LogicalNodeLabelBackground,null,new Rect(x-3,y-2,text.Width+6,text.Height+4));dc.DrawText(text,new Point(x,y));
                }
            }
            foreach(var position in node.Positions)
            {
                var foot=ToScreen(new Point((double)position.X,(double)position.Y));
                dc.DrawLine(PreviewPen,center,foot);
                dc.DrawEllipse(Brushes.White,NodePen(node.SideLength),foot,3,3);
            }

        }
        if ((IsPlacingLogicalNode || IsMovingLogicalNode) && logicalNodes.Mode == LogicalNodeMode.Ready && LogicalNodeEditor.Candidate(PointerPosition) is { } candidate)
        {
            var size = logicalNodes.Moving?.SideLength ?? LogicalNodePlacementSize;
            var valid = ArgumentMap.Rules.LogicalNodeGeometryRules.ValidateShape((decimal)candidate.X,(decimal)candidate.Y,size) is null;
            var half = size / 2d;
            var rect=new Rect(ToScreen(new Point(candidate.X-half,candidate.Y+half)),ToScreen(new Point(candidate.X+half,candidate.Y-half)));
            DrawNodeShape(dc,size,NodeFill(size),valid ? (size==8?RelationNodePen:PreviewPen) : UnsetCrossPen,rect);
            if (logicalNodes.Moving is { } moving)
            {
                dc.DrawLine(PreviewPen,ToScreen(new Point((double)moving.X,(double)moving.Y)),ToScreen(candidate));
                dc.DrawText(CreateText($"移动至 ({candidate.X:0.0}, {candidate.Y:0.0}) · 左键 / A / Enter 确认，Esc 取消"), new Point(rect.Left,rect.Bottom+5));
            }
            else if(pastedLogicalNodeProperties is {} pasted)
                dc.DrawText(CreateText($"粘贴 {pasted.Value} · 左键确认，Esc 取消"),new Point(rect.Left,rect.Bottom+5));
        }
    }

    private void DrawLogicalNodeLabels(DrawingContext dc,Rect plot)
    {
        if(!ShowLogicalNodeLabels)return;
        foreach(var node in logicalNodes.Nodes)
        {
            var center=ToScreen(new((double)node.X,(double)node.Y));
            if(!plot.Contains(center) || LogicalNodeLabel(node) is not {} label)continue;
            DrawLogicalNodeLabel(dc,label);
        }
    }

    private (FormattedText Text,Point At)? LogicalNodeLabel(LogicalNodeSnapshot node)
        =>LayoutLogicalNodeLabel(node,ToScreen(new((double)node.X,(double)node.Y)));

    private (FormattedText Text,Point At)? LayoutLogicalNodeLabel(LogicalNodeSnapshot node,Point center)
    {
        if(!ShowLogicalNodeLabels || Scale*node.SideLength<14 || string.IsNullOrWhiteSpace(node.Properties.Value))return null;
        var text=CreateText(node.Properties.Value+(string.IsNullOrWhiteSpace(node.Properties.LogicLanguage)?"":"\n"+node.Properties.LogicLanguage));
        // Center each wrapped line in a paragraph no wider than the square.
        // At small zoom levels reserve enough room for one full-size glyph.
        text.MaxTextWidth=Math.Max(14,node.SideLength*Scale-6);text.TextAlignment=TextAlignment.Center;text.Trimming=TextTrimming.None;
        return (text,new(center.X-text.MaxTextWidth/2,center.Y+MapTextFontSize*.5));
    }
    private static Rect LogicalNodeLabelBounds((FormattedText Text,Point At) label)
        =>new(label.At.X-3,label.At.Y-2,label.Text.MaxTextWidth+6,label.Text.Height+4);
    private static void DrawLogicalNodeLabel(DrawingContext dc,(FormattedText Text,Point At) label)
    {
        dc.DrawRoundedRectangle(LogicalNodeLabelBackground,null,LogicalNodeLabelBounds(label),3,3);
        dc.DrawText(label.Text,label.At);
    }
    private LogicalNodeSnapshot? FindLogicalNodeAt(Point screen)
    {
        if(!Plot.Contains(screen))return null;
        var world=viewport.ScreenToWorld(RelativeToPlot(screen));
        return logicalNodes.Nodes.Where(n=>n.Contains((decimal)world.X,(decimal)world.Y) ||
            LogicalNodeLabel(n) is {} label && LogicalNodeLabelBounds(label).Contains(screen))
            .OrderBy(n=>n.Contains((decimal)world.X,(decimal)world.Y)?0:1)
            .ThenBy(n=>(ToScreen(new((double)n.X,(double)n.Y))-screen).LengthSquared).FirstOrDefault();
    }
    public bool SelectLogicalNodeAt(Point screen)
    {
        if(IsSaving || IsConnectingNodes || IsPastingArea || IsFlowDiagramImportActive || IsAreaMoveMode || IsMovingLogicalNode || IsPlacingLogicalNode || editor.Mode is LineEditorMode.Ready or LineEditorMode.Drawing)return false;
        if(FindLogicalNodeAt(screen) is not {} node)return false;
        editor.Cancel();crossEditor.LeaveSelection();logicalNodeAction=true;crossAction=false;
        lastPointer=screen;logicalNodes.Select(node.Code);Refresh();Focus();return true;
    }

    private void DrawNodes(DrawingContext dc, IReadOnlyList<GridPoint> nodes, Rect plot, Pen pen)
    {
        foreach (var node in nodes)
        {
            var screen = ToScreen(node.ToPoint());
            if (plot.Contains(screen)) dc.DrawEllipse(Brushes.White, pen, screen, 4, 4);
        }
    }
    private Point AreaWorld(Point screen)
    {var p=viewport.ScreenToWorld(RelativeToPlot(screen));return new(Math.Clamp(p.X,0,500000),Math.Clamp(p.Y,0,500000));}
    private void DrawAreaMove(DrawingContext dc)
    {
        if(!IsAreaMoveMode)return;
        if(areaBoxStart is {} start && areaBoxEnd is {} end)
        {dc.DrawRectangle(LogicalNodeFill,PreviewPen,new Rect(ToScreen(start),ToScreen(end)));return;}
        if(areaMove.Selection is not {} selection)return;
        var delta=AreaDelta();
        var bounds=selection.Bounds;
        dc.DrawRectangle(null,SelectedCrossPen,new Rect(ToScreen(new((double)bounds.Left,(double)bounds.Top)),ToScreen(new((double)bounds.Right,(double)bounds.Bottom))));
        bool InWorld(decimal x,decimal y)=>x+delta.X>=0&&x+delta.X<=500000&&y+delta.Y>=0&&y+delta.Y<=500000;
        var valid=selection.Lines.All(l=>l.Nodes.All(n=>InWorld(n.X,n.Y)))&&selection.LogicalNodes.All(n=>InWorld(n.Left,n.Bottom)&&InWorld(n.Right,n.Top));
        var pen=valid?PreviewPen:UnsetCrossPen;
        Point Shift(decimal x,decimal y)=>ToScreen(new((double)x+delta.X,(double)y+delta.Y));
        dc.DrawRectangle(null,pen,new Rect(Shift(bounds.Left,bounds.Top),Shift(bounds.Right,bounds.Bottom)));
        foreach(var line in selection.Lines)
            DrawPolyline(dc,line.Nodes.Select(n=>new GridPoint(n.X+delta.X,n.Y+delta.Y)).ToArray(),pen);
        foreach(var node in selection.LogicalNodes)
            DrawNodeShape(dc,node.SideLength,NodeFill(node.SideLength),valid&&node.SideLength==8?RelationNodePen:pen,new Rect(Shift(node.Left,node.Top),Shift(node.Right,node.Bottom)));
        foreach(var cross in selection.Crosses.Where(c=>crossEditor.IsVisible(c.State)))dc.DrawEllipse(Brushes.White,pen,Shift(cross.Key.Position.X2/2m,cross.Key.Position.Y2/2m),6,6);
        var top=Shift(bounds.Left,bounds.Top);
        dc.DrawText(CreateText($"整体偏移 ({delta.X}, {delta.Y}) · 左键 / Enter 校验落点 · Esc 取消"),new(top.X,top.Y-20));
    }

    private static Pen MakePreviewPen()
    {
        var pen = new Pen(MakeBrush("#228459"), 1.5) { DashStyle = DashStyles.Dash };
        pen.Freeze();
        return pen;
    }

    private static Brush MakeBrush(string color,double opacity=1)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)){Opacity=opacity};
        brush.Freeze();
        return brush;
    }

    private static Pen MakePen(string color, double thickness = 1)
    {
        var pen = new Pen(MakeBrush(color), thickness);
        pen.Freeze();
        return pen;
    }
}
