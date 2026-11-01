using System.Collections.Concurrent;
using System.Windows.Automation;

namespace SeerHover;

/// <summary>
/// 把 UIA 调用固定在一个 STA 线程上，避免 MTA 线程池下的行为差异。
/// 队列串行执行；调用方超时返回后任务仍会跑完（结果丢弃），不会把 STA 线程搞坏。
/// </summary>
internal sealed class StaRunner : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;

    public StaRunner(string name = "SeerHover-UIA")
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = name };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void Loop()
    {
        foreach (var job in _queue.GetConsumingEnumerable())
        {
            try { job(); } catch { }
        }
    }

    public T Run<T>(Func<T> fn, int timeoutMs = 800)
    {
        T result = default!;
        var done = new ManualResetEventSlim(false);
        try
        {
            if (!_queue.TryAdd(() =>
            {
                try { result = fn(); }
                catch { }
                finally { try { done.Set(); } catch (ObjectDisposedException) { } }
            }))
                return default!;

            done.Wait(timeoutMs);
            return result;
        }
        catch (InvalidOperationException)
        {
            return default!;   // 队列已关闭（退出流程）
        }
        finally
        {
            // 任务里的 Set() 已容错，超时后直接释放事件也不会有事
            done.Dispose();
        }
    }

    public void Dispose()
    {
        try { _queue.CompleteAdding(); } catch { }
    }
}

/// <summary>托管 UI Automation 封装（UIAutomationClient 随 WindowsDesktop 运行时提供）。</summary>
internal static class Uia
{
    private static StaRunner? _sta;
    private static StaRunner Runner => _sta ??= new StaRunner();

    /// <summary>枚举窗口内所有文件项（名字 + 屏幕矩形）。</summary>
    public static List<ExplorerItem>? EnumItems(IntPtr hwnd)
    {
        return Runner.Run(() =>
        {
            try
            {
                var root = AutomationElement.FromHandle(hwnd);
                if (root == null) return null;

                // 只扫文件列表子树：导航窗格 / 功能区不在里面，元素少一大截
                var scope = ItemsViewScope(hwnd) ?? root;
                var list = ReadItems(scope);

                // 换了版本/皮肤导致子树里取不到项时，退回整窗口再扫一遍
                if (list.Count == 0 && !ReferenceEquals(scope, root)) list = ReadItems(root);
                return list;
            }
            catch (Exception ex)
            {
                Logger.Error("enum items: " + ex.Message);
                return null;
            }
        }, 3000);
    }

    /// <summary>
    /// 文件列表子树的 UIA 根。
    /// 注意：SHELLDLL_DefView 在 UIA 里的 ClassName 是 DUIListView（和 Win32 类名不同），
    /// 所以这里只按 Win32 类名拿到句柄，是否可用由「扫出来的项数」来兜底，不再比对类名。
    /// </summary>
    private static AutomationElement? ItemsViewScope(IntPtr hwnd)
    {
        IntPtr view = Native.ItemsViewOf(hwnd);
        if (view == IntPtr.Zero) return null;
        try
        {
            var el = AutomationElement.FromHandle(view);
            if (el == null) return null;
            var box = el.Current.BoundingRectangle;
            return box.Width > 40 && box.Height > 40 ? el : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// 取文件项。先用「List 容器的直接子项」这条快路径（比整子树遍历便宜一半以上），
    /// 拿不到再退回整子树，兼容不同版本的资源管理器。
    /// 属性用 CacheRequest 一次批量取回，避免每个元素跨进程问两遍。
    /// </summary>
    private static List<ExplorerItem> ReadItems(AutomationElement scope)
    {
        var itemCond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem);
        var listCond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.List);

        var list = new List<ExplorerItem>(64);
        var request = new CacheRequest { AutomationElementMode = AutomationElementMode.None };
        request.Add(AutomationElement.NameProperty);
        request.Add(AutomationElement.BoundingRectangleProperty);

        using (request.Activate())
        {
            AutomationElement? listEl = null;
            try { listEl = scope.FindFirst(TreeScope.Descendants, listCond); }
            catch { }

            if (listEl != null)
            {
                try { Collect(listEl.FindAll(TreeScope.Children, itemCond), list); }
                catch { }
                if (list.Count > 0) return list;
            }

            try { Collect(scope.FindAll(TreeScope.Descendants, itemCond), list); }
            catch { }
        }
        return list;
    }

