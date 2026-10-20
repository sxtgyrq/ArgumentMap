using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using ArgumentMap.Business;
using ArgumentMap.Contracts;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfArgumentMap;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class LogicalNodeInstanceUiTests
{
    [STATestMethod]
    public void SizePickerChoosesParent_ContinuousPlacement_EditingDerivesLogic_ClipboardChecksLatestParent()
    {
        using var handler=new DirectLogicalNodeHttpStub();var parent=LogicalNodeInstanceTests.Parent(handler.Repository);
        using var http=new HttpClient(handler){BaseAddress=new Uri("http://localhost/")};
        var client=new ArgumentMap.Client.HttpLineClient(http,new("owner","test"));var main=new MainWindow(client){Left=-10000,Top=-10000,ShowInTaskbar=false,ShowActivated=false};
        try
        {
            main.Show();main.UpdateLayout();var view=(CoordinateMapView)main.FindName("MapView");view.RefreshLogicalNodesAsync().GetAwaiter().GetResult();
            var picker=new LogicalNodeSizeWindow(false,view.InstanceParents){Owner=main};
            try
            {
                picker.Show();LogicalNodeSnapshot? selected=null;picker.InstanceSelected+=p=>selected=p;
                ((Button)picker.FindName("Size10Button")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.AreEqual(parent.Code,((ComboBox)picker.FindName("InstanceParentInput")).SelectedItem is LogicalNodeSnapshot p?p.Code:"");
                ((Button)picker.FindName("ConfirmInstanceButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.IsNotNull(selected);Assert.IsTrue(view.BeginInstancePlacement(selected));
            }
            finally{picker.Close();}
            Assert.IsTrue(view.PlaceLogicalNodeAsync(new(90,50)).GetAwaiter().GetResult());Assert.IsTrue(view.IsPlacingLogicalNode);
            Assert.IsTrue(view.PlaceLogicalNodeAsync(new(130,50)).GetAwaiter().GetResult());
            var children=new LogicalNodeService(handler.Repository).GetAll().Data!.Where(n=>n.SideLength==10).ToArray();Assert.AreEqual(2,children.Length);
            Assert.IsTrue(children.All(n=>n.Properties.Value=="people"&&n.Properties.LogicLanguage=="people ∈ PEOPLE"));
            view.BeginLogicalNodePlacement(4); // Switching size discards the instance placement template.
            Assert.IsTrue(view.PlaceLogicalNodeAsync(new(170,50)).GetAwaiter().GetResult());
            Assert.AreEqual("False",new LogicalNodeService(handler.Repository).GetAll().Data!.Single(n=>n.SideLength==4).Properties.Value);
            var editor=new LogicalNodeWindow(client,"owner",new(),children[0]);
            try
            {
                Assert.IsTrue(((TextBox)editor.FindName("LogicInput")).IsReadOnly);
                ((TextBox)editor.FindName("ValueInput")).Text="PEOPLE";
                Assert.AreEqual("people ∈ PEOPLE",((TextBox)editor.FindName("LogicInput")).Text);
            }
            finally{editor.Close();}
            view.CancelEditing();Assert.IsTrue(view.BeginPasteLogicalNode(LogicalNodeClipboard.Encode(10,children[0].Properties)));
            var service=new LogicalNodeService(handler.Repository);
            Assert.IsTrue(service.Update(new(parent.Code,"owner",parent.Version,"OTHERS","",LogicLanguage:parent.Properties.LogicLanguage)).Success);
            var before=handler.Repository.Transaction(s=>s.LogicalNodes.Count);
            Assert.IsFalse(view.PlaceLogicalNodeAsync(new(210,50)).GetAwaiter().GetResult());
            StringAssert.Contains(view.OperationMessage,"LN12");Assert.IsTrue(view.IsPlacingLogicalNode);
            Assert.AreEqual(before,handler.Repository.Transaction(s=>s.LogicalNodes.Count));
        }
        finally{main.Close();}
    }
    [STATestMethod]
    public void ConcaveOutlineCutsQuarterCirclesIntoEveryCorner_AndStaysWithinSquare()
    {
        var shape=CoordinateMapView.InstanceOutline(new Rect(0,0,100,100),10);
        Assert.IsTrue(shape.FillContains(new Point(50,50)));
        foreach(var p in new[]{new Point(1,1),new Point(99,1),new Point(1,99),new Point(99,99)})Assert.IsFalse(shape.FillContains(p));
        // A convex rounded rectangle would include (3,3); the inward notch removes it.
        Assert.IsFalse(shape.FillContains(new Point(3,3)));
        Assert.IsTrue(shape.FillContains(new Point(8,8)));
        Assert.IsFalse(shape.FillContains(new Point(-1,50)));Assert.AreEqual(new Rect(0,0,100,100),shape.Bounds);
        var visual=new DrawingVisual();using(var dc=visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White,null,new Rect(0,0,300,150));
            dc.PushTransform(new TranslateTransform(25,25));dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(30,217,119,6)),new Pen(Brushes.DarkGoldenrod,2),shape);dc.Pop();
            dc.PushTransform(new TranslateTransform(180,25));dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(30,217,119,6)),new Pen(Brushes.DarkGoldenrod,2),CoordinateMapView.InstanceOutline(new Rect(0,0,100,100),5));dc.Pop();
        }
        var bitmap=new RenderTargetBitmap(300,150,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);
        var path=Path.Combine(ProjectPaths.Root,".artifacts","renders","ln10-concave.png");Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(path);encoder.Save(stream);
    }
    [TestMethod]
    public void ClipboardRejectsInvalidInstanceContent_AndAllowsDuplicateValidContent()
    {
        var p=new LogicalNodeProperties{Value="people",LogicLanguage="people ∈ PEOPLE",Info="实例"};
        Assert.AreEqual(p,LogicalNodeClipboard.Decode(LogicalNodeClipboard.Encode(10,p))!.Properties);
        foreach(var bad in new[]{p with{Value="PEOPLE"},p with{LogicLanguage="∀people(people∈PEOPLE)"},p with{Value=""}})
            Assert.IsFalse(LogicalNodeClipboard.Valid(10,bad));
    }
}
