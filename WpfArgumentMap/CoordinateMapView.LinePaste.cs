using System.Windows;
using System.Windows.Media;
using ArgumentMap.Contracts;

namespace WpfArgumentMap;

public sealed partial class CoordinateMapView
{
    private ILinePasteClient? linePasteClient;
    private LineCopyData? pastedLine;
    private bool linePasteBusy;
    public bool IsPastingLine=>pastedLine is not null;
    internal bool TryCopySelection(out string text)
    {
        if(IsAreaMoveMode)return TryCopyArea(out text);
        if(logicalNodes.IsActive)return TryCopySelectedLogicalNode(out text);
        text="";if(!CanUseClipboard())return false;
        if(editor.Mode!=LineEditorMode.Selecting||editor.Selected is not {Nodes.Count:>=2} line)
        {ClipboardNotice("请先高亮选中一条线路或 LN4/6/8/10/12，再按 Ctrl+C。");return false;}
        var copy=new LineCopyData(line.Code,line.Version,line.Nodes.Select((n,i)=>new LineNode(line.Code,i,n.X,n.Y)).ToArray(),line.Properties,line.IsLocked);
        if(!LineClipboard.Valid(copy)){ClipboardNotice("线路副本超出支持的格式，请刷新后重试。");return false;}
        text=LineClipboard.Encode(copy);return true;
    }
    internal bool BeginPasteSelection(string? text)
    {
        if(MapAreaClipboard.Decode(text,out var isAreaClipboard) is {} area)return BeginPasteArea(area);
        if(isAreaClipboard)
        {ClipboardNotice("框选副本无效或已过期；Cross 必须为整数坐标，请刷新地图后重新框选复制。");return false;}
        return LineClipboard.Decode(text) is {} copy?BeginPasteLine(copy):BeginPasteLogicalNode(text);
    }
    internal bool BeginPasteLine(LineCopyData copy)
    {
        if(!CanUseClipboard()||!LineClipboard.Valid(copy))return false;
        if(linePasteClient is null){ClipboardNotice("当前服务不支持线路粘贴，请连接最新版 Server。");return false;}
        editor.Cancel();logicalNodes.Leave();crossEditor.LeaveSelection();IsPlacingLogicalNode=false;
        flowImportAction=false;areaMoveAction=false;dragPosition=null;ReleaseMouseCapture();
        pastedLine=copy with{Nodes=copy.Nodes.ToArray()};
        ClipboardNotice("粘贴线路：鼠标落点对应线路第一个节点，整条线路按整数坐标平移；左键确认，Esc 取消。形状及线路属性保留。");Focus();return true;
    }
    private void CancelLinePaste()
    {if(linePasteBusy)return;pastedLine=null;ClipboardNotice("已取消线路粘贴，未保存副本。");}
    internal GridPoint[]? LinePasteNodes(Point point)
    {
        if(pastedLine is not {} copy||!double.IsFinite(point.X)||!double.IsFinite(point.Y)||point.X<0||point.Y<0||point.X>500000||point.Y>500000)return null;
        var at=EightDirectionSnap.Round(point);var dx=at.X-copy.Nodes[0].X;var dy=at.Y-copy.Nodes[0].Y;
        return copy.Nodes.Select(n=>new GridPoint(n.X+dx,n.Y+dy)).ToArray();
    }
    public async Task<bool> PlacePastedLineAsync(Point point)
    {
        if(IsSaving||pastedLine is not {} copy||linePasteClient is null)return false;
        var nodes=LinePasteNodes(point);
        if(nodes is null||nodes.Any(n=>n.X<0||n.Y<0||n.X>500000||n.Y>500000))
        {ClipboardNotice("粘贴后部分线路超出地图范围，请重新选择落点。");return false;}
        linePasteBusy=true;Refresh();
        try
        {
            var result=await linePasteClient.PasteLineAsync(copy,nodes[0].X,nodes[0].Y);
            if(!result.Success||result.Data is null){ClipboardNotice(result.Message);return false;}
            pastedLine=null;ApplyFlowDiagramMap(result.Data.Map);editor.SelectLine(result.Data.Line.Code);
            ClipboardNotice(result.Message+" 可再次 Ctrl+V 粘贴。");return true;
        }
        finally{linePasteBusy=false;Refresh();}
    }
    private void DrawLinePastePreview(DrawingContext dc)
    {
        if(PointerPosition is not {} point||pastedLine is not {} copy||LinePasteNodes(point) is not {} nodes)return;
        var valid=nodes.All(n=>n.X>=0&&n.Y>=0&&n.X<=500000&&n.Y<=500000);var pen=valid?DraftPen:UnsetCrossPen;
        var line=new MapLine(copy.Code,nodes,copy.Version,copy.IsLocked,copy.Properties);
        DrawPolyline(dc,nodes,pen);DrawNodes(dc,nodes,Plot,pen);DrawDirectionArrows(dc,line,Plot,pen);
        var at=ToScreen(nodes[0].ToPoint());dc.DrawEllipse(Brushes.White,pen,at,6,6);
        dc.DrawText(CreateText($"线路首点 ({nodes[0].X}, {nodes[0].Y}) · 左键校验并保存，Esc 取消"),new(at.X+12,at.Y+12));
    }
}