    private static void Collect(AutomationElementCollection found, List<ExplorerItem> into)
    {
        for (int i = 0; i < found.Count; i++)
        {
            string name;
            System.Windows.Rect box;
            try
            {
                var el = found[i];
                name = el.Cached.Name ?? string.Empty;
                box = el.Cached.BoundingRectangle;
                if (name.Length == 0)
                {
                    // 个别 provider 不支持缓存请求，退回实时读取
                    name = el.Current.Name ?? string.Empty;
                    box = el.Current.BoundingRectangle;
                }
            }
            catch (ElementNotAvailableException) { continue; }
            catch { continue; }

            if (name.Length == 0 || box.Width < 2 || box.Height < 2) continue;
            into.Add(new ExplorerItem
            {
                Name = name.Trim(),
                Rect = new Native.RECT
                {
                    Left = (int)Math.Round(box.Left),
                    Top = (int)Math.Round(box.Top),
                    Right = (int)Math.Round(box.Right),
                    Bottom = (int)Math.Round(box.Bottom)
                }
            });
        }
    }

    /// <summary>诊断：直接扫某个窗口/元素子树，返回项数与耗时（用于对比扫描范围）。</summary>
    public readonly record struct ScanStat(int Count, double Ms);

    public static ScanStat ScanScope(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return new ScanStat(-1, 0);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int count = Runner.Run(() =>
        {
            try
            {
                var el = AutomationElement.FromHandle(hwnd);
                return el == null ? -1 : ReadItems(el).Count;
            }
            catch { return -1; }
        }, 6000);
        return new ScanStat(count, sw.Elapsed.TotalMilliseconds);
    }

    /// <summary>诊断：元素句柄在 UIA 里的类名 / 名称（判断子树根是否可用）。</summary>
    public static string ElementInfo(IntPtr hwnd)
        => Runner.Run(() =>
        {
            try
            {
                var el = AutomationElement.FromHandle(hwnd);
                if (el == null) return "null";
                return $"class='{el.Current.ClassName}' name='{el.Current.Name}' type='{el.Current.ControlType?.ProgrammaticName}'";
            }
            catch (Exception ex) { return "ERR " + ex.Message; }
        }, 3000);

    /// <summary>诊断用：统计窗口内能被 UIA 识别为列表项的元素。</summary>
    public static string DumpListItems(IntPtr hwnd)
    {
        return Runner.Run(() =>
        {
            var sb = new StringBuilder();
            try
            {
                var root = AutomationElement.FromHandle(hwnd);
                if (root == null) return "FromHandle returned null";
                var wr = root.Current.BoundingRectangle;
                Native.GetWindowRect(hwnd, out var wr32);
                sb.AppendLine($"window: '{root.Current.Name}' class='{root.Current.ClassName}'");
                sb.AppendLine($"win32 rect={wr32} uia=[{wr.Left:0},{wr.Top:0},{wr.Right:0},{wr.Bottom:0}]");

                var cond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem);
                var items = root.FindAll(TreeScope.Descendants, cond);
                sb.AppendLine($"ListItem count={items.Count}");
                for (int i = 0; i < Math.Min(items.Count, 6); i++)
                {
                    var r = items[i].Current.BoundingRectangle;
                    sb.AppendLine($"   '{items[i].Current.Name}' [{r.Left:0},{r.Top:0},{r.Right:0},{r.Bottom:0}]");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("ERR " + ex.GetType().Name + ": " + ex.Message);
            }
            return sb.ToString();
        }, 6000);
    }

