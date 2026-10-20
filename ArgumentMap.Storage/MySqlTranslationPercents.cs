using ArgumentMap.Contracts;
using MySqlConnector;

namespace ArgumentMap.Storage;

public sealed partial class MySqlLineRepository
{
    // Rigid translation changes Cross primary keys and replaces LineNodes (cascading LN
    // projections). Preserve unchanged DECIMAL values before either deletion; converting
    // their 18 digits through the transport double would otherwise round the last digits.
    private static (Dictionary<CrossKey,(decimal First,decimal Second)> Crosses,Dictionary<LogicalNodePositionKey,decimal> Positions)
        ReadTranslationPercents(MySqlConnection connection,MySqlTransaction transaction,LineStoreState state,
            Dictionary<string,LineSnapshot> beforeLines,Dictionary<CrossKey,CrossSnapshot> beforeCrosses,
            Dictionary<LogicalNodePositionKey,LogicalNodePosition> beforePositions)
    {
        var translations=new Dictionary<string,(int X,int Y)>(StringComparer.Ordinal);
        foreach(var old in beforeLines.Values)
        {
            if(old.Nodes.Length<2||!state.Lines.TryGetValue(old.Code,out var now)||old.Nodes.Length!=now.Nodes.Length)continue;
            var dx=now.Nodes[0].X-old.Nodes[0].X;var dy=now.Nodes[0].Y-old.Nodes[0].Y;
            if((dx!=0||dy!=0)&&old.Nodes.Zip(now.Nodes).All(p=>p.Second.X-p.First.X==dx&&p.Second.Y-p.First.Y==dy&&p.Second.Order==p.First.Order))
                translations.Add(old.Code,(dx,dy));
        }
        var keys=new Dictionary<CrossKey,CrossKey>();
        foreach(var old in beforeCrosses.Values)
        {
            if(!translations.TryGetValue(old.Key.RoadCode1,out var a)||!translations.TryGetValue(old.Key.RoadCode2,out var b)||a!=b)continue;
            var key=old.Key with{Position=new(old.Key.Position.X2+2*a.X,old.Key.Position.Y2+2*a.Y)};
            if(state.Crosses.TryGetValue(key,out var now)&&old.RoadOrder1==now.RoadOrder1&&old.RoadOrder2==now.RoadOrder2&&old.Percent1==now.Percent1&&old.Percent2==now.Percent2)
                keys.Add(old.Key,key);
        }
        var crosses=new Dictionary<CrossKey,(decimal,decimal)>();
        if(keys.Count>0)
        {
            using var command=Command(connection,transaction,"SELECT RoadCode1,RoadCode2,X,Y,Percent1,Percent2 FROM Crosses");
            using var reader=command.ExecuteReader();while(reader.Read())
            {
                var oldKey=new CrossKey(reader.GetString(0),reader.GetString(1),new((int)(reader.GetDecimal(2)*2),(int)(reader.GetDecimal(3)*2)));
                if(keys.TryGetValue(oldKey,out var key))crosses.Add(key,(reader.GetDecimal(4),reader.GetDecimal(5)));
            }
        }
        var positions=new Dictionary<LogicalNodePositionKey,decimal>();
        var positionKeys=beforePositions.Values.Where(p=>translations.ContainsKey(p.RoadCode)&&state.LogicalNodePositions.TryGetValue(p.Key,out var now)&&p.RoadPercent==now.RoadPercent).Select(p=>p.Key).ToHashSet();
        if(positionKeys.Count>0)
        {
            using var command=Command(connection,transaction,"SELECT LogicalNodeCode,RoadCode,RoadOrder,RoadPercent FROM LogicalNodePositions");
            using var reader=command.ExecuteReader();while(reader.Read())
            {
                var key=new LogicalNodePositionKey(reader.GetString(0),reader.GetString(1),reader.GetInt32(2));
                if(positionKeys.Contains(key))positions.Add(key,reader.GetDecimal(3));
            }
        }
        return(crosses,positions);
    }
}
