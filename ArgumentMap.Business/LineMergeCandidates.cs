using ArgumentMap.Contracts;
using ArgumentMap.Storage;

namespace ArgumentMap.Business;

internal static class LineMergeCandidates
{
    internal static IEnumerable<(int First, int Second)> Enumerate(LineSnapshot first, LineSnapshot second, LineStoreState state)
    {
        var p=first.Nodes[^1]; var previous=first.Nodes[^2]; var q=second.Nodes[0]; var next=second.Nodes[1];
        long ax=Math.Sign(p.X-previous.X), ay=Math.Sign(p.Y-previous.Y), bx=Math.Sign(next.X-q.X), by=Math.Sign(next.Y-q.Y);
        long firstLength=Math.Max(Math.Abs(p.X-previous.X),Math.Abs(p.Y-previous.Y));
        long secondLength=Math.Max(Math.Abs(next.X-q.X),Math.Abs(next.Y-q.Y));
        long firstMinimum=firstLength-RayLimit(previous,ax,ay),secondMinimum=secondLength-RayLimit(next,-bx,-by);
        var moves=new HashSet<(int First,int Second)>{(0,0)};
        foreach(var (nx,ny) in new (long,long)[]{(1,0),(0,1),(1,1),(1,-1)})
        {
            // n dot (Q-P)=0. Integer solutions describe all axis/45-degree bridges.
            long a=nx*ax+ny*ay, b=nx*bx+ny*by, c=-(nx*(q.X-p.X)+ny*(q.Y-p.Y));
            // Collinear rays keep the same complete route between the two fixed anchors;
            // moving their already-valid original endpoints cannot repair an invalid bridge.
            if(a==0&&b==0)continue;
            var (g,x,y)=Bezout(a,b); if(c%g!=0)continue;
            long d1=x*(c/g),d2=y*(c/g),s1=b/g,s2=-a/g,low=long.MinValue/4,high=long.MaxValue/4;
            if(!Bound(d1,s1,firstMinimum,firstLength-1,ref low,ref high)||!Bound(d2,s2,secondMinimum,secondLength-1,ref low,ref high)||low>high)continue;
            var events=new HashSet<long>{low,high};
            void Event(long numerator,long denominator)
            {
                if(denominator==0)return;
                var floor=Floor(numerator,denominator);
                for(var k=floor-1;k<=floor+2;k++)if(k>=low&&k<=high)events.Add(k);
            }
            long px=p.X-d1*ax,py=p.Y-d1*ay,qx=q.X+d2*bx,qy=q.Y+d2*by;
            long psx=-s1*ax,psy=-s1*ay,qsx=s2*bx,qsy=s2*by;
            void Point(long x2,long y2)
            {
                Event(nx*x2+ny*y2-2*(nx*px+ny*py),2*(nx*psx+ny*psy));
                Event(x2-2*px,2*psx);Event(y2-2*py,2*psy);
                Event(x2-2*qx,2*qsx);Event(y2-2*qy,2*qsy);
            }
            // Validity changes only at geometry boundaries. Test the integer neighbours of
            // those events, rather than scanning up to 500000 squared endpoint combinations.
            foreach(var road in state.Lines.Values)
            {
                foreach(var node in road.Nodes)Point(2L*node.X,2L*node.Y);
                for(var i=1;i<road.Nodes.Length;i++)
                {
                    var start=road.Nodes[i-1];var end=road.Nodes[i];long rx=end.X-start.X,ry=end.Y-start.Y;
                    Event(rx*(start.Y-py)-ry*(start.X-px),rx*psy-ry*psx);
                    Event(rx*(start.Y-qy)-ry*(start.X-qx),rx*qsy-ry*qsx);
                }
            }
            foreach(var cross in state.Crosses.Keys)Point(cross.Position.X2,cross.Position.Y2);
            foreach(var node in state.LogicalNodes.Values)
                foreach(var xx in new[]{node.Left,node.Right})foreach(var yy in new[]{node.Bottom,node.Top})Point((long)(xx*2),(long)(yy*2));
            Event(qx-px,psx-qsx);Event(qy-py,psy-qsy);Event(d2-d1,s1-s2);
            Event(-d1,s1);Event(-d2,s2);Event(-d1-d2,s1+s2);
            foreach(var k in events)moves.Add((checked((int)(d1+s1*k)),checked((int)(d2+s2*k))));
        }
        int Priority((int First,int Second) m)
        {
            long dx=q.X+m.Second*bx-(p.X-m.First*ax),dy=q.Y+m.Second*by-(p.Y-m.First*ay);
            if((dx==0&&dy==0)||(dx!=0&&dy!=0&&Math.Abs(dx)!=Math.Abs(dy)))return int.MaxValue;
            long firstDot=ax*dx+ay*dy,secondDot=bx*dx+by*dy;
            if(firstDot<0||secondDot<0)return int.MaxValue;
            return(firstDot==0?1:0)+(secondDot==0?1:0);
        }
        return moves.Where(m=>Priority(m)!=int.MaxValue).OrderBy(Priority)
            .ThenBy(m=>(long)Math.Abs(m.First)+Math.Abs(m.Second)).ThenBy(m=>Math.Max(Math.Abs(m.First),Math.Abs(m.Second)))
            .ThenBy(m=>m.First).ThenBy(m=>m.Second);
    }

    private static long RayLimit(LineNode anchor,long dx,long dy)=>Math.Min(dx==0?MapLimits.CoordinateMaximum:dx>0?MapLimits.CoordinateMaximum-anchor.X:anchor.X,
        dy==0?MapLimits.CoordinateMaximum:dy>0?MapLimits.CoordinateMaximum-anchor.Y:anchor.Y);
    private static bool Bound(long offset,long step,long minimum,long maximum,ref long low,ref long high)
    {
        if(step==0)return offset>=minimum&&offset<=maximum;
        if(step>0){low=Math.Max(low,Ceiling(minimum-offset,step));high=Math.Min(high,Floor(maximum-offset,step));}
        else{low=Math.Max(low,Ceiling(maximum-offset,step));high=Math.Min(high,Floor(minimum-offset,step));}
        return low<=high;
    }
    private static long Floor(long a,long b){if(b<0){a=-a;b=-b;}var q=a/b;return a%b<0?q-1:q;}
    private static long Ceiling(long a,long b)=>-Floor(-a,b);
    private static (long G,long X,long Y) Bezout(long a,long b)
    {
        long r=Math.Abs(a),s=Math.Abs(b),x=1,y=0,u=0,v=1;
        while(s!=0){var q=r/s;(r,s)=(s,r-q*s);(x,u)=(u,x-q*u);(y,v)=(v,y-q*v);}
        return(r,x*Math.Sign(a),y*Math.Sign(b));
    }
}
