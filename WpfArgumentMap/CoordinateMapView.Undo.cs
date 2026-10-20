using ArgumentMap.Contracts;

namespace WpfArgumentMap;

public sealed partial class CoordinateMapView
{
    private IMapUndoClient? undoClient;
    private bool undoBusy;
    public async Task<bool> UndoDatabaseAsync()
    {
        if (IsSaving) return false;
        if (editor.Mode is LineEditorMode.Ready or LineEditorMode.Drawing || IsMovingLogicalNode ||
            IsPlacingLogicalNode || IsPastingLine || IsPastingArea || IsFlowDiagramImportActive)
        {
            ClipboardNotice("请先保存或取消当前绘制、移动、粘贴或导入草稿，再撤销数据库操作；当前草稿已保留。");
            return false;
        }
        if (undoClient is null) { ClipboardNotice("当前连接不支持数据库撤销，请连接最新版 Server。"); return false; }
        undoBusy = true; ClipboardNotice("正在从数据库撤销上一次操作…");
        try
        {
            var result = await undoClient.UndoAsync();
            if (!result.Success || result.Data is null) { ClipboardNotice(result.Message); return false; }
            // Selection versions are obsolete after compensation. Background K/J
            // responses are revision-guarded by ReplaceCrosses/ReplaceNodes.
            areaMove.Cancel(); editor.Cancel(); logicalNodes.Leave(); crossEditor.LeaveSelection();
            areaBoxStart = areaBoxEnd = areaAnchor = dragPosition = null; ReleaseMouseCapture();
            editor.ReplaceLines(result.Data.Map.Lines); crossEditor.ReplaceCrosses(result.Data.Map.Crosses);
            logicalNodes.ReplaceNodes(result.Data.Map.LogicalNodes);
            areaMoveAction = crossAction = flowImportAction = false;
            ClipboardNotice(result.Message); return true;
        }
        finally { undoBusy = false; Refresh(); }
    }
}
