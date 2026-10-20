using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using ArgumentMap.Business;
using ArgumentMap.Contracts;
using ArgumentMap.Server;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MySqlConnector;
using static ArgumentMap.Tests.LogicalNodeMySqlTests;

namespace ArgumentMap.Tests;

[TestClass, TestCategory("MySql")]
public sealed class PercentDecimalReviewTests
{
    private static object? Scalar(MySqlSettings settings, string sql)
    {
        using var connection = new MySqlConnection(settings.ConnectionString()); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar();
    }

    private static MySqlLineRepository Fixture(MySqlSettings settings)
    {
        var repository = new MySqlLineRepository(settings); Add(repository, 90, 110, 100, 110, 130, 110); Add(repository, 110, 90, 110, 140);
        var service = new CrossService(repository); Assert.IsTrue(service.Recalculate().Success);
        Assert.IsTrue(new LogicalNodeService(repository).Create(new("owner",120.5m,110.5m,6,new())).Success); return repository;
    }

    [TestMethod, DataRow(499999,false), DataRow(99991,false), DataRow(499999,true), DataRow(99991,true)]
    public Task UnchangedSmallRatios_RepeatedJAndKDoNotChangeVersionsOrPercentRows(int length,bool onlyK) => Isolated(settings =>
    {
        var repository = new MySqlLineRepository(settings); TestMap.HistoricalRoad(repository,0,2000,length,2000); Add(repository,10,1990,10,2010);
        var crosses = new CrossService(repository); Assert.IsTrue(crosses.Recalculate().Success);
        var nodes = new LogicalNodeService(repository); Assert.IsTrue(nodes.Create(new("owner",3.5m,2000.5m,6,new())).Success);
        var before = Snapshot(repository); var nodeTime = Scalar(settings,"SELECT UpdatedAt FROM LogicalNodePositions"); var crossTime = Scalar(settings,"SELECT UpdatedAt FROM Crosses");
        for (var i=0;i<4;i++)
        {
            if(onlyK)Assert.IsTrue(crosses.Recalculate().Success);
            else Assert.IsTrue(nodes.Calculate().Success);
            Assert.AreEqual(before,Snapshot(repository),onlyK?"K must not increase Cross version for unchanged rounded percent.":"J must not treat storage rounding as a position change.");
        }
        Assert.AreEqual(nodeTime,Scalar(settings,"SELECT UpdatedAt FROM LogicalNodePositions")); Assert.AreEqual(crossTime,Scalar(settings,"SELECT UpdatedAt FROM Crosses"));
        Assert.AreEqual(before,Snapshot(new MySqlLineRepository(settings))); return Task.CompletedTask;
    });

    [TestMethod]
    public Task MetadataOnlyEdits_PreserveAll18DigitsOfUnchangedStoredPercents() => Isolated(settings =>
    {
        var repository=Fixture(settings);
        Sql(settings,"UPDATE Crosses SET Percent1=0.123456789012345678,Percent2=0.987654321098765432; UPDATE LogicalNodePositions SET RoadPercent=0.683333333333333345");
        var crosses=new CrossService(repository); var nodes=new LogicalNodeService(repository);
        foreach(var state in new[]{CrossState.Connected,CrossState.Disconnected,CrossState.Road1ToRoad2,CrossState.Unset})
        {
            var cross=crosses.GetAll().Data!.Single(); Assert.IsTrue(crosses.Edit(new(cross.Key,"owner",cross.Version,state)).Success);
            Assert.AreEqual(0.123456789012345678m,Scalar(settings,"SELECT Percent1 FROM Crosses")); Assert.AreEqual(0.987654321098765432m,Scalar(settings,"SELECT Percent2 FROM Crosses"));
        }
        var node=nodes.GetAll().Data!.Single(); Assert.IsTrue(nodes.Update(new(node.Code,"owner",node.Version,"中文值","说明")).Success);
        var road=repository.Transaction(s=>s.Lines.Values.Single(r=>r.Nodes.Length==3));
        Assert.IsTrue(new LineSaveService(repository).SaveProperties(new(road.Code,road.Owner,road.Version,road.Properties with{Speed=88})).Success);
        Assert.AreEqual(0.683333333333333345m,Scalar(settings,"SELECT RoadPercent FROM LogicalNodePositions"),"Road version update must not discard unchanged decimal digits.");
        return Task.CompletedTask;
    });

