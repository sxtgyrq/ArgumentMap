using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ArgumentMap.Business;
using ArgumentMap.Client;
using ArgumentMap.Contracts;
using ArgumentMap.Storage;
using ArgumentMap.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfArgumentMap;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class LogicLanguageUiTests
{
    public TestContext TestContext{get;set;}=null!;
    [STATestMethod]
    public void EditorSavesUnicodeFormula_AndMoveButtonProtectsUnsavedLogic()
    {
        using var handler=new DirectLogicalNodeHttpStub();using var http=new HttpClient(handler){BaseAddress=new Uri("http://127.0.0.1:1/")};
        var service=new LogicalNodeService(handler.Repository);var node=service.Create(new("owner",110.5m,110.5m,12,new(){LogicLanguage="S"})).Data!;
        var window=new LogicalNodeWindow(new HttpLineClient(http,new("owner","test")),"owner",new(),node);
        try
        {
            var logic=(TextBox)window.FindName("LogicInput");Assert.AreEqual("S",logic.Text);Assert.AreEqual(255,logic.MaxLength);
            logic.Text="(S ∧ ¬P) → A";var moves=0;window.MoveRequested+=(_,_)=>moves++;
            ((Button)window.FindName("MoveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert.AreEqual(0,moves);
            ((Button)window.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            LogicalNodeWindowEntryTests.DrainUntil(()=>service.GetAll().Data!.Single().Version>node.Version);
            var saved=service.GetAll().Data!.Single();Assert.AreEqual("(S ∧ ¬P) → A",saved.Properties.LogicLanguage);Assert.AreEqual(node.X,saved.X);Assert.AreEqual(node.SideLength,saved.SideLength);
        }
        finally{window.Close();}
    }
    [STATestMethod,TestCategory("Rendering")]
    public void DiagramMenuFocusesSavedSquaresAndDisplaysValuesFormulasArrowsAndBranchLabels()
    {
        using var handler=new DirectLogicalNodeHttpStub();var import=new EquipmentDiagramData(handler.Repository).Import("owner",250002,249920);Assert.IsTrue(import.Success,import.Message);
        using var http=new HttpClient(handler){BaseAddress=new Uri("http://127.0.0.1:1/")};
        var window=new MainWindow(new HttpLineClient(http,new("owner","test"))){Width=1400,Height=980,WindowStartupLocation=WindowStartupLocation.Manual,Left=-10000,Top=-10000,ShowActivated=false,ShowInTaskbar=false};
        try
        {
            window.Show();window.SetToolPanelExpanded(false);window.UpdateLayout();var load=window.InitializeLinesAsync();LogicalNodeWindowEntryTests.DrainUntil(()=>load.IsCompleted);Assert.IsTrue(load.Result);
            var view=(CoordinateMapView)window.FindName("MapView");Assert.IsTrue(view.FocusLogicalNodes(import.Data!.Select(n=>n.Code)));window.UpdateLayout();Assert.IsTrue(view.Scale>1);Assert.IsTrue(view.Center.Y<250001);
            Assert.IsNull(window.FindName("ReasoningDiagramMenu"));
            Assert.AreEqual(10,view.LogicalNodeCount);Assert.AreEqual(3,view.LineCount);Assert.AreEqual(2,view.CrossCount);
            var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);
            var path=Path.Combine(ProjectPaths.Root,".artifacts","renders","equipment-diagnosis-diagram.png");Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(path))encoder.Save(file);TestContext.AddResultFile(path);
        }
        finally{window.Close();}
    }
}
