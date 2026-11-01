using System.Runtime.InteropServices;

namespace SeerHover;

/// <summary>
/// 资源管理器「空白处双击 = 回到上一层」（等同 Alt+↑）。
///
/// 做法：
/// 1. 全局低级鼠标钩子只做一件事——用两次左键按下的时间/位移判断双击（钩子里不能做重活，会拖慢整机鼠标）；
/// 2. 判定放到后台线程：先用 Win32 找到文件列表窗口（CabinetWClass → SHELLDLL_DefView → DirectUIHWND），
///    要求点击点落在它的矩形内（导航窗格 / 地址栏 / 工具栏都不算空白处）；
/// 3. 再用 UIA 项矩形排除「点在文件上」，并用「当前没有选中项」兜住 UIA 矩形偏小的误差
///    （双击落在项的格子里时资源管理器会选中它，此时不动，避免开了文件又退回上一层）；
/// 4. 全部通过才合成 Alt+↑。
///
/// 桌面（Progman / WorkerW）不是 CabinetWClass，天然不会触发。
/// </summary>
internal sealed class ExplorerUp : IDisposable
{
    private readonly Func<bool> _enabled;

    private Native.HookProc? _proc;          // 必须留住引用，否则委托被 GC 后钩子会崩
    private IntPtr _hook;
    private Thread? _thread;
    private uint _threadId;
    private volatile bool _stop;

    private Native.POINT _prevPt;
    private DateTime _prevAt = DateTime.MinValue;
    private DateTime _lastFireAt = DateTime.MinValue;
    private int _busy;

    public ExplorerUp(Func<bool> enabled) => _enabled = enabled;

    public bool HookInstalled => _hook != IntPtr.Zero;

    public void Start()
    {
        if (_thread != null) return;
        _thread = new Thread(Loop) { IsBackground = true, Name = "SeerHover-DblClick" };
        _thread.Start();
    }

    private void Loop()
    {
        _threadId = Native.GetCurrentThreadId();
        _proc = HookCallback;
        _hook = Native.SetWindowsHookExW(Native.WH_MOUSE_LL, _proc, Native.GetModuleHandleW(null), 0);
        if (_hook == IntPtr.Zero)
        {
            Logger.Error("mouse hook install failed: " + Marshal.GetLastWin32Error());
            return;
        }
        Logger.Info("double-click hook installed");

        // 低级钩子需要一个消息泵才会被回调
        int r;
        while (!_stop && (r = Native.GetMessageW(out var msg, IntPtr.Zero, 0, 0)) != 0)
        {
            if (r == -1) break;
            Native.TranslateMessage(ref msg);
            Native.DispatchMessageW(ref msg);
        }

        if (_hook != IntPtr.Zero) Native.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        Logger.Info("double-click hook removed");
    }

