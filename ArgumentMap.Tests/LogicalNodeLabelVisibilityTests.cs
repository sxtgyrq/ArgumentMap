using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ArgumentMap.Business;
using ArgumentMap.Client;
using ArgumentMap.Contracts;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfArgumentMap;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class LogicalNodeLabelVisibilityTests
{
    public TestContext TestContext{get;set;}=null!;
    private static Point Screen(CoordinateMapView view,Point world)=>(Point)typeof(CoordinateMapView).GetMethod("ToScreen",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(view,[world])!;
    private static (FormattedText Text,Point At)? Label(CoordinateMapView view,LogicalNodeSnapshot node)=>
        ((FormattedText Text,Point At)?)typeof(CoordinateMapView).GetMethod("LogicalNodeLabel",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(view,[node]);
    private static byte[] Pixels(CoordinateMapView view)
    {
        view.UpdateLayout();var bitmap=new RenderTargetBitmap((int)view.ActualWidth,(int)view.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(view);
        var pixels=new byte[bitmap.PixelWidth*bitmap.PixelHeight*4];bitmap.CopyPixels(pixels,bitmap.PixelWidth*4,0);return pixels;
    }
    private static (CoordinateMapView View,LogicalNodeSnapshot Node) Fixture(int size)
    {
        var view=new CoordinateMapView();view.Measure(new Size(1000,600));view.Arrange(new Rect(0,0,1000,600));
        var line=new LineSnapshot("ABCDEFGHIJ","owner",false,1,[new("ABCDEFGHIJ",0,249960,249999),new("ABCDEFGHIJ",1,250060,249999)]);
        var editor=(LineEditor)typeof(CoordinateMapView).GetField("editor",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(view)!;
        editor.ReplaceLines([line]);
        var node=new LogicalNodeSnapshot("KLMNOPQRST","owner",0,250000.5m,250000.5m,size,new(){Value=size==18?"开始":"否",LogicLanguage=size==18?"START":"¬P"});
        var nodes=(LogicalNodeEditor)typeof(CoordinateMapView).GetField("logicalNodes",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(view)!;nodes.ReplaceNodes([node]);
        view.UpdateLayout();view.ResetView();view.ZoomAtCenter(8);view.UpdateLayout();return(view,node);
    }

    [STATestMethod,TestCategory("Rendering"),DataRow(4),DataRow(18)]
    public void LabelBackgroundIs70PercentTransparent_RoadShowsThrough_AndHideRestoreIsExact(int size)
    {
        var (view,node)=Fixture(size);var label=Label(view,node)!.Value;view.SetShowLogicalNodeLabels(false);var hidden=Pixels(view);
        view.SetShowLogicalNodeLabels(true);var shown=Pixels(view);
        var road=Screen(view,new(250000,249999));var x=(int)Math.Floor(label.At.X-1);var y=(int)Math.Floor(road.Y);var index=(y*(int)view.ActualWidth+x)*4;
        Assert.IsTrue(hidden[index]>hidden[index+2]+60,"The sample must be on the blue road, in label padding away from text.");
        var background=new[]{252,249,247};
        for(var channel=0;channel<3;channel++)
        {
            var expected=.7*hidden[index+channel]+.3*background[channel];
            Assert.AreEqual(expected,(double)shown[index+channel],2.0,"The label background must retain 70% of the underlying road color.");
        }
        Assert.IsTrue(shown[index+2]<180,"An opaque pale label would hide the blue road.");
        Assert.IsFalse(hidden.SequenceEqual(shown),"Visible labels must still render text.");
        if(size==4){Save(view,"ln-labels-visible.png");view.SetShowLogicalNodeLabels(false);Save(view,"ln-labels-hidden.png");}
        view.SetShowLogicalNodeLabels(false);CollectionAssert.AreEqual(hidden,Pixels(view));view.SetShowLogicalNodeLabels(true);CollectionAssert.AreEqual(shown,Pixels(view));
    }

    [STATestMethod]
    public void HiddenLabelsAreNotInvisibleHitTargets_SquareSelectionStillWorks()
    {
        var (view,node)=Fixture(4);node=node with{Properties=new(){Value="False",LogicLanguage="¬READY_TO_SEND(s,m)"}};
        ((LogicalNodeEditor)typeof(CoordinateMapView).GetField("logicalNodes",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(view)!).ReplaceNodes([node]);
        var label=Label(view,node)!.Value;var target=new Point(label.At.X+label.Text.MaxTextWidth/2,label.At.Y+label.Text.Height-1);
        var square=new Rect(Screen(view,new((double)node.Left,(double)node.Top)),Screen(view,new((double)node.Right,(double)node.Bottom)));
        Assert.IsFalse(square.Contains(target),"The wrapped label must extend vertically beyond the square for this test.");
        Assert.IsTrue(view.SelectLogicalNodeAt(target));view.SetShowLogicalNodeLabels(false);Assert.IsNull(Label(view,node));Assert.IsFalse(view.SelectLogicalNodeAt(target));
        Assert.IsTrue(view.SelectLogicalNodeAt(Screen(view,new((double)node.X,(double)node.Y))));
        view.SetShowLogicalNodeLabels(true);Assert.IsTrue(view.SelectLogicalNodeAt(target));
    }

    [STATestMethod]
    public void ImportPreviewLabelsUseSameSwitch_AndTogglingKeepsPreview()
    {
        var (view,node)=Fixture(4);var ghost=node with{Code="UVWXYZABCD",X=node.X+20,Properties=new(){Value="预览文字",LogicLanguage="¬Q"}};
        var snapshot=new MapAreaSnapshot([],[ghost],[]);Assert.IsTrue(view.BeginFlowDiagramImport());view.ShowFlowDiagramPreview(snapshot);
        var label=Label(view,ghost)!.Value;var shown=Pixels(view);view.SetShowLogicalNodeLabels(false);var hidden=Pixels(view);var changes=0;
        for(var y=(int)label.At.Y;y<(int)(label.At.Y+label.Text.Height);y++)for(var x=(int)label.At.X;x<(int)(label.At.X+label.Text.MaxTextWidth);x++)
            if(!shown.AsSpan((y*(int)view.ActualWidth+x)*4,4).SequenceEqual(hidden.AsSpan((y*(int)view.ActualWidth+x)*4,4)))changes++;
        Assert.IsTrue(changes>20,"Preview values/formulas should disappear too.");Assert.IsTrue(view.IsFlowDiagramImportActive);
        Assert.AreSame(snapshot,typeof(CoordinateMapView).GetField("flowPreview",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(view));
        view.SetShowLogicalNodeLabels(true);CollectionAssert.AreEqual(shown,Pixels(view));
    }

    [STATestMethod,DataRow(4),DataRow(6),DataRow(8),DataRow(10),DataRow(12),DataRow(18)]
    public void LabelIsHorizontallyCentered_WithHalfEmTopMargin_AndLongLogicWrapsWithoutFourLineTruncation(int size)
    {
        var (view,node)=Fixture(size);node=node with{Properties=new(){Value="发送消息",LogicLanguage="respondToMessage(s,m) ∧ readyToContinue(s,m)"}};
        foreach(var factor in new[]{1d,2d,.5d})
        {
            view.ZoomAtCenter(factor);var label=Label(view,node)!.Value;var center=Screen(view,new((double)node.X,(double)node.Y));
            Assert.AreEqual(center.X,label.At.X+label.Text.MaxTextWidth/2,1e-6);Assert.AreEqual(5.5,label.At.Y-center.Y,1e-6,"At 11 DIP font size, 0.5em is 5.5 DIP regardless of map zoom.");
            Assert.AreEqual(TextAlignment.Center,label.Text.TextAlignment);Assert.IsTrue(label.Text.MaxTextWidth<=size*view.Scale);
            Assert.AreEqual(TextTrimming.None,label.Text.Trimming);
            Assert.IsTrue(label.Text.Width<=size*view.Scale,"Unbroken logic identifiers must wrap inside the square width too.");
            Assert.AreEqual("发送消息\nrespondToMessage(s,m) ∧ readyToContinue(s,m)",label.Text.Text);Assert.IsTrue(label.Text.MaxLineCount>4);
        }
        var longLabel=Label(view,node)!.Value;var shortLabel=Label(view,node with{Properties=new(){Value="A",LogicLanguage="P"}})!.Value;
        Assert.IsTrue(longLabel.Text.Height>shortLabel.Text.Height,"Long words and Chinese must wrap to additional lines.");
    }

    [STATestMethod,TestCategory("Rendering")]
    public void AllSizesRenderCenteredWrappedLabels_AndImportUsesSameLayout()
    {
        var view=new CoordinateMapView();view.Measure(new Size(1250,750));view.Arrange(new Rect(0,0,1250,750));view.UpdateLayout();view.ResetView();view.ZoomAtCenter(12);view.UpdateLayout();
        var sizes=new[]{4,6,8,10,12,18};var values=new[]{"False","响应消息","⊊","human","HUMAN","开始"};
        var logic=new[]{"¬readyToRespond(s,m)","respondToMessage(s,m)","⊊","human ∈ HUMAN","HUMAN","START"};
        var nodes=sizes.Select((size,i)=>new LogicalNodeSnapshot(new string((char)('A'+i),10),"owner",0,249968.5m+(i%3)*32,250014.5m-(i/3)*28,size,new(){Value=values[i],LogicLanguage=logic[i]})).ToArray();
        foreach(var node in nodes)Assert.IsTrue(new Rect(view.RenderSize).Contains(Screen(view,new((double)node.X,(double)node.Y))),"All rendered test nodes must be on screen.");
        var editor=(LogicalNodeEditor)typeof(CoordinateMapView).GetField("logicalNodes",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(view)!;editor.ReplaceNodes(nodes);
        var lines=new[]{new LineSnapshot("ROADAAAAAA","owner",false,0,[new("ROADAAAAAA",0,249950,250014),new("ROADAAAAAA",1,250050,250014)]),new LineSnapshot("ROADBBBBBB","owner",false,0,[new("ROADBBBBBB",0,249950,249986),new("ROADBBBBBB",1,250050,249986)])};
        ((LineEditor)typeof(CoordinateMapView).GetField("editor",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(view)!).ReplaceLines(lines);
        Save(view,"ln-centered-wrapped-labels.png");editor.ReplaceNodes([]);Assert.IsTrue(view.BeginFlowDiagramImport());view.ShowFlowDiagramPreview(new(lines,nodes,[]));
        Save(view,"ln-centered-wrapped-import.png");
    }

    [STATestMethod]
    public void MenuAndToolbarStaySynchronized_DefaultVisible_RefreshAndNavigationKeepPreference_NoDataChanges()
    {
        using var handler=new DirectLogicalNodeHttpStub();LogicalNodeMySqlTests.Add(handler.Repository,249960,250000,250060,250000);
        Assert.IsTrue(new LogicalNodeService(handler.Repository).Create(new("owner",250000.5m,250000.5m,4,new(){Value="False",LogicLanguage=""})).Success);
        using var http=new HttpClient(handler){BaseAddress=new Uri("http://localhost/")};var window=new MainWindow(new HttpLineClient(http,new("owner","test"))){WindowStartupLocation=WindowStartupLocation.Manual,Left=-10000,Top=-10000,ShowActivated=false,ShowInTaskbar=false};
        try
        {
            window.Show();var load=window.InitializeLinesAsync();LogicalNodeWindowEntryTests.DrainUntil(()=>load.IsCompleted);Assert.IsTrue(load.Result);
            var view=(CoordinateMapView)window.FindName("MapView");var menu=(MenuItem)window.FindName("LogicalNodeLabelsMenu");var toggle=(CheckBox)window.FindName("LogicalNodeLabelsToggle");
            Assert.IsTrue(view.ShowLogicalNodeLabels);Assert.IsTrue(menu.IsChecked);Assert.AreEqual(true,toggle.IsChecked);var before=LogicalNodeMySqlTests.Snapshot(handler.Repository);
            toggle.IsChecked=false;Assert.IsFalse(view.ShowLogicalNodeLabels);Assert.IsFalse(menu.IsChecked);
            view.ResetView();view.FitView();view.RefreshLogicalNodesAsync().GetAwaiter().GetResult();view.RefreshLinesAsync().GetAwaiter().GetResult();view.FocusLogicalNodes();
            Assert.IsFalse(view.ShowLogicalNodeLabels);Assert.IsFalse(menu.IsChecked);Assert.AreEqual(false,toggle.IsChecked);
            menu.IsChecked=true;menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));Assert.IsTrue(view.ShowLogicalNodeLabels);Assert.AreEqual(true,toggle.IsChecked);
            view.SetShowLogicalNodeLabels(false);Assert.IsFalse(menu.IsChecked);Assert.AreEqual(false,toggle.IsChecked);
            Assert.AreEqual(before,LogicalNodeMySqlTests.Snapshot(handler.Repository));Assert.AreEqual(0,handler.Creates+handler.AreaMoves+handler.AreaDeletes+handler.FlowImports+handler.CrossCalculations+handler.LogicalNodeCalculations);
        }
        finally{window.Close();}
    }

    private void Save(CoordinateMapView view,string name)
    {
        view.InvalidateVisual();view.UpdateLayout();var bitmap=new RenderTargetBitmap((int)view.ActualWidth,(int)view.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(view);
        var path=Path.Combine(ProjectPaths.Root,".artifacts","renders",name);Directory.CreateDirectory(Path.GetDirectoryName(path)!);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(path))encoder.Save(file);TestContext.AddResultFile(path);
    }
}
