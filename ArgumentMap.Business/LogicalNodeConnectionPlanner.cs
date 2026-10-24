using System.Diagnostics;
using ArgumentMap.Contracts;
using ArgumentMap.Rules;
using ArgumentMap.Storage;

namespace ArgumentMap.Business;

/// <summary>Bounded multi-source visibility-grid search, entirely on an unlocked snapshot.</summary>
internal sealed partial class LogicalNodeConnectionPlanner(LineStoreState map,LogicalNodeSnapshot source,LogicalNodeSnapshot target,string code,CancellationToken cancellation,LogicalNodeConnectionKind kind=LogicalNodeConnectionKind.Direct)
{
    private readonly record struct Point(int X,int Y);
    private sealed record Port(Point Tail,Point Gate,int Heading);
    private static readonly (int X,int Y)[] Directions=[(1,0),(1,1),(0,1),(-1,1),(-1,0),(-1,-1),(0,-1),(1,-1)];
    private readonly Stopwatch timer=Stopwatch.StartNew();
    private readonly HashSet<Point> occupied=map.Lines.Values.SelectMany(l=>l.Nodes).Select(n=>new Point(n.X,n.Y)).ToHashSet();
    private readonly (LineNode A,LineNode B)[] segments=map.Lines.Values.SelectMany(l=>l.Nodes.Zip(l.Nodes.Skip(1)).Select(p=>(p.First,p.Second))).ToArray();
    private readonly HashSet<CrossPosition> crossings=[];
    private readonly Dictionary<(Point,Point,string?),bool> edges=[];
    private LineNode Node(Point p)=>new(code,0,p.X,p.Y);
    private bool Expired(){cancellation.ThrowIfCancellationRequested();return timer.Elapsed.TotalSeconds>12;}
    private static bool InRange(Point p)=>p.X>=0&&p.Y>=0&&p.X<=MapLimits.CoordinateMaximum&&p.Y<=MapLimits.CoordinateMaximum;
    private static double Length(Point a,Point b)=>Math.Sqrt((double)(b.X-a.X)*(b.X-a.X)+(double)(b.Y-a.Y)*(b.Y-a.Y));
    private static bool Turn(int a,int b){var d=Math.Abs(a-b);d=Math.Min(d,8-d);return d<=2;}// travel turn 0/45/90 => interior 180/135/90
    private IEnumerable<Port> Ports(LogicalNodeSnapshot n,bool start)
    {
        for(var axis=0;axis<2;axis++)
        for(var cross=(int)decimal.Ceiling(axis==0?n.Bottom:n.Left);cross<=(int)decimal.Floor(axis==0?n.Top:n.Right);cross++)
        for(var sign=-1;sign<=1;sign+=2)
        {
            var low=axis==0?n.Left:n.Bottom;var high=axis==0?n.Right:n.Top;
            var tail=sign==1?(int)decimal.Floor(low-5):(int)decimal.Ceiling(high+5);
            var gate=sign==1?(int)decimal.Ceiling(high):(int)decimal.Floor(low);
            var p=new Port(axis==0?new(tail,cross):new(cross,tail),axis==0?new(gate,cross):new(cross,gate),axis==0?(sign==1?0:4):(sign==1?2:6));
            if(!start)p=new(p.Tail,p.Gate,(p.Heading+4)%8);
            if(start&&kind==LogicalNodeConnectionKind.GoTo)
            {
                var direction=Directions[p.Heading];
                while(occupied.Contains(p.Gate))p=p with{Gate=new(p.Gate.X+direction.X,p.Gate.Y+direction.Y)};
            }
            if(InRange(p.Tail)&&InRange(p.Gate)&&Clear(p.Tail,p.Gate,n.Code))yield return p;
        }
    }
    private bool Clear(Point a,Point b,string? allowedSquare=null)
    {
        if(edges.TryGetValue((a,b,allowedSquare),out var result))return result;
        bool Check()
        {
            if(a==b||occupied.Contains(a)||occupied.Contains(b))return false;
            var first=Node(a);var last=Node(b);
            foreach(var square in map.LogicalNodes.Values)
                if(square.Code!=allowedSquare&&LogicalNodeConnectionGeometry.Touches(square,first,last))return false;
            foreach(var cross in crossings)
            {
                long ax=2L*a.X,ay=2L*a.Y,bx=2L*b.X,by=2L*b.Y;
                if((bx-ax)*(cross.Y2-ay)==(by-ay)*(cross.X2-ax)&&cross.X2>=Math.Min(ax,bx)&&cross.X2<=Math.Max(ax,bx)&&cross.Y2>=Math.Min(ay,by)&&cross.Y2<=Math.Max(ay,by))return false;
            }
            foreach(var (c,d) in segments)
            {
                if(Math.Max(a.X,b.X)<Math.Min(c.X,d.X)||Math.Min(a.X,b.X)>Math.Max(c.X,d.X)||Math.Max(a.Y,b.Y)<Math.Min(c.Y,d.Y)||Math.Min(a.Y,b.Y)>Math.Max(c.Y,d.Y))continue;
                if(LineGeometry.Overlaps(first,last,c,d)||LineGeometry.TryIntersection(first,last,c,d,out _)&&!LineGeometry.IsPerpendicular(first,last,c,d))return false;
            }
            return true;
        }
        result=Check();edges[(a,b,allowedSquare)]=result;edges[(b,a,allowedSquare)]=result;return result;
    }
    internal ServiceResult<LogicalNodeConnectionPlan> Find()
    {
        if(kind==LogicalNodeConnectionKind.Find)return FindFromSource();
        // Derive even uncalculated intersections: passing an old pair's crossing is forbidden.
        for(var i=0;i<segments.Length;i++)
        {
            if(i%32==0&&Expired())return NotFound();
            for(var j=0;j<i;j++)
                if(segments[i].A.LineCode!=segments[j].A.LineCode&&LineGeometry.TryIntersection(segments[i].A,segments[i].B,segments[j].A,segments[j].B,out var p))crossings.Add(p);
        }
        var starts=Ports(source,true).ToArray();
        if(kind==LogicalNodeConnectionKind.GoTo)return FindGoTo(starts);
        var ends=Ports(target,false).ToArray();
        if(starts.Length==0||ends.Length==0)return NotFound();
        foreach(var margin in new[]{32,128,512,2048})
        {
            if(Expired())break;
            var result=Search(starts,ends,margin);
            if(result is not null)return ServiceResult<LogicalNodeConnectionPlan>.Ok(result,"连接路线已规划，尚未保存；确认后依次保存线路、K 计算和设置 Cross 不连通。");
        }
        return NotFound();
    }
    private LogicalNodeConnectionPlan? Search(Port[] starts,Port[] ends,int margin)
    {
        var ports=starts.Concat(ends).ToArray();
        var left=Math.Max(0,ports.Min(p=>Math.Min(p.Tail.X,p.Gate.X))-margin);var right=Math.Min(500000,ports.Max(p=>Math.Max(p.Tail.X,p.Gate.X))+margin);
        var bottom=Math.Max(0,ports.Min(p=>Math.Min(p.Tail.Y,p.Gate.Y))-margin);var top=Math.Min(500000,ports.Max(p=>Math.Max(p.Tail.Y,p.Gate.Y))+margin);
        var xs=ports.SelectMany(p=>new[]{p.Tail.X,p.Gate.X}).ToHashSet();var ys=ports.SelectMany(p=>new[]{p.Tail.Y,p.Gate.Y}).ToHashSet();
        var extraX=new HashSet<int>{left,right};var extraY=new HashSet<int>{bottom,top};
        void Add(int x,int y){for(var d=-1;d<=1;d++){if(x+d>=left&&x+d<=right)extraX.Add(x+d);if(y+d>=bottom&&y+d<=top)extraY.Add(y+d);}}
        foreach(var n in map.LogicalNodes.Values)
            if(n.Right>=left&&n.Left<=right&&n.Top>=bottom&&n.Bottom<=top)
            {Add((int)decimal.Floor(n.Left),(int)decimal.Floor(n.Bottom));Add((int)decimal.Ceiling(n.Right),(int)decimal.Ceiling(n.Top));}
        foreach(var p in occupied)if(p.X>=left&&p.X<=right&&p.Y>=bottom&&p.Y<=top)Add(p.X,p.Y);
        foreach(var p in crossings)if(p.X>=left&&p.X<=right&&p.Y>=bottom&&p.Y<=top)Add((int)Math.Floor(p.X),(int)Math.Floor(p.Y));
        double AxisDistance(int x,bool horizontal)=>Math.Min(Math.Abs(x-(double)(horizontal?source.X:source.Y)),Math.Abs(x-(double)(horizontal?target.X:target.Y)));
        foreach(var x in extraX.OrderBy(x=>AxisDistance(x,true)).ThenBy(x=>x).Take(Math.Max(0,128-xs.Count)))xs.Add(x);
        foreach(var y in extraY.OrderBy(y=>AxisDistance(y,false)).ThenBy(y=>y).Take(Math.Max(0,128-ys.Count)))ys.Add(y);
        var xValues=xs.Order().ToArray();var yValues=ys.Order().ToArray();var xi=xValues.Select((x,i)=>(x,i)).ToDictionary(p=>p.x,p=>p.i);var yi=yValues.Select((y,i)=>(y,i)).ToDictionary(p=>p.y,p=>p.i);
        int Id(Point p)=>yi[p.Y]*xValues.Length+xi[p.X];
        Point At(int id)=>new(xValues[id%xValues.Length],yValues[id/xValues.Length]);
        var goals=ends.GroupBy(p=>Id(p.Gate)).ToDictionary(g=>g.Key,g=>g.ToArray());
        var size=xValues.Length*yValues.Length*8;var costs=Enumerable.Repeat(double.PositiveInfinity,size).ToArray();var parents=Enumerable.Repeat(-2,size).ToArray();var roots=new int[size];
        var queue=new PriorityQueue<int,(double Cost,int Tie)>();var sequence=0;
        double Estimate(Point p)=>ends.Min(e=>Length(p,e.Gate));
        for(var i=0;i<starts.Length;i++)
        {
            var port=starts[i];var state=Id(port.Gate)*8+port.Heading;var cost=Length(port.Tail,port.Gate);
            if(cost>=costs[state])continue;costs[state]=cost;parents[state]=-1;roots[state]=i;queue.Enqueue(state,(cost+Estimate(port.Gate),sequence++));
        }
        var visited=new bool[size];var expanded=0;
        while(queue.TryDequeue(out var current,out _))
        {
            if(visited[current])continue;visited[current]=true;
            if(++expanded%128==0&&Expired())return null;
            var id=current/8;var direction=current%8;var at=At(id);
            if(goals.TryGetValue(id,out var matching))
                foreach(var end in matching.Where(p=>Turn(direction,p.Heading)))
                {
                    var path=new List<Point>{end.Tail};var step=current;
                    while(step>=0){path.Add(At(step/8));if(parents[step]==-1){path.Add(starts[roots[step]].Tail);break;}step=parents[step];}
                    path.Reverse();var reduced=new List<Point>();
                    foreach(var p in path)
                    {
                        if(reduced.Count>0&&reduced[^1]==p)continue;
                        if(reduced.Count>=2){var a=reduced[^2];var b=reduced[^1];if((long)(b.X-a.X)*(p.Y-b.Y)==(long)(b.Y-a.Y)*(p.X-b.X)&&((long)(b.X-a.X)*(p.X-b.X)+(long)(b.Y-a.Y)*(p.Y-b.Y))>0)reduced.RemoveAt(reduced.Count-1);}
                        reduced.Add(p);
                    }
                    if(reduced.Count>128)continue;
                    var plan=new LogicalNodeConnectionPlan(LogicalNodeConnectionAnchor.From(source),LogicalNodeConnectionAnchor.From(target),code,reduced.Select((p,i)=>new LineNode(code,i,p.X,p.Y)).ToArray());
                    var working=LineTopologyService.Clone(map);working.Lines[code]=new(code,"route",false,0,[]);
                    if(!LogicalNodeConnectionGeometry.ValidateTargets(working,plan).Success)continue;
                    if(LineSaveService.ValidateShape(working,new(code,"route",0,plan.Nodes),plan.Nodes) is not null)continue;
                    var divided=LineSubdivision.Divide(working,plan.Nodes);
                    if(divided.Success&&divided.Data!.Length<=4096)return plan with{Nodes=divided.Data};
                }
            for(var nextHeading=0;nextHeading<8;nextHeading++)
            {
                if(!Turn(direction,nextHeading))continue;
                var (dx,dy)=Directions[nextHeading];Point? next=null;
                if(dx==0){var y=yi[at.Y]+dy;if(y>=0&&y<yValues.Length)next=new(at.X,yValues[y]);}
                else if(dy==0){var x=xi[at.X]+dx;if(x>=0&&x<xValues.Length)next=new(xValues[x],at.Y);}
                else for(var x=xi[at.X]+dx;x>=0&&x<xValues.Length;x+=dx)
                {var y=at.Y+dy*Math.Abs(xValues[x]-at.X);if(yi.ContainsKey(y)){next=new(xValues[x],y);break;}}
                if(next is not {} p||!Clear(at,p))continue;
                var following=Id(p)*8+nextHeading;var cost=costs[current]+Length(at,p)+(direction==nextHeading?0:8);
                if(cost>=costs[following])continue;costs[following]=cost;parents[following]=current;roots[following]=roots[current];queue.Enqueue(following,(cost+Estimate(p),sequence++));
            }
        }
        return null;
    }
    private static ServiceResult<LogicalNodeConnectionPlan> NotFound()=>ServiceResult<LogicalNodeConnectionPlan>.Fail("ConnectionRouteNotFound","当前规划范围内未找到符合线路规则且避开其他 LN 的路线，请调整 LN 位置或手工绘制。");
}
