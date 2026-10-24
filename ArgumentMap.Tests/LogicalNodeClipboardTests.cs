using System.Net.Http;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ArgumentMap.Business;
using ArgumentMap.Client;
using ArgumentMap.Contracts;
using ArgumentMap.Server;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfArgumentMap;
using static ArgumentMap.Tests.LogicalNodeMySqlTests;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class LogicalNodeClipboardTests
{
    internal static LogicalNodeProperties Props(int size,string symbol="=")=>new(){Value=size==10?"people(id=1)":size==12?"S":size==6?"可重复的判断":symbol,LogicLanguage=size==10?"people(id=1)∈PEOPLE∧1∈PEOPLE.ID":size==12?"S":size==6?"P":LogicalNodeRelationRules.Expression(symbol),Info="说明原文\n复制后保留"};
    private static LogicalNodeEditor Editor(CoordinateMapView view)=>(LogicalNodeEditor)typeof(CoordinateMapView).GetField("logicalNodes",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(view)!;
    private static MainWindow Window(ILineClient client)=>new(client){Left=-10000,Top=-10000,WindowStartupLocation=WindowStartupLocation.Manual,ShowInTaskbar=false,ShowActivated=false};

    [STATestMethod,DataRow(8,"¬","keyboard"),DataRow(8,"¬","edit-menu"),DataRow(8,"¬","ln-menu"),DataRow(12,"=","keyboard"),DataRow(6,"=","keyboard"),DataRow(8,"=","keyboard"),DataRow(8,"→","keyboard"),
        DataRow(12,"=","edit-menu"),DataRow(6,"=","edit-menu"),DataRow(8,"=","edit-menu"),DataRow(8,"→","edit-menu"),
        DataRow(12,"=","ln-menu"),DataRow(6,"=","ln-menu"),DataRow(8,"=","ln-menu"),DataRow(8,"→","ln-menu"),
        DataRow(10,"=","keyboard"),DataRow(10,"=","edit-menu"),DataRow(10,"=","ln-menu"),DataRow(8,"↔","keyboard"),DataRow(8,"↔","edit-menu"),DataRow(8,"↔","ln-menu"),DataRow(8,"⊇","keyboard"),DataRow(8,"⊇","edit-menu"),DataRow(8,"⊇","ln-menu"),DataRow(8,"⊋","keyboard"),DataRow(8,"⊋","edit-menu"),DataRow(8,"⊋","ln-menu"),DataRow(8,"⊆","keyboard"),DataRow(8,"⊆","edit-menu"),DataRow(8,"⊆","ln-menu"),DataRow(8,"⊊","keyboard"),DataRow(8,"⊊","edit-menu"),DataRow(8,"⊊","ln-menu")]
    public void CopyPasteUsesMouseAndNewOwnership_ServerNumbersOnlyTheRequiredField_OriginalReferencesRemain(int size,string symbol,string entry)
    {
        var map=new TestMap();var road=map.Add(10,50,450,50);var service=new LogicalNodeService(map.Repository);
        if(size==10)LogicalNodeInstanceTests.Parent(map.Repository,x:100.5m,y:150.5m);
        var p=Props(size,symbol);var source=service.Create(new("other",100.5m,50.5m,size,p)).Data!;
        for(var i=1;i<=2;i++)Assert.IsTrue(service.Create(new("other",100.5m+40*i,50.5m,size,p)).Success);
        if(size==6)
        {
            Assert.IsTrue(service.Create(new("other",50.5m,50.5m,6,p with{LogicLanguage="p"})).Success);
            Assert.IsTrue(service.Create(new("other",50.5m,100.5m,4,new(){Value="True",LogicLanguage="P"})).Success);
            Assert.IsTrue(service.Create(new("other",90.5m,100.5m,4,new(){Value="False",LogicLanguage="¬P"})).Success);
        }
        var original=service.GetAll().Data!.ToDictionary(n=>n.Code);var client=new LogicalNodeMoveUiTests.Client(map);var main=Window(client);
        try
        {
            main.Show();var view=(CoordinateMapView)main.FindName("MapView");view.RefreshLinesAsync().GetAwaiter().GetResult();view.RefreshLogicalNodesAsync().GetAwaiter().GetResult();Editor(view).Select(source.Code);Assert.AreEqual(false,Editor(view).Selected!.CanEdit);
            string? text=null;main.WriteLn4Clipboard=s=>text=s;main.ReadLn4Clipboard=()=>text;var before=map.Snapshot();
            if(entry=="keyboard")Assert.IsTrue(main.HandleLn4ClipboardShortcut(Key.C,ModifierKeys.Control,false,view));
            else ((MenuItem)main.FindName(entry=="edit-menu"?"CopySelectionMenu":"CopyLn4Menu")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.IsNotNull(text);Assert.IsFalse(text.Contains(source.Code,StringComparison.Ordinal));var decoded=LogicalNodeClipboard.Decode(text)!;Assert.AreEqual(size,decoded.SideLength);Assert.AreEqual(p,decoded.Properties);
            if(entry=="keyboard")Assert.IsTrue(main.HandleLn4ClipboardShortcut(Key.V,ModifierKeys.Control,false,view));
            else ((MenuItem)main.FindName(entry=="edit-menu"?"PasteSelectionMenu":"PasteLn4Menu")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.IsTrue(view.IsPlacingLogicalNode);Assert.AreEqual(size,view.LogicalNodePlacementSize);Assert.AreEqual(before,map.Snapshot());Assert.AreEqual(0,client.Creates);
            Assert.IsFalse(view.PlaceLogicalNodeAsync(new(100.2,50.8)).GetAwaiter().GetResult());Assert.IsTrue(view.IsPlacingLogicalNode);Assert.AreEqual(before,map.Snapshot());
            Assert.IsTrue(view.PlaceLogicalNodeAsync(new(300.2,50.8)).GetAwaiter().GetResult());Assert.IsFalse(view.IsPlacingLogicalNode);
            var copy=service.GetAll().Data!.Single(n=>n.X==300.5m);Assert.AreNotEqual(source.Code,copy.Code);Assert.AreEqual("owner",copy.Owner);Assert.AreEqual(source.Properties.Info,copy.Properties.Info);Assert.AreEqual(size,copy.SideLength);Assert.AreEqual(road.Code,copy.Positions.Single().RoadCode);Assert.AreEqual(300.5m,copy.Positions.Single().X);Assert.IsFalse(copy.Positions.Single().IsChanged);
            Assert.AreEqual(size==12?"S3":p.Value,copy.Properties.Value);Assert.AreEqual(size==6?"P3":p.LogicLanguage,copy.Properties.LogicLanguage);
            if(size is 6 or 12)StringAssert.Contains(view.OperationMessage,size==6?"P3":"S3");
            main.HandleLn4ClipboardShortcut(Key.V,ModifierKeys.Control,false,view);Assert.IsTrue(view.PlaceLogicalNodeAsync(new(380,50)).GetAwaiter().GetResult());
            copy=service.GetAll().Data!.Single(n=>n.X==380.5m);Assert.AreEqual(size==12?"S4":p.Value,copy.Properties.Value);Assert.AreEqual(size==6?"P4":p.LogicLanguage,copy.Properties.LogicLanguage);
            foreach(var old in original.Values)
            {var current=service.GetAll().Data!.Single(n=>n.Code==old.Code);Assert.AreEqual(old.Version,current.Version);Assert.AreEqual(old.Properties,current.Properties);CollectionAssert.AreEqual(old.Positions,current.Positions);}
        }
        finally{main.Close();}
    }

    [TestMethod,DataRow("size18"),DataRow("size-string"),DataRow("size-fraction"),DataRow("unknown(id=1)"),DataRow("identity"),DataRow("positions"),DataRow("duplicate"),DataRow("missing"),DataRow("format"),
        DataRow("null"),DataRow("blank6"),DataRow("blank12"),DataRow("long-value"),DataRow("long-logic"),DataRow("long-info"),DataRow("relation-value"),DataRow("relation-logic"),DataRow("relation-empty")]
    public void InvalidClipboardCannotInjectIdentityUnsupportedSizesOrBypassTypeRules(string kind)
    {
        var json=JsonNode.Parse(LogicalNodeClipboard.Encode(6,Props(6)))!.AsObject();string? raw=null;
        switch(kind)
        {
            case "size18":json["size"]=18;break;case "size-string":json["size"]="6";break;case "size-fraction":json["size"]=6.5;break;
            case "unknown(id=1)":json["sql"]="text";break;case "identity":json["owner"]="admin";break;case "positions":json["positions"]=new JsonArray();break;
            case "duplicate":raw=json.ToJsonString().Replace("\"size\":6","\"size\":6,\"size\":12");break;
            case "missing":json.Remove("size");break;case "format":json["format"]="argumentmap.flow/1";break;case "null":json["logic"]=null;break;
            case "blank6":json["logic"]=" ";break;case "blank12":json["size"]=12;json["logic"]="";break;
            case "long-value":json["value"]=new string('中',33);break;case "long-logic":json["logic"]=new string('P',256);break;case "long-info":json["info"]=new string('中',256);break;
            default:json["size"]=8;json["value"]=kind=="relation-value"?"=1":"=";json["logic"]=kind=="relation-empty"?"":"P";break;
        }
        Assert.IsNull(LogicalNodeClipboard.Decode(raw??json.ToJsonString()),kind);
    }

    [TestMethod,DataRow("¬"),DataRow("¬(?)")]
    public void NegationClipboardAcceptsLegacyLogic_AndAlwaysWritesPlaceholder(string logic)
    {
        var json=new JsonObject{["format"]="argumentmap.ln/1",["size"]=8,["value"]="¬",["logic"]=logic,["info"]="保留说明"};
        var decoded=LogicalNodeClipboard.Decode(json.ToJsonString());Assert.IsNotNull(decoded);
        Assert.AreEqual("¬",decoded.Properties.Value);Assert.AreEqual("¬(?)",decoded.Properties.LogicLanguage);
        Assert.AreEqual("保留说明",decoded.Properties.Info);
        var encoded=JsonNode.Parse(LogicalNodeClipboard.Encode(decoded.SideLength,decoded.Properties))!;
        Assert.AreEqual("¬(?)",encoded["logic"]!.GetValue<string>());
    }

    [TestMethod,DataRow("¬(P)"),DataRow("¬P"),DataRow("¬(?) "),DataRow(""),DataRow("NOT")]
    public void NegationClipboardRejectsOperandsAndNoncanonicalLogic(string logic)
    {
        var json=new JsonObject{["format"]="argumentmap.ln/1",["size"]=8,["value"]="¬",["logic"]=logic,["info"]=""};
        Assert.IsNull(LogicalNodeClipboard.Decode(json.ToJsonString()));
    }

    [STATestMethod,DataRow(6),DataRow(8),DataRow(12)]
    public void CancelAndChangingPlacementTypeDiscardCopy_AndLn18IsExcluded(int size)
    {
        var view=new CoordinateMapView();var map=new TestMap();view.ConfigureClient(new LogicalNodeMoveUiTests.Client(map));var text=LogicalNodeClipboard.Encode(size,Props(size));
        Assert.IsTrue(view.BeginPasteSelection(text));view.HandleEditorKeyAsync(Key.Escape).GetAwaiter().GetResult();Assert.IsFalse(view.IsPlacingLogicalNode);Assert.AreEqual(0,new LogicalNodeService(map.Repository).GetAll().Data!.Length);
        Assert.IsTrue(view.BeginPasteSelection(text));Assert.IsTrue(view.BeginLogicalNodePlacement(4));Assert.IsTrue(view.PlaceLogicalNodeAsync(new(50,50)).GetAwaiter().GetResult());var ordinary=new LogicalNodeService(map.Repository).GetAll().Data!.Single();Assert.AreEqual("False",ordinary.Properties.Value);Assert.AreEqual("",ordinary.Properties.LogicLanguage);Assert.AreEqual("",ordinary.Properties.Info);
        var start=new LogicalNodeService(map.Repository).Create(new("owner",150.5m,150.5m,18,new(){Value="开始"})).Data!;view.RefreshLogicalNodesAsync().GetAwaiter().GetResult();Editor(view).Select(start.Code);Assert.IsFalse(view.TryCopySelection(out var rejected));Assert.AreEqual("",rejected);
    }

    [STATestMethod,DataRow(6,"bounds"),DataRow(8,"bounds"),DataRow(12,"bounds"),DataRow(6,"node"),DataRow(8,"node"),DataRow(12,"node"),
        DataRow(6,"diagonal"),DataRow(8,"diagonal"),DataRow(12,"diagonal"),DataRow(6,"cross"),DataRow(8,"cross"),DataRow(12,"cross")]
    public void ServerRejectsConflictAddedAfterPreview_WithNoMutationAndKeepsMousePlacement(int size,string conflict)
    {
        var map=new TestMap();var view=new CoordinateMapView();view.ConfigureClient(new LogicalNodeMoveUiTests.Client(map));view.RefreshLinesAsync().GetAwaiter().GetResult();view.RefreshLogicalNodesAsync().GetAwaiter().GetResult();Assert.IsTrue(view.BeginPasteSelection(LogicalNodeClipboard.Encode(size,Props(size))));
        var point=new Point(210,200);
        if(conflict=="bounds")point=new(1,1);
        if(conflict=="node")map.Add(210,200,250,200);
        if(conflict=="diagonal"){map.Add(200,190,220,210);point=new(210,200);}
        if(conflict=="cross")
        {map.Add(190,200,230,200);map.Add(210,180,210,220);map.Calculate();var cross=map.Crosses.GetAll().Data!.Single();Assert.IsTrue(map.Crosses.Edit(new(cross.Key,"owner",cross.Version,CrossState.Connected)).Success);}
        var before=map.Snapshot();Assert.IsFalse(view.PlaceLogicalNodeAsync(point).GetAwaiter().GetResult());Assert.IsTrue(view.IsPlacingLogicalNode);Assert.AreEqual(before,map.Snapshot());
        Assert.IsTrue(view.PlaceLogicalNodeAsync(new(350,350)).GetAwaiter().GetResult());Assert.IsFalse(view.IsPlacingLogicalNode);
    }

    [STATestMethod]
    public void KAndJMayCompleteDuringPastePreview_AndCopyMetadataSurvivesBothRefreshes()
    {
        var map=new TestMap();var source=new LogicalNodeService(map.Repository).Create(new("owner",100.5m,100.5m,12,Props(12))).Data!;
        var kGate=new TaskCompletionSource<ServiceResult<CrossCalculationProgress>>(TaskCreationOptions.RunContinuationsAsynchronously);var jGate=new TaskCompletionSource<ServiceResult<LogicalNodeCalculationProgress>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client=new BackgroundCrossTests.Client(map){Step=()=>kGate.Task,JStep=_=>jGate.Task};var main=Window(client);Task<bool>? k=null,j=null;
        try
        {
            main.Show();var view=(CoordinateMapView)main.FindName("MapView");view.RefreshLogicalNodesAsync().GetAwaiter().GetResult();Editor(view).Select(source.Code);Assert.IsTrue(view.TryCopySelection(out var copy));
            k=view.CalculateCrossesAsync();j=view.CalculateLogicalNodesAsync();LogicalNodeWindowEntryTests.DrainUntil(()=>view.IsCalculatingCrosses&&view.IsCalculatingLogicalNodes);
            Assert.IsTrue(view.BeginPasteSelection(copy));kGate.SetResult(ServiceResult<CrossCalculationProgress>.Ok(new(false,0,0),"K完成"));jGate.SetResult(ServiceResult<LogicalNodeCalculationProgress>.Ok(new(false,0,1),"J完成"));
            LogicalNodeWindowEntryTests.DrainUntil(()=>k.IsCompleted&&j.IsCompleted);Assert.IsTrue(k.Result);Assert.IsTrue(j.Result);Assert.IsTrue(view.IsPlacingLogicalNode);Assert.AreEqual(12,view.LogicalNodePlacementSize);
            Assert.IsTrue(view.PlaceLogicalNodeAsync(new(300,300)).GetAwaiter().GetResult());var node=new LogicalNodeService(map.Repository).GetAll().Data!.Single(n=>n.Code!=source.Code);Assert.AreEqual("S1",node.Properties.Value);Assert.AreEqual("S",node.Properties.LogicLanguage);
        }
        finally{main.Close();kGate.TrySetResult(ServiceResult<CrossCalculationProgress>.Ok(new(false,0,0),"完成"));jGate.TrySetResult(ServiceResult<LogicalNodeCalculationProgress>.Ok(new(false,0,0),"完成"));if(k is not null&&j is not null)LogicalNodeWindowEntryTests.DrainUntil(()=>k.IsCompleted&&j.IsCompleted);}
    }
}

[TestClass,TestCategory("MySql"),TestCategory("Http")]
public sealed class LogicalNodeClipboardMySqlTests
{
    [TestMethod,DataRow(12,"="),DataRow(6,"="),DataRow(8,"="),DataRow(8,"→"),DataRow(8,"⊇"),DataRow(8,"⊋"),DataRow(8,"⊆"),DataRow(8,"⊊"),DataRow(8,"↔")]
    public Task TwoAccountsPasteSameCopy_UniqueSuffixesAreAtomic_AndFailedWriteConsumesNoSuffix(int size,string symbol)=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);Add(repository,10,50,450,50);var service=new LogicalNodeService(repository);var source=service.Create(new("source",100.5m,50.5m,size,LogicalNodeClipboardTests.Props(size,symbol))).Data!;
        var copy=LogicalNodeClipboard.Decode(LogicalNodeClipboard.Encode(source.SideLength,source.Properties))!;
        await using var server=await LocalLineServer.StartAsync(repository:repository);using var h1=new HttpClient{BaseAddress=server.BaseAddress};using var h2=new HttpClient{BaseAddress=server.BaseAddress};
        var alice=await TestLogin.ClientAsync(h1,server,"alice");var bob=await TestLogin.ClientAsync(h2,server,"bob");var before=LineTopologyMySqlTests.RawSnapshot(settings);
        Sql(settings,"CREATE TRIGGER FailTypedLnPaste BEFORE INSERT ON LogicalNodePositions FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected typed LN paste failure'");
        try{Assert.AreEqual("DatabaseError",(await alice.CreateLogicalNodeAsync(200.5m,50.5m,copy.SideLength,copy.Properties)).Code);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));}
        finally{Sql(settings,"DROP TRIGGER FailTypedLnPaste");}
        var results=await Task.WhenAll(alice.CreateLogicalNodeAsync(200.5m,50.5m,copy.SideLength,copy.Properties),bob.CreateLogicalNodeAsync(300.5m,50.5m,copy.SideLength,copy.Properties));
        Assert.IsTrue(results.All(r=>r.Success),string.Join(";",results.Select(r=>r.Message)));var nodes=results.Select(r=>r.Data!).ToArray();CollectionAssert.AreEquivalent(new[]{"alice","bob"},nodes.Select(n=>n.Owner).ToArray());Assert.AreEqual(2,nodes.Select(n=>n.Code).Distinct().Count());
        if(size==12){CollectionAssert.AreEquivalent(new[]{"S1","S2"},nodes.Select(n=>n.Properties.Value).ToArray());Assert.IsTrue(nodes.All(n=>n.Properties.LogicLanguage=="S"));}
        else if(size==6){CollectionAssert.AreEquivalent(new[]{"P1","P2"},nodes.Select(n=>n.Properties.LogicLanguage).ToArray());Assert.IsTrue(nodes.All(n=>n.Properties.Value==source.Properties.Value));}
        else Assert.IsTrue(nodes.All(n=>n.Properties.Value==symbol&&n.Properties.LogicLanguage==LogicalNodeRelationRules.Expression(symbol)));
        Assert.IsTrue(nodes.All(n=>n.Positions.Length==1&&!n.Positions[0].IsChanged&&n.Properties.Info==source.Properties.Info));var unchanged=service.GetAll().Data!.Single(n=>n.Code==source.Code);Assert.AreEqual(source.Properties,unchanged.Properties);Assert.AreEqual(source.Version,unchanged.Version);
        before=LineTopologyMySqlTests.RawSnapshot(settings);await TestLogin.ClientAsync(h1,server,"alice");Assert.AreEqual("LoginExpired",(await alice.CreateLogicalNodeAsync(400.5m,50.5m,size,copy.Properties)).Code);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        Assert.AreEqual(Snapshot(repository),Snapshot(new MySqlLineRepository(settings)));
    });
}
