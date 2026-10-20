using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using ArgumentMap.Business;
using ArgumentMap.Client;
using ArgumentMap.Contracts;
using ArgumentMap.Server;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MySqlConnector;
using WpfArgumentMap;
using static ArgumentMap.Tests.LogicalNodeMySqlTests;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class LogicalNode12LogicTests
{
    [TestMethod]
    public void DefaultsUseFinalUppercaseNumberedValue_CustomLogicCanDifferOrRepeat_EditingNeverOverwritesIt()
    {
        var map=new TestMap();var service=new LogicalNodeService(map.Repository);
        var first=service.Create(new("owner",50.5m,50.5m,12,new(){Value="s"}));Assert.IsTrue(first.Success,first.Message);
        Assert.AreEqual("S",first.Data!.Properties.Value);Assert.AreEqual("S",first.Data.Properties.LogicLanguage);
        var second=service.Create(new("owner",100.5m,50.5m,12,new(){Value="s",LogicLanguage=" \t"}));Assert.IsTrue(second.Success,second.Message);
        Assert.AreEqual("S1",second.Data!.Properties.Value);Assert.AreEqual("S1",second.Data.Properties.LogicLanguage);
        var custom=service.Create(new("owner",150.5m,50.5m,12,new(){Value="s",LogicLanguage=" (P ∧ Q) → A "}));Assert.IsTrue(custom.Success,custom.Message);
        Assert.AreEqual("S2",custom.Data!.Properties.Value);Assert.AreEqual(" (P ∧ Q) → A ",custom.Data.Properties.LogicLanguage);
        var repeated=service.Create(new("owner",200.5m,50.5m,12,new(){Value="other",LogicLanguage=custom.Data.Properties.LogicLanguage}));Assert.IsTrue(repeated.Success,repeated.Message);
        var changed=service.Update(new(first.Data.Code,"owner",first.Data.Version,"t",""));Assert.IsTrue(changed.Success,changed.Message);Assert.AreEqual("T",changed.Data!.Properties.Value);Assert.AreEqual("S",changed.Data.Properties.LogicLanguage);
        Assert.IsTrue(service.Update(new(changed.Data.Code,"owner",changed.Data.Version,"t","",LogicLanguage:"custom s")).Success);
        var before=map.Snapshot();
        foreach(var blank in new[]{""," \t\r\n","\u2003\u00a0"})
        {Assert.AreEqual(LogicalNodeTextRules.HasLineBreak(blank)?LogicalNodeTextRules.ErrorCode:"LogicalNodeLogicRequired",service.Update(new(second.Data.Code,"owner",second.Data.Version,"new value","",LogicLanguage:blank)).Code);Assert.AreEqual(before,map.Snapshot());}
        Assert.ThrowsExactly<LogicalNodeLogicRequiredException>(()=>map.Repository.Transaction(s=>{s.LogicalNodes[second.Data.Code]=second.Data with{Properties=second.Data.Properties with{LogicLanguage=""}};return true;}));Assert.AreEqual(before,map.Snapshot());
        Assert.AreEqual("LogicalNodeLogicRequired",service.Create(new("owner",250.5m,50.5m,12,new(){Value=" \t"})).Code);Assert.AreEqual(before,map.Snapshot());
    }

    [STATestMethod,DataRow(false),DataRow(true)]
    public void NewEditorShowsDefaultAndTracksValueUntilCustomised_ServerAllocatesFinalDefault(bool custom)
    {
        using var handler=new DirectLogicalNodeHttpStub();var service=new LogicalNodeService(handler.Repository);
        Assert.IsTrue(service.Create(new("owner",50.5m,50.5m,12,new(){Value="s"})).Success);
        using var http=new HttpClient(handler){BaseAddress=new Uri("http://localhost/")};var window=new LogicalNodeWindow(new HttpLineClient(http,new("owner","test")),"owner",new(100.5,50.5),sideLength:12)
        {Left=-10000,Top=-10000,ShowActivated=false,ShowInTaskbar=false,WindowStartupLocation=WindowStartupLocation.Manual};
        try
        {
            window.Show();var value=(TextBox)window.FindName("ValueInput");var logic=(TextBox)window.FindName("LogicInput");Assert.AreEqual("逻辑节点",logic.Text);
            value.Text="s";Assert.AreEqual("S",logic.Text);
            if(custom){logic.Text="自定义 p";value.Text="other";Assert.AreEqual("自定义 p",logic.Text);value.Text="s";Assert.AreEqual("自定义 p",logic.Text);}
            ((Button)window.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));LogicalNodeWindowEntryTests.DrainUntil(()=>!window.IsVisible);
            var saved=service.GetAll().Data!.Single(n=>n.X==100.5m);Assert.AreEqual("S1",saved.Properties.Value);Assert.AreEqual(custom?"自定义 p":"S1",saved.Properties.LogicLanguage);
        }
        finally{window.Close();}
    }

    [STATestMethod]
    public void ExistingEditorKeepsIndependentLogic_AndBlocksBlankUntilUserProvidesAReplacement()
    {
        using var handler=new DirectLogicalNodeHttpStub();var service=new LogicalNodeService(handler.Repository);var created=service.Create(new("owner",50.5m,50.5m,12,new(){Value="s",LogicLanguage="custom"})).Data!;
        using var http=new HttpClient(handler){BaseAddress=new Uri("http://localhost/")};var window=new LogicalNodeWindow(new HttpLineClient(http,new("owner","test")),"owner",new(50.5,50.5),created)
        {Left=-10000,Top=-10000,ShowActivated=false,ShowInTaskbar=false,WindowStartupLocation=WindowStartupLocation.Manual};
        try
        {
            window.Show();var value=(TextBox)window.FindName("ValueInput");var logic=(TextBox)window.FindName("LogicInput");Assert.AreEqual("custom",logic.Text);
            value.Text="t";Assert.AreEqual("custom",logic.Text);logic.Text=" \t";var before=Snapshot(handler.Repository);
            ((Button)window.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));StringAssert.Contains(((TextBlock)window.FindName("Status")).Text,"不能为空");Assert.AreEqual(before,Snapshot(handler.Repository));
            logic.Text="other expression";((Button)window.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));LogicalNodeWindowEntryTests.DrainUntil(()=>!window.IsVisible);
            var saved=service.GetAll().Data!.Single();Assert.AreEqual("T",saved.Properties.Value);Assert.AreEqual("other expression",saved.Properties.LogicLanguage);
        }
        finally{window.Close();}
    }
}

