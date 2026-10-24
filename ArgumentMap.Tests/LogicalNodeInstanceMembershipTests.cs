using System.Net.Http;
using System.Text.Json.Nodes;
using System.Windows.Controls;
using ArgumentMap.Business;
using ArgumentMap.Contracts;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfArgumentMap;
using MySqlConnector;
using static ArgumentMap.Tests.LogicalNodeMySqlTests;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class LogicalNodeInstanceMembershipTests
{
    [TestMethod,DataRow("human()","human()∈HUMAN"),
        DataRow("human(id=1)","human(id=1)∈HUMAN∧1∈HUMAN.ID"),
        DataRow("human(name=要瑞卿)","human(name=要瑞卿)∈HUMAN∧要瑞卿∈HUMAN.NAME"),
        DataRow("human(id=1,name=要瑞卿)","human(id=1,name=要瑞卿)∈HUMAN∧1∈HUMAN.ID∧要瑞卿∈HUMAN.NAME"),
        DataRow("human(Name=Alice,ID=ABC)","human(Name=Alice,ID=ABC)∈HUMAN∧Alice∈HUMAN.NAME∧ABC∈HUMAN.ID")]
    public void ExactRequestedExpressionsAreSharedBySaveClipboardImportRenameAndUndo(string value,string expression)
    {
        var repo=new InMemoryLineRepository();var parent=LogicalNodeInstanceTests.Parent(repo,"HUMAN");
        var child=LogicalNodeInstanceTests.Child(repo,value);Assert.AreEqual(expression,child.Properties.LogicLanguage);
        Assert.AreEqual(child.Properties,LogicalNodeClipboard.Decode(LogicalNodeClipboard.Encode(10,child.Properties))!.Properties);
        var imported=new FlowDiagramService(repo).Import(new("owner",LogicalNodeInstanceTests.Document(value,"HUMAN"),1000.5m,1000.5m));Assert.IsTrue(imported.Success,imported.Message);
        Assert.IsTrue(imported.Data!.Added.LogicalNodes.Where(n=>n.SideLength==10).All(n=>n.Properties.LogicLanguage==expression.Replace("human(","human1(").Replace("HUMAN","HUMAN1")));
        var renamed=MapUndoTests.Edit(repo,r=>new LogicalNodeService(r).Update(new(parent.Code,"owner",parent.Version,"PERSON","")));
        Assert.IsTrue(renamed.Success,renamed.Message);Assert.AreEqual(expression.Replace("human(","person(").Replace("HUMAN","PERSON"),new LogicalNodeService(repo).GetAll().Data!.Single(n=>n.Code==child.Code).Properties.LogicLanguage);
        Assert.IsTrue(new MapUndoService(repo).Undo("owner").Success);Assert.AreEqual(expression,new LogicalNodeService(repo).GetAll().Data!.Single(n=>n.Code==child.Code).Properties.LogicLanguage);
    }

    [TestMethod,DataRow("human(id=1,)"),DataRow("human(,name=Alice)"),DataRow("human(id=1,,name=Alice)"),
        DataRow("human(id=1,name=)"),DataRow("human(id=1, =Alice)"),DataRow("human( )")]
    public void EmptyAttributeItemsAreRejectedButEmptyParenthesesAreAllowed(string invalid)
    {
        var repo=new InMemoryLineRepository();LogicalNodeInstanceTests.Parent(repo,"HUMAN");
        Assert.AreEqual("InvalidInstanceValue",new LogicalNodeService(repo).Create(new("owner",90.5m,50.5m,10,new(){Value=invalid})).Code);
        Assert.IsFalse(LogicalNodeClipboard.Valid(10,new(){Value=invalid,LogicLanguage=LogicalNodeInstanceRules.Expression(invalid)}));
    }

    [TestMethod]
    public void ExpandedChineseMembershipCanRoundTripThroughClipboardBeyondFormerTextLimit()
    {
        var repo=new InMemoryLineRepository();var parent="H"+new string('人',31);LogicalNodeInstanceTests.Parent(repo,parent);
        var value=LogicalNodeInstanceRules.Normalize(parent)+"("+string.Join(',',Enumerable.Repeat("a=人",46))+")";
        var child=LogicalNodeInstanceTests.Child(repo,value);
        Assert.IsTrue(child.Properties.LogicLanguage.Length>255);
        var encoded=LogicalNodeClipboard.Encode(10,child.Properties);Assert.IsTrue(encoded.Length>8192);
        Assert.AreEqual(child.Properties,LogicalNodeClipboard.Decode(encoded)!.Properties);
    }

    [STATestMethod]
    public void EditorSwitchingBetweenEmptyAndMultipleAttributesReplacesEntireGeneratedLogic()
    {
        using var handler=new DirectLogicalNodeHttpStub();LogicalNodeInstanceTests.Parent(handler.Repository,"HUMAN");
        var child=LogicalNodeInstanceTests.Child(handler.Repository,"human()");
        using var http=new HttpClient(handler){BaseAddress=new Uri("http://localhost/")};
        var window=new LogicalNodeWindow(new ArgumentMap.Client.HttpLineClient(http,new("owner","test")),"owner",new(),child);
        try
        {
            var value=(TextBox)window.FindName("ValueInput");var logic=(TextBox)window.FindName("LogicInput");
            Assert.IsTrue(logic.IsReadOnly);Assert.AreEqual(4096,logic.MaxLength);
            value.Text="HUMAN(id=1,name=要瑞卿)";Assert.AreEqual("human(id=1,name=要瑞卿)",value.Text);
            Assert.AreEqual("human(id=1,name=要瑞卿)∈HUMAN∧1∈HUMAN.ID∧要瑞卿∈HUMAN.NAME",logic.Text);
            value.Text="human()";Assert.AreEqual("human()∈HUMAN",logic.Text);
        }
        finally{window.Close();}
    }
}

