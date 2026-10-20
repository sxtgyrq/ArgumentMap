using ArgumentMap.Contracts;
using ArgumentMap.Storage;

namespace ArgumentMap.Business;

/// <summary>Enumerate nearest integer representatives of every geometric validity interval.</summary>
internal static class NodePositionCandidates
{
    private static readonly (int X,int Y)[] Directions=[(1,0),(0,1),(1,1),(1,-1)];
    internal static IEnumerable<(int X,int Y)> Find(LineStoreState state,LineNode[] nodes,int index,double x,double y)
    {
        var candidates=new HashSet<(int X,int Y)>();
        var original=nodes[index];var anchors=new List<LineNode>();if(index>0)anchors.Add(nodes[index-1]);if(index+1<nodes.Length)anchors.Add(nodes[index+1]);
        var other=state.Lines.Values.Where(l=>l.Code!=nodes[0].LineCode).ToArray();
        var segments=other.SelectMany(l=>l.Nodes.Zip(l.Nodes.Skip(1))).Concat(nodes.Zip(nodes.Skip(1)).Where((_,i)=>i!=index-1&&i!=index)).ToArray();
        var points=other.SelectMany(l=>l.Nodes).Concat(nodes).ToArray();
        static long Cross(long ax,long ay,long bx,long by)=>ax*by-ay*bx;
        void Add(long px,long py){if(px is >=0 and <=MapLimits.CoordinateMaximum&&py is >=0 and <=MapLimits.CoordinateMaximum)candidates.Add(((int)px,(int)py));}
        void Family(LineNode anchor,int dx,int dy)
        {
            var low=-MapLimits.CoordinateMaximum;var high=MapLimits.CoordinateMaximum;
            void Clip(int origin,int direction)
            {
                if(direction==0)return;var a=-origin/direction;var b=(MapLimits.CoordinateMaximum-origin)/direction;
                low=Math.Max(low,Math.Min(a,b));high=Math.Min(high,Math.Max(a,b));
            }
            Clip(anchor.X,dx);Clip(anchor.Y,dy);
            void Event(double t)
            {
                if(!double.IsFinite(t)||t<low-1||t>high+1)return;
                for(var k=(int)Math.Floor(t)-1;k<=(int)Math.Ceiling(t)+1;k++)if(k>=low&&k<=high)Add(anchor.X+(long)dx*k,anchor.Y+(long)dy*k);
            }
            void Point(LineNode p)
            {
                if(Cross(p.X-anchor.X,p.Y-anchor.Y,dx,dy)==0)Event(dx!=0?(p.X-anchor.X)/dx:(p.Y-anchor.Y)/dy);
            }
            Event(low);Event(high);Event(0);Event(((x-anchor.X)*dx+(y-anchor.Y)*dy)/(dx*dx+dy*dy));
            foreach(var p in points)Point(p);
            var stepLimit=dx!=0&&dy!=0?353:MapLimits.SavedSegmentLengthMaximum;
            Event(stepLimit);Event(-stepLimit);
            foreach(var a in anchors)if(Cross(a.X-anchor.X,a.Y-anchor.Y,dx,dy)==0)
            {
                var t=dx!=0?(a.X-anchor.X)/dx:(a.Y-anchor.Y)/dy;Event(t-stepLimit);Event(t+stepLimit);
            }
            foreach(var (a,b) in segments)
            {
                long sx=b.X-a.X,sy=b.Y-a.Y;var denominator=Cross(dx,dy,sx,sy);
                if(denominator!=0)Event((double)Cross(a.X-anchor.X,a.Y-anchor.Y,sx,sy)/denominator);
                else if(Cross(a.X-anchor.X,a.Y-anchor.Y,dx,dy)==0){Point(a);Point(b);}
            }
            foreach(var ln in state.LogicalNodes.Values)
            {
                if(dx!=0){Event((double)(ln.Left-anchor.X)/dx);Event((double)(ln.Right-anchor.X)/dx);}
                if(dy!=0){Event((double)(ln.Bottom-anchor.Y)/dy);Event((double)(ln.Top-anchor.Y)/dy);}
            }
        }
        if(anchors.Count==1)foreach(var (dx,dy) in Directions)Family(anchors[0],dx,dy);
        else foreach(var (ax,ay) in Directions)foreach(var (bx,by) in Directions)
        {
            var first=anchors[0];var second=anchors[1];var determinant=Cross(ax,ay,bx,by);
            if(determinant==0)
            {
                if(Cross(second.X-first.X,second.Y-first.Y,ax,ay)==0)Family(first,ax,ay);
                continue;
            }
            var numerator=Cross(second.X-first.X,second.Y-first.Y,bx,by);
            if(numerator%determinant==0){var t=numerator/determinant;Add(first.X+ax*t,first.Y+ay*t);}
        }
        return candidates.OrderBy(p=>(p.X-x)*(p.X-x)+(p.Y-y)*(p.Y-y))
            .ThenBy(p=>Math.Abs(p.X-original.X)+Math.Abs(p.Y-original.Y)).ThenBy(p=>p.X).ThenBy(p=>p.Y);
    }
}
