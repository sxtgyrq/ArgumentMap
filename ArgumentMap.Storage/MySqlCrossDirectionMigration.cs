using MySqlConnector;

namespace ArgumentMap.Storage;

public sealed partial class MySqlLineRepository
{
    private void UpgradeCrossDirections(MySqlConnection connection)
    {
        using var version = connection.CreateCommand();
        version.CommandText = "SELECT SchemaVersion FROM MapMetadata WHERE Id=1";
        var current = Convert.ToInt32(version.ExecuteScalar());
        if (current is 10 or 11 or 12 or 13 or 14 or 15 or 16 or 17 or 18 or 19 or 20 or 21 or 22 or 23 or 24 or 25 or 26 or 27 or 28 or 29 or 30 or 31) return;
        if (current != 9) throw new InvalidOperationException("数据库架构版本不兼容。");
        // The schema lease excludes business transactions. One atomic ALTER keeps all existing
        // coordinate, code, segment and version checks while allowing the old one-way states 2/3.
        // A retry after ALTER but before the metadata write can repeat the same ALTER safely.
        using var constraint=connection.CreateCommand();
        constraint.CommandText="SELECT CONSTRAINT_NAME FROM information_schema.TABLE_CONSTRAINTS WHERE CONSTRAINT_SCHEMA=@db AND TABLE_NAME='Crosses' AND CONSTRAINT_TYPE='CHECK' AND CONSTRAINT_NAME IN ('AM9_CK_Cross','AM15_CK_Cross','AM16_CK_Cross')";
        constraint.Parameters.AddWithValue("@db",DatabaseName);
        var constraintName=Convert.ToString(constraint.ExecuteScalar());
        if(string.IsNullOrEmpty(constraintName))throw new InvalidOperationException("缺少 Cross 数据约束，无法恢复架构升级。");
        using var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE Crosses DROP CHECK AM9_CK_Cross, ADD CONSTRAINT AM9_CK_Cross CHECK (" +
            "REGEXP_LIKE(Line1Code, '^[A-Z]{10}$', 'c') AND REGEXP_LIKE(Line2Code, '^[A-Z]{10}$', 'c') AND Line1Code > Line2Code " +
            "AND X BETWEEN 0 AND 500000 AND Y BETWEEN 0 AND 500000 AND X*2=FLOOR(X*2) AND Y*2=FLOOR(Y*2) " +
            "AND Segment1>=0 AND Segment2>=0 AND Line1Version>=0 AND Line2Version>=0 AND CrossState IN (-1,0,1,2,3) AND Version>=0)";
        if(ColumnExists(connection,"Crosses","RoadCode1"))
            foreach(var (oldName,newName) in CrossRoadColumnNames)alter.CommandText=alter.CommandText.Replace(oldName,newName);
        alter.CommandText=alter.CommandText.Replace("AM9_CK_Cross",constraintName);
        alter.ExecuteNonQuery();
        version.CommandText = "UPDATE MapMetadata SET SchemaVersion=10 WHERE Id=1 AND SchemaVersion=9";
        version.ExecuteNonQuery();
    }
}
