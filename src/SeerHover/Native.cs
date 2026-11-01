using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SeerHover;

/// <summary>Win32 互操作集合。所有坐标均为物理像素（进程为 Per-Monitor V2 感知）。</summary>
internal static class Native
{
    public const uint GA_ROOT = 2;
    public const uint GA_ROOTOWNER = 3;

    public const int VK_LBUTTON = 0x01;
    public const int VK_SHIFT = 0x10;
    public const int VK_CONTROL = 0x11;
    public const int VK_MENU = 0x12;
    public const int VK_ESCAPE = 0x1B;
    public const int VK_UP = 0x26;

    public const uint KEYEVENTF_KEYUP = 0x0002;

    public const int WH_MOUSE_LL = 14;
    public const int WM_LBUTTONDOWN = 0x0201;
    public const uint WM_QUIT = 0x0012;
    public const int SM_CXDOUBLECLK = 36;
    public const int SM_CYDOUBLECLK = 37;

    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;

    public const uint WM_COPYDATA = 0x004A;
    public const uint SMTO_ABORTIFHUNG = 0x0002;
    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;

        public POINT(int x, int y) { X = x; Y = y; }
        public override string ToString() => $"{X},{Y}";
        public static bool operator ==(POINT a, POINT b) => a.X == b.X && a.Y == b.Y;
        public static bool operator !=(POINT a, POINT b) => !(a == b);
        public override bool Equals(object? obj) => obj is POINT p && p.X == X && p.Y == Y;
        public override int GetHashCode() => (X * 397) ^ Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
        public bool IsEmpty => Width <= 0 || Height <= 0;
        public override string ToString() => $"({Left},{Top})-({Right},{Bottom}) {Width}x{Height}";
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct COPYDATASTRUCT
    {
        public IntPtr dwData;
        public int cbData;
        public IntPtr lpData;
    }

    /// <summary>低级鼠标钩子回调里的鼠标事件信息（坐标是物理屏幕像素）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetMessageW(out MSG msg, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr DispatchMessageW(ref MSG msg);

