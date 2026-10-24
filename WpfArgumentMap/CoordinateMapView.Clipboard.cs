using ArgumentMap.Contracts;

namespace WpfArgumentMap;

public sealed partial class CoordinateMapView
{
    private LogicalNodeProperties? pastedLogicalNodeProperties;
    internal void ClipboardNotice(string message)
    {
        if(IsConnectingNodes){connectionMessage=message;Refresh();return;}
        if(IsAreaMoveMode||IsPastingArea){areaMoveAction=true;areaMove.Notify(message);}
        else {logicalNodeAction=true;logicalNodes.Notify(message);}
        Refresh();
    }
    private bool CanUseClipboard()
    {
        if(IsSaving||PendingMapOperation()||PendingLogicalNodeMove())return false;
        if(editor.Mode is LineEditorMode.Ready or LineEditorMode.Drawing)
        {ClipboardNotice("请先保存或取消线路绘制，再复制粘贴线路或 LN。");return false;}
        return true;
    }
    internal bool TryCopySelectedLogicalNode(out string text)
    {
        text="";if(!CanUseClipboard())return false;
        if(logicalNodes.Selected is not {} node||!LogicalNodeClipboard.Valid(node.SideLength,node.Properties))
        {ClipboardNotice("请选中有效的 LN4、LN6、LN8、LN10 或 LN12 再复制；LN18 不支持复制。");return false;}
        text=LogicalNodeClipboard.Encode(node.SideLength,node.Properties);return true;
    }
    internal bool BeginPasteLogicalNode(string? text)
    {
        if(!CanUseClipboard())return false;
        if(LogicalNodeClipboard.Decode(text) is not {} copy)
        {ClipboardNotice("剪贴板中没有有效的线路或 LN4/6/8/10/12 副本，请先选中对象并按 Ctrl+C。");return false;}
        if(!BeginLogicalNodePlacement(copy.SideLength))return false;
        pastedLogicalNodeProperties=copy.Properties;
        ClipboardNotice($"粘贴 LN{copy.SideLength}（{copy.Properties.Value}）：鼠标选择落点，左键确认，Esc 取消；保存时按新增规则校验和编号。");Focus();return true;
    }
}
