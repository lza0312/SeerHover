namespace SeerHover;

internal sealed class ExplorerItem
{
    public string Name = string.Empty;
    public Native.RECT Rect;
}

/// <summary>
/// 资源管理器文件项索引。
/// 不依赖 UIA 的 ElementFromPoint（Win11 的文件列表是 DirectUI 控件，祖先链里取不到 ListItem），
/// 改为枚举窗口内所有 ListItem 的矩形，再用光标坐标做命中测试。
///
/// 热路径（每几十毫秒一次）只做：一次 GetWindowRect + 内存矩形命中；UIA 扫描按 TTL 或「标题变了」才做，
/// 且扫描期间其他线程继续用旧快照，不会把鼠标轮询卡住。
/// </summary>
internal sealed class ExplorerItemCache
{
    private const int TtlMs = 2500;
    private const int MinRefreshGapMs = 700;
    private const int TitleCheckMs = 400;

    /// <summary>悬停预览与「双击空白处返回上一层」共用同一份索引，省掉一次 UIA 扫描。</summary>
    public static ExplorerItemCache Shared { get; } = new();

    private readonly object _sync = new();
    private IntPtr _hwnd;
    private Native.RECT _winRect;
    private string _title = string.Empty;
    private long _refreshedAt;
    private long _titleCheckedAt;
    private volatile List<ExplorerItem> _items = new();
    private int _scanning;

    // UIA 偶发会漏掉一两个 item（尤其 Seer 窗口刚显示、资源管理器重绘时）。
    // 光标没动就沿用上次命中，避免把「枚举抖动」误判成「光标离开文件」。
    private ExplorerItem? _lastHit;
    private Native.POINT _lastPoint;

    public ExplorerItem? HitTest(IntPtr hwnd, Native.POINT point)
    {
        long now = Environment.TickCount64;

        // 窗口换了 / 移动过 → 矩形全部失效
        bool windowMoved = false;
        if (hwnd != _hwnd)
        {
            windowMoved = true;
        }
        else if (Native.GetWindowRect(hwnd, out var wr))
        {
            windowMoved = !SameRect(wr, _winRect);
        }

        // 标题变化 = 换了目录（项名字全变），每 400ms 抽查一次就够
        bool titleDue = now - _titleCheckedAt >= TitleCheckMs;
        bool expired = now - _refreshedAt > TtlMs;

        if (windowMoved || titleDue || expired)
        {
            string title = windowMoved || titleDue ? TitleOf(hwnd) : _title;
            _titleCheckedAt = now;
            if (windowMoved || expired || title != _title) Refresh(hwnd, title);
        }

        var items = _items;
        var hit = Find(items, point);
        if (hit != null)
        {
            _lastHit = hit;
            _lastPoint = point;
            return hit;
        }

        // 光标几乎没动 → 认为还是同一个文件项，别被一次漏扫带偏
        if (_lastHit != null && Math.Abs(point.X - _lastPoint.X) <= 3 && Math.Abs(point.Y - _lastPoint.Y) <= 3)
            return _lastHit;

        // 未命中多半是滚动/刷新/切换了目录：限频重扫一次
        if (now - _refreshedAt > MinRefreshGapMs)
        {
            Refresh(hwnd, TitleOf(hwnd));
            hit = Find(_items, point);
            if (hit != null)
            {
                _lastHit = hit;
                _lastPoint = point;
            }
        }
        return hit;
    }

    public void Invalidate()
    {
        lock (_sync)
        {
            _hwnd = IntPtr.Zero;
            _title = string.Empty;
            _refreshedAt = 0;
            _titleCheckedAt = 0;
            _items = new List<ExplorerItem>(0);
            _lastHit = null;
        }
    }

    private static ExplorerItem? Find(List<ExplorerItem> items, Native.POINT p)
    {
        // 倒序：光标通常落在后加入的项上，命中更快；行为与正序一致（矩形不重叠）
        for (int i = items.Count - 1; i >= 0; i--)
        {
            var it = items[i];
            if (p.X >= it.Rect.Left && p.X <= it.Rect.Right &&
                p.Y >= it.Rect.Top && p.Y <= it.Rect.Bottom)
                return it;
        }
        return null;
    }

    private void Refresh(IntPtr hwnd, string title)
    {
        // 已有线程在扫（双击判定与悬停轮询可能撞上）：先用旧快照，别排队等
        if (Interlocked.CompareExchange(ref _scanning, 1, 0) != 0) return;
        try
        {
            long start = Environment.TickCount64;
            var items = Uia.EnumItems(hwnd);

            Native.GetWindowRect(hwnd, out var wr);
            lock (_sync)
            {
                _hwnd = hwnd;
                _winRect = wr;
                _title = title;
                _refreshedAt = Environment.TickCount64;
                _titleCheckedAt = _refreshedAt;
                // 扫描失败（null）时保留旧索引：UIA 偶发失败不该让悬停整体失效；
                // 成功但为空（空目录 / 换了视图）就清空，免得旧项一直「幽灵命中」。
                if (items != null) _items = items;
            }

            if (Logger.IsVerbose)
                Logger.Info($"item scan: {items?.Count ?? 0} items in {Environment.TickCount64 - start}ms ({title})");
        }
        finally
        {
            Interlocked.Exchange(ref _scanning, 0);
        }
    }

    private static bool SameRect(Native.RECT a, Native.RECT b) =>
        a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    private static string TitleOf(IntPtr hwnd) => Native.TitleOf(hwnd);
}