    /// <summary>按名字选中窗口内的某个列表项（模拟用户在资源管理器里点选）。</summary>
    public static bool SelectItem(IntPtr hwnd, string name)
    {
        return Runner.Run(() =>
        {
            try
            {
                var root = AutomationElement.FromHandle(hwnd);
                if (root == null) return false;
                var cond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem);
                var found = root.FindAll(TreeScope.Descendants, cond);
                for (int i = 0; i < found.Count; i++)
                {
                    var el = found[i];
                    string n;
                    try { n = (el.Current.Name ?? string.Empty).Trim(); } catch { continue; }
                    if (!string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!el.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object p)) continue;
                    var sel = (SelectionItemPattern)p;
                    sel.Select();
                    return true;
                }
            }
            catch (Exception ex) { Logger.Error("select item: " + ex.Message); }
            return false;
        }, 4000);
    }

    /// <summary>
    /// 资源管理器里当前选中的文件项数量（无法判定时返回 -1）。
    /// 用途：双击空白处会清空选中（0），而双击落在项的格子里会选中它（>=1）——
    /// 用来兜住「UIA 项矩形比实际可点区域小」的误差，避免误把点中文件当成空白处而误退回上一层。
    /// </summary>
    public static int SelectionCount(IntPtr hwnd)
    {
        return Runner.Run(() =>
        {
            try
            {
                var root = AutomationElement.FromHandle(hwnd);
                if (root == null) return -1;

                // 先只看文件列表子树（导航窗格里的 List 不算），拿不到再退回整窗口
                var scope = ItemsViewScope(hwnd) ?? root;
                int n = CountSelected(scope);
                if (n >= 0) return n;
                if (!ReferenceEquals(scope, root)) return CountSelected(root);
                return -1;
            }
            catch (Exception ex)
            {
                Logger.Error("selection count: " + ex.Message);
                return -1;
            }
        }, 1200);
    }

    private static int CountSelected(AutomationElement root)
    {
        try
        {
            var listCond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.List);
            var lists = root.FindAll(TreeScope.Descendants, listCond);
            for (int i = 0; i < lists.Count; i++)
            {
                try
                {
                    if (!lists[i].TryGetCurrentPattern(SelectionPattern.Pattern, out object p)) continue;
                    return ((SelectionPattern)p).Current.GetSelection().Length;
                }
                catch { }
            }

            // 退路：数一数 ListItem 里的 IsSelected
            var itemCond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem);
            var items = root.FindAll(TreeScope.Descendants, itemCond);
            int n = 0;
            for (int i = 0; i < items.Count; i++)
            {
                try
                {
                    if (items[i].TryGetCurrentPattern(SelectionItemPattern.Pattern, out object q) &&
                        ((SelectionItemPattern)q).Current.IsSelected) n++;
                }
                catch { }
            }
            return n;
        }
        catch { return -1; }
    }

    /// <summary>诊断用：打印光标下元素及其祖先链。</summary>
    public static string? Describe(int x, int y)
    {
        return Runner.Run(() =>
        {
            try
            {
                var el = AutomationElement.FromPoint(new System.Windows.Point(x, y));
                if (el == null) return null;
                var parts = new List<string>();
                for (int i = 0; i < 5 && el != null; i++)
                {
                    string n, c, t;
                    try
                    {
                        n = (el.Current.Name ?? string.Empty).Trim();
                        c = el.Current.ClassName ?? string.Empty;
                        t = el.Current.ControlType?.ProgrammaticName ?? "?";
                    }
                    catch { break; }
                    parts.Add($"{t}:'{n}':{c}");
                    try { el = TreeWalker.ControlViewWalker.GetParent(el); }
                    catch { break; }
                }
                return string.Join(" <- ", parts);
            }
            catch (Exception ex)
            {
                return $"ERR {ex.GetType().Name}: {ex.Message}";
            }
        });
    }
}
