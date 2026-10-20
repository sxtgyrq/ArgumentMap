using ArgumentMap.Contracts;
using ArgumentMap.Storage;

namespace ArgumentMap.Business;

public sealed class NodeEditService(ILineRepository repository)
{
    public ServiceResult<NodePositionProposal> FindPosition(FindNodePositionRequest request)
    {
        var nodes=request.Nodes?.ToArray();
        return repository.Transaction(state=>
        {
            ServiceResult<NodePositionProposal> Error(string code,string message)=>ServiceResult<NodePositionProposal>.Fail(code,message);
            if(LineSaveService.CheckAccess(state,request.LineCode,request.Owner,out var line) is {} access)return Error(access.Code,access.Message);
            if(line!.Version!=request.Version)return Error("VersionConflict","线路已被其他用户修改，请保留草稿，刷新后重新编辑。");
            if(nodes is null||nodes.Length<2)return Error("TooFewNodes","至少两个节点后才能编辑节点位置。");
            if(request.NodeOrder<0||request.NodeOrder>=nodes.Length)return Error("InvalidNodeOrder","要编辑的节点不存在，请重新选择。");
            if(!double.IsFinite(request.TargetX)||!double.IsFinite(request.TargetY)||request.TargetX is <0 or >MapLimits.CoordinateMaximum||request.TargetY is <0 or >MapLimits.CoordinateMaximum)
                return Error("OutOfBounds","请把目标位置移到 0–500000 的坐标范围内。");
            for(var i=0;i<nodes.Length;i++)
                if(nodes[i] is not {} n||n.LineCode!=request.LineCode||n.Order!=i||n.X is <0 or >MapLimits.CoordinateMaximum||n.Y is <0 or >MapLimits.CoordinateMaximum)
                    return Error("InvalidNode","草稿的节点格式、序号、编码或坐标范围不正确。");
            ServiceResult<LineSnapshot>? last=null;string? subdivisionFailure=null;
            foreach(var (x,y) in NodePositionCandidates.Find(state,nodes,request.NodeOrder,request.TargetX,request.TargetY))
            {
                var candidate=nodes.ToArray();candidate[request.NodeOrder]=candidate[request.NodeOrder] with{X=x,Y=y};
                if(LineSaveService.ValidateShape(state,new(request.LineCode,request.Owner,request.Version,candidate),candidate) is {} invalid){last=invalid;continue;}
                var divided=LineSubdivision.Divide(state,candidate);if(!divided.Success){subdivisionFailure=divided.Message;continue;}
                return ServiceResult<NodePositionProposal>.Ok(new(request.NodeOrder,x,y),
                    $"节点序号 {request.NodeOrder} 已吸附到最接近目标的合法整数位置 ({x}, {y})；F6 重新校验保存，K 重算 Cross。");
            }
            return Error("NoValidNodePosition","保持相邻节点不动时，找不到满足八方向、角度、重复/重叠、Cross、LN 和长度限制的位置。"+(subdivisionFailure??last?.Message??""));
        });
    }
}
