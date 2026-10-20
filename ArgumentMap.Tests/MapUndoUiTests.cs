using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ArgumentMap.Business;
using ArgumentMap.Client;
using ArgumentMap.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfArgumentMap;

namespace ArgumentMap.Tests;

[TestClass, TestCategory("Unit")]
public sealed class MapUndoUiTests
{
    private static MainWindow Window(HttpLineClient client) => new(client)
    { Width = 1100, Height = 800, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };

    [STATestMethod, DataRow("key"), DataRow("menu")]
    public void ShortcutAndMenuUndoDatabaseAndRefreshAllCaches(string entry)
    {
        using var handler = new DirectLogicalNodeHttpStub(); var selection = MapAreaPasteTests.Fixture(handler.Repository);
        Assert.IsTrue(MapUndoTests.Edit(handler.Repository, r => new MapAreaMoveService(r).Move(new("owner", selection, 300, 300))).Success);
        using var http = new HttpClient(handler) { BaseAddress = new("http://localhost/") }; var window = Window(new(http, new("owner", "test")));
        try
        {
            window.Show(); var view = (CoordinateMapView)window.FindName("MapView"); window.InitializeLinesAsync().GetAwaiter().GetResult();
            Assert.IsTrue(view.BeginAreaMove()); Assert.IsTrue(view.SelectMapAreaAsync(new(310,310,550,390), new()).GetAwaiter().GetResult());
            if (entry == "key") Assert.IsTrue(window.HandleUndoShortcut(Key.Z, ModifierKeys.Control, false, view));
            else ((MenuItem)window.FindName("UndoMapMenu")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.AreEqual(1, handler.Undos); Assert.IsFalse(view.IsAreaMoveMode); Assert.AreEqual(2, view.LineCount); Assert.AreEqual(6, view.LogicalNodeCount); Assert.AreEqual(1, view.CrossCount);
            Assert.AreEqual(50.5m, handler.Repository.Transaction(s => s.LogicalNodes.Values.Single(n => n.SideLength == 12).X));
            StringAssert.Contains(view.OperationMessage, "已从数据库撤销"); Assert.IsFalse(view.IsSaving);
        }
        finally { window.Close(); }
    }

    [STATestMethod]
    public void NativeTextUndoAndModifierKeysNeverInvokeDatabaseUndo_KeyRepeatIsSuppressed()
    {
        using var handler = new DirectLogicalNodeHttpStub(); using var http = new HttpClient(handler) { BaseAddress = new("http://localhost/") }; var window = Window(new(http,new("owner","test")));
        try
        {
            window.Show(); var view = (CoordinateMapView)window.FindName("MapView");
            var text = new TextBox { Text = "text" };
            Assert.IsFalse(window.HandleUndoShortcut(Key.Z, ModifierKeys.Control, false, text)); Assert.AreEqual("text", text.Text);
            Assert.IsFalse(window.HandleUndoShortcut(Key.Z, ModifierKeys.Control, false, new PasswordBox()));
            Assert.IsFalse(window.HandleUndoShortcut(Key.Z, ModifierKeys.None, false, view));
            Assert.IsFalse(window.HandleUndoShortcut(Key.Z, ModifierKeys.Control | ModifierKeys.Shift, false, view));
            Assert.IsTrue(window.HandleUndoShortcut(Key.Z, ModifierKeys.Control, true, view)); Assert.AreEqual(0, handler.Undos);
            Assert.IsTrue(window.HandleUndoShortcut(Key.Z, ModifierKeys.Control, false, view)); Assert.AreEqual(1, handler.Undos);
            StringAssert.Contains(view.OperationMessage, "没有可撤销");
        }
        finally { window.Close(); }
    }

    [STATestMethod]
    public void UncommittedPasteIsRetained_AndBusyUndoCannotRunTwice_FailureKeepsSelection()
    {
        var previous = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        using var handler = new DirectLogicalNodeHttpStub(); var selection = MapAreaPasteTests.Fixture(handler.Repository);
        using var http = new HttpClient(handler) { BaseAddress = new("http://localhost/") }; var window = Window(new(http,new("owner","test")));
        try
        {
            window.Show(); var view = (CoordinateMapView)window.FindName("MapView"); var init = window.InitializeLinesAsync(); LogicalNodeWindowEntryTests.DrainUntil(() => init.IsCompleted);
            Assert.IsTrue(view.BeginAreaMove()); var select = view.SelectMapAreaAsync(MapAreaPasteTests.Bounds,new()); LogicalNodeWindowEntryTests.DrainUntil(()=>select.IsCompleted);
            Assert.IsTrue(view.TryCopySelection(out var text)); Assert.IsTrue(view.BeginPasteSelection(text));
            Assert.IsFalse(view.UndoDatabaseAsync().GetAwaiter().GetResult()); Assert.AreEqual(0, handler.Undos); Assert.IsTrue(view.IsPastingArea);
            view.CancelEditing(); Assert.IsFalse(view.IsPastingArea);
            Assert.IsTrue(view.BeginAreaMove()); select=view.SelectMapAreaAsync(MapAreaPasteTests.Bounds,new()); LogicalNodeWindowEntryTests.DrainUntil(()=>select.IsCompleted);
            handler.UndoGate = new(TaskCreationOptions.RunContinuationsAsynchronously); var undo = view.UndoDatabaseAsync(); LogicalNodeWindowEntryTests.DrainUntil(() => handler.Undos == 1);
            Assert.IsTrue(view.IsSaving); Assert.IsFalse(view.UndoDatabaseAsync().GetAwaiter().GetResult());
            Assert.IsFalse(view.BeginAreaMove()); Assert.IsFalse(view.BeginFlowDiagramImport()); view.CancelEditing(); Assert.IsTrue(view.IsAreaMoveMode);
            handler.UndoGate.SetResult(ServiceResult<MapUndoResult>.Fail("UndoConflict","其他用户修改，未撤销")); LogicalNodeWindowEntryTests.DrainUntil(()=>undo.IsCompleted);
            Assert.IsFalse(undo.GetAwaiter().GetResult()); Assert.IsFalse(view.IsSaving); Assert.IsTrue(view.IsAreaMoveMode); StringAssert.Contains(view.OperationMessage,"未撤销"); Assert.AreEqual(1,handler.Undos);
        }
        finally { window.Close(); SynchronizationContext.SetSynchronizationContext(previous); }
    }
}
