using System.Text.Json;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using ArgumentMap.Server;
using ArgumentMap.Business;
using ArgumentMap.Contracts;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MySqlConnector;
using static ArgumentMap.Tests.LogicalNodeMySqlTests;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("MySql")]
public sealed class CrossRoadNamingMySqlTests
{
    private static readonly (string Old,string New)[] Columns=[("Line1Code","RoadCode1"),("Line2Code","RoadCode2"),("Segment1","RoadOrder1"),("Segment2","RoadOrder2"),("Line1Version","RoadVersion1"),("Line2Version","RoadVersion2")];
    [TestMethod,DataRow(false),DataRow(true)]
    public Task Schema14RenamePreservesStatesOrdersFullDoublePercentsAndMarkers_RetriesInterruptedPublication(bool interrupt)=>Isolated(settings=>
    {
        var repository=new MySqlLineRepository(settings);var a=Add(repository,90,110,100,110,130,110);var b=Add(repository,110,90,110,100,110,140);
        Add(repository,200,200,201,201);Add(repository,200,201,201,200);var crosses=new CrossService(repository);
        while(crosses.Calculate().Data!.CanContinue){}
        foreach(var cross in crosses.GetAll().Data!)Assert.IsTrue(crosses.Edit(new(cross.Key,"owner",cross.Version,cross.Key.Contains(a.Code)?CrossState.Road1ToRoad2:CrossState.Road2ToRoad1)).Success);
        var node=new LogicalNodeService(repository).Create(new("owner",120.5m,110.5m,6,new(){Value="中文 LN"})).Data!;
        repository.Transaction(s=>{s.PendingSegments.Add((a.Code,0));s.DirtyLines.Add(a.Code);foreach(var key in s.Crosses.Keys)s.StaleCrosses.Add(key);return true;});
        var before=Snapshot(repository);var originals=repository.Transaction(s=>s.Crosses.Values.OrderBy(c=>c.Key).ToArray());
        Assert.AreEqual(0,crosses.GetAll().Data!.Length);
        RecreateV14(settings);Sql(settings,"UPDATE MapMetadata SET SchemaVersion=14 WHERE Id=1");
        if(interrupt)
        {
            Sql(settings,"CREATE TRIGGER TestCrossRoadPublication BEFORE UPDATE ON MapMetadata FOR EACH ROW BEGIN IF NEW.SchemaVersion=15 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected Cross naming publication failure'; END IF; END");
            try{Assert.ThrowsExactly<MySqlException>(()=>new MySqlLineRepository(settings));}
            finally{Sql(settings,"DROP TRIGGER TestCrossRoadPublication");}
            Assert.ThrowsExactly<InvalidOperationException>(()=>repository.Transaction(s=>true));
        }
        repository=new MySqlLineRepository(settings);Assert.AreEqual(before,Snapshot(repository));
        if(!interrupt){Assert.IsNotNull(repository.CrossRoadNamingBackupPath);using var backup=JsonDocument.Parse(File.ReadAllText(repository.CrossRoadNamingBackupPath));Assert.AreEqual(originals.Length,backup.RootElement.GetProperty("Tables").GetProperty("Crosses").GetArrayLength());}
        using var connection=new MySqlConnection(settings.ConnectionString());connection.Open();using var check=connection.CreateCommand();check.Parameters.AddWithValue("@db",settings.Database);
        check.CommandText="SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=@db AND TABLE_NAME='Crosses' AND COLUMN_NAME IN ('Line1Code','Line2Code','Segment1','Segment2','Line1Version','Line2Version')";Assert.AreEqual(0,Convert.ToInt32(check.ExecuteScalar()));
        check.CommandText="SELECT GROUP_CONCAT(COLUMN_NAME ORDER BY ORDINAL_POSITION) FROM information_schema.KEY_COLUMN_USAGE WHERE TABLE_SCHEMA=@db AND TABLE_NAME='Crosses' AND CONSTRAINT_NAME='PRIMARY'";Assert.AreEqual("RoadCode1,RoadCode2,X,Y",check.ExecuteScalar());
        CollectionAssert.AreEqual(originals,new MySqlLineRepository(settings).Transaction(s=>s.Crosses.Values.OrderBy(c=>c.Key).ToArray()));
        Assert.AreEqual(0,new CrossService(repository).GetAll().Data!.Length);CollectionAssert.AreEqual(node.Positions,new LogicalNodeService(repository).GetAll().Data!.Single().Positions);
        foreach(var invalid in new[]{"RoadOrder1=-1","RoadVersion2=-1","Percent1=1.1","RoadCode1=RoadCode2","X=500001"})
            Assert.ThrowsExactly<MySqlException>(()=>Sql(settings,"UPDATE Crosses SET "+invalid));
        Assert.AreEqual(before,Snapshot(repository));
        Assert.IsTrue(new LineSaveService(repository).Delete(new(a.Code,a.Owner,a.Version)).Success);
        Assert.IsFalse(new CrossService(repository).GetAll().Data!.Any(c=>c.Key.Contains(a.Code)));Assert.AreEqual(0,new LogicalNodeService(repository).GetAll().Data!.Single().Positions.Length);
        return Task.CompletedTask;
    });
    [TestMethod]
    public Task InvalidSourcePercentRollsBackShadowCopy_OriginalTableRetainedUntilRetry()=>Isolated(settings=>
    {
        var repository=new MySqlLineRepository(settings);Add(repository,90,110,130,110);Add(repository,110,90,110,130);var crosses=new CrossService(repository);
        while(crosses.Calculate().Data!.CanContinue){}
        var cross=crosses.GetAll().Data!.Single();Assert.IsTrue(crosses.Edit(new(cross.Key,"owner",cross.Version,CrossState.Road1ToRoad2)).Success);var before=Snapshot(repository);
        RecreateV14(settings);Sql(settings,"UPDATE MapMetadata SET SchemaVersion=14 WHERE Id=1; ALTER TABLE Crosses DROP CHECK AM9_CK_Cross_Position; UPDATE Crosses SET Percent1=1.5");
        Assert.ThrowsExactly<MySqlException>(()=>new MySqlLineRepository(settings));
        using var connection=new MySqlConnection(settings.ConnectionString());connection.Open();using var check=connection.CreateCommand();
        check.CommandText="SELECT Percent1 FROM Crosses";Assert.AreEqual(1.5,Convert.ToDouble(check.ExecuteScalar()));
        check.CommandText="SELECT SchemaVersion FROM MapMetadata WHERE Id=1";Assert.AreEqual(14,Convert.ToInt32(check.ExecuteScalar()));
        check.CommandText="SELECT COUNT(*) FROM Crosses_RoadNamesV15";Assert.AreEqual(0,Convert.ToInt32(check.ExecuteScalar()),"Failed copy transaction must roll back every shadow row.");
        check.CommandText="UPDATE Crosses SET Percent1=@percent";check.Parameters.AddWithValue("@percent",cross.Percent1);check.ExecuteNonQuery();
        var migrated=new MySqlLineRepository(settings);Assert.AreEqual(before,Snapshot(migrated));
        return Task.CompletedTask;
    });
    [TestMethod]
    public Task RenamedTableResumesOlderDirectionUpgradeWithItsActualConstraintName()=>Isolated(settings=>
    {
        var repository=new MySqlLineRepository(settings);Add(repository,90,110,130,110);Add(repository,110,90,110,130);var service=new CrossService(repository);
        while(service.Calculate().Data!.CanContinue){}
        var cross=service.GetAll().Data!.Single();Assert.IsTrue(service.Edit(new(cross.Key,"owner",cross.Version,CrossState.Road2ToRoad1)).Success);
        var before=Snapshot(repository);RecreateV14(settings);Sql(settings,"UPDATE MapMetadata SET SchemaVersion=14 WHERE Id=1");
        var renamed=new MySqlLineRepository(settings);Assert.AreEqual(before,Snapshot(renamed));
        // Table publication uses AM15_CK_Cross, while the older recovery step used to assume AM9.
        Sql(settings,"UPDATE MapMetadata SET SchemaVersion=9 WHERE Id=1");
        var resumed=new MySqlLineRepository(settings);Assert.AreEqual(before,Snapshot(resumed));Assert.AreEqual(CrossState.Road2ToRoad1,new CrossService(resumed).GetAll().Data!.Single().State);
        return Task.CompletedTask;
    });
    [TestMethod,TestCategory("Http")]
    public Task ActualHttpResponsesAndEditRequestsUseRoadNames_LnUsesSingleRoadTuple_ObsoleteKeysAreRejected()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var server=await LocalLineServer.StartAsync(repository:repository);
        using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");
        var a=await Add(client,90,110,100,110,110,110,130,110);var b=await Add(client,110,90,110,100,110,140);
        b=(await client.SaveLinePropertiesAsync(b.Code,b.Version,b.Properties with{Speed=50})).Data!;
        while((await client.CalculateCrossAsync()).Data!.CanContinue){}
        var cross=(await client.GetCrossesAsync()).Data!.Single();var ln=(await client.CreateLogicalNodeAsync(120.5m,110.5m,6,new())).Data!;
        using(var response=await http.PostAsJsonAsync("GetAllCrossData",new SessionRequest(client.Session.UserName,client.Session.LoginCode)))
        using(var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            var data=json.RootElement.GetProperty("data")[0];var key=data.GetProperty("key");
            Assert.AreEqual(cross.Key.RoadCode1,key.GetProperty("roadCode1").GetString());Assert.AreEqual(cross.Key.RoadCode2,key.GetProperty("roadCode2").GetString());
            Assert.AreEqual(cross.RoadOrder1,data.GetProperty("roadOrder1").GetInt32());Assert.AreEqual(cross.RoadOrder2,data.GetProperty("roadOrder2").GetInt32());
            Assert.AreEqual(cross.Percent1,data.GetProperty("percent1").GetDouble());Assert.AreEqual(cross.Percent2,data.GetProperty("percent2").GetDouble());
            Assert.AreEqual(cross.Key.RoadCode1==a.Code?a.Version:b.Version,data.GetProperty("roadVersion1").GetInt64());Assert.AreEqual(cross.Key.RoadCode2==a.Code?a.Version:b.Version,data.GetProperty("roadVersion2").GetInt64());
            foreach(var obsolete in new[]{"line1Code","line2Code"})Assert.IsFalse(key.TryGetProperty(obsolete,out _));
            foreach(var obsolete in new[]{"segment1","segment2","line1Version","line2Version"})Assert.IsFalse(data.TryGetProperty(obsolete,out _));
        }
        using(var response=await http.PostAsJsonAsync("GetLogicalNodes",new SessionRequest(client.Session.UserName,client.Session.LoginCode)))
        using(var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            var position=json.RootElement.GetProperty("data")[0].GetProperty("positions")[0];
            Assert.AreEqual(a.Code,position.GetProperty("roadCode").GetString());Assert.AreEqual(2,position.GetProperty("roadOrder").GetInt32());Assert.AreEqual(0.525,position.GetProperty("roadPercent").GetDouble());
        }
        var before=Snapshot(repository);
        using(var response=await http.PostAsJsonAsync("EditCross",new{Cross=new{Line1Code=cross.Key.RoadCode1,Line2Code=cross.Key.RoadCode2,Position=cross.Key.Position},Owner=client.Session.UserName,Version=cross.Version,State=CrossState.Connected,LoginCode=client.Session.LoginCode}))
        {
            var result=await response.Content.ReadFromJsonAsync<ServiceResult<CrossSnapshot>>();Assert.IsFalse(result!.Success);Assert.AreEqual("InvalidCrossKey",result.Code);
        }
        Assert.AreEqual(before,Snapshot(repository));
        var edited=await client.SetCrossStateAsync(cross.Key,cross.Version,CrossState.Road2ToRoad1);Assert.IsTrue(edited.Success);
        Assert.AreEqual(CrossState.Road2ToRoad1,(await client.GetCrossesAsync()).Data!.Single().State);CollectionAssert.AreEqual(ln.Positions,(await client.GetLogicalNodesAsync()).Data!.Single().Positions);
    });
    private static void RecreateV14(MySqlSettings settings)
    {
        using var connection=new MySqlConnection(settings.ConnectionString());connection.Open();using var command=connection.CreateCommand();
        command.CommandText="SELECT * FROM Crosses";var rows=new List<Dictionary<string,object>>();
        using(var reader=command.ExecuteReader())while(reader.Read())
        {
            var row=new Dictionary<string,object>();for(var i=0;i<reader.FieldCount;i++)row[reader.GetName(i)]=reader.GetValue(i);rows.Add(row);
        }
        // This helper operates only on an owned disposable test database.
        Assert.IsTrue(settings.Database.StartsWith("ArgumentMap_Integration_"));Sql(settings,"DROP TABLE Crosses");
        var schema=File.ReadAllText(Path.Combine(ProjectPaths.Root,"ArgumentMap.Storage","Schema.CodeV14.sql"));var start=schema.IndexOf("CREATE TABLE IF NOT EXISTS Crosses (",StringComparison.Ordinal);
        Sql(settings,schema.Substring(start,schema.IndexOf(';',start)-start));
        foreach(var row in rows)
        {
            var columns=row.Keys.Select(n=>Columns.FirstOrDefault(c=>c.New==n) is var pair && pair.Old is not null?pair.Old:n).ToArray();
            command.Parameters.Clear();command.CommandText="INSERT INTO Crosses ("+string.Join(',',columns.Select(n=>$"`{n}`"))+") VALUES ("+string.Join(',',Enumerable.Range(0,row.Count).Select(i=>"@p"+i))+")";
            var index=0;foreach(var value in row.Values)command.Parameters.AddWithValue("@p"+index++,value);command.ExecuteNonQuery();
        }
    }
}
