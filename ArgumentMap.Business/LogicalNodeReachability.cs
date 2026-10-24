using ArgumentMap.Contracts;
using ArgumentMap.Rules;
using ArgumentMap.Storage;

namespace ArgumentMap.Business;

/// <summary>Reverse graph of CURRENT road geometry. No K/J writes, stale projections or implicit transfers.</summary>
internal sealed class LogicalNodeReachability
{
    // A private direction view: reverse travel on every road and reverse transfer
    // permissions. This changes neither live geometry nor stored Cross settings.
    internal static LineStoreState ReverseDirections(LineStoreState map)
    {
        var reversed=new LineStoreState();
        foreach(var road in map.Lines.Values)
            reversed.Lines.Add(road.Code,road with{Nodes=road.Nodes.Reverse().Select((n,i)=>n with{Order=i}).ToArray()});
        foreach(var node in map.LogicalNodes.Values)reversed.LogicalNodes.Add(node.Code,node);
        foreach(var cross in map.Crosses.Values)
            reversed.Crosses.Add(cross.Key,cross with{State=cross.State switch
            {
                CrossState.Road1ToRoad2=>CrossState.Road2ToRoad1,
                CrossState.Road2ToRoad1=>CrossState.Road1ToRoad2,
                _=>cross.State
            }});
        return reversed;
    }

    internal static LogicalNodeContinuationPoint[]? ForwardPath(LineStoreState map,LogicalNodeSnapshot source,
        LogicalNodeConnectionJoin join,string? excludedRoad=null,CancellationToken cancellation=default,Func<bool>? expired=null)
        =>Build(ReverseDirections(map),source,excludedRoad,cancellation,expired).Path(join)?.Reverse().ToArray();

    internal readonly record struct Position(decimal X, decimal Y);
    internal sealed record Piece(LineSnapshot Road, int Segment, Position A, Position B, int Next);
    private sealed record Vertex(string Road, Position Point);
    private readonly List<Vertex> vertices=[];
    private readonly Dictionary<int,LogicalNodeContinuationPoint> arrivals=[];
    private int[] next=[];
    internal List<Piece> Pieces { get; } = [];

