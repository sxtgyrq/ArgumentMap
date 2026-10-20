using System.Net.Http;
using System.Globalization;
using System.Text.Json;
using ArgumentMap.Business;
using ArgumentMap.Client;
using ArgumentMap.Contracts;
using ArgumentMap.Server;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MySqlConnector;
using static ArgumentMap.Tests.LogicalNodeMySqlTests;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("MySql"),TestCategory("Http")]
public sealed class LogicalNodeCoordinateMySqlTests
{
    private static readonly string[] Invalid=["100","100.2","100.49","100.51","100.5001","100.4999","100.5000000000000000000001","100.4999999999999999999999"];
    private static LogicalNodeProperties Properties(int size)=>new(){Value=size==4?"False":size==8?"=":size==12?"S":"node",LogicLanguage=size==6?"P":""};
    private static int Version(MySqlSettings settings)
    {using var c=new MySqlConnection(settings.ConnectionString());c.Open();using var q=c.CreateCommand();q.CommandText="SELECT SchemaVersion FROM MapMetadata WHERE Id=1";return Convert.ToInt32(q.ExecuteScalar());}

    [TestMethod,DataRow(4),DataRow(6),DataRow(8),DataRow(12),DataRow(18)]
    public Task BothAxesRejectIntegersAndOtherFractions_HttpDalAndRawSql_NoRounding(int size)=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var server=await LocalLineServer.StartAsync(repository:repository);
        using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");
        var created=await client.CreateLogicalNodeAsync(100.5m,100.5m,size,Properties(size));Assert.IsTrue(created.Success,created.Message);var node=created.Data!;
        var before=LineTopologyMySqlTests.RawSnapshot(settings);
        foreach(var axis in new[]{"X","Y"})
        foreach(var input in Invalid)
        {
            var value=decimal.Parse(input,CultureInfo.InvariantCulture);var x=axis=="X"?value:100.5m;var y=axis=="Y"?value:100.5m;
            Assert.AreEqual("InvalidLogicalNodeCoordinate",(await client.CreateLogicalNodeAsync(x,y,size,Properties(size))).Code);
            Assert.AreEqual("InvalidLogicalNodeCoordinate",(await client.MoveLogicalNodeAsync(node.Code,node.Version,x,y)).Code);
            Assert.ThrowsExactly<InvalidLogicalNodeCoordinateException>(()=>repository.Transaction(s=>
            {s.LogicalNodes[node.Code]=s.LogicalNodes[node.Code] with{X=x,Y=y};s.DirtyLines.Add("AAAAAAAAAA");return true;}));
            Assert.AreEqual(3819,Assert.ThrowsExactly<MySqlException>(()=>Sql(settings,$"UPDATE LogicalNodes SET {axis}={input} WHERE Code='{node.Code}'")).Number,input);
            var code=axis=="X"?"AAAAAAAAAA":"BBBBBBBBBB";
            Assert.AreEqual(3819,Assert.ThrowsExactly<MySqlException>(()=>Sql(settings,$"INSERT INTO LogicalNodes (Code,Owner,X,Y,SideLength,`Value`) VALUES ('{code}','owner',{(axis=="X"?input:"100.5")},{(axis=="Y"?input:"100.5")},4,'False')")).Number,input);
            Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));Assert.IsFalse(repository.Transaction(s=>s.DirtyLines.Contains("AAAAAAAAAA")));
        }
        // Raw SQL gets all 28 fractional places before CHECK, including more digits
        // than the same coordinate can represent in a .NET decimal.
        Assert.AreEqual(3819,Assert.ThrowsExactly<MySqlException>(()=>Sql(settings,"UPDATE LogicalNodes SET X=100.5000000000000000000000000001")).Number);
        Sql(settings,"UPDATE LogicalNodes SET X=100.50,Y=100.5000");
        var read=(await client.GetLogicalNodesAsync()).Data!.Single();Assert.AreEqual(100.5m,read.X);Assert.AreEqual("100.5",read.X.ToString(CultureInfo.InvariantCulture));
        Assert.AreEqual(Snapshot(repository),Snapshot(new MySqlLineRepository(settings)));
    });

    private static string CanonicalRows(MySqlSettings settings)
    {
        using var document=JsonDocument.Parse(LineTopologyMySqlTests.RawSnapshot(settings));
        // RawSnapshot nests each row as JSON text. Canonicalize numeric scale only;
        // keep every property, version, marker, timestamp and exact numeric value.
        return JsonSerializer.Serialize(document.RootElement.EnumerateObject().Select(table=>new{table.Name,Rows=table.Value.EnumerateArray().Select(row=>
        {
            using var parsed=JsonDocument.Parse(row.GetString()!);
            return string.Join("|",parsed.RootElement.EnumerateObject().Select(p=>p.Name+":"+(p.Value.ValueKind==JsonValueKind.Number&&p.Value.TryGetDecimal(out var d)?d.ToString("G29",CultureInfo.InvariantCulture):p.Value.GetRawText())));
        }).Order(StringComparer.Ordinal).ToArray()}));
    }

    [TestMethod,DataRow("normal"),DataRow("ddl"),DataRow("publish"),DataRow("existing-check")]
    public Task Schema25UpgradeIsAtomic_PreservesAllData_AndResumesInterruptedDdlOrPublication(string phase)=>Isolated(settings=>
    {
        var repository=new MySqlLineRepository(settings);var service=new LogicalNodeService(repository);Add(repository,20,50,400,50);
        var x=60.5m;foreach(var size in new[]{4,6,8,12,18}){Assert.IsTrue(service.Create(new("owner",x,50.5m,size,Properties(size))).Success);x+=50;}
        Sql(settings,"ALTER TABLE LogicalNodes "+(phase=="existing-check"?"":"DROP CHECK CK_LN_CenterHalf, ")+"MODIFY COLUMN X DECIMAL(7,1) NOT NULL, MODIFY COLUMN Y DECIMAL(7,1) NOT NULL; UPDATE MapMetadata SET SchemaVersion=25 WHERE Id=1");
        var before=CanonicalRows(settings);
        if(phase=="ddl")Sql(settings,"ALTER TABLE MapMetadata ADD CONSTRAINT CK_LN_CenterHalf CHECK (1=1)");
        if(phase=="publish")Sql(settings,"CREATE TRIGGER FailCoordinatePublish BEFORE UPDATE ON MapMetadata FOR EACH ROW BEGIN IF NEW.SchemaVersion=26 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected coordinate publication failure'; END IF; END");
        if(phase is "ddl" or "publish")
        {
            try
            {
                Assert.ThrowsExactly<MySqlException>(()=>new MySqlLineRepository(settings));Assert.AreEqual(2501,Version(settings));Assert.AreEqual(before,CanonicalRows(settings));
                Assert.ThrowsExactly<InvalidOperationException>(()=>repository.Transaction(s=>true));
            }
            finally{Sql(settings,phase=="ddl"?"ALTER TABLE MapMetadata DROP CHECK CK_LN_CenterHalf":"DROP TRIGGER FailCoordinatePublish");}
        }
        repository=new MySqlLineRepository(settings);Assert.AreEqual(MySqlLineRepository.CurrentSchemaVersion,Version(settings));Assert.IsTrue(System.IO.File.Exists(repository.LogicalNodeMigrationBackupPath));Assert.AreEqual(before,CanonicalRows(settings));
        Assert.AreEqual(3819,Assert.ThrowsExactly<MySqlException>(()=>Sql(settings,"UPDATE LogicalNodes SET X=100.51")).Number);
        var raw=LineTopologyMySqlTests.RawSnapshot(settings);new MySqlLineRepository(settings);Assert.AreEqual(raw,LineTopologyMySqlTests.RawSnapshot(settings));
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task CurrentSchemaMissingCoordinateGuardFailsClosed_AndMissingPositionTableCanRecover()=>Isolated(settings=>
    {
        var repository=new MySqlLineRepository(settings);Add(repository,20,50,200,50);var service=new LogicalNodeService(repository);Assert.IsTrue(service.Create(new("owner",100.5m,50.5m,6,Properties(6))).Success);
        var before=Snapshot(repository);Sql(settings,"DROP TABLE LogicalNodePositions");repository=new MySqlLineRepository(settings);Assert.AreEqual(before,Snapshot(repository));
        Sql(settings,"ALTER TABLE LogicalNodes DROP CHECK CK_LN_CenterHalf");Assert.ThrowsExactly<InvalidOperationException>(()=>new MySqlLineRepository(settings));return Task.CompletedTask;
    });
}
