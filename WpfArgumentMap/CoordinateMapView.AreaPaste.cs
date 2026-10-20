using System.Windows;
using System.Windows.Media;
using ArgumentMap.Contracts;

namespace WpfArgumentMap;

public sealed partial class CoordinateMapView
{
    private IMapAreaPasteClient? areaPasteClient;
    private MapAreaSelection? pastedArea;
    private bool areaPasteBusy;
    public bool IsPastingArea=>pastedArea is not null;
    internal bool TryCopyArea(out string text)
    {
        text="";
        if(IsSaving||!IsAreaMoveMode||areaMove.Selection is not {} selection)
        {ClipboardNotice("请先按 F7 完成框选，再 Ctrl+C 复制整组对象。");return false;}
        if(selection.LogicalNodes.Any(n=>n.SideLength==18))
        {ClipboardNotice("选区包含 LN18 开始节点，不能复制；请重新框选并排除开始节点，移动和删除仍可使用。");return false;}
        if(!MapAreaCopyRules.Valid(selection))
        {ClipboardNotice("选区含无效的 LN、换行字段，或超过单批上限，请分批框选并核对节点内容后复制。");return false;}
        text=MapAreaClipboard.Encode(selection);
        if(text.Length<=MapAreaCopyRules.MaximumTextLength)return true;
        text="";ClipboardNotice("框选副本超过 100 万字符，请分批复制。");return false;
    }
    internal bool BeginPasteArea(MapAreaSelection selection)
    {
        if(IsSaving)return false;
        if(IsFlowDiagramImportActive||IsPastingLine||IsMovingLogicalNode||IsPlacingLogicalNode||editor.Mode is LineEditorMode.Ready or LineEditorMode.Drawing)
        {ClipboardNotice("请先完成或取消当前编辑，再粘贴框选副本。");return false;}
        if(!MapAreaCopyRules.Valid(selection)){ClipboardNotice("剪贴板中的框选副本无效，请重新复制。");return false;}
        if(areaPasteClient is null){ClipboardNotice("当前服务不支持整组粘贴，请连接最新版 Server。");return false;}
        areaMove.Cancel();editor.Cancel();logicalNodes.Leave();crossEditor.LeaveSelection();IsPlacingLogicalNode=false;
        areaBoxStart=areaBoxEnd=areaAnchor=dragPosition=null;ReleaseMouseCapture();pastedArea=selection;
        areaMoveAction=true;logicalNodeAction=crossAction=flowImportAction=false;
        ClipboardNotice("整组粘贴：鼠标落点对应原选框左上角，整体吸附整数偏移；左键 / Enter 按新增保存，Esc 取消。原对象保持不变。");Focus();return true;
    }
    private void CancelAreaPaste()
    {if(areaPasteBusy)return;pastedArea=null;areaMoveAction=true;areaMove.Notify("已取消整组粘贴，未新增或移动任何对象。");Refresh();}
    internal (int X,int Y)? AreaPasteDelta(Point point)
    {
        if(pastedArea is not {} source||!double.IsFinite(point.X)||!double.IsFinite(point.Y)||point.X<0||point.Y<0||point.X>500000||point.Y>500000)return null;
        var at=EightDirectionSnap.Round(point);
        return (at.X-(int)Math.Floor(source.Bounds.Left),at.Y-(int)Math.Ceiling(source.Bounds.Top));
    }
    private static bool AreaPasteInBounds(MapAreaSelection source,int dx,int dy)=>
        source.Lines.All(l=>l.Nodes.All(n=>n.X+dx is >=0 and <=500000&&n.Y+dy is >=0 and <=500000))&&
        source.LogicalNodes.All(n=>n.Left+dx>=0&&n.Right+dx<=500000&&n.Bottom+dy>=0&&n.Top+dy<=500000);
    public async Task<bool> PlacePastedAreaAsync(Point point)
    {
        if(IsSaving||pastedArea is not {} source||areaPasteClient is null)return false;
        if(AreaPasteDelta(point) is not {} delta||!AreaPasteInBounds(source,delta.X,delta.Y))
        {ClipboardNotice("粘贴后部分对象超出地图范围，请更换落点。");return false;}
        areaPasteBusy=true;Refresh();
        try
        {
            var result=await areaPasteClient.PasteMapAreaAsync(source,delta.X,delta.Y);
            if(!result.Success||result.Data is null){ClipboardNotice(result.Message);return false;}
            pastedArea=null;editor.ReplaceLines(result.Data.Map.Lines);crossEditor.ReplaceCrosses(result.Data.Map.Crosses);logicalNodes.ReplaceNodes(result.Data.Map.LogicalNodes);
            areaMoveAction=true;areaMove.Notify(result.Message+" 可再次 Ctrl+V 粘贴。");return true;
        }
        finally{areaPasteBusy=false;Refresh();}
    }
    private void DrawAreaPastePreview(DrawingContext dc)
    {
        if(pastedArea is not {} source||PointerPosition is not {} point||AreaPasteDelta(point) is not {} delta)return;
        var valid=AreaPasteInBounds(source,delta.X,delta.Y);var pen=valid?PreviewPen:UnsetCrossPen;
        Point Shift(decimal x,decimal y)=>ToScreen(new((double)x+delta.X,(double)y+delta.Y));
        dc.DrawRectangle(null,pen,new Rect(Shift(source.Bounds.Left,source.Bounds.Top),Shift(source.Bounds.Right,source.Bounds.Bottom)));
        foreach(var node in source.LogicalNodes)
            DrawNodeShape(dc,node.SideLength,NodeFill(node.SideLength),valid&&node.SideLength==8?RelationNodePen:pen,new Rect(Shift(node.Left,node.Top),Shift(node.Right,node.Bottom)));
        foreach(var road in source.Lines)
        {
            var nodes=road.Nodes.Select(n=>new GridPoint(n.X+delta.X,n.Y+delta.Y)).ToArray();
            DrawPolyline(dc,nodes,pen);DrawDirectionArrows(dc,new(road.Code,nodes,road.Version,road.IsLocked,road.Properties,road.Owner),Plot,pen);
        }
        foreach(var cross in source.Crosses.Where(c=>crossEditor.IsVisible(c.State)))
        {
            var crossPen=valid?cross.State switch{CrossState.Disconnected=>DisconnectedCrossPen,CrossState.Connected=>ConnectedCrossPen,CrossState.Road1ToRoad2=>Road1ExitCrossPen,CrossState.Road2ToRoad1=>Road2ExitCrossPen,_=>UnsetCrossPen}:pen;
            dc.DrawEllipse(Brushes.White,crossPen,Shift(cross.Key.Position.X2/2m,cross.Key.Position.Y2/2m),6,6);
        }
        foreach(var node in source.LogicalNodes)
            if(LogicalNodeLabel(node with{X=node.X+delta.X,Y=node.Y+delta.Y}) is {} label)DrawLogicalNodeLabel(dc,label);
        var top=Shift(source.Bounds.Left,source.Bounds.Top);
        dc.DrawText(CreateText($"整组新增 · {source.Lines.Length} 条线路 / {source.LogicalNodes.Length} 个 LN · 左键 / Enter 保存 · Esc 取消"),new(top.X,top.Y-20));
    }
}
