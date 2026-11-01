using System.Runtime.InteropServices;
using System.Text;

namespace SeerHover;

/// <summary>Seer 官方 IPC（WM_COPYDATA，UTF-16LE，命令见 Seer-sdk/AI_PROMPT.md）。</summary>
internal sealed class SeerIpc
{
    public const int CmdInvokeMain = 5000;
    public const int CmdIsVisible = 5004;
    public const int CmdHide = 5005;
    public const int CmdGetCurrentFile = 5006;
    public const int CmdGetVersion = 5007;
    public const int CmdIsTracking = 5008;

    private const string MainClass = "SeerWindowClass";
    private const string ViewerClass = "SeerViewerWindowClass";

    private IntPtr _hwnd;
    private DateTime _lastResolve = DateTime.MinValue;

    public IntPtr Window
    {
        get
        {
            if (_hwnd != IntPtr.Zero && Native.IsWindow(_hwnd)) return _hwnd;
            if ((DateTime.UtcNow - _lastResolve).TotalMilliseconds < 1000) return _hwnd;
            _lastResolve = DateTime.UtcNow;
            _hwnd = Native.FindWindowW(MainClass, null);
            if (_hwnd == IntPtr.Zero) _hwnd = Native.FindWindowW(ViewerClass, null);
            return _hwnd;
        }
    }

    public bool IsRunning => Window != IntPtr.Zero;

    public IntPtr Send(int command, string? payload, IntPtr target = default)
    {
        IntPtr hwnd = target == IntPtr.Zero ? Window : target;
        if (hwnd == IntPtr.Zero) return IntPtr.Zero;

        IntPtr buffer = IntPtr.Zero;
        try
        {
            var cds = new Native.COPYDATASTRUCT { dwData = new IntPtr(command) };
            if (payload != null)
            {
                buffer = Marshal.StringToHGlobalUni(payload);
                cds.cbData = Encoding.Unicode.GetByteCount(payload) + 2;
                cds.lpData = buffer;
            }
            IntPtr ok = Native.SendMessageTimeoutW(hwnd, Native.WM_COPYDATA, IntPtr.Zero, ref cds,
                Native.SMTO_ABORTIFHUNG, 1500, out IntPtr result);
            if (ok == IntPtr.Zero) return IntPtr.Zero;
            return result;
        }
        catch (Exception ex)
        {
            Logger.Error("ipc send failed: " + ex.Message);
            return IntPtr.Zero;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        }
    }

    public bool IsVisible => Window != IntPtr.Zero && Send(CmdIsVisible, null).ToInt64() != 0;

    /// <summary>本地窗口 API 判定可见性，比 IPC 5004 快一个数量级（几何纠偏要抢时间）</summary>
    public bool IsVisibleFast => Native.IsWindowVisible(Window);

    public void Invoke(string path) => Send(CmdInvokeMain, path);

    public void InvokeStandalone(string path) => Send(5009, path);

    public void InvokePin(string path) => Send(5010, path);
    public void Hide() => Send(CmdHide, null);

    public long Version => Send(CmdGetVersion, null).ToInt64();

    public bool IsTrackingEnabled => Send(CmdIsTracking, null).ToInt64() != 0;

    /// <summary>查询 Seer 当前真正在预览的文件（5006 + 命名共享内存）。空串表示取不到。</summary>
    public string GetCurrentFile() => GetCurrentFileFor(Window);

    // 共享内存按进程复用一个（每次预览都 CreateFileMapping/MapViewOfFile 太浪费）
    private readonly object _mapSync = new();
    private IntPtr _map;
    private IntPtr _mapView;
    private string _mapName = string.Empty;

    private bool EnsureMapLocked()
    {
        if (_mapView != IntPtr.Zero) return true;
        _mapName = $"SeerCurFile_{Environment.ProcessId}";
        _map = Native.CreateFileMappingW(new IntPtr(-1), IntPtr.Zero, 0x04 /*PAGE_READWRITE*/, 0, 65536, _mapName);
        if (_map == IntPtr.Zero) return false;
        _mapView = Native.MapViewOfFile(_map, 0xF001F /*FILE_MAP_ALL_ACCESS*/, 0, 0, UIntPtr.Zero);
        if (_mapView == IntPtr.Zero)
        {
            Native.CloseHandle(_map);
            _map = IntPtr.Zero;
            return false;
        }
        return true;
    }

    /// <summary>查询指定窗口当前预览的文件。</summary>
    public string GetCurrentFileFor(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return string.Empty;
        try
        {
            lock (_mapSync)
            {
                if (!EnsureMapLocked()) return string.Empty;
                Marshal.WriteInt16(_mapView, 0, 0);
                Send(CmdGetCurrentFile, _mapName, hwnd);
                string s = Marshal.PtrToStringUni(_mapView) ?? string.Empty;
                return s.TrimEnd('\0');
            }
        }
        catch (Exception ex)
        {
            Logger.Error("get current file failed: " + ex.Message);
            return string.Empty;
        }
    }

    /// <summary>释放共享内存（退出时调用，测试里也方便）。</summary>
    public void Dispose()
    {
        lock (_mapSync)
        {
            if (_mapView != IntPtr.Zero) Native.UnmapViewOfFile(_mapView);
            if (_map != IntPtr.Zero) Native.CloseHandle(_map);
            _mapView = IntPtr.Zero;
            _map = IntPtr.Zero;
        }
    }

    public bool IsWindowAlive => Window != IntPtr.Zero;

    public Native.RECT GetRect()
    {
        var r = new Native.RECT();
        IntPtr h = Window;
        if (h != IntPtr.Zero) Native.GetWindowRect(h, out r);
        return r;
    }
}
