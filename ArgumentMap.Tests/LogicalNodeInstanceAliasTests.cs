using System.Net.Http;
using System.Windows.Controls;
using ArgumentMap.Business;
using ArgumentMap.Contracts;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MySqlConnector;
using WpfArgumentMap;
using static ArgumentMap.Tests.LogicalNodeMySqlTests;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class LogicalNodeInstanceAliasTests
{
    [TestMethod,DataRow("human() a","a∈HUMAN"),DataRow("human(id=1) 甲a1","甲a1∈HUMAN∧1∈HUMAN.ID"),
        DataRow("human(name=要瑞卿) 乙","乙∈HUMAN∧要瑞卿∈HUMAN.NAME"),
        DataRow("human(id=1,name=要瑞卿) c","c∈HUMAN∧1∈HUMAN.ID∧要瑞卿∈HUMAN.NAME"),
        DataRow("HUMAN(name=Alice) Alice1","Alice1∈HUMAN∧Alice∈HUMAN.NAME"),
        DataRow("human(id=1)   Mixed Name  ","Mixed Name∈HUMAN∧1∈HUMAN.ID")]
    public void AliasBecomesSubject_WhilePrefixStillDeterminesClass_AndAllCreationPathsPreserveIt(string value,string expected)
    {
        var repo=new InMemoryLineRepository();var parent=LogicalNodeInstanceTests.Parent(repo,"HUMAN");
        var child=LogicalNodeInstanceTests.Child(repo,value);Assert.AreEqual(expected,child.Properties.LogicLanguage);
        Assert.AreEqual(LogicalNodeInstanceRules.Normalize(value),child.Properties.Value);Assert.AreEqual("HUMAN",LogicalNodeInstanceRules.ParentValue(value));
        Assert.AreEqual(child.Properties,LogicalNodeClipboard.Decode(LogicalNodeClipboard.Encode(10,child.Properties))!.Properties);
        var import=new FlowDiagramService(repo).Import(new("owner",LogicalNodeInstanceTests.Document(value,"HUMAN"),1000.5m,1000.5m));Assert.IsTrue(import.Success,import.Message);
        Assert.IsTrue(import.Data!.Added.LogicalNodes.Where(n=>n.SideLength==10).All(n=>n.Properties.LogicLanguage==expected.Replace("HUMAN","HUMAN1")));
        var selected=MapAreaPasteTests.Select(repo,new(30,30,110,70));
        var pasted=new MapAreaPasteService(repo).Paste(new("owner",selected,300,300));Assert.IsTrue(pasted.Success,pasted.Message);
        Assert.AreEqual(expected.Replace("HUMAN","HUMAN2"),pasted.Data!.Added.LogicalNodes.Single(n=>n.SideLength==10).Properties.LogicLanguage);
        var before=MapUndoTests.Content(repo);
        Assert.IsTrue(MapUndoTests.Edit(repo,r=>new LogicalNodeService(r).Update(new(parent.Code,"owner",parent.Version,"PERSON",""))).Success);
        Assert.AreEqual(expected.Replace("HUMAN","PERSON"),new LogicalNodeService(repo).GetAll().Data!.Single(n=>n.Code==child.Code).Properties.LogicLanguage);
        Assert.IsTrue(new MapUndoService(repo).Undo("owner").Success);Assert.AreEqual(before,MapUndoTests.Content(repo));
    }

    [TestMethod,DataRow("human()a"),DataRow("human() "),DataRow("human() a()"),DataRow("human(id=1,) a"),DataRow("human(id=) a")]
    public void InvalidAliasOrAttributeSyntaxDoesNotSave(string value)
    {
        var repo=new InMemoryLineRepository();LogicalNodeInstanceTests.Parent(repo,"HUMAN");
        Assert.AreEqual("InvalidInstanceValue",new LogicalNodeService(repo).Create(new("owner",90.5m,50.5m,10,new(){Value=value})).Code);
    }

    [STATestMethod]
    public void EditorAddsChangesAndRemovesAlias_AndNavigationUsesClassPrefix()
    {
        using var handler=new DirectLogicalNodeHttpStub();var parent=LogicalNodeInstanceTests.Parent(handler.Repository,"HUMAN");var child=LogicalNodeInstanceTests.Child(handler.Repository,"human(id=1) a");
        using var http=new HttpClient(handler){BaseAddress=new Uri("http://localhost/")};
        var window=new LogicalNodeWindow(new ArgumentMap.Client.HttpLineClient(http,new("owner","test")),"owner",new(),child);
        try
        {
            var value=(TextBox)window.FindName("ValueInput");var logic=(TextBox)window.FindName("LogicInput");
            value.Text="HUMAN(id=1,name=Alice) 甲a1";Assert.AreEqual("human(id=1,name=Alice) 甲a1",value.Text);
            Assert.AreEqual("甲a1∈HUMAN∧1∈HUMAN.ID∧Alice∈HUMAN.NAME",logic.Text);
            LogicalNodeSnapshot? found=null;window.InstanceParentLocationRequested+=node=>found=node;
            Assert.IsTrue(window.LocateInstanceParentAsync().GetAwaiter().GetResult());Assert.AreEqual(parent.Code,found!.Code);
            value.Text="human(id=1,name=Alice)";Assert.AreEqual("human(id=1,name=Alice)∈HUMAN∧1∈HUMAN.ID∧Alice∈HUMAN.NAME",logic.Text);
            value.Text="human() c";Assert.AreEqual("c∈HUMAN",logic.Text);
        }
        finally{window.Close();}
    }
}