[TestClass,TestCategory("MySql")]
public sealed class LogicalNodeInstanceMembershipMySqlTests
{
    [TestMethod,DataRow("human()","human()∈HUMAN"),
        DataRow("human(id=1,name=要瑞卿)","human(id=1,name=要瑞卿)∈HUMAN∧1∈HUMAN.ID∧要瑞卿∈HUMAN.NAME"),
        DataRow("human(Name=Alice,ID=ABC)","human(Name=Alice,ID=ABC)∈HUMAN∧Alice∈HUMAN.NAME∧ABC∈HUMAN.ID")]
    public Task SqlEnforcesCompleteAttributeMembershipWithoutChangingOtherNodes(string value,string expected)=>Isolated(settings=>
    {
        var repo=new MySqlLineRepository(settings);LogicalNodeInstanceTests.Parent(repo,"HUMAN");var child=LogicalNodeInstanceTests.Child(repo,value);
        Assert.AreEqual(expected,child.Properties.LogicLanguage);
        foreach(var wrong in new[]{value+"∈HUMAN",expected+"∧extra∈HUMAN.ID",expected.Replace("HUMAN","PERSON"),value+" ∈ HUMAN"}.Where(s=>s!=expected))
        {
            using var c=new MySqlConnection(settings.ConnectionString());c.Open();using var q=c.CreateCommand();q.CommandText="UPDATE LogicalNodes SET LogicLanguage=@logic WHERE SideLength=10";q.Parameters.AddWithValue("@logic",wrong);
            Assert.AreEqual(3819,Assert.ThrowsExactly<MySqlException>(()=>q.ExecuteNonQuery()).Number,wrong);
        }
        var other=new LogicalNodeService(repo).Create(new("owner",140.5m,50.5m,6,new(){LogicLanguage="P"}));Assert.IsTrue(other.Success,other.Message);
        Assert.AreEqual(3819,Assert.ThrowsExactly<MySqlException>(()=>Sql(settings,"UPDATE LogicalNodes SET LogicLanguage=REPEAT('P',256) WHERE SideLength=6")).Number);
        Assert.AreEqual(expected,new LogicalNodeService(new MySqlLineRepository(settings)).GetAll().Data!.Single(n=>n.Code==child.Code).Properties.LogicLanguage);
        Sql(settings,"DROP TRIGGER TR_LN10_Attributes_Insert");Assert.ThrowsExactly<InvalidOperationException>(()=>new MySqlLineRepository(settings));
        return Task.CompletedTask;
    });

