using ArgumentMap.Contracts;
using ArgumentMap.Storage;

namespace ArgumentMap.Business;

/// <summary>Refine validated geometry in memory, inside the caller's map transaction.</summary>
internal static class LineSubdivision
{
    internal static ServiceResult<LineNode[]> Divide(LineStoreState state,LineNode[] nodes)
    {
        // Exclude the replaced shape of this road, but reserve every submitted node and every
        // other road's node, including points added earlier while refining another segment.
        var used=state.Lines.Values.Where(l=>l.Code!=nodes[0].LineCode).SelectMany(l=>l.Nodes)
            .Concat(nodes).Select(n=>(n.X,n.Y)).ToHashSet();
        var output=new List<LineNode>{nodes[0]};
        for(var segment=0;segment<nodes.Length-1;segment++)
        {
            var first=nodes[segment];var last=nodes[segment+1];
            var dx=Math.Sign(last.X-first.X);var dy=Math.Sign(last.Y-first.Y);
            var count=Math.Max(Math.Abs(last.X-first.X),Math.Abs(last.Y-first.Y));
            var factor=dx*dx+dy*dy;
            bool TooLong(int steps)=>(long)steps*steps*factor>(long)MapLimits.SavedSegmentLengthMaximum*MapLimits.SavedSegmentLengthMaximum;
            LineNode At(int k)=>new(first.LineCode,0,first.X+dx*k,first.Y+dy*k);
            if(!TooLong(count)){output.Add(last);continue;}

            // Project closed LN squares onto this integer ray. Jump across a forbidden range
            // instead of probing every point inside it. Rounded corners are visual only.
            var forbidden=new List<(int Low,int High)>();
            foreach(var square in state.LogicalNodes.Values)
            {
                var low=1;var high=count-1;
                bool Clip(int origin,int direction,decimal minimum,decimal maximum)
                {
                    if(direction==0)return origin>=minimum&&origin<=maximum;
                    var a=(minimum-origin)/direction;var b=(maximum-origin)/direction;
                    low=Math.Max(low,(int)decimal.Ceiling(Math.Min(a,b)));
                    high=Math.Min(high,(int)decimal.Floor(Math.Max(a,b)));return low<=high;
                }
                if(Clip(first.X,dx,square.Left,square.Right)&&Clip(first.Y,dy,square.Bottom,square.Top))forbidden.Add((low,high));
            }
            forbidden.Sort((a,b)=>a.Low.CompareTo(b.Low));
            var merged=new List<(int Low,int High)>();
            foreach(var interval in forbidden)
                if(merged.Count>0&&interval.Low<=merged[^1].High+1)merged[^1]=(merged[^1].Low,Math.Max(merged[^1].High,interval.High));
                else merged.Add(interval);
            (int Low,int High)? Block(int k)
            {
                var low=0;var high=merged.Count-1;
                while(low<=high)
                {
                    var mid=(low+high)/2;var range=merged[mid];
                    if(k<range.Low)high=mid-1;else if(k>range.High)low=mid+1;else return range;
                }
                return null;
            }
            int Free(int candidate,int limit,int direction)
            {
                while(direction<0?candidate>=limit:candidate<=limit)
                {
                    if(Block(candidate) is {} blocked){candidate=direction<0?blocked.Low-1:blocked.High+1;continue;}
                    var point=At(candidate);if(!used.Contains((point.X,point.Y)))return candidate;
                    candidate+=direction;
                }
                return -1;
            }
            // Explicit stack also handles a densely occupied ray without recursive stack growth.
            var pending=new Stack<(int Start,int End)>();pending.Push((0,count));
            while(pending.TryPop(out var part))
            {
                if(!TooLong(part.End-part.Start)){output.Add(At(part.End));continue;}
                var sum=part.Start+part.End;
                var lower=Free(sum/2,part.Start+1,-1);var upper=Free((sum+1)/2,part.End-1,1);
                var split=lower<0?upper:upper<0?lower:sum-2*lower<=2*upper-sum?lower:upper;
                if(split<0)return ServiceResult<LineNode[]>.Fail("CannotSubdivideSegment",
                    $"第 {segment+1} 段 ({first.X}, {first.Y}) → ({last.X}, {last.Y}) 超过 500，无法找到可细分的整数点：候选点均位于 LN 内或与已有节点重复。请调整线路后再保存。");
                var point=At(split);used.Add((point.X,point.Y));
                pending.Push((split,part.End));pending.Push((part.Start,split));
            }
        }
        // Geometric refinement preserves the original path, corners, intersection angles and
        // overlap results. Only inserted vertices can conflict; all were checked above. Re-running
        // quadratic whole-shape intersection checks is unnecessary for these collinear additions.
        return ServiceResult<LineNode[]>.Ok(output.Select((p,i)=>p with{Order=i}).ToArray(),"");
    }
}
