using System.Net.Http;
using ArgumentMap.Business;
using ArgumentMap.Contracts;
using ArgumentMap.Server;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MySqlConnector;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("MySql")]
public sealed class SpeedDefaultsMySqlTests
{
    private static int DefaultSpeed(MySqlSettings settings)
    {
        using var connection=new MySqlConnection(settings.ConnectionString());connection.Open();using var query=connection.CreateCommand();
        query.CommandText="SELECT COLUMN_DEFAULT FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=@db AND TABLE_NAME='Lines' AND COLUMN_NAME='Speed'";
        query.Parameters.AddWithValue("@db",settings.Database);return Convert.ToInt32(query.ExecuteScalar());
    }

    [TestMethod]
    public Task HttpCreateAndSqlDefaultUse100_ExistingSpeedsSurviveDefaultUpgradeAndRestart()=>LogicalNodeMySqlTests.Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);Assert.AreEqual(100,DefaultSpeed(settings));
        var road=LogicalNodeMySqlTests.Add(repository,10,50,400,50);Assert.AreEqual(100,road.Properties.Speed);
        var saved=new LineSaveService(repository).SaveProperties(new(road.Code,road.Owner,road.Version,road.Properties with{Speed=5}));Assert.IsTrue(saved.Success,saved.Message);
        LogicalNodeRequiredLogicTests.Add(repository,"P",100.5m);
        var before=LineTopologyMySqlTests.RawSnapshot(settings);
        LogicalNodeMySqlTests.Sql(settings,"ALTER TABLE `Lines` ALTER COLUMN Speed SET DEFAULT 5");Assert.AreEqual(5,DefaultSpeed(settings));
        repository=new MySqlLineRepository(settings);Assert.AreEqual(100,DefaultSpeed(settings));Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        new MySqlLineRepository(settings);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        await using var server=await LocalLineServer.StartAsync(repository:repository);
        using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");
        var created=await client.CreateAsync();Assert.IsTrue(created.Success,created.Message);Assert.AreEqual(100,created.Data!.Properties.Speed);
        var detail=await client.GetLineDetailAsync(created.Data.Code);Assert.IsTrue(detail.Success,detail.Message);Assert.AreEqual(100,detail.Data!.Properties.Speed);
        Assert.AreEqual(5,(await client.GetLineDetailAsync(road.Code)).Data!.Properties.Speed);
        LogicalNodeMySqlTests.Sql(settings,"INSERT INTO `Lines` (LineCode,Owner) VALUES ('AAAAAAAAAA','sql default test')");
        Assert.AreEqual(100,repository.Transaction(s=>s.Lines["AAAAAAAAAA"].Properties.Speed));
    });
}
