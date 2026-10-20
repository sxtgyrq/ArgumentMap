namespace ArgumentMap.Contracts;

public sealed record FindNodePositionRequest(string LineCode,string Owner,long Version,LineNode[] Nodes,int NodeOrder,
    double TargetX,double TargetY,string LoginCode="");
public sealed record NodePositionProposal(int NodeOrder,int X,int Y);

public interface ILineNodeEditClient
{
    Task<ServiceResult<NodePositionProposal>> FindNodePositionAsync(string lineCode,long version,LineNode[] nodes,int nodeOrder,double targetX,double targetY);
}