    /// <summary>钩子回调：只做双击判定，重活丢给线程池。</summary>
    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && (int)wParam == Native.WM_LBUTTONDOWN)
            {
                // 只读 MSLLHOOKSTRUCT 头两个 int（x/y），不做整块结构体复制——这是全系统鼠标回调，越轻越好
                int x = Marshal.ReadInt32(lParam, 0);
                int y = Marshal.ReadInt32(lParam, 4);
                MaybeDoubleClick(new Native.POINT(x, y));
            }
        }
        catch (Exception ex)
        {
            Logger.Error("mouse hook: " + ex.Message);
        }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    // 双击时间/宽容区几乎不变，缓存起来省掉每次点击的 3 次系统调用
    private static long _clickCfgAt;
    private static int _dblMs = 500;
    private static int _dblCx = 4;
    private static int _dblCy = 4;

    private static void RefreshClickConfig()
    {
        long now = Environment.TickCount64;
        if (now - _clickCfgAt < 5000) return;
        _clickCfgAt = now;
        _dblMs = (int)Native.GetDoubleClickTime();
        _dblCx = Math.Max(4, Native.GetSystemMetrics(Native.SM_CXDOUBLECLK) / 2);
        _dblCy = Math.Max(4, Native.GetSystemMetrics(Native.SM_CYDOUBLECLK) / 2);
    }

    private void MaybeDoubleClick(Native.POINT pt)
    {
        var now = DateTime.UtcNow;
        double gap = (now - _prevAt).TotalMilliseconds;
        RefreshClickConfig();

        int dx = Math.Abs(pt.X - _prevPt.X);
        int dy = Math.Abs(pt.Y - _prevPt.Y);
        bool second = gap <= _dblMs && dx <= _dblCx && dy <= _dblCy;

        _prevPt = pt;
        _prevAt = second ? DateTime.MinValue : now;   // 三击不重复触发

        if (!second) return;
        if (!_enabled()) return;
        // Ctrl/Shift/Alt 双击有自己的含义（多选、拖拽等），不掺和
        if (Native.IsKeyDown(Native.VK_CONTROL) || Native.IsKeyDown(Native.VK_SHIFT) || Native.IsKeyDown(Native.VK_MENU)) return;
        // 一次双击只处理一次（还有别的双击来源时也别叠着退回好几层）
        if ((DateTime.UtcNow - _lastFireAt).TotalMilliseconds < 350) return;
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;

        // 钩子里必须先抓住「点击前」的窗口与标题：等资源管理器处理完再取，
        // 双击文件夹时窗口可能已经跳到新目录了，后面的空白判定就会错位。
        IntPtr root = Native.RootOf(Native.WindowFromPoint(pt));
        if (!Native.IsWindow(root) || !HoverResolver.IsExplorerRoot(root)) { Interlocked.Exchange(ref _busy, 0); return; }
        string title = Native.TitleOf(root);
        _lastFireAt = DateTime.UtcNow;

        _ = Task.Run(() =>
        {
            try { Handle(root, pt, title); }
            catch (Exception ex) { Logger.Error("double-click up: " + ex.Message); }
            finally { Interlocked.Exchange(ref _busy, 0); }
        });
    }

    private void Handle(IntPtr root, Native.POINT pt, string titleBefore)
    {
        // 先让资源管理器处理完这次双击（空白处它什么都不做，不会和后续按键打架）
        Thread.Sleep(140);
        if (!_enabled()) return;
        if (!Native.IsWindow(root)) return;

        // 标题变了 = 这次双击已经让窗口跳到了别的目录（双击的是文件夹），不掺和
        if (!string.Equals(Native.TitleOf(root), titleBefore, StringComparison.Ordinal))
        {
            Logger.Info("double-click: folder changed, ignored");
            return;
        }

        if (!IsEmptyItemsArea(root, pt))
        {
            Logger.Info("double-click: not empty items area, ignored");
            return;
        }

        Logger.Info("double-click empty area -> Alt+Up");
        Native.SendAltUp();
    }

    /// <summary>该点是否真的落在文件列表的空白处。</summary>
    private static bool IsEmptyItemsArea(IntPtr root, Native.POINT pt)
    {
        IntPtr view = Native.ItemsViewOf(root);
        if (view == IntPtr.Zero) return false;
        if (!Native.InRect(view, pt)) return false;

        // 复用悬停预览那份索引：悬停时刚扫过，这里基本是纯内存命中
        if (ExplorerItemCache.Shared.HitTest(root, pt) != null) return false;

        // 兜底：双击项的「格子」时资源管理器会选中它；选中数 >0 说明不是空白处
        if (Uia.SelectionCount(root) > 0) return false;
        return true;
    }

    /// <summary>诊断：不触发动作，只报告这个点会怎么判定。</summary>
    public static string Probe(int x, int y)
    {
        var pt = new Native.POINT(x, y);
        IntPtr root = Native.RootOf(Native.WindowFromPoint(pt));
        var sb = new StringBuilder();
        sb.AppendLine($"point: {x},{y}");
        sb.AppendLine($"root: class='{Native.ClassOf(root)}' proc='{Native.ProcessNameOf(root)}'");

        if (!HoverResolver.IsExplorerRoot(root))
        {
            sb.AppendLine("=> not an explorer window, would ignore");
            return sb.ToString();
        }

        IntPtr view = Native.ItemsViewOf(root);
        sb.AppendLine($"items view: 0x{view.ToInt64():X} rect={(Native.GetWindowRect(view, out var vr) ? vr.ToString() : "n/a")}");
        bool inView = view != IntPtr.Zero && Native.InRect(view, pt);
        sb.AppendLine($"in items view: {inView}");

        var items = Uia.EnumItems(root);
        sb.AppendLine($"items: {items?.Count.ToString() ?? "null"}");
        bool onItem = false;
        if (items != null)
        {
            foreach (var it in items)
            {
                if (pt.X >= it.Rect.Left && pt.X <= it.Rect.Right && pt.Y >= it.Rect.Top && pt.Y <= it.Rect.Bottom)
                {
                    sb.AppendLine($"on item: '{it.Name}' {it.Rect}");
                    onItem = true;
                }
            }
        }

        int sel = Uia.SelectionCount(root);
        sb.AppendLine($"selection: {sel}");
        sb.AppendLine($"=> would go up: {inView && !onItem && sel == 0}");
        return sb.ToString();
    }

    public void Dispose()
    {
        _stop = true;
        if (_threadId != 0) Native.PostThreadMessageW(_threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        try { _thread?.Join(800); } catch { }
        _thread = null;
    }
}
