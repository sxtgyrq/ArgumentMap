using System.IO;
using System.Net.Http;
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

[TestClass,TestCategory("Unit")]
public sealed class LogicalNodeOperandUiTests
{
    public TestContext TestContext{get;set;}=null!;
    [STATestMethod,DataRow("Class","SENDMSG(S,M)"),DataRow("Instance","sendmsg(s,m)"),DataRow("Custom","SendMsg(s,m)")]
    public void EditorShowsOnlyLn6Mode_NormalizesDraftAndSavesKind(string kind,string expected)
    {
        using var handler=new DirectLogicalNodeHttpStub();using var http=new HttpClient(handler){BaseAddress=new("http://localhost/")};var client=new HttpLineClient(http,new("owner","test"));
        var window=new LogicalNodeWindow(client,"owner",new(50.5,50.5),sideLength:6){Left=-10000,Top=-10000,ShowActivated=false,ShowInTaskbar=false,WindowStartupLocation=WindowStartupLocation.Manual};
        try
        {
            window.Show();var choice=(ComboBox)window.FindName("OperandKindInput");var logic=(TextBox)window.FindName("LogicInput");
            Assert.AreEqual(Visibility.Visible,((FrameworkElement)window.FindName("OperandKindPanel")).Visibility);Assert.AreEqual("Custom",choice.SelectedValue);
            logic.Text="SendMsg(s,m)";choice.SelectedValue=kind;Assert.AreEqual(expected,logic.Text);
            logic.Text="SendMsg(s,m)";Assert.AreEqual(expected,logic.Text);Assert.AreEqual("逻辑节点",((TextBox)window.FindName("ValueInput")).Text);
            ((Button)window.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));LogicalNodeWindowEntryTests.DrainUntil(()=>handler.Creates==1&&!window.IsVisible);
            Assert.AreEqual(kind,handler.LastCreate!.Properties.OperandKind);Assert.AreEqual(expected,handler.LastCreate.Properties.LogicLanguage);
        }finally{window.Close();}
    }
    [STATestMethod,DataRow(4),DataRow(8),DataRow(10),DataRow(12),DataRow(18)]
    public void OtherNodeEditorsDoNotOfferOperandKind(int size)
    {
        using var handler=new DirectLogicalNodeHttpStub();using var http=new HttpClient(handler){BaseAddress=new("http://localhost/")};var window=new LogicalNodeWindow(new HttpLineClient(http,new("owner","test")),"owner",new(50.5,50.5),sideLength:size);
        try{Assert.AreEqual(Visibility.Collapsed,((FrameworkElement)window.FindName("OperandKindPanel")).Visibility);}finally{window.Close();}
    }
    [STATestMethod,TestCategory("Rendering")]
    public void OpeningExistingCustomPreservesText_ChangingModeIsUnsavedAndBlocksMove()
    {
        using var handler=new DirectLogicalNodeHttpStub();var n=LogicalNodeOperandTests.Add(handler.Repository,"Custom","MiXeD");using var http=new HttpClient(handler){BaseAddress=new("http://localhost/")};var window=new LogicalNodeWindow(new HttpLineClient(http,new("owner","test")),"owner",new(50.5,50.5),n){Left=-10000,Top=-10000,ShowActivated=false,ShowInTaskbar=false,WindowStartupLocation=WindowStartupLocation.Manual};
        try
        {
            window.Show();var choice=(ComboBox)window.FindName("OperandKindInput");var logic=(TextBox)window.FindName("LogicInput");Assert.AreEqual("MiXeD",logic.Text);choice.SelectedValue="Class";Assert.AreEqual("MIXED",logic.Text);Assert.AreEqual("MiXeD",handler.Repository.Transaction(s=>s.LogicalNodes[n.Code].Properties.LogicLanguage));
            ((Button)window.FindName("MoveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));StringAssert.Contains(((TextBlock)window.FindName("Status")).Text,"尚未保存");Assert.IsTrue(window.IsVisible);
            window.UpdateLayout();var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));var path=Path.Combine(ProjectPaths.Root,".artifacts","renders","ln6-operand-editor.png");Directory.CreateDirectory(Path.GetDirectoryName(path)!);using(var file=File.Create(path))png.Save(file);TestContext.AddResultFile(path);
        }finally{window.Close();}
    }
}
