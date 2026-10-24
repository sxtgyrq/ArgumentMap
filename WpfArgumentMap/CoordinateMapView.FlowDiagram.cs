using System.Windows;
using System.Windows.Media;
using ArgumentMap.Contracts;

namespace WpfArgumentMap;

public sealed partial class CoordinateMapView
{
    private bool flowImportAction;
    private string flowImportMessage="";
    private MapAreaSnapshot? flowPreview;
    private Action<Point?>? flowOriginPicked;
    public bool IsFlowDiagramImportActive { get; private set; }
    public bool IsFlowDiagramImportBusy { get; private set; }
    public bool IsPickingFlowDiagramOrigin=>flowOriginPicked is not null;
    public event EventHandler? FlowDiagramCancelRequested;
    public event EventHandler? FlowDiagramReturnRequested;
    public bool BeginFlowDiagramImport()
    {
        if(IsSaving||IsFlowDiagramImportActive)return false;
        if(IsConnectingNodes||IsPastingLine||IsPastingArea){PendingMapOperation();return false;}
        flowImportAction=true;
        if(editor.Mode is LineEditorMode.Ready or LineEditorMode.Drawing||IsMovingLogicalNode||IsAreaMoveMode)
        {flowImportMessage="请先保存或取消当前线路、LN 移动或框选移动，再导入流程图。";Refresh();return false;}
        editor.Cancel();logicalNodes.Leave();crossEditor.LeaveSelection();IsPlacingLogicalNode=false;
        IsFlowDiagramImportActive=true;flowPreview=null;flowImportMessage="粘贴流程图 JSON，选择锚点位置，再校验预览并导入。";Refresh();return true;
    }
    internal void EndFlowDiagramImport(string message)
    {IsFlowDiagramImportActive=false;IsFlowDiagramImportBusy=false;flowPreview=null;flowOriginPicked=null;flowImportMessage=message;Refresh();}
    internal void SetFlowDiagramBusy(bool value)
    {IsFlowDiagramImportBusy=value;Refresh();}
    internal void SetFlowDiagramMessage(string message){flowImportMessage=message;Refresh();}
    internal bool BeginFlowDiagramOriginPick(Action<Point?> callback)
    {
        if(!IsFlowDiagramImportActive||IsSaving)return false;
        flowPreview=null;flowOriginPicked=callback;flowImportMessage="点击地图选择流程图锚点 LN 的中心，自动吸附到整数＋0.5；Esc 返回导入窗口。";Refresh();Focus();return true;
    }
    public bool SelectFlowDiagramOrigin(Point point)
    {
        if(flowOriginPicked is not {} callback||IsSaving||!double.IsFinite(point.X)||!double.IsFinite(point.Y))return false;
        var selected=SnapFlowOrigin(point);flowOriginPicked=null;flowImportMessage=$"已选择锚点 ({selected.X:0.0}, {selected.Y:0.0})，请在导入窗口校验预览。";callback(selected);Refresh();return true;
    }
    private static Point SnapFlowOrigin(Point p)=>new(Math.Clamp(Math.Floor(p.X)+.5,.5,499999.5),Math.Clamp(Math.Floor(p.Y)+.5,.5,499999.5));
    private void CancelFlowDiagramInteraction()
    {
        if(flowOriginPicked is {} callback){flowOriginPicked=null;flowImportMessage="已取消选点，请在导入窗口继续。";callback(null);Refresh();}
        else FlowDiagramCancelRequested?.Invoke(this,EventArgs.Empty);
    }
    private void ReturnToFlowDiagramWindow()=>FlowDiagramReturnRequested?.Invoke(this,EventArgs.Empty);
    internal void StopFlowDiagramOriginPick(){flowOriginPicked=null;Refresh();}
    internal void ShowFlowDiagramPreview(MapAreaSnapshot? snapshot,bool fit=false)
    {
        flowPreview=snapshot;
        if(fit&&snapshot is not null)
        {
            var points=snapshot.LogicalNodes.SelectMany(n=>new[]{new Point((double)n.Left,(double)n.Bottom),new Point((double)n.Right,(double)n.Top)})
                .Concat(snapshot.Lines.SelectMany(l=>l.Nodes.Select(n=>new Point(n.X,n.Y)))).ToArray();
            if(points.Length>0)
            {
                fitToWindow=false;viewport.FitBounds(new Rect(new Point(points.Min(p=>p.X)-20,points.Min(p=>p.Y)-20),new Point(points.Max(p=>p.X)+65,points.Max(p=>p.Y)+20)));
            }
        }
        Refresh();
    }
    internal void ApplyFlowDiagramMap(MapAreaSnapshot snapshot)
    {editor.ReplaceLines(snapshot.Lines);crossEditor.ReplaceCrosses(snapshot.Crosses);logicalNodes.ReplaceNodes(snapshot.LogicalNodes);SetShowArrows(true);Refresh();}
    private void DrawFlowDiagramPreview(DrawingContext dc)
    {
        if(!IsFlowDiagramImportActive)return;
        if(IsPickingFlowDiagramOrigin&&PointerPosition is {} pointer)
        {
            var p=SnapFlowOrigin(pointer);var at=ToScreen(p);dc.DrawEllipse(Brushes.White,PreviewPen,at,7,7);
            dc.DrawLine(PreviewPen,new(at.X-13,at.Y),new(at.X+13,at.Y));dc.DrawLine(PreviewPen,new(at.X,at.Y-13),new(at.X,at.Y+13));
            dc.DrawText(CreateText($"流程图锚点 ({p.X:0.0}, {p.Y:0.0}) · 左键选择 · Esc 返回"),new(at.X+15,at.Y+12));
        }
        if(flowPreview is not {} preview)return;
        foreach(var ln in preview.LogicalNodes)
        {
            var a=ToScreen(new((double)ln.Left,(double)ln.Top));var b=ToScreen(new((double)ln.Right,(double)ln.Bottom));
            DrawNodeShape(dc,ln.SideLength,NodeFill(ln.SideLength),ln.SideLength==8?RelationNodePen:PreviewPen,new Rect(a,b));
        }
        foreach(var road in preview.Lines)
        {
            var line=new MapLine(road.Code,road.Nodes.Select(n=>new GridPoint(n.X,n.Y)),road.Version,road.IsLocked,road.Properties,road.Owner);
            DrawPolyline(dc,line.Nodes,PreviewPen);DrawDirectionArrows(dc,line,Plot,PreviewPen);
        }
        foreach(var cross in preview.Crosses)
        {
            if(!cross.Key.IsValid||!crossEditor.IsVisible(cross.State))continue;
            var pen=cross.State switch{CrossState.Disconnected=>DisconnectedCrossPen,CrossState.Connected=>ConnectedCrossPen,CrossState.Road1ToRoad2=>Road1ExitCrossPen,CrossState.Road2ToRoad1=>Road2ExitCrossPen,_=>UnsetCrossPen};
            dc.DrawEllipse(Brushes.White,pen,ToScreen(new(cross.Key.Position.X,cross.Key.Position.Y)),5,5);
        }
        foreach(var ln in preview.LogicalNodes)
            if(LogicalNodeLabel(ln) is {} label)DrawLogicalNodeLabel(dc,label);
    }
}