    internal static LogicalNodeReachability Build(LineStoreState map,LogicalNodeSnapshot target,string? excludedRoad=null,
        CancellationToken cancellation=default,Func<bool>? expired=null)
    {
        var graph=new LogicalNodeReachability();
        var roads=map.Lines.Values.Where(r=>r.Code!=excludedRoad&&r.Nodes.Length>=2).OrderBy(r=>r.Code).ToArray();
        // Exact distance in grid steps, rather than index + normalized percent: thirds such as
        // 1/3 must not drift away from integer Cross coordinates after decimal multiplication.
        var offsets=roads.ToDictionary(r=>r.Code,r=>
        {
            var at=new decimal[r.Nodes.Length];
            for(var i=1;i<at.Length;i++)at[i]=at[i-1]+Math.Max(Math.Abs(r.Nodes[i].X-r.Nodes[i-1].X),Math.Abs(r.Nodes[i].Y-r.Nodes[i-1].Y));
            return at;
        });
        var events=roads.ToDictionary(r=>r.Code,r=>new SortedSet<decimal>(offsets[r.Code]));
        var seeds=new List<(string Road,decimal At,LogicalNodeContinuationPoint Center)>();
        void Check()
        {
            cancellation.ThrowIfCancellationRequested();
            if(expired?.Invoke()==true)throw new TimeoutException();
        }
        int Segment(LineSnapshot road,decimal q)
        {var index=Array.BinarySearch(offsets[road.Code],q);return Math.Min(index>=0?index:~index-1,road.Nodes.Length-2);}
        Position At(LineSnapshot road,decimal q)
        {
            var i=Segment(road,q);var t=q-offsets[road.Code][i];var a=road.Nodes[i];var b=road.Nodes[i+1];
            return new(a.X+Math.Sign(b.X-a.X)*t,a.Y+Math.Sign(b.Y-a.Y)*t);
        }
        decimal? Location(LineSnapshot road,CrossPosition point)
        {
            if(LineGeometry.FindPointOnLine(road.Nodes,point) is not {} at)return null;
            var a=road.Nodes[at.Segment];var b=road.Nodes[at.Segment+1];
            return offsets[road.Code][at.Segment]+Math.Max(Math.Abs(point.X2/2m-a.X),Math.Abs(point.Y2/2m-a.Y));
        }
        // A crossing only enables switching when both participating roads STILL contain it.
        // Unset/missing/stale-away crossings never acquire a transfer by merely being near a road.
        var transfers=new List<(CrossSnapshot Cross,decimal A,decimal B)>();
        var byCode=roads.ToDictionary(r=>r.Code);
        foreach(var cross in map.Crosses.Values.OrderBy(c=>c.Key))
        {
            Check();
            if(!cross.CanTransfer||LogicalNodeRules.ContainsAny(map,cross.Key.Position)||
                !byCode.TryGetValue(cross.Key.RoadCode1,out var a)||!byCode.TryGetValue(cross.Key.RoadCode2,out var b)||
                Location(a,cross.Key.Position) is not {} qa||Location(b,cross.Key.Position) is not {} qb)continue;
            events[a.Code].Add(qa);events[b.Code].Add(qb);transfers.Add((cross,qa,qb));
        }
        foreach(var road in roads)
        for(var i=0;i<road.Nodes.Length-1;i++)
        {
            Check();var a=road.Nodes[i];var b=road.Nodes[i+1];
            foreach(var square in map.LogicalNodes.Values)
            {
                if(!Clip(square,a,b,out var lo,out var hi))continue;
                var offset=offsets[road.Code][i];events[road.Code].Add(offset+lo);events[road.Code].Add(offset+hi);
                if(square.Code!=target.Code||a.X!=b.X&&a.Y!=b.Y||lo==hi)continue;
                var center=new LogicalNodeContinuationPoint(road.Code,a.Y==b.Y?square.X:a.X,a.X==b.X?square.Y:a.Y);
                if(road.Properties.PassInDirection==1)seeds.Add((road.Code,offset+lo,center));
                if(road.Properties.PassInOpposeDirection==1)seeds.Add((road.Code,offset+hi,center));
            }
            if(events[road.Code].Count>100000)throw new TimeoutException();
        }
        var ids=new Dictionary<(string Road,decimal At),int>();
        foreach(var road in roads)
        foreach(var q in events[road.Code])
        {if(graph.vertices.Count>=200000)throw new TimeoutException();ids.Add((road.Code,q),graph.vertices.Count);graph.vertices.Add(new(road.Code,At(road,q)));}
        var incoming=Enumerable.Range(0,graph.vertices.Count).Select(_=>new List<int>()).ToArray();
        var edges=new List<Piece>();
        foreach(var road in roads)
        {
            Check();var cuts=events[road.Code].ToArray();
            for(var i=0;i<cuts.Length-1;i++)
            {
                var middle=At(road,(cuts[i]+cuts[i+1])/2);
                if(map.LogicalNodes.Values.Any(n=>n.Contains(middle.X,middle.Y)))continue;
                var a=ids[(road.Code,cuts[i])];var b=ids[(road.Code,cuts[i+1])];
                var segment=Segment(road,cuts[i]);
                if(road.Properties.PassInDirection==1)
                {incoming[b].Add(a);edges.Add(new(road,segment,graph.vertices[a].Point,graph.vertices[b].Point,b));}
                if(road.Properties.PassInOpposeDirection==1)
                {incoming[a].Add(b);edges.Add(new(road,segment,graph.vertices[a].Point,graph.vertices[b].Point,a));}
            }
        }
        foreach(var (cross,qa,qb) in transfers)
        {
            var a=ids[(cross.Key.RoadCode1,qa)];var b=ids[(cross.Key.RoadCode2,qb)];
            if(cross.AllowsTransfer(cross.Key.RoadCode1,cross.Key.RoadCode2))incoming[b].Add(a);
            if(cross.AllowsTransfer(cross.Key.RoadCode2,cross.Key.RoadCode1))incoming[a].Add(b);
        }
        graph.next=Enumerable.Repeat(-2,graph.vertices.Count).ToArray();var queue=new Queue<int>();
        foreach(var seed in seeds)
        {
            var id=ids[(seed.Road,seed.At)];if(graph.next[id]!=-2)continue;
            graph.next[id]=-1;graph.arrivals.Add(id,seed.Center);queue.Enqueue(id);
        }
        bool Blocked(int id)=>!graph.arrivals.ContainsKey(id)&&map.LogicalNodes.Values.Any(n=>n.Contains(graph.vertices[id].Point.X,graph.vertices[id].Point.Y));
        while(queue.TryDequeue(out var at))
        {
            Check();
            if(Blocked(at))continue; // Also stop a diagonal merely touching another LN's corner.
            foreach(var previous in incoming[at])
                if(graph.next[previous]==-2){graph.next[previous]=at;queue.Enqueue(previous);}
        }
        graph.Pieces.AddRange(edges.Where(e=>graph.next[e.Next]!=-2&&!Blocked(e.Next)));
        return graph;
    }

