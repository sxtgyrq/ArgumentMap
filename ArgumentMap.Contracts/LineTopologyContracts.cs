namespace ArgumentMap.Contracts;

public sealed record SplitLineRequest(string LineCode, string Owner, long Version, int CutBeforeOrder, string LoginCode = "");
public sealed record MergeLinesRequest(string FirstCode, long FirstVersion, string SecondCode, long SecondVersion,
    string Owner, string LoginCode = "");
public sealed record MergePlan(int FirstX,int FirstY,int SecondX,int SecondY,int DirectionX,int DirectionY,int FirstCorner,int SecondCorner,int AdjustedGridSteps);
public sealed record MergePreview(LineSnapshot First,LineSnapshot Second,MergePlan[] Plans);
public sealed record ApplyMergeRequest(string FirstCode,long FirstVersion,string SecondCode,long SecondVersion,string Owner,
    int FirstX,int FirstY,int SecondX,int SecondY,string LoginCode="");
public sealed record LineTopologyResult(LineSnapshot[] Lines, string[] RemovedCodes, int AdjustedGridSteps = 0);

public interface ILineTopologyClient
{
    Task<ServiceResult<LineTopologyResult>> SplitLineAsync(string lineCode, long version, int cutBeforeOrder);
    Task<ServiceResult<MergePreview>> GetMergePlansAsync(string firstCode, long firstVersion, string secondCode, long secondVersion);
    Task<ServiceResult<LineTopologyResult>> ApplyMergePlanAsync(MergePreview preview,MergePlan plan);
}