[TestClass,TestCategory("MySql"),TestCategory("Http")]
public sealed class LogicalNode12LogicMySqlTests
{
    private static int Version(MySqlSettings settings)
    {using var c=new MySqlConnection(settings.ConnectionString());c.Open();using var q=c.CreateCommand();q.CommandText="SELECT SchemaVersion FROM MapMetadata WHERE Id=1";return Convert.ToInt32(q.ExecuteScalar());}

    [TestMethod]
    public Task TwoServersAllocateMatchingValueAndLogic_RejectBlankThroughHttpDalAndSql_AndPersistCustomLogic()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var a=await LocalLineServer.StartAsync(repository:repository);await using var b=await LocalLineServer.StartAsync(repository:new MySqlLineRepository(settings));
        using var h1=new HttpClient{BaseAddress=a.BaseAddress};using var h2=new HttpClient{BaseAddress=b.BaseAddress};var alice=await TestLogin.ClientAsync(h1,a,"alice");var bob=await TestLogin.ClientAsync(h2,b,"bob");
        var results=await Task.WhenAll(alice.CreateLogicalNodeAsync(50.5m,50.5m,12,new(){Value="s"}),bob.CreateLogicalNodeAsync(100.5m,50.5m,12,new(){Value="s"}));
        Assert.IsTrue(results.All(r=>r.Success));CollectionAssert.AreEquivalent(new[]{"S","S1"},results.Select(r=>r.Data!.Properties.Value).ToArray());Assert.IsTrue(results.All(r=>r.Data!.Properties.Value==r.Data.Properties.LogicLanguage));
        var node=results[0].Data!;var before=LineTopologyMySqlTests.RawSnapshot(settings);
        Assert.AreEqual("LogicalNodeLogicRequired",(await alice.UpdateLogicalNodeAsync(node.Code,node.Version,node.Properties.Value,""," \t")).Code);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        Assert.ThrowsExactly<LogicalNodeLogicRequiredException>(()=>repository.Transaction(s=>{s.LogicalNodes[node.Code]=node with{Properties=node.Properties with{LogicLanguage=""}};return true;}));
        foreach(var blank in new[]{""," \t\r\n","\u2003\u00a0"})
        {
            using var c=new MySqlConnection(settings.ConnectionString());c.Open();using var q=c.CreateCommand();q.CommandText="UPDATE LogicalNodes SET LogicLanguage=@logic WHERE Code=@code";q.Parameters.AddWithValue("@logic",blank);q.Parameters.AddWithValue("@code",node.Code);
            Assert.AreEqual(3819,Assert.ThrowsExactly<MySqlException>(()=>q.ExecuteNonQuery()).Number);
        }
        Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        Assert.IsTrue((await alice.UpdateLogicalNodeAsync(node.Code,node.Version,node.Properties.Value,""," custom lowercase ")).Success);
        Assert.AreEqual(" custom lowercase ",new LogicalNodeService(new MySqlLineRepository(settings)).GetAll().Data!.Single(n=>n.Code==node.Code).Properties.LogicLanguage);
    });

    [TestMethod,DataRow("none"),DataRow("dml"),DataRow("ddl"),DataRow("publish")]
    public Task Upgrade22BacksUpAndRepairsOnlyBlankLn12_RecoversAtomicallyAndNeverBumpsVersionsTwice(string phase)=>Isolated(settings=>
    {
        new MySqlLineRepository(settings);Sql(settings,"ALTER TABLE LogicalNodes DROP CHECK CK_LN_Logic12Required; UPDATE MapMetadata SET SchemaVersion=22 WHERE Id=1");
        Sql(settings,"""
            INSERT INTO LogicalNodes(Code,Owner,Version,X,Y,SideLength,`Value`,Info,LogicLanguage) VALUES
            ('AAAAAAAAAA','owner',1,50.5,50.5,12,'S','保留说明',''),
            ('BBBBBBBBBB','owner',2,100.5,50.5,12,'T','保留说明','  '),
            ('CCCCCCCCCC','owner',3,150.5,50.5,12,'U','保留说明',' custom '),
            ('DDDDDDDDDD','owner',4,200.5,50.5,12,'','保留说明',''),
            ('EEEEEEEEEE','owner',5,250.5,50.5,6,'判断','保留说明','P'),
            ('FFFFFFFFFF','owner',6,300.5,50.5,4,'False','保留说明','¬P');
            """);
        var before=LineTopologyMySqlTests.RawSnapshot(settings);
        if(phase=="dml")Sql(settings,"CREATE TRIGGER TestLn12Migration BEFORE UPDATE ON LogicalNodes FOR EACH ROW BEGIN IF OLD.Code='BBBBBBBBBB' THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected LN12 update failure'; END IF; END");
        if(phase=="ddl")Sql(settings,"ALTER TABLE MapMetadata ADD CONSTRAINT CK_LN_Logic12Required CHECK (1=1)");
        if(phase=="publish")Sql(settings,"CREATE TRIGGER TestLn12Migration BEFORE UPDATE ON MapMetadata FOR EACH ROW BEGIN IF NEW.SchemaVersion=23 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected LN12 publish failure'; END IF; END");
        if(phase!="none")
        {
            try{Assert.ThrowsExactly<MySqlException>(()=>new MySqlLineRepository(settings));Assert.AreEqual(phase=="dml"?22:2201,Version(settings));if(phase=="dml")Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));}
            finally{Sql(settings,phase=="ddl"?"ALTER TABLE MapMetadata DROP CHECK CK_LN_Logic12Required":"DROP TRIGGER TestLn12Migration");}
        }
        var repository=new MySqlLineRepository(settings);Assert.AreEqual(MySqlLineRepository.CurrentSchemaVersion,Version(settings));var nodes=repository.Transaction(s=>s.LogicalNodes);
        Assert.AreEqual("S",nodes["AAAAAAAAAA"].Properties.LogicLanguage);Assert.AreEqual(2L,nodes["AAAAAAAAAA"].Version);Assert.AreEqual("T",nodes["BBBBBBBBBB"].Properties.LogicLanguage);Assert.AreEqual(3L,nodes["BBBBBBBBBB"].Version);
        Assert.AreEqual(" custom ",nodes["CCCCCCCCCC"].Properties.LogicLanguage);Assert.AreEqual(3L,nodes["CCCCCCCCCC"].Version);Assert.AreEqual("LN_DDDDDDDDDD",nodes["DDDDDDDDDD"].Properties.LogicLanguage);Assert.AreEqual("",nodes["DDDDDDDDDD"].Properties.Value);
        Assert.AreEqual(5L,nodes["EEEEEEEEEE"].Version);Assert.AreEqual("False",nodes["FFFFFFFFFF"].Properties.Value);Assert.AreEqual("¬P",nodes["FFFFFFFFFF"].Properties.LogicLanguage);Assert.AreEqual(6L,nodes["FFFFFFFFFF"].Version);
        using var plan=JsonDocument.Parse(System.IO.File.ReadAllText(repository.LogicalNode12LogicBackupPath!));Assert.AreEqual(23,plan.RootElement.GetProperty("TargetVersion").GetInt32());Assert.IsTrue(System.IO.File.Exists(plan.RootElement.GetProperty("FullBackup").GetString()));
        var after=LineTopologyMySqlTests.RawSnapshot(settings);new MySqlLineRepository(settings);Assert.AreEqual(after,LineTopologyMySqlTests.RawSnapshot(settings));return Task.CompletedTask;
    });
}