    internal LogicalNodeContinuationPoint[]? Path(LogicalNodeConnectionJoin join)
    {
        var x=join.Position.X2/2m;var y=join.Position.Y2/2m;
        foreach(var piece in Pieces.Where(p=>p.Road.Code==join.RoadCode))
        {
            if(!Contains(piece,new(x,y)))continue;
            var path=new List<LogicalNodeContinuationPoint>{new(join.RoadCode,x,y)};var step=piece.Next;var count=0;
            void Add(LogicalNodeContinuationPoint p)
            {
                if(path[^1]==p)return;
                if(path.Count>=2&&path[^2].RoadCode==p.RoadCode&&path[^1].RoadCode==p.RoadCode)
                {
                    var a=path[^2];var b=path[^1];var dx=b.X-a.X;var dy=b.Y-a.Y;var ex=p.X-b.X;var ey=p.Y-b.Y;
                    if(dx*ey-dy*ex==0&&dx*ex+dy*ey>0)path.RemoveAt(path.Count-1);
                }
                path.Add(p);
            }
            while(step>=0&&++count<=vertices.Count)
            {
                var v=vertices[step];Add(new(v.Road,v.Point.X,v.Point.Y));
                if(next[step]==-1){Add(arrivals[step]);return path.Count<=4096?path.ToArray():null;}
                step=next[step];
            }
        }
        return null;
    }
    internal static bool Contains(Piece piece,Position point)=>
        (piece.B.X-piece.A.X)*(point.Y-piece.A.Y)==(piece.B.Y-piece.A.Y)*(point.X-piece.A.X)&&
        point.X>=Math.Min(piece.A.X,piece.B.X)&&point.X<=Math.Max(piece.A.X,piece.B.X)&&
        point.Y>=Math.Min(piece.A.Y,piece.B.Y)&&point.Y<=Math.Max(piece.A.Y,piece.B.Y);

    private static bool Clip(LogicalNodeSnapshot square,LineNode a,LineNode b,out decimal low,out decimal high)
    {
        decimal lo=0,hi=Math.Max(Math.Abs(b.X-a.X),Math.Abs(b.Y-a.Y));
        bool Axis(decimal origin,decimal delta,decimal min,decimal max)
        {
            if(delta==0)return origin>=min&&origin<=max;
            var x=(min-origin)/delta;var y=(max-origin)/delta;
            lo=Math.Max(lo,Math.Min(x,y));hi=Math.Min(hi,Math.Max(x,y));return lo<=hi;
        }
        var hit=Axis(a.X,Math.Sign(b.X-a.X),square.Left,square.Right)&&Axis(a.Y,Math.Sign(b.Y-a.Y),square.Bottom,square.Top);
        low=lo;high=hi;return hit;
    }
}
