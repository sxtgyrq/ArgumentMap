using ArgumentMap.Contracts;

namespace WpfArgumentMap;

internal sealed class MapAreaMoveEditor
{
    private IMapAreaMoveClient? client;
    internal bool IsActive{get;private set;}
    internal bool IsBusy{get;private set;}
    internal MapAreaSelection? Selection{get;private set;}
    internal string Message{get;private set;}="F7 框选移动。";
    internal void Configure(IMapAreaMoveClient? value)=>client=value;
    internal void Notify(string message)=>Message=message;
    internal bool Begin()
    {
        if(IsBusy)return false;
        if(client is null){Message="当前连接不支持框选移动。";return false;}
        IsActive=true;Selection=null;Message="按住左键拖出矩形，再松开：线路全部线段、LN 整个正方形都在框内才选中，部分框入不选；缺少完整所属线路的 Cross 跳过。左键 / Enter 移动，R 删除，Ctrl+C 复制整组 → Ctrl+V 鼠标落点新增，Esc 取消。";return true;
    }
    internal void Cancel(){if(IsBusy)return;IsActive=false;Selection=null;Message="已取消框选移动，原数据保持不变。";}
    internal async Task<bool> SelectAsync(MapAreaBounds bounds)
    {
        if(IsBusy||!IsActive||client is null)return false;
        Selection=null;
        if(bounds.Left>=bounds.Right||bounds.Bottom>=bounds.Top)
        {Message="选框没有宽度或高度。请按住鼠标左键拖出矩形，再松开；单击不会完成框选。";return false;}
        IsBusy=true;Message="正在读取并校验框选对象…";
        try
        {
            var result=await client.SelectMapAreaAsync(bounds);Message=result.Message;
            if(!result.Success||result.Data is null)return false;
            if(result.Data.Crosses is null||result.Data.Crosses.Any(c=>c is null||!c.Key.IsValid))
            {Message="服务端返回的框选包含无效或非整数 Cross，请更新 Server 后重新框选。";return false;}
            Selection=result.Data;return true;
        }
        finally{IsBusy=false;}
    }
    internal async Task<ServiceResult<MapAreaSnapshot>> DeleteAsync()
    {
        if(IsBusy||!IsActive||Selection is null||client is null){if(!IsBusy)Message="请先按 F7 并完成框选，再按 R 删除。";return ServiceResult<MapAreaSnapshot>.Fail("NoSelection",Message);}
        IsBusy=true;Message="正在事务内复核框选对象并删除，清理关联 Cross 和 LN 位置…";
        try
        {
            var result=await client.DeleteMapAreaAsync(Selection);Message=result.Message;
            if(result.Success&&result.Data is not null){IsActive=false;Selection=null;}
            return result;
        }
        finally{IsBusy=false;}
    }
    internal async Task<ServiceResult<MapAreaSnapshot>> MoveAsync(int x,int y)
    {
        if(IsBusy||!IsActive||Selection is null||client is null)return ServiceResult<MapAreaSnapshot>.Fail("NoSelection","请先完成框选。");
        IsBusy=true;Message="正在事务内校验并整体移动，重算 Cross 和 LN 位置…";
        try
        {
            var result=await client.MoveMapAreaAsync(Selection,x,y);Message=result.Message;
            if(result.Success&&result.Data is not null){IsActive=false;Selection=null;}
            return result;
        }
        finally{IsBusy=false;}
    }
}
