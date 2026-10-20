using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ArgumentMap.Client;
using ArgumentMap.Contracts;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfArgumentMap;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Rendering")]
public sealed class LineSpeedLabelTests
{
    public TestContext TestContext{get;set;}=null!;
    private static LineEditor Editor(CoordinateMapView view)=>(LineEditor)typeof(CoordinateMapView).GetField("editor",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(view)!;
    private static Rect[] Labels(CoordinateMapView view)=>((List<Rect>)typeof(CoordinateMapView).GetField("speedLabelBounds",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(view)!).ToArray();
    private static LineSnapshot Road(int dx=1,int dy=0,int speed=100,bool both=false)=>new("ABCDEFGHIJ","owner",false,1,
        [new("ABCDEFGHIJ",0,250000-dx*100,250000-dy*100),new("ABCDEFGHIJ",1,250000+dx*100,250000+dy*100)])
        {Properties=new(){Speed=speed,PassInOpposeDirection=(short)(both?1:0)}};
    private static CoordinateMapView View(LineSnapshot line)
    {
        var view=new CoordinateMapView();view.Measure(new Size(1000,600));view.Arrange(new Rect(0,0,1000,600));
        Editor(view).ReplaceLines([line]);view.UpdateLayout();view.ResetView();view.ZoomAtCenter(4);view.SetShowArrows(true);return view;
    }
    private static byte[] Pixels(CoordinateMapView view)
    {
        view.UpdateLayout();var bitmap=new RenderTargetBitmap((int)view.ActualWidth,(int)view.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(view);
        var pixels=new byte[bitmap.PixelWidth*bitmap.PixelHeight*4];bitmap.CopyPixels(pixels,bitmap.PixelWidth*4,0);return pixels;
    }
    [STATestMethod,DataRow(1,0),DataRow(1,1),DataRow(0,1),DataRow(-1,1),DataRow(-1,0),DataRow(-1,-1),DataRow(0,-1),DataRow(1,-1)]
    public void EightDirections_RenderSparseUprightLabels_AndHideRestoresExactPixels(int dx,int dy)
    {
        var view=View(Road(dx,dy));Assert.IsFalse(view.ShowLineSpeeds);var hidden=Pixels(view);Assert.AreEqual(0,Labels(view).Length);
        view.SetShowLineSpeeds(true);var shown=Pixels(view);var labels=Labels(view);
        if(dx==1&&dy==0)Save(view,"line-speeds-visible.png");
        Assert.IsTrue(labels.Length>0,$"No labels; speed={view.ShowLineSpeeds}, arrows={view.ShowArrows}, scale={view.Scale}, lines={view.LineCount}, size={view.ActualWidth}x{view.ActualHeight}");Assert.IsTrue(labels.Length<=4,$"Labels are too dense: {labels.Length}");
        var plot=new Rect(72,30,928,570);
        foreach(var label in labels)
        {
            Assert.IsTrue(plot.Contains(label));var changed=0;
            for(var y=(int)Math.Ceiling(label.Top);y<Math.Floor(label.Bottom);y++)for(var x=(int)Math.Ceiling(label.Left);x<Math.Floor(label.Right);x++)
                if(!shown.AsSpan((y*1000+x)*4,4).SequenceEqual(hidden.AsSpan((y*1000+x)*4,4)))changed++;
            Assert.IsTrue(changed>30,"Each label must actually draw visible speed text.");
        }
        for(var i=0;i<labels.Length;i++)for(var j=i+1;j<labels.Length;j++)Assert.IsFalse(labels[i].IntersectsWith(labels[j]));
        view.SetShowLineSpeeds(false);CollectionAssert.AreEqual(hidden,Pixels(view));Assert.AreEqual(0,Labels(view).Length);
        if(dx==1&&dy==0)Save(view,"line-speeds-hidden.png");
    }
    [STATestMethod]
    public void BidirectionalPairSharesLabels_CurrentSpeedChangesText_AndArrowSwitchHidesSpeeds()
    {
        var line=Road();var view=View(line);view.SetShowLineSpeeds(true);var before=Pixels(view);var bounds=Labels(view);
        Editor(view).ReplaceLines([line with{Properties=line.Properties with{PassInOpposeDirection=1}}]);view.SetShowLogicalNodeLabels(false);Pixels(view);
        CollectionAssert.AreEqual(bounds,Labels(view),"A paired arrow uses one shared speed label.");
        Editor(view).ReplaceLines([line with{Properties=line.Properties with{Speed=500}}]);view.SetShowLogicalNodeLabels(true);var after=Pixels(view);
        Assert.IsFalse(before.SequenceEqual(after),"Render the actual road speed, rather than a constant 100.");
        view.SetShowArrows(false);var noArrows=Pixels(view);Assert.AreEqual(0,Labels(view).Length);Assert.IsTrue(view.ShowLineSpeeds);
        view.SetShowLineSpeeds(false);CollectionAssert.AreEqual(noArrows,Pixels(view));
        view.SetShowLineSpeeds(true);Assert.IsTrue(view.ShowArrows);Pixels(view);Assert.IsTrue(Labels(view).Length>0);
    }
    [STATestMethod]
    public void SubdividedRoadsAndParallelRoads_DoNotCrowdLabels_AndOffscreenEndpointsStillShowSpeed()
    {
        var line=Road();var view=View(line);
        var nodes=Enumerable.Range(0,11).Select(i=>new LineNode(line.Code,i,249900+i*20,250000)).ToArray();
        var parallel=line with{Code="KLMNOPQRST",Nodes=nodes.Select(n=>n with{LineCode="KLMNOPQRST",Y=n.Y+2}).ToArray()};
        Editor(view).ReplaceLines([line with{Nodes=nodes},parallel]);view.SetShowLineSpeeds(true);Pixels(view);var labels=Labels(view);
        Assert.IsTrue(labels.Length>0);Assert.IsTrue(labels.Length<10,"F6 subdivisions must not produce a label at every segment.");
        for(var i=0;i<labels.Length;i++)for(var j=i+1;j<labels.Length;j++)Assert.IsFalse(labels[i].IntersectsWith(labels[j]));
        Editor(view).ReplaceLines([line with{Nodes=[new(line.Code,0,0,250000),new(line.Code,1,500000,250000)]}]);
        view.SetShowLineSpeeds(false);view.SetShowLineSpeeds(true);Pixels(view);Assert.IsTrue(Labels(view).Length>0);
    }
    [STATestMethod]
    public void ToolbarAfterLnLabel_AndViewMenu_DefaultOff_SynchronizeAndKeepPreferenceWithoutApiWrites()
    {
        using var stub=new DirectLogicalNodeHttpStub();LogicalNodeMySqlTests.Add(stub.Repository,249900,250000,250100,250000);
        using var http=new HttpClient(stub){BaseAddress=new Uri("http://localhost/")};
        var window=new MainWindow(new HttpLineClient(http,new("owner","test"))){WindowStartupLocation=WindowStartupLocation.Manual,Left=-10000,Top=-10000,ShowActivated=false,ShowInTaskbar=false};
        try
        {
            window.Show();var load=window.InitializeLinesAsync();LogicalNodeWindowEntryTests.DrainUntil(()=>load.IsCompleted);Assert.IsTrue(load.Result);
            var view=(CoordinateMapView)window.FindName("MapView");var toggle=(CheckBox)window.FindName("LineSpeedsToggle");var menu=(MenuItem)window.FindName("LineSpeedsMenu");var ln=(CheckBox)window.FindName("LogicalNodeLabelsToggle");
            Assert.AreSame(ln.Parent,toggle.Parent);var parent=(Panel)toggle.Parent;Assert.AreEqual(parent.Children.IndexOf(ln)+1,parent.Children.IndexOf(toggle));
            Assert.IsFalse(view.ShowLineSpeeds);Assert.AreEqual(false,toggle.IsChecked);Assert.IsFalse(menu.IsChecked);var before=LogicalNodeMySqlTests.Snapshot(stub.Repository);
            toggle.IsChecked=true;Assert.IsTrue(view.ShowLineSpeeds);Assert.IsTrue(menu.IsChecked);Assert.IsTrue(view.ShowArrows);
            view.ResetView();view.FitView();view.RefreshLinesAsync().GetAwaiter().GetResult();Assert.IsTrue(view.ShowLineSpeeds);
            menu.IsChecked=false;menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));Assert.IsFalse(view.ShowLineSpeeds);Assert.AreEqual(false,toggle.IsChecked);
            menu.IsChecked=true;menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));Assert.AreEqual(true,toggle.IsChecked);
            Assert.AreEqual(before,LogicalNodeMySqlTests.Snapshot(stub.Repository));Assert.AreEqual(0,stub.Creates+stub.AreaMoves+stub.AreaDeletes+stub.FlowImports+stub.CrossCalculations+stub.LogicalNodeCalculations);
        }
        finally{window.Close();}
    }
    [STATestMethod]
    public void ImportPreviewUsesSpeedSwitch_WithoutLosingPreview()
    {
        var view=View(Road());Editor(view).ReplaceLines([]);var snapshot=new MapAreaSnapshot([Road(speed:37)],[],[]);
        Assert.IsTrue(view.BeginFlowDiagramImport());view.ShowFlowDiagramPreview(snapshot);Pixels(view);Assert.AreEqual(0,Labels(view).Length);
        view.SetShowLineSpeeds(true);var shown=Pixels(view);Assert.IsTrue(Labels(view).Length>0);
        view.SetShowLineSpeeds(false);Assert.IsFalse(shown.SequenceEqual(Pixels(view)));Assert.IsTrue(view.IsFlowDiagramImportActive);
    }
    private void Save(CoordinateMapView view,string name)
    {
        var bitmap=new RenderTargetBitmap((int)view.ActualWidth,(int)view.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(view);
        var path=Path.Combine(ProjectPaths.Root,".artifacts","renders",name);Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(path))encoder.Save(file);TestContext.AddResultFile(path);
    }
}