    [TestMethod,DataRow(false),DataRow(true)]
    public Task UpgradeRealVersion32PreservesValuesAndUndo_RetriesWithoutDoubleVersionIncrement(bool interrupt)=>Isolated(settings=>
    {
        var repo=new MySqlLineRepository(settings);var parent=LogicalNodeInstanceTests.Parent(repo,"HUMAN");
        var child=LogicalNodeInstanceTests.Child(repo,"human(name=Alice)");
        Assert.IsTrue(MapUndoTests.Edit(repo,r=>new LogicalNodeService(r).Update(new(parent.Code,"owner",parent.Version,"PERSON",""))).Success);
        Sql(settings,"DROP TRIGGER TR_LN10_Attributes_Insert; DROP TRIGGER TR_LN10_Attributes_Update; ALTER TABLE LogicalNodes DROP CHECK CK_LN_InstanceLogic, DROP CHECK CK_LN_InstanceAttributes, DROP CHECK CK_LN_LogicLength");
        Sql(settings,"UPDATE LogicalNodes SET LogicLanguage=CONCAT(`Value`,' ∈ ',UPPER(SUBSTRING_INDEX(`Value`,'(',1))) WHERE SideLength=10");
        using(var c=new MySqlConnection(settings.ConnectionString()))
        {
            c.Open();using var q=c.CreateCommand();q.CommandText="SELECT Sequence,Payload FROM MapUndoHistory";var rows=new List<(long,JsonNode)>();using(var reader=q.ExecuteReader())while(reader.Read())rows.Add((reader.GetInt64(0),JsonNode.Parse(reader.GetString(1))!));
            foreach(var (seq,payload) in rows)
            {
                foreach(var change in payload["Delta"]!["Nodes"]!.AsArray())foreach(var key in new[]{"Before","After"})
                    if(change![key] is {} node&&node["SideLength"]!.GetValue<int>()==10)
                    {var p=node["Properties"]!;var value=p["Value"]!.GetValue<string>();p["LogicLanguage"]=value+" ∈ "+LogicalNodeInstanceRules.ParentValue(value);}
                using var update=c.CreateCommand();update.CommandText="UPDATE MapUndoHistory SET Payload=@payload WHERE Sequence=@seq";update.Parameters.AddWithValue("@payload",payload.ToJsonString());update.Parameters.AddWithValue("@seq",seq);update.ExecuteNonQuery();
            }
        }
        Sql(settings,"ALTER TABLE LogicalNodes MODIFY COLUMN LogicLanguage VARCHAR(255) NOT NULL DEFAULT '', ADD CONSTRAINT CK_LN_InstanceLogic CHECK (SideLength<>10 OR CONVERT(LogicLanguage USING binary)=CONVERT(CONCAT(`Value`,' ∈ ',UPPER(SUBSTRING_INDEX(`Value`,'(',1))) USING binary)), ADD CONSTRAINT CK_LN_InstanceAttributes CHECK (SideLength<>10 OR REGEXP_LIKE(`Value`,'^[^()]+[(][^()=]+=[^()]+[)]$','c')); UPDATE MapMetadata SET SchemaVersion=32 WHERE Id=1");
        if(interrupt)
        {
            Sql(settings,"CREATE TRIGGER FailMembershipPublish BEFORE UPDATE ON MapMetadata FOR EACH ROW BEGIN IF NEW.SchemaVersion=35 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected membership publication failure'; END IF; END");
            try{Assert.ThrowsExactly<MySqlException>(()=>new MySqlLineRepository(settings));Assert.ThrowsExactly<InvalidOperationException>(()=>repo.Transaction(s=>true));}
            finally{Sql(settings,"DROP TRIGGER FailMembershipPublish");}
        }
        repo=new MySqlLineRepository(settings);var current=new LogicalNodeService(repo).GetAll().Data!.Single(n=>n.SideLength==10);
        Assert.AreEqual("person(name=Alice)",current.Properties.Value);Assert.AreEqual("person(name=Alice)∈PERSON∧Alice∈PERSON.NAME",current.Properties.LogicLanguage);Assert.AreEqual(child.Version+2,current.Version);
        Assert.AreEqual(current,new LogicalNodeService(new MySqlLineRepository(settings)).GetAll().Data!.Single(n=>n.Code==child.Code) with{Positions=current.Positions});
        var undo=new MapUndoService(repo).Undo("owner");Assert.IsTrue(undo.Success,undo.Message);
        Assert.AreEqual("human(name=Alice)∈HUMAN∧Alice∈HUMAN.NAME",undo.Data!.Map.LogicalNodes.Single(n=>n.Code==child.Code).Properties.LogicLanguage);
        return Task.CompletedTask;
    });
}
