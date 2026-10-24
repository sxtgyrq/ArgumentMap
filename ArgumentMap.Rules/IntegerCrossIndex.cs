using ArgumentMap.Contracts;

namespace ArgumentMap.Rules;

// An immutable geometry snapshot. Grid samples are temporary lookup keys, never LineNodes.
// Cross state is deliberately not cached: before/after guards must inspect current passage settings.
public sealed class IntegerCrossIndex
{
    public sealed record Contact(string OtherRoadCode,int OtherSegment,CrossPosition Position);
    private sealed record Segment(string RoadCode,int Order,LineNode A,LineNode B);
    private readonly record struct Meeting(int First,int Second,CrossPosition Position);
    private struct Occupants(int first)
    {
        public int First=first;
        public List<int>? Others;
    }
    private readonly LineSnapshot[] lines;
    private readonly Dictionary<string,LineSnapshot> byCode;
    private readonly Dictionary<(string Road,int Segment),Contact[]> contacts=[];
    private readonly CrossKey[] intersections;
    // Bound allocations on very long maps/pre-save geometry. The exact fallback
    // has identical integer-only semantics and does not silently omit candidates.
    private const long MaximumGridEntries=2_000_000;
    public bool UsesPointIndex {get;}
    public long EnumeratedPointEntries {get;}
    public IReadOnlyList<CrossKey> Intersections=>intersections;

    public IntegerCrossIndex(IEnumerable<LineSnapshot> source)
    {
        lines=source.OrderBy(l=>l.Code,StringComparer.Ordinal).Select(l=>l with{Nodes=l.Nodes.ToArray()}).ToArray();
        byCode=lines.ToDictionary(l=>l.Code,StringComparer.Ordinal);
        var segments=lines.SelectMany(l=>Enumerable.Range(0,Math.Max(0,l.Nodes.Length-1))
            .Select(i=>new Segment(l.Code,i,l.Nodes[i],l.Nodes[i+1]))).ToArray();
        EnumeratedPointEntries=segments.Sum(s=>(long)Math.Max(Math.Abs((long)s.B.X-s.A.X),Math.Abs((long)s.B.Y-s.A.Y))+1);
        // A road never intersects itself in the Cross model. Avoid enumerating a
        // long isolated road when there cannot be an inter-road candidate.
        if(lines.Count(l=>l.Nodes.Length>=2)<2){intersections=[];return;}
        UsesPointIndex=EnumeratedPointEntries<=MaximumGridEntries && segments.All(s=>LineGeometry.IsEightDirection(s.A,s.B));
        var meetings=new List<Meeting>();
        if(UsesPointIndex)
        {
            var grid=new Dictionary<long,Occupants>();
            for(var i=0;i<segments.Length;i++)
            {
                var segment=segments[i];
                var dx=Math.Sign(segment.B.X-segment.A.X);var dy=Math.Sign(segment.B.Y-segment.A.Y);
                var steps=Math.Max(Math.Abs(segment.B.X-segment.A.X),Math.Abs(segment.B.Y-segment.A.Y));
                for(var step=0;step<=steps;step++)
                {
                    var x=segment.A.X+step*dx;var y=segment.A.Y+step*dy;
                    var key=((long)x<<32)|(uint)y;
                    if(!grid.TryGetValue(key,out var bucket)){grid.Add(key,new(i));continue;}
                    AddMeeting(bucket.First,i,x,y);
                    if(bucket.Others is not null)foreach(var previous in bucket.Others)AddMeeting(previous,i,x,y);
                    (bucket.Others??=[]).Add(i);grid[key]=bucket;
                }
            }
            void AddMeeting(int a,int b,int x,int y)
            {
                var first=segments[a];var second=segments[b];
                if(first.RoadCode==second.RoadCode)return;
                // Parallel/collinear occupancy is not a unique intersection. Save
                // validation separately rejects positive-length overlaps.
                long ax=first.B.X-first.A.X,ay=first.B.Y-first.A.Y,bx=second.B.X-second.A.X,by=second.B.Y-second.A.Y;
                if(ax*by-ay*bx==0)return;
                meetings.Add(new(a,b,new(checked(x*2),checked(y*2))));
            }
        }
        else
        {
            for(var i=0;i<segments.Length;i++)for(var j=i+1;j<segments.Length;j++)
                if(segments[i].RoadCode!=segments[j].RoadCode && LineGeometry.TryCrossIntersection(segments[i].A,segments[i].B,segments[j].A,segments[j].B,out var point))
                    meetings.Add(new(i,j,point));
        }
        // Match the historical road-pair / segment-pair order, including the first
        // reported violation and the final canonical location for repeated hits.
        var ordered=meetings.OrderBy(m=>segments[m.First].RoadCode,StringComparer.Ordinal)
            .ThenBy(m=>segments[m.Second].RoadCode,StringComparer.Ordinal)
            .ThenBy(m=>segments[m.First].Order).ThenBy(m=>segments[m.Second].Order).ToArray();
        var unique=new HashSet<CrossKey>();var keys=new List<CrossKey>();
        var perSegment=new Dictionary<(string Road,int Segment),List<Contact>>();
        foreach(var meeting in ordered)
        {
            var a=segments[meeting.First];var b=segments[meeting.Second];
            var key=CrossKey.Create(a.RoadCode,b.RoadCode,meeting.Position);
            if(unique.Add(key))keys.Add(key);
            Add(a,b,meeting.Position);Add(b,a,meeting.Position);
        }
        foreach(var (key,value) in perSegment)
            contacts.Add(key,value.OrderBy(c=>c.OtherRoadCode,StringComparer.Ordinal).ThenBy(c=>c.OtherSegment).ToArray());
        intersections=keys.ToArray();
        void Add(Segment from,Segment to,CrossPosition point)
        {
            var key=(from.RoadCode,from.Order);
            if(!perSegment.TryGetValue(key,out var values))perSegment.Add(key,values=[]);
            values.Add(new(to.RoadCode,to.Order,point));
        }
    }

    public IReadOnlyList<Contact> ForSegment(string road,int segment)=>contacts.GetValueOrDefault((road,segment))??[];

    // Exact comparison also detects in-place array changes; version numbers alone
    // are insufficient while a transaction constructs or moves a new shape.
    public bool Matches(IEnumerable<LineSnapshot> current)
    {
        var count=0;
        foreach(var line in current)
        {
            count++;
            if(!byCode.TryGetValue(line.Code,out var old)||!old.Nodes.SequenceEqual(line.Nodes))return false;
        }
        return count==lines.Length;
    }
}