    [DllImport("user32.dll")]
    public static extern bool PostThreadMessageW(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string? cls, string? title);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandleW(string? name);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT p);

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT p);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassNameW(IntPtr hWnd, StringBuilder sb, int maxCount);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowW(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    public static extern IntPtr GetShellWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextW(IntPtr hWnd, StringBuilder sb, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, uint msg, IntPtr wParam,
        ref COPYDATASTRUCT lParam, uint flags, uint timeout, out IntPtr result);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessageW(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    public static extern bool AttachConsole(int dwProcessId);

    /// <summary>把系统定时器精度提到 1ms，让 Thread.Sleep(5) 真的是 5ms（默认约 15.6ms）</summary>
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    public static extern uint TimeBeginPeriod(uint ms);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    public static extern uint TimeEndPeriod(uint ms);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateFileMappingW(IntPtr hFile, IntPtr secAttr, uint protect,
        uint maxHigh, uint maxLow, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr MapViewOfFile(IntPtr hMap, uint access, uint offHigh, uint offLow, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool UnmapViewOfFile(IntPtr ptr);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr h);

    /// <summary>进程级 DPI 感知（-4 = PER_MONITOR_AWARE_V2），保证 GetWindowRect / UIA / 光标坐标同一坐标系。</summary>
    [DllImport("user32.dll")]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>让系统标题栏跟随深色（Win10 1809+ 用 20，更早用 19）。</summary>
    public static void SetDarkTitleBar(IntPtr hwnd, bool dark)
    {
        if (hwnd == IntPtr.Zero) return;
        int v = dark ? 1 : 0;
        try
        {
            if (DwmSetWindowAttribute(hwnd, 20, ref v, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, 19, ref v, sizeof(int));
        }
        catch { }
    }

    public static void EnablePerMonitorDpi()
    {
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr templ);

    // 轮询线程每几十毫秒就要取一次类名/标题，复用缓冲区避免每次 new StringBuilder + 临时字符串
    [ThreadStatic] private static StringBuilder? _buf;

    private static StringBuilder Buffer(int capacity)
    {
        var sb = _buf ??= new StringBuilder(capacity);
        sb.Clear();
        if (sb.Capacity < capacity) sb.Capacity = capacity;
        return sb;
    }

    public static string ClassOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return string.Empty;
        var sb = Buffer(256);
        int n = GetClassNameW(hwnd, sb, sb.Capacity);
        return n > 0 ? sb.ToString(0, n) : string.Empty;
    }

    public static string TitleOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return string.Empty;
        var sb = Buffer(512);
        int n = GetWindowTextW(hwnd, sb, sb.Capacity);
        return n > 0 ? sb.ToString(0, n) : string.Empty;
    }

    private static readonly Dictionary<uint, string> ProcNameCache = new();

    public static string ProcessNameOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return string.Empty;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return string.Empty;
        lock (ProcNameCache)
        {
            if (ProcNameCache.TryGetValue(pid, out var cached)) return cached;
            string name;
            try { name = Process.GetProcessById((int)pid).ProcessName; }
            catch { name = string.Empty; }
            ProcNameCache[pid] = name;
            return name;
        }
    }

    /// <summary>窗口类名 + 进程名（按句柄缓存 4 秒）。轮询每次都要问，这两项又基本不变。</summary>
    public readonly record struct WindowInfo(string Class, string Process);

    private static readonly Dictionary<IntPtr, (WindowInfo Info, long At)> InfoCache = new();
    private static readonly object InfoSync = new();

    public static WindowInfo InfoOf(IntPtr hwnd, int ttlMs = 4000)
    {
        if (hwnd == IntPtr.Zero) return new WindowInfo(string.Empty, string.Empty);

        long now = Environment.TickCount64;
        lock (InfoSync)
        {
            if (InfoCache.TryGetValue(hwnd, out var hit) && now - hit.At < ttlMs) return hit.Info;
        }

        var info = new WindowInfo(ClassOf(hwnd), ProcessNameOf(hwnd));
        lock (InfoSync)
        {
            if (InfoCache.Count > 64) InfoCache.Clear();   // 资源管理器窗口数量很少，超了直接清空
            InfoCache[hwnd] = (info, now);
        }
        return info;
    }

    public static void ForgetWindowInfo(IntPtr hwnd)
    {
        lock (InfoSync) InfoCache.Remove(hwnd);
    }

    /// <summary>句柄属于指定进程（忽略大小写）。</summary>
    public static bool IsProcess(IntPtr hwnd, string processName)
        => string.Equals(InfoOf(hwnd).Process, processName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Scoped 1ms 定时精度：只在预览窗口定位的那几百毫秒里提高精度，别让整机 24 小时都跑在 1ms 定时上。</summary>
    public readonly struct TimerResolution : IDisposable
    {
        private readonly bool _raised;

        private TimerResolution(bool raised) => _raised = raised;

        public static TimerResolution Begin(int ms = 1)
        {
            try { TimeBeginPeriod((uint)ms); return new TimerResolution(true); }
            catch { return default; }
        }

        public void Dispose()
        {
            if (!_raised) return;
            try { TimeEndPeriod(1); } catch { }
        }
    }

    public static bool IsKeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    public static IntPtr RootOf(IntPtr hwnd) => hwnd == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hwnd, GA_ROOT);

    /// <summary>光标下的顶层窗口（一次 P/Invoke 拿到 under + root，热路径里别调两遍）。</summary>
    public static IntPtr RootAt(POINT p, out IntPtr under)
    {
        under = WindowFromPoint(p);
        return under == IntPtr.Zero ? IntPtr.Zero : GetAncestor(under, GA_ROOT);
    }

    public static MONITORINFO MonitorOf(int x, int y)
    {
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        IntPtr mon = MonitorFromPoint(new POINT(x, y), MONITOR_DEFAULTTONEAREST);
        GetMonitorInfoW(mon, ref mi);
        return mi;
    }

    /// <summary>WinExe 进程在命令行模式下接管父控制台输出。</summary>
    public static bool AttachParentConsole()
    {
        if (!AttachConsole(-1)) return false;
        try
        {
            var stdout = Console.OpenStandardOutput();
            var writer = new StreamWriter(stdout) { AutoFlush = true };
            Console.SetOut(writer);
            return true;
        }
        catch { return false; }
    }

    /// <summary>在窗口子树里按类名找一个子窗口（depth 限制层数，避免在资源管理器里扫太深）。</summary>
    public static IntPtr FindChildClass(IntPtr parent, int depth, params string[] classes)
    {
        if (parent == IntPtr.Zero || depth <= 0) return IntPtr.Zero;

        foreach (var cls in classes)
        {
            IntPtr hit = FindWindowExW(parent, IntPtr.Zero, cls, null);
            if (hit != IntPtr.Zero) return hit;
        }

        IntPtr child = IntPtr.Zero;
        while ((child = FindWindowExW(parent, child, null, null)) != IntPtr.Zero)
        {
            IntPtr hit = FindChildClass(child, depth - 1, classes);
            if (hit != IntPtr.Zero) return hit;
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// 资源管理器文件列表（图标/详细信息那块）的窗口句柄。
    /// Win10：CabinetWClass → SHELLDLL_DefView → DirectUIHWND；
    /// Win11 24H2：CabinetWClass → ShellTabWindowClass → DUIViewWndClassName → DirectUIHWND
    ///            → CtrlNotifySink → SHELLDLL_DefView。
    /// 两种结构里 SHELLDLL_DefView 的矩形都正好是「文件区」（不含导航窗格、地址栏、状态栏），
    /// 所以直接找它；找不到就返回 0（宁可不开这个功能，也不要把导航窗格当成空白处）。
    /// </summary>
    private static readonly Dictionary<IntPtr, (IntPtr View, long At)> ViewCache = new();
    private static readonly object ViewSync = new();

    public static IntPtr ItemsViewOf(IntPtr explorerRoot)
    {
        if (explorerRoot == IntPtr.Zero) return IntPtr.Zero;

        long now = Environment.TickCount64;
        lock (ViewSync)
        {
            if (ViewCache.TryGetValue(explorerRoot, out var hit) && now - hit.At < 10_000 && IsWindow(hit.View))
                return hit.View;
        }

        // 窗口结构（多标签页切换）可能变，缓存过期或句柄失效就重新找
        IntPtr view = FindChildClass(explorerRoot, 6, "SHELLDLL_DefView");
        lock (ViewSync)
        {
            if (ViewCache.Count > 32) ViewCache.Clear();
            ViewCache[explorerRoot] = (view, now);
        }
        return view;
    }

    /// <summary>点是否在某个窗口矩形内（含边界）。</summary>
    public static bool InRect(IntPtr hwnd, POINT p)
    {
        if (!GetWindowRect(hwnd, out var r)) return false;
        return p.X >= r.Left && p.X <= r.Right && p.Y >= r.Top && p.Y <= r.Bottom;
    }

    /// <summary>合成 Alt+↑（资源管理器「回到上一层」）。</summary>
    public static void SendAltUp()
    {
        keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
        Thread.Sleep(12);
        keybd_event(VK_UP, 0, 0, UIntPtr.Zero);
        Thread.Sleep(12);
        keybd_event(VK_UP, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        Thread.Sleep(12);
        keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }
}
