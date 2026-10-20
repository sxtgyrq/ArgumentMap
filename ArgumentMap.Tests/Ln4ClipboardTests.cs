using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
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

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class Ln4ClipboardTests
{
    private static MainWindow Window(ILineClient client)=>new(client){WindowStartupLocation=WindowStartupLocation.Manual,Left=-10000,Top=-10000,ShowInTaskbar=false,ShowActivated=false};
    private static LogicalNodeEditor Editor(CoordinateMapView view)=>(LogicalNodeEditor)typeof(CoordinateMapView).GetField("logicalNodes",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(view)!;
    private static LineEditor Lines(CoordinateMapView view)=>(LineEditor)typeof(CoordinateMapView).GetField("editor",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(view)!;
    internal static LogicalNodeSnapshot Seed(ILineRepository repository,string value="True",string logic="P",int size=4,decimal x=100.5m,string owner="other")
    {var result=new LogicalNodeService(repository).Create(new(owner,x,50.5m,size,new(){Value=value,LogicLanguage=logic,Info="复制说明\n保留原文"}));Assert.IsTrue(result.Success,result.Message);return result.Data!;}
    private static void Select(CoordinateMapView view,string code){Assert.IsTrue(view.RefreshLogicalNodesAsync().GetAwaiter().GetResult());Editor(view).Select(code);}

    [STATestMethod,DataRow("True","P"),DataRow("False","¬P"),DataRow("True",""),DataRow("False","")]
    public void CtrlCopyPasteWaitsForMousePoint_CreatesIndependentOwnedNode_AndRecalculatesPositions(string value,string logic)
    {
        var map=new TestMap();var road=map.Add(10,50,400,50);Seed(map.Repository,"判断","P",6,50.5m);var source=Seed(map.Repository,value,logic);
        var client=new LogicalNodeMoveUiTests.Client(map);var main=Window(client);
        try
        {
            main.Show();var view=(CoordinateMapView)main.FindName("MapView");view.RefreshLinesAsync().GetAwaiter().GetResult();Select(view,source.Code);Assert.AreEqual(false,Editor(view).Selected!.CanEdit);
            string? clipboard=null;main.WriteLn4Clipboard=text=>clipboard=text;main.ReadLn4Clipboard=()=>clipboard;var before=map.Snapshot();
            Assert.IsTrue(main.HandleLn4ClipboardShortcut(Key.C,ModifierKeys.Control,false,view));Assert.IsNotNull(clipboard);Assert.IsFalse(clipboard.Contains(source.Code,StringComparison.Ordinal));
            Assert.IsTrue(main.HandleLn4ClipboardShortcut(Key.V,ModifierKeys.Control,false,view));Assert.IsTrue(view.IsPlacingLogicalNode);Assert.AreEqual(4,view.LogicalNodePlacementSize);Assert.AreEqual(0,client.Creates);Assert.AreEqual(before,map.Snapshot());
            Assert.IsTrue(view.PlaceLogicalNodeAsync(new Point(250.2,50.8)).GetAwaiter().GetResult());Assert.IsFalse(view.IsPlacingLogicalNode);
            var copy=new LogicalNodeService(map.Repository).GetAll().Data!.Single(n=>n.X==250.5m);Assert.AreNotEqual(source.Code,copy.Code);Assert.AreEqual("owner",copy.Owner);Assert.AreEqual(source.Properties,copy.Properties);Assert.AreEqual(4,copy.SideLength);
            Assert.AreEqual(road.Code,copy.Positions.Single().RoadCode);Assert.AreEqual(250.5m,copy.Positions.Single().X);Assert.AreNotEqual(source.Positions.Single().RoadPercent,copy.Positions.Single().RoadPercent);
            Assert.AreEqual(source.Code,new LogicalNodeService(map.Repository).GetAll().Data!.Single(n=>n.X==100.5m).Code);
            main.HandleLn4ClipboardShortcut(Key.V,ModifierKeys.Control,false,view);Assert.IsTrue(view.PlaceLogicalNodeAsync(new Point(300,50)).GetAwaiter().GetResult());Assert.AreEqual(2,client.Creates);
        }
        finally{main.Close();}
    }

    [STATestMethod]
    public void EscAndF5DiscardPasteMetadata_AndRejectedPointCanBeRetriedWithoutWriting()
    {
        var map=new TestMap();Seed(map.Repository,"判断","P",6,50.5m);var source=Seed(map.Repository);var client=new LogicalNodeMoveUiTests.Client(map);var main=Window(client);
        try
        {
            main.Show();var view=(CoordinateMapView)main.FindName("MapView");Select(view,source.Code);Assert.IsTrue(view.TryCopySelectedLogicalNode(out var text));Assert.IsTrue(view.BeginPasteLogicalNode(text));var before=map.Snapshot();
            Assert.IsFalse(view.PlaceLogicalNodeAsync(new Point(104,50)).GetAwaiter().GetResult());Assert.IsTrue(view.IsPlacingLogicalNode);Assert.AreEqual(before,map.Snapshot());Assert.AreEqual(0,client.Creates);
            view.HandleEditorKeyAsync(Key.Escape).GetAwaiter().GetResult();Assert.IsFalse(view.IsPlacingLogicalNode);Assert.IsFalse(view.PlaceLogicalNodeAsync(new Point(200,50)).GetAwaiter().GetResult());
            Assert.IsTrue(view.BeginLogicalNodePlacement(4));Assert.IsTrue(view.PlaceLogicalNodeAsync(new Point(200,50)).GetAwaiter().GetResult());var ordinary=new LogicalNodeService(map.Repository).GetAll().Data!.Single(n=>n.X==200.5m);Assert.AreEqual("False",ordinary.Properties.Value);Assert.AreEqual("",ordinary.Properties.LogicLanguage);Assert.AreEqual("",ordinary.Properties.Info);
            Assert.IsTrue(view.BeginPasteLogicalNode(text));Assert.IsTrue(view.BeginLogicalNodePlacement(6));Assert.IsTrue(view.PlaceLogicalNodeAsync(new Point(300,50)).GetAwaiter().GetResult());var six=new LogicalNodeService(map.Repository).GetAll().Data!.Single(n=>n.X==300.5m);Assert.AreEqual(6,six.SideLength);Assert.AreEqual("LN_"+six.Code,six.Properties.LogicLanguage);
        }
        finally{main.Close();}
    }

    [STATestMethod,DataRow("rename"),DataRow("delete"),DataRow("occupied")]
    public void ServerRechecksChangedSourceOrNewGeometry_RejectsWholePasteAndKeepsPlacement(string change)
    {
        var map=new TestMap();var decision=Seed(map.Repository,"判断","P",6,50.5m);var source=Seed(map.Repository);var service=new LogicalNodeService(map.Repository);var client=new LogicalNodeMoveUiTests.Client(map);var view=new CoordinateMapView();view.ConfigureClient(client);Select(view,source.Code);Assert.IsTrue(view.TryCopySelectedLogicalNode(out var text));Assert.IsTrue(view.BeginPasteLogicalNode(text));
        if(change=="rename")Assert.IsTrue(service.Update(new(decision.Code,decision.Owner,decision.Version,"判断","",LogicLanguage:"Q")).Success);
        else if(change=="delete")Assert.IsTrue(service.Delete(new(decision.Code,decision.Owner,decision.Version)).Success);
        else Seed(map.Repository,"False","",4,250.5m);
        var before=map.Snapshot();Assert.IsFalse(view.PlaceLogicalNodeAsync(new Point(250,50)).GetAwaiter().GetResult());Assert.AreEqual(before,map.Snapshot());Assert.IsTrue(view.IsPlacingLogicalNode);Assert.AreEqual(1,client.Creates);
        if(change=="occupied")Assert.IsTrue(view.PlaceLogicalNodeAsync(new Point(300,50)).GetAwaiter().GetResult());else StringAssert.Contains(view.OperationMessage,"唯一 LN6");
    }

    [STATestMethod,DataRow("bounds"),DataRow("node"),DataRow("diagonal"),DataRow("connectedCross")]
    public void PastedNodeMustRespectBoundsRoadNodesDiagonalAndCrossRules(string kind)
    {
        var map=new TestMap();Seed(map.Repository,"判断","P",6,50.5m);var source=Seed(map.Repository);var target=kind switch{"bounds"=>new Point(1,50),"node"=>new Point(200,200),"diagonal"=>new Point(210,210),_=>new Point(210,200)};
        if(kind=="node")map.Add(200,200,240,200);if(kind=="diagonal")map.Add(200,200,220,220);
        if(kind=="connectedCross"){map.Add(190,200,230,200);map.Add(210,180,210,220);map.Calculate();var cross=map.Crosses.GetAll().Data!.Single();Assert.IsTrue(map.Crosses.Edit(new(cross.Key,"owner",cross.Version,CrossState.Connected)).Success);}
        var view=new CoordinateMapView();view.ConfigureClient(new LogicalNodeMoveUiTests.Client(map));view.RefreshLinesAsync().GetAwaiter().GetResult();Select(view,source.Code);Assert.IsTrue(view.TryCopySelectedLogicalNode(out var text));Assert.IsTrue(view.BeginPasteLogicalNode(text));var before=map.Snapshot();
        Assert.IsFalse(view.PlaceLogicalNodeAsync(target).GetAwaiter().GetResult());Assert.AreEqual(before,map.Snapshot());Assert.IsTrue(view.IsPlacingLogicalNode);
    }

    [STATestMethod]
    public void CtrlDoesNotReverseLine_PlainCStillReverses_VDoesNothing_AndPasteCannotDiscardDraft()
    {
        var map=new TestMap();var client=new LogicalNodeMoveUiTests.Client(map);var main=Window(client);
        try
        {
            main.Show();var view=(CoordinateMapView)main.FindName("MapView");Assert.IsTrue(view.NewLineAsync().GetAwaiter().GetResult());Assert.IsTrue(view.BeginDrawingAsync().GetAwaiter().GetResult());var editor=Lines(view);editor.AddPoint(new(100,100));editor.AddPoint(new(110,100));editor.AddPoint(new(120,100));var original=editor.DraftNodes.ToArray();
            main.WriteLn4Clipboard=_=>Assert.Fail("A drawing must not be copied as LN4.");main.ReadLn4Clipboard=()=>Ln4Clipboard.Encode(new(){Value="False"});
            Assert.IsTrue(main.HandleLn4ClipboardShortcut(Key.C,ModifierKeys.Control,false,view));CollectionAssert.AreEqual(original,editor.DraftNodes.ToArray());
            Assert.IsTrue(main.HandleLn4ClipboardShortcut(Key.V,ModifierKeys.Control,false,view));Assert.IsFalse(view.IsPlacingLogicalNode);CollectionAssert.AreEqual(original,editor.DraftNodes.ToArray());
            Assert.IsFalse(main.HandleLn4ClipboardShortcut(Key.C,ModifierKeys.None,false,view));var key=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(main)!,0,Key.C){RoutedEvent=Keyboard.PreviewKeyDownEvent};main.RaiseEvent(key);LogicalNodeWindowEntryTests.DrainUntil(()=>editor.DraftNodes.First().X==120);Assert.IsTrue(key.Handled);
            var reversed=editor.DraftNodes.ToArray();key=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(main)!,0,Key.V){RoutedEvent=Keyboard.PreviewKeyDownEvent};main.RaiseEvent(key);Assert.IsFalse(key.Handled);CollectionAssert.AreEqual(reversed,editor.DraftNodes.ToArray());
        }
        finally{main.Close();}
    }

    [STATestMethod]
    public void TextFieldsKeepStandardCopyPaste_OtherModifiersAndRepeatsDoNotAct_ClipboardBusyIsReported()
    {
        var map=new TestMap();var source=Seed(map.Repository,"False","");var main=Window(new LogicalNodeMoveUiTests.Client(map));
        try
        {
            main.Show();var view=(CoordinateMapView)main.FindName("MapView");Select(view,source.Code);var reads=0;var writes=0;main.WriteLn4Clipboard=_=>writes++;main.ReadLn4Clipboard=()=>{reads++;return null;};
            foreach(var input in new IInputElement[]{new TextBox(),new RichTextBox(),new PasswordBox()})foreach(var key in new[]{Key.C,Key.V})Assert.IsFalse(main.HandleLn4ClipboardShortcut(key,ModifierKeys.Control,false,input));
            Assert.AreEqual(0,reads);Assert.AreEqual(0,writes);Assert.IsFalse(main.HandleLn4ClipboardShortcut(Key.C,ModifierKeys.Control|ModifierKeys.Shift,false,view));Assert.IsFalse(main.HandleLn4ClipboardShortcut(Key.C,ModifierKeys.Alt,false,view));
            Assert.IsTrue(main.HandleLn4ClipboardShortcut(Key.C,ModifierKeys.Control,true,view));Assert.IsTrue(main.HandleLn4ClipboardShortcut(Key.V,ModifierKeys.Control,true,view));Assert.AreEqual(0,reads);Assert.AreEqual(0,writes);
            main.WriteLn4Clipboard=_=>throw new ExternalException("busy");Assert.IsTrue(main.HandleLn4ClipboardShortcut(Key.C,ModifierKeys.Control,false,view));StringAssert.Contains(view.OperationMessage,"剪贴板暂时被占用");
            main.ReadLn4Clipboard=()=>throw new ExternalException("busy");Assert.IsTrue(main.HandleLn4ClipboardShortcut(Key.V,ModifierKeys.Control,false,view));Assert.IsFalse(view.IsPlacingLogicalNode);
            ((MenuItem)main.FindName("CopyLn4Menu")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));StringAssert.Contains(view.OperationMessage,"剪贴板暂时被占用");
        }
        finally{main.Close();}
    }

    [TestMethod,DataRow("text"),DataRow("format"),DataRow("owner"),DataRow("duplicate"),DataRow("value"),DataRow("null"),DataRow("long"),DataRow("missing")]
    public void UnrelatedOrMalformedClipboardIsRejectedWithoutReadingObjectIdentity(string kind)
    {
        var text=Ln4Clipboard.Encode(new(){Value="True",LogicLanguage="P",Info="说明"});var json=JsonNode.Parse(text)!;
        switch(kind){case "text":text="普通文字";break;case "duplicate":text=text.Replace("\"format\":","\"format\":\"argumentmap.ln4/1\",\"format\":",StringComparison.Ordinal);break;case "format":json["format"]="wrong";break;case "owner":json["owner"]="admin";break;case "value":json["value"]="true";break;case "null":json["logic"]=null;break;case "long":json["info"]=new string('x',256);break;default:json.AsObject().Remove("info");break;}
        if(kind is not ("text" or "duplicate"))text=json.ToJsonString();Assert.IsNull(Ln4Clipboard.Decode(text));
    }

    [STATestMethod,DataRow(18)]
    public void CopyExcludesUniqueStartLn18(int size)
    {
        var map=new TestMap();var source=Seed(map.Repository,"普通节点","P",size);var view=new CoordinateMapView();view.ConfigureClient(new LogicalNodeMoveUiTests.Client(map));Select(view,source.Code);Assert.IsFalse(view.TryCopySelectedLogicalNode(out var text));Assert.AreEqual("",text);
    }
}

