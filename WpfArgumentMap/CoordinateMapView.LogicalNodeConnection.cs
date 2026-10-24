using System.Windows;
using System.Windows.Media;
using ArgumentMap.Contracts;

namespace WpfArgumentMap;

public sealed partial class CoordinateMapView
{
    private ILogicalNodeConnectionClient? connectionClient;
    private bool connectionBusy;
    private LogicalNodeConnectionAnchor? connectionSource,connectionTarget;
    private LogicalNodeConnectionPlan? connectionPlan;
    private string connectionMessage="";
    public bool IsConnectingNodes{get;private set;}
    public bool HasConnectionPreview=>connectionPlan is not null;
    internal LogicalNodeConnectionPlan? ConnectionPreview=>connectionPlan;
    public string ConnectionSourceTitle=>ConnectionTitle(connectionSource);
    public string ConnectionTargetTitle=>ConnectionTitle(connectionTarget);
    public event EventHandler? LogicalNodeConnectionChoicesRequested;
    private string ConnectionTitle(LogicalNodeConnectionAnchor? anchor)
    {
        var node=logicalNodes.Nodes.FirstOrDefault(n=>n.Code==anchor?.Code);
        return node is null?"未选择":$"{node.Properties.Value} · LN{node.SideLength} · {node.Code}";
    }
    public bool BeginLogicalNodeConnection()
    {
        if(IsSaving||PendingMapOperation())return false;
        if(connectionClient is null){ClipboardNotice("当前连接不支持 LN 自动连接，请更新客户端和 Server。");return false;}
        if(editor.Mode is LineEditorMode.Ready or LineEditorMode.Drawing||IsMovingLogicalNode||IsAreaMoveMode||IsPlacingLogicalNode)
        {ClipboardNotice("请先保存或取消当前线路、LN 落点、移动或框选，再连接两个 LN。");return false;}
        editor.Cancel();logicalNodes.Leave();crossEditor.LeaveSelection();dragPosition=null;ReleaseMouseCapture();
        connectionSource=connectionTarget=null;connectionPlan=null;IsConnectingNodes=true;
        connectionMessage="连接：先点击第一个 LN 正方形内部，再点击第二个；点击只确定 LN。Esc 取消。";
        Refresh();Focus();return true;
    }
    internal bool PickConnectionPoint(Point point)
    {
        if(!IsConnectingNodes||IsSaving||connectionPlan is not null)return false;
        if(!double.IsFinite(point.X)||!double.IsFinite(point.Y)||point.X<0||point.Y<0||point.X>500000||point.Y>500000)return false;
        var node=logicalNodes.Nodes.FirstOrDefault(n=>n.Contains((decimal)point.X,(decimal)point.Y));
        if(node is null){connectionMessage="该点不在 LN 正方形内部，请重新点击；文字标签和保护区域不算 LN。";Refresh();return false;}
        if(connectionSource is null)
        {connectionSource=LogicalNodeConnectionAnchor.From(node);connectionMessage=$"已选起点：{ConnectionSourceTitle}。请点击另一个 LN 内部；方向按第一个 → 第二个。";Refresh();return true;}
        if(node.Code==connectionSource.Code){connectionMessage="两次点击位于同一个 LN，请选择另一个 LN。";Refresh();return false;}
        connectionTarget=LogicalNodeConnectionAnchor.From(node);connectionMessage="已选择两个 LN，请选择连接方式。";Refresh();
        LogicalNodeConnectionChoicesRequested?.Invoke(this,EventArgs.Empty);return true;
    }
    public async Task<bool> PreviewLogicalNodeConnectionAsync(LogicalNodeConnectionKind kind=LogicalNodeConnectionKind.Direct)
    {
        if(!IsConnectingNodes||IsSaving||connectionSource is null||connectionTarget is null||connectionClient is null)return false;
        connectionBusy=true;connectionPlan=null;connectionMessage="正在按现有线路规则规划连接，避开其他 LN；尚未写入数据库……";Refresh();
        try
        {
            var result=await connectionClient.PreviewLogicalNodeConnectionAsync(connectionSource,connectionTarget,kind);
            if(!result.Success||result.Data is null)
            {connectionTarget=null;connectionMessage=result.Message+" 可重新点击第二个 LN，或 Esc 取消。";return false;}
            if(result.Data.Kind!=kind)
            {connectionTarget=null;connectionMessage="Server 未返回所选连接方式，请更新 Server 后重试。";return false;}
            connectionPlan=result.Data;
            connectionMessage=(kind switch
            {
                LogicalNodeConnectionKind.Find=>"寻找预览：青色虚线从 LN1 沿已有通路到接出 Cross；新线由 Cross 穿过 LN2，末端延伸 5.5。",
                LogicalNodeConnectionKind.GoTo=>"前往预览：新线穿过 LN1，至接入处结束；青色虚线为已有通路，按箭头到达 LN2。",
                _=>"连接预览：横竖穿越两个 LN，两端各延伸 5.5。"
            })+
                "Enter / F6 确认，Backspace 重选第二个 LN，Esc 取消。";
            return true;
        }
        finally{connectionBusy=false;Refresh();Focus();}
    }
    public async Task<bool> SaveLogicalNodeConnectionAsync()
    {
        if(!IsConnectingNodes||IsSaving||connectionPlan is not {} plan||connectionClient is null)return false;
        connectionBusy=true;connectionMessage="正在保存线路 → K 计算 → 设置连接 Cross；成功前请勿重复操作……";Refresh();
        try
        {
            var result=await connectionClient.SaveLogicalNodeConnectionAsync(plan);
            if(!result.Success||result.Data is null){connectionMessage=result.Message+" 连接未确认成功；可重试 Enter，或 Backspace 重新规划。";return false;}
            IsConnectingNodes=false;connectionPlan=null;connectionSource=connectionTarget=null;
            ApplyFlowDiagramMap(result.Data.Map);editor.SelectLine(result.Data.Line.Code);flowImportAction=false;areaMoveAction=false;crossAction=false;
            ClipboardNotice(result.Message);return true;
        }
        finally{connectionBusy=false;Refresh();Focus();}
    }
    public void CancelLogicalNodeConnection()
    {
        if(!IsConnectingNodes||IsSaving)return;
        IsConnectingNodes=false;connectionSource=connectionTarget=null;connectionPlan=null;
        // A transport failure can hide a committed save; exiting must not claim it never happened.
        ClipboardNotice("已退出 LN 连接模式。");Focus();
    }
    private void ResetConnectionTarget()
    {if(IsSaving)return;connectionPlan=null;connectionTarget=null;connectionMessage=connectionSource is null?"请先点击第一个 LN 正方形内部；Esc 取消。":"已保留第一个 LN，请重新点击第二个 LN；Esc 取消。";Refresh();}
    private void DrawLogicalNodeConnection(DrawingContext dc)
    {
        if(!IsConnectingNodes)return;
        if(connectionPlan is {} plan)
        {
            if(plan.Kind!=LogicalNodeConnectionKind.Direct)
            {
                var pen=new Pen(Brushes.Teal,2){DashStyle=DashStyles.Dash};var arrow=new Pen(Brushes.Teal,2);
                foreach(var (a,b) in plan.Continuation.Zip(plan.Continuation.Skip(1)))
                    if(a.RoadCode==b.RoadCode)
                    {
                        var from=ToScreen(new((double)a.X,(double)a.Y));var to=ToScreen(new((double)b.X,(double)b.Y));dc.DrawLine(pen,from,to);
                        var direction=to-from;
                        if(direction.Length>20)
                        {
                            var middle=from+direction*.5;direction.Normalize();var normal=new Vector(-direction.Y,direction.X);
                            dc.DrawLine(arrow,middle-direction*6+normal*3,middle);dc.DrawLine(arrow,middle-direction*6-normal*3,middle);
                        }
                    }
                if(plan.Join is {} join)
                {
                    var point=ToScreen(new(join.Position.X,join.Position.Y));dc.DrawEllipse(Brushes.White,pen,point,6,6);
                    dc.DrawText(CreateText(plan.Kind==LogicalNodeConnectionKind.Find?"接出":"接入"),new(point.X+8,point.Y-18));
                }
            }
            var nodes=plan.Nodes.Select(n=>new GridPoint(n.X,n.Y)).ToArray();
            DrawPolyline(dc,nodes,DraftPen);DrawNodes(dc,nodes,Plot,DraftPen);
            DrawDirectionArrows(dc,new MapLine(plan.LineCode,nodes),Plot,DraftPen);
        }
        var number=0;
        foreach(var anchor in new[]{connectionSource,connectionTarget})
        {
            number++;if(anchor is null)continue;var half=anchor.SideLength/2.0;
            var at=ToScreen(new((double)anchor.X-half,(double)anchor.Y+half));var size=anchor.SideLength*Scale;
            dc.DrawRectangle(null,SelectedPen,new Rect(at,new Size(size,size)));
            var badge=ToScreen(new((double)anchor.X,(double)anchor.Y));dc.DrawEllipse(Brushes.White,SelectedPen,badge,10,10);
            var text=CreateText(number.ToString());dc.DrawText(text,new(badge.X-text.Width/2,badge.Y-text.Height/2));
        }
    }
}
