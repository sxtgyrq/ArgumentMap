using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ArgumentMap.Business;
using ArgumentMap.Client;
using ArgumentMap.Contracts;
using ArgumentMap.Rules;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfArgumentMap;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class LogicalNodeRelationTests
{
    public TestContext TestContext {get;set;}=null!;
    internal static LogicalNodeSnapshot Add(ILineRepository repository,decimal x=100.5m,decimal y=100.5m,string value="=",string logic="")
    {var result=new LogicalNodeService(repository).Create(new("owner",x,y,8,new(){Value=value,LogicLanguage=logic,Info="关系说明"}));Assert.IsTrue(result.Success,result.Message);return result.Data!;}

    [TestMethod]
    public void AllSevenSymbolsRepeatWithoutGlobalCountLimit_LogicAlwaysMatchesValue_AndDefaultIsEquality()
    {
        var map=new TestMap();var service=new LogicalNodeService(map.Repository);
        for(var i=0;i<350;i++)Add(map.Repository,20.5m+(i%30)*20,20.5m+(i/30)*20,new[]{"=","→","↔","⊇","⊋","⊆","⊊"}[i%7],"P");
        var all=service.GetAll().Data!;Assert.AreEqual(350,all.Length);foreach(var value in new[]{"=","→","↔","⊇","⊋","⊆","⊊"})Assert.AreEqual(50,all.Count(n=>n.Properties.Value==value));Assert.IsTrue(all.All(n=>n.Properties.LogicLanguage==n.Properties.Value));
        var created=service.Create(new("owner",800.5m,800.5m,8,new()));Assert.IsTrue(created.Success,created.Message);Assert.AreEqual("=",created.Data!.Properties.Value);Assert.AreEqual("=",created.Data.Properties.LogicLanguage);
    }

    [TestMethod,DataRow(""),DataRow("等同"),DataRow("蕴含"),DataRow("True"),DataRow("False"),DataRow("=="),DataRow("->"),DataRow(" = "),DataRow("⊃"),DataRow("⊂"),DataRow("∈"),DataRow("⊆ "),DataRow("包含"),DataRow("<->"),DataRow("⇔"),DataRow("↔ ")]
    public void CreateUpdateAndDalRejectAnyOtherValue_WithoutPartialChanges(string value)
    {
        var map=new TestMap();var node=Add(map.Repository);var service=new LogicalNodeService(map.Repository);var before=map.Snapshot();
        Assert.AreEqual("InvalidRelationValue",service.Create(new("owner",200.5m,100.5m,8,new(){Value=value})).Code);Assert.AreEqual(before,map.Snapshot());
        Assert.AreEqual("InvalidRelationValue",service.Update(new(node.Code,"owner",node.Version,value,"changed",LogicLanguage:"new")).Code);Assert.AreEqual(before,map.Snapshot());
        Assert.ThrowsExactly<InvalidLogicalNodeRelationException>(()=>map.Repository.Transaction(s=>{s.LogicalNodes[node.Code]=node with{Properties=node.Properties with{Value=value}};return true;}));Assert.AreEqual(before,map.Snapshot());
    }

    [TestMethod]
    public void EditMoveAndAreaMoveKeepRelationsIndependentOfLn4AndLn6Rules()
    {
        var map=new TestMap();map.Add(20,50,80,50);var service=new LogicalNodeService(map.Repository);var relation=Add(map.Repository,50.5m,50.5m);
        var edit=service.Update(new(relation.Code,"owner",relation.Version,"→","说明",LogicLanguage:"P → Q"));Assert.IsTrue(edit.Success,edit.Message);relation=edit.Data!;Assert.AreEqual(1,relation.Positions.Length);
        Assert.AreEqual("NotOwner",service.Update(new(relation.Code,"other",relation.Version,"=","",LogicLanguage:"")).Code);
        var moved=service.Move(new(relation.Code,"owner",relation.Version,60.5m,50.5m));Assert.IsTrue(moved.Success,moved.Message);Assert.AreEqual(relation.Properties,moved.Data!.Properties);
        Assert.AreEqual("VersionConflict",service.Update(new(relation.Code,"owner",relation.Version,"=","",LogicLanguage:"")).Code);
        map.Calculate();var selection=MapAreaMoveTests.Select(map);var shifted=MapAreaMoveTests.Move(map,selection);Assert.IsTrue(shifted.Success,shifted.Message);Assert.AreEqual(relation.Properties,shifted.Data!.LogicalNodes.Single().Properties);Assert.AreEqual(8,shifted.Data.LogicalNodes.Single().SideLength);
        var source=service.Create(new("owner",300.5m,100.5m,6,new(){Value="判断",LogicLanguage="P"})).Data!;var branch=service.Create(new("owner",330.5m,100.5m,4,new(){Value="False",LogicLanguage="¬P"})).Data!;var independent=Add(map.Repository,360.5m,100.5m,"→","P");
        Assert.IsTrue(service.Update(new(source.Code,"owner",source.Version,"判断","",LogicLanguage:"Q")).Success);var all=service.GetAll().Data!;Assert.AreEqual("¬Q",all.Single(n=>n.Code==branch.Code).Properties.LogicLanguage);Assert.AreEqual("→",all.Single(n=>n.Code==independent.Code).Properties.LogicLanguage);
    }

    [TestMethod,DataRow("touch"),DataRow("bounds"),DataRow("road-node"),DataRow("diagonal"),DataRow("cross")]
    public void ChamferDoesNotRelaxExistingSquareGeometryAndCrossRestrictions(string kind)
    {
        var map=new TestMap();decimal x=100.5m,y=100.5m;
        if(kind=="touch")Add(map.Repository,108.5m,100.5m);
        if(kind=="bounds")x=3.5m;
        if(kind=="road-node")map.Add(100,100,120,100);
        if(kind=="diagonal")map.Add(90,90,110,110);
        if(kind=="cross"){map.Add(90,100,110,100);map.Add(100,90,100,110);map.Calculate();}
        var before=map.Snapshot();var result=new LogicalNodeService(map.Repository).Create(new("owner",x,y,8,new()));Assert.IsFalse(result.Success);Assert.AreEqual(before,map.Snapshot());
    }

    [TestMethod,DataRow("="),DataRow("→"),DataRow("⊇"),DataRow("⊋"),DataRow("⊆"),DataRow("⊊"),DataRow("↔")]
    public void ImportPreviewAndCommitFillMatchingLogic_AndRejectInvalidWholeBatch(string value)
    {
        var map=new TestMap();var service=new FlowDiagramService(map.Repository);
        var document=new JsonObject{["format"]="argumentmap.flow/1",["origin"]="relation",["ln"]=new JsonArray(new JsonObject{["id"]="relation",["at"]=new JsonArray(.5m,.5m),["size"]=8,["value"]=value}),["roads"]=new JsonArray(),["crosses"]=new JsonArray()};
        var request=new FlowDiagramRequest("owner",document.ToJsonString(),100.5m,100.5m);var before=map.Snapshot();var preview=service.Preview(request);Assert.IsTrue(preview.Success,preview.Message);Assert.AreEqual(before,map.Snapshot());
        Assert.AreEqual(value,preview.Data!.Added.LogicalNodes.Single().Properties.LogicLanguage);Assert.IsTrue(service.Import(request).Success);Assert.IsTrue(service.Import(request with{X=150.5m}).Success);Assert.AreEqual(2,new LogicalNodeService(map.Repository).GetAll().Data!.Length);
        before=map.Snapshot();document["ln"]![0]!["value"]="蕴含";Assert.IsFalse(service.Import(request with{Text=document.ToJsonString(),X=200.5m}).Success);Assert.AreEqual(before,map.Snapshot());
        document["ln"]![0]!["value"]=value;document["ln"]![0]!["logic"]=null;Assert.IsFalse(FlowDiagramLanguage.Parse(document.ToJsonString()).Success);
    }

    [STATestMethod,DataRow("⊊"),DataRow("↔")]
    public void F5ContinuousCreation_DefaultEquality_AndEditHasExactlySevenOptionsWithoutEndpointSelectors(string chosenValue)
    {
        var chooser=new LogicalNodeSizeWindow(false);int selected=0;chooser.SizeSelected+=size=>selected=size;
        ((Button)chooser.FindName("Size8Button")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert.AreEqual(8,selected);
        using var handler=new DirectLogicalNodeHttpStub();using var http=new HttpClient(handler){BaseAddress=new Uri("http://127.0.0.1:1/")};var client=new HttpLineClient(http,new("owner","test"));var view=new CoordinateMapView();view.ConfigureClient(client);
        Assert.IsTrue(view.BeginLogicalNodePlacement(8));Assert.IsTrue(view.PlaceLogicalNodeAsync(new(100,100)).GetAwaiter().GetResult());Assert.IsTrue(view.IsPlacingLogicalNode);Assert.IsTrue(view.PlaceLogicalNodeAsync(new(120,100)).GetAwaiter().GetResult());Assert.AreEqual(2,handler.Creates);
        var node=new LogicalNodeService(handler.Repository).GetAll().Data!.First();var window=new LogicalNodeWindow(client,"owner",new(),node);
        try
        {
            var values=(ComboBox)window.FindName("RelationValueInput");Assert.AreEqual(Visibility.Visible,values.Visibility);CollectionAssert.AreEqual(new[]{"=","→","↔","⊇","⊋","⊆","⊊"},values.Items.OfType<ComboBoxItem>().Select(i=>(string)i.Tag).ToArray());
            Assert.AreEqual(Visibility.Collapsed,((TextBox)window.FindName("ValueInput")).Visibility);Assert.AreEqual(Visibility.Collapsed,((ComboBox)window.FindName("BranchValueInput")).Visibility);Assert.IsFalse(((StackPanel)window.FindName("GeometryPanel")).IsEnabled);
            var logic=(TextBox)window.FindName("LogicInput");Assert.IsTrue(logic.IsReadOnly);Assert.AreEqual("=",logic.Text);
            foreach(var value in new[]{"=","→","↔","⊇","⊋","⊆","⊊"}){values.SelectedValue=value;Assert.AreEqual(value,logic.Text);}values.SelectedValue=chosenValue;((Button)window.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var saved=new LogicalNodeService(handler.Repository).GetAll().Data!.Single(n=>n.Code==node.Code);Assert.AreEqual(chosenValue,saved.Properties.Value);Assert.AreEqual(chosenValue,saved.Properties.LogicLanguage);
        }
        finally{window.Close();}
    }

    [TestMethod,DataRow(1d),DataRow(16d),DataRow(32d)]
    public void ChamferHasExactlyEightStraightVertices_AndHalfUnitLegsAtEveryScale(double scale)
    {
        var points=CoordinateMapView.ChamferPoints(new Rect(0,0,8*scale,8*scale),.5*scale);Assert.AreEqual(8,points.Length);
        CollectionAssert.AreEqual(new[]{new Point(.5*scale,0),new Point(7.5*scale,0),new Point(8*scale,.5*scale),new Point(8*scale,7.5*scale),new Point(7.5*scale,8*scale),new Point(.5*scale,8*scale),new Point(0,7.5*scale),new Point(0,.5*scale)},points);
    }

    [STATestMethod,TestCategory("Rendering")]
    public void Ln8CyanChamfersAndLn6PurpleRoundCorners_RenderDistinctly_InMapAndImportPreview()
    {
        var view=new CoordinateMapView();view.Measure(new Size(1100,700));view.Arrange(new Rect(0,0,1100,700));view.UpdateLayout();view.ResetView();view.ZoomAtCenter(12);view.UpdateLayout();view.SetShowLogicalNodeLabels(true);
        var six=new LogicalNodeSnapshot("AAAAAAAAAA","owner",0,249980.5m,250000.5m,6,new(){Value="LN6 判断",LogicLanguage="P"});
        var equal=new LogicalNodeSnapshot("BBBBBBBBBB","owner",0,250000.5m,250000.5m,8,new(){Value="=",LogicLanguage="="});var implication=equal with{Code="CCCCCCCCCC",X=250020.5m,Properties=new(){Value="→",LogicLanguage="→"}};
        var editor=(LogicalNodeEditor)typeof(CoordinateMapView).GetField("logicalNodes",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(view)!;editor.ReplaceNodes([six,equal,implication]);view.InvalidateVisual();view.UpdateLayout();
        foreach(var n in new[]{six,equal,implication}){var at=(Point)typeof(CoordinateMapView).GetMethod("ToScreen",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(view,[new Point((double)n.X,(double)n.Y)])!;Assert.IsTrue(new Rect(view.RenderSize).Contains(at),"The rendered nodes must be visible.");}
        Save(view,"ln8-map.png");Assert.IsTrue(view.BeginFlowDiagramImport());view.ShowFlowDiagramPreview(new([], [equal with{Y=249980.5m},implication with{Y=249980.5m}],[]));view.UpdateLayout();Save(view,"ln8-import-preview.png");
        void Save(FrameworkElement element,string file)
        {
            var bitmap=new RenderTargetBitmap(1100,700,96,96,PixelFormats.Pbgra32);bitmap.Render(element);var path=Path.Combine(ProjectPaths.Root,".artifacts","renders",file);Directory.CreateDirectory(Path.GetDirectoryName(path)!);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create(path))png.Save(stream);TestContext.AddResultFile(path);
        }
    }
}