[TestClass,TestCategory("MySql"),TestCategory("Http")]
public sealed class Ln4ClipboardMySqlTests
{
    [TestMethod]
    public Task HttpPasteRevalidatesAndRollsBackNodeAndPositionsOnSqlFailure_ThenRetryGetsIndependentOwnership()=>
        LogicalNodeMySqlTests.Isolated(async settings=>
        {
            var repository=new MySqlLineRepository(settings);LogicalNodeMySqlTests.Add(repository,10,50,400,50);Ln4ClipboardTests.Seed(repository,"判断","P",6,50.5m);var source=Ln4ClipboardTests.Seed(repository,"False","¬P");
            await using var server=await LocalLineServer.StartAsync(repository:repository);using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"alice");
            var properties=Ln4Clipboard.Decode(Ln4Clipboard.Encode(source.Properties))!;var before=LineTopologyMySqlTests.RawSnapshot(settings);
            LogicalNodeMySqlTests.Sql(settings,"CREATE TRIGGER TestClipboardFailure BEFORE INSERT ON LogicalNodePositions FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected LN4 paste position failure'");
            try{Assert.AreEqual("DatabaseError",(await client.CreateLogicalNodeAsync(250.5m,50.5m,4,properties)).Code);}
            finally{LogicalNodeMySqlTests.Sql(settings,"DROP TRIGGER TestClipboardFailure");}
            Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
            var retry=await client.CreateLogicalNodeAsync(250.5m,50.5m,4,properties);Assert.IsTrue(retry.Success,retry.Message);Assert.AreEqual("alice",retry.Data!.Owner);Assert.AreNotEqual(source.Code,retry.Data.Code);Assert.AreEqual(properties,retry.Data.Properties);Assert.AreEqual(250.5m,retry.Data.Positions.Single().X);
            Assert.AreEqual(LogicalNodeMySqlTests.Snapshot(repository),LogicalNodeMySqlTests.Snapshot(new MySqlLineRepository(settings)));
        });
}