    [TestMethod, DataRow("fr-FR"), DataRow("de-DE"), DataRow("zh-CN"), TestCategory("Http")]
    public Task SmallPercentHttpResponses_MatchFreshReadsAndIgnoreCurrentCulture(string culture) => Isolated(async settings =>
    {
        var previous=CultureInfo.CurrentCulture; var previousUi=CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo(culture); CultureInfo.CurrentUICulture=CultureInfo.CurrentCulture;
            var repository=new MySqlLineRepository(settings); await using var server=await LocalLineServer.StartAsync(repository:repository);
            using var http=new HttpClient{BaseAddress=server.BaseAddress}; var client=await TestLogin.ClientAsync(http,server,"owner");
            TestMap.HistoricalRoad(repository,0,2000,499999,2000); await Add(client,10,1990,10,2010);
            while((await client.CalculateCrossAsync()).Data!.CanContinue){}
            var created=await client.CreateLogicalNodeAsync(3.5m,2000.5m,6,new()); Assert.IsTrue(created.Success,created.Message);
            var read=(await client.GetLogicalNodesAsync()).Data!.Single(); CollectionAssert.AreEqual(created.Data!.Positions,read.Positions,"Create and GET must return the same stored precision.");
            var before=Snapshot(repository); for(var i=0;i<3;i++)Assert.IsTrue((await client.CalculateLogicalNodeAsync()).Success); Assert.AreEqual(before,Snapshot(repository));
            var cross=(await client.GetCrossesAsync()).Data!.Single(); var edit=await client.SetCrossStateAsync(cross.Key,cross.Version,CrossState.Connected); Assert.IsTrue(edit.Success);
            Assert.AreEqual(JsonSerializer.Serialize(edit.Data),JsonSerializer.Serialize((await client.GetCrossesAsync()).Data!.Single()));
            Assert.AreEqual(Snapshot(repository),Snapshot(new MySqlLineRepository(settings)));
        }
        finally{CultureInfo.CurrentCulture=previous; CultureInfo.CurrentUICulture=previousUi;}
    });

    [TestMethod, DataRow(false), DataRow(true)]
    public Task DecimalWriteFault_RollsBackExactStoredDigitsAndAllowsRetry(bool position) => Isolated(settings =>
    {
        var repository=Fixture(settings);
        Sql(settings,"UPDATE Crosses SET Percent1=0.123456789012345678,Percent2=0.987654321098765432; UPDATE LogicalNodePositions SET RoadPercent=0.683333333333333345");
        var before=Snapshot(repository); var cross=new CrossService(repository).GetAll().Data!.Single(); var road=repository.Transaction(s=>s.Lines.Values.Single(r=>r.Nodes.Length==3));
        Sql(settings,position
            ? "CREATE TRIGGER TestDecimalWrite BEFORE INSERT ON LogicalNodePositions FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected decimal position failure'"
            : "CREATE TRIGGER TestDecimalWrite BEFORE UPDATE ON Crosses FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected decimal Cross failure'");
        try
        {
            if(position)Assert.ThrowsExactly<MySqlException>(()=>new LineSaveService(repository).SaveProperties(new(road.Code,road.Owner,road.Version,road.Properties with{Speed=77})));
            else Assert.ThrowsExactly<MySqlException>(()=>new CrossService(repository).Edit(new(cross.Key,"owner",cross.Version,CrossState.Connected)));
        }
        finally{Sql(settings,"DROP TRIGGER TestDecimalWrite");}
        Assert.AreEqual(before,Snapshot(new MySqlLineRepository(settings)));
        Assert.AreEqual(0.123456789012345678m,Scalar(settings,"SELECT Percent1 FROM Crosses")); Assert.AreEqual(0.683333333333333345m,Scalar(settings,"SELECT RoadPercent FROM LogicalNodePositions"));
        if(position)Assert.IsTrue(new LineSaveService(repository).SaveProperties(new(road.Code,road.Owner,road.Version,road.Properties with{Speed=77})).Success);
        else Assert.IsTrue(new CrossService(repository).Edit(new(cross.Key,"owner",cross.Version,CrossState.Connected)).Success);
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task Schema16MissingPositionTable_RecoversDerivedDecimalPositionsWithoutChangingMap() => Isolated(settings =>
    {
        var repository=Fixture(settings); var before=Snapshot(repository); Sql(settings,"DROP TABLE LogicalNodePositions");
        var recovered=new MySqlLineRepository(settings); Assert.AreEqual(before,Snapshot(recovered));
        Assert.AreEqual("decimal(20,18)",Scalar(settings,"SELECT COLUMN_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='LogicalNodePositions' AND COLUMN_NAME='RoadPercent'"));
        Assert.AreEqual(MySqlLineRepository.CurrentSchemaVersion,Convert.ToInt32(Scalar(settings,"SELECT SchemaVersion FROM MapMetadata WHERE Id=1"))); return Task.CompletedTask;
    });

    [TestMethod,TestCategory("Http")]
    public Task LongRoadHttp_InsertRemoveReverseAndRestartKeepPositionsAndCrossStateStable() => Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var server=await LocalLineServer.StartAsync(repository:repository);
        using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");
        var road=await Add(client,0,2000,1999,2000);await Add(client,10,1990,10,2010);
        while((await client.CalculateCrossAsync()).Data!.CanContinue){}
        var originalCross=(await client.GetCrossesAsync()).Data!.Single();Assert.IsTrue((await client.SetCrossStateAsync(originalCross.Key,originalCross.Version,CrossState.Connected)).Success);
        var node=(await client.CreateLogicalNodeAsync(3.5m,2000.5m,6,new())).Data!;var originalPercent=node.Positions.Single().RoadPercent;
        for(var step=0;step<4;step++)
        {
            ServiceResult<LineSnapshot> changed=step switch
            {
                0=>await client.SaveAsync(road.Code,road.Version,TestMap.Shape(road,0,2000,1000,2000,1999,2000).Nodes),
                1=>await client.SaveAsync(road.Code,road.Version,TestMap.Shape(road,0,2000,1999,2000).Nodes),
                _=>await client.ReverseLineAsync(road.Code,road.Version)
            };
            Assert.IsTrue(changed.Success,changed.Message);road=changed.Data!;while((await client.CalculateCrossAsync()).Data!.CanContinue){}
            var cross=(await client.GetCrossesAsync()).Data!.Single();Assert.AreEqual(originalCross.Key,cross.Key);Assert.AreEqual(CrossState.Connected,cross.State);
            var position=(await client.GetLogicalNodesAsync()).Data!.Single().Positions.Single();Assert.AreEqual(road.Version,position.RoadVersion);
            var segment=road.Nodes[position.RoadOrder];var end=road.Nodes[position.RoadOrder+1];Assert.AreEqual(3.5,segment.X+(end.X-segment.X)*position.RoadPercent,1e-9);
            var before=Snapshot(repository);for(var i=0;i<3;i++)Assert.IsTrue((await client.CalculateLogicalNodeAsync()).Success);Assert.AreEqual(before,Snapshot(repository));
            Assert.IsTrue(new CrossService(repository).Recalculate().Success);Assert.AreEqual(before,Snapshot(repository));
        }
        Assert.AreEqual(originalPercent,(await client.GetLogicalNodesAsync()).Data!.Single().Positions.Single().RoadPercent);
        Assert.AreEqual(Snapshot(repository),Snapshot(new MySqlLineRepository(settings)));
    });

    [TestMethod, DataRow(false), DataRow(true)]
    public Task PartiallyConvertedSchema15_PreservesDecimalSourceDigitsInBothTables(bool onlyPositionWasDouble) => Isolated(settings =>
    {
        var repository=Fixture(settings);
        Sql(settings,"UPDATE Crosses SET Percent2=0.987654321098765432; UPDATE LogicalNodePositions SET RoadPercent=0.683333333333333345");
        Sql(settings,onlyPositionWasDouble?"ALTER TABLE LogicalNodePositions MODIFY RoadPercent DOUBLE NOT NULL":"ALTER TABLE Crosses MODIFY Percent1 DOUBLE NOT NULL DEFAULT 0");
        Sql(settings,"UPDATE MapMetadata SET SchemaVersion=15 WHERE Id=1"); var migrated=new MySqlLineRepository(settings);
        Assert.AreEqual(0.987654321098765432m,Scalar(settings,"SELECT Percent2 FROM Crosses"));
        if(!onlyPositionWasDouble)Assert.AreEqual(0.683333333333333345m,Scalar(settings,"SELECT RoadPercent FROM LogicalNodePositions"));
        Assert.AreEqual(MySqlLineRepository.CurrentSchemaVersion,Convert.ToInt32(Scalar(settings,"SELECT SchemaVersion FROM MapMetadata WHERE Id=1"))); Assert.AreEqual(1,new CrossService(migrated).GetAll().Data!.Length);
        return Task.CompletedTask;
    });
}