[TestClass,TestCategory("MySql")]
public sealed class LogicalNodeInstanceAliasMySqlTests
{
    [TestMethod,DataRow("human() a","a∈HUMAN"),DataRow("human(id=1) 甲a1","甲a1∈HUMAN∧1∈HUMAN.ID"),
        DataRow("human(name=要瑞卿) 乙","乙∈HUMAN∧要瑞卿∈HUMAN.NAME"),
        DataRow("human(id=1,name=要瑞卿) c","c∈HUMAN∧1∈HUMAN.ID∧要瑞卿∈HUMAN.NAME"),
        DataRow("human(id=1)   Mixed Name  ","Mixed Name∈HUMAN∧1∈HUMAN.ID")]
    public Task SqlAndHttpEnforceAliasExpressionIncludingWhitespaceAndCase(string value,string expected)=>Isolated(async settings=>
    {
        var repo=new MySqlLineRepository(settings);LogicalNodeInstanceTests.Parent(repo,"HUMAN");
        await using var server=await ArgumentMap.Server.LocalLineServer.StartAsync(repository:repo);using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");
        var saved=await client.CreateLogicalNodeAsync(90.5m,50.5m,10,new(){Value=value});Assert.IsTrue(saved.Success,saved.Message);Assert.AreEqual(expected,saved.Data!.Properties.LogicLanguage);
        using var c=new MySqlConnection(settings.ConnectionString());c.Open();
        foreach(var wrong in new[]{value+"∈HUMAN",expected.Replace("HUMAN","OTHER"),expected+"∧BAD∈HUMAN.ID"})
        {using var q=c.CreateCommand();q.CommandText="UPDATE LogicalNodes SET LogicLanguage=@logic WHERE SideLength=10";q.Parameters.AddWithValue("@logic",wrong);Assert.AreEqual(3819,Assert.ThrowsExactly<MySqlException>(()=>q.ExecuteNonQuery()).Number);}
        var changed=await client.UpdateLogicalNodeAsync(saved.Data.Code,saved.Data.Version,"human() NEWa1","","");Assert.IsTrue(changed.Success,changed.Message);Assert.AreEqual("NEWa1∈HUMAN",changed.Data!.Properties.LogicLanguage);
        Assert.IsTrue((await client.UndoAsync()).Success);Assert.AreEqual(expected,new LogicalNodeService(new MySqlLineRepository(settings)).GetAll().Data!.Single(n=>n.SideLength==10).Properties.LogicLanguage);
    });

    [TestMethod,DataRow(false),DataRow(true)]
    public Task Version33UpgradePreservesDataAndUndo_AndInterruptedPublicationCanRetry(bool interrupt)=>Isolated(settings=>
    {
        var repo=new MySqlLineRepository(settings);LogicalNodeInstanceTests.Parent(repo,"HUMAN");var child=LogicalNodeInstanceTests.Child(repo,"human(id=1,name=Alice)");
        var moved=MapUndoTests.Edit(repo,r=>new LogicalNodeService(r).Move(new(child.Code,"owner",child.Version,90.5m,100.5m)));Assert.IsTrue(moved.Success,moved.Message);
        // Reproduce v33's no-alias format CHECK. Its values, full expressions and
        // history schema are otherwise unchanged by the optional alias feature.
        Sql(settings,"ALTER TABLE LogicalNodes DROP CHECK CK_LN_InstanceAttributes, ADD CONSTRAINT CK_LN_InstanceAttributes CHECK (SideLength<>10 OR REGEXP_LIKE(`Value`,'^[^()]+[(]([^()=,]+=[^(),]+(,[^()=,]+=[^(),]+)*)?[)]$','c')); UPDATE MapMetadata SET SchemaVersion=33 WHERE Id=1");
        var before=LineTopologyMySqlTests.RawSnapshot(settings);
        if(interrupt)
        {
            Sql(settings,"CREATE TRIGGER FailAliasPublish BEFORE UPDATE ON MapMetadata FOR EACH ROW BEGIN IF NEW.SchemaVersion=35 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected alias publication failure'; END IF; END");
            try{Assert.ThrowsExactly<MySqlException>(()=>new MySqlLineRepository(settings));Assert.ThrowsExactly<InvalidOperationException>(()=>repo.Transaction(s=>true));}
            finally{Sql(settings,"DROP TRIGGER FailAliasPublish");}
        }
        repo=new MySqlLineRepository(settings);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        Assert.IsTrue(new MapUndoService(repo).Undo("owner").Success);Assert.AreEqual(child.Y,new LogicalNodeService(repo).GetAll().Data!.Single(n=>n.Code==child.Code).Y);
        Assert.IsTrue(new LogicalNodeService(repo).Create(new("owner",150.5m,50.5m,10,new(){Value="human() a"})).Success);
        return Task.CompletedTask;
    });
}
