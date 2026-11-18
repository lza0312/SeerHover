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

    /// <summary>
    /// 一个标签的身份。多标签共享顶层 HWND，序号在 Shell.Windows()/DefView 里都不可靠，
    /// 名称才是三者（TabItem / Shell.LocationName / ShellTabWindowClass.Name）共用的键。
    /// </summary>
    public readonly record struct TabInfo(int Index, string Name)
    {
        public bool HasName => !string.IsNullOrEmpty(Name);
    }

    /// <summary>枚举窗口内所有文件项（名字 + 屏幕矩形）。</summary>
    public static List<ExplorerItem>? EnumItems(IntPtr hwnd)
        => Runner.Run(() => EnumItemsCore(hwnd, ActiveTabCore(hwnd)), 3000);

    /// <summary>按指定标签枚举文件项；调用方（缓存）保证标签与文件夹路径一致。</summary>
    public static List<ExplorerItem>? EnumItems(IntPtr hwnd, TabInfo tab)
        => Runner.Run(() => EnumItemsCore(hwnd, tab), 3000);

    private static List<ExplorerItem>? EnumItemsCore(IntPtr hwnd, TabInfo tab)
    {
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root == null) return null;

            // 只扫文件列表子树：导航窗格 / 功能区不在里面，元素少一大截。
            // 多标签页下会有多个 DefView（矩形完全相同），必须按激活标签的【名字】取对应那个：
            // DefView 的枚举顺序与 TabItem / Shell.Windows() 顺序不保证一致（实测可能完全相反）。
            var scope = ItemsViewScopeCore(hwnd, tab) ?? root;
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
    }

    /// <summary>
    /// 文件列表子树的 UIA 根。
    /// 注意：SHELLDLL_DefView 在 UIA 里的 ClassName 是 DUIListView（和 Win32 类名不同），
    /// 所以这里只按 Win32 类名拿到句柄，是否可用由「扫出来的项数」来兜底，不再比对类名。
    /// </summary>
    private static AutomationElement? ItemsViewScopeCore(IntPtr hwnd, TabInfo tab)
    {
        IntPtr view = ActiveItemsViewCore(hwnd, tab);
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
    /// 找到当前激活标签对应的 SHELLDLL_DefView。
    /// 关键：不按序号取——Win11 里 DefView 的 Z 序 / 枚举顺序与 TabItem 顺序无关。
    /// 沿 DefView 的 UIA 祖先链找到 ShellTabWindowClass，它的 Name 就是标签名。
    /// </summary>
    private static IntPtr ActiveItemsViewCore(IntPtr hwnd, TabInfo tab)
    {
        try
        {
            var views = Native.FindAllClass(hwnd, 8, "SHELLDLL_DefView");
            if (views.Count == 0) return IntPtr.Zero;
            if (views.Count == 1 || !tab.HasName) return views[0];

            for (int i = 0; i < views.Count; i++)
            {
                string owner = ViewOwnerNameCore(views[i]);
                if (owner.Length > 0 && string.Equals(owner, tab.Name, StringComparison.OrdinalIgnoreCase))
                    return views[i];
            }

            // 标签名对不上（特殊文件夹/改名）时退一步：取 UIA 报告为可见的那个 DefView。
            for (int i = 0; i < views.Count; i++)
            {
                try
                {
                    var el = AutomationElement.FromHandle(views[i]);
                    if (el != null && !el.Current.IsOffscreen) return views[i];
                }
                catch { }
            }
            return views[0];
        }
        catch { return IntPtr.Zero; }
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

    /// <summary>诊断：选中指定序号的标签（只用于 --select-tab 自检）。</summary>
    public static bool SelectTab(IntPtr hwnd, int index)
        => Runner.Run(() =>
        {
            try
            {
                var root = AutomationElement.FromHandle(hwnd);
                if (root == null) return false;
                var cond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem);
                var tabs = root.FindAll(TreeScope.Descendants, cond);
                if (index < 0 || index >= tabs.Count) return false;
                if (!tabs[index].TryGetCurrentPattern(SelectionItemPattern.Pattern, out object p)) return false;
                ((SelectionItemPattern)p).Select();
                return true;
            }
            catch { return false; }
        }, 2000);

    /// <summary>当前激活标签（序号 + 名称）。单标签/无法确定时名称为空、序号 0。</summary>
    public static TabInfo ActiveTab(IntPtr hwnd) => Runner.Run(() => ActiveTabCore(hwnd), 1200);

    /// <summary>只能在 STA Runner 的 job 里调用，避免嵌套 Runner 造成 1.2 秒空等。</summary>
    private static TabInfo ActiveTabCore(IntPtr hwnd)
    {
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root == null) return default;
            var cond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem);
            var tabs = root.FindAll(TreeScope.Descendants, cond);
            for (int i = 0; i < tabs.Count; i++)
            {
                try
                {
                    if (tabs[i].TryGetCurrentPattern(SelectionItemPattern.Pattern, out object p) &&
                        ((SelectionItemPattern)p).Current.IsSelected)
                        return new TabInfo(i, (tabs[i].Current.Name ?? string.Empty).Trim());
                }
                catch { }
            }
        }
        catch { }
        return default;
    }

    public static int SelectedTabIndex(IntPtr hwnd) => ActiveTab(hwnd).Index;

    /// <summary>诊断：Shell.Windows() 里同一 HWND 对应的所有 folder path（多标签会有多条）。</summary>
    public static string ShellPaths(IntPtr hwnd)
    {
        try
        {
            var t = Type.GetTypeFromProgID("Shell.Application");
            if (t == null) return "no Shell.Application";
            dynamic shell = Activator.CreateInstance(t)!;
            dynamic windows = shell.Windows();
            var sb = new StringBuilder();
            int count = (int)windows.Count;
            sb.Append($"count={count}");
            for (int i = 0; i < count; i++)
            {
                try
                {
                    dynamic w = windows.Item(i);
                    long h = (long)w.HWND;
                    if (new IntPtr(h) != hwnd) continue;
                    string p = (string)w.Document.Folder.Self.Path;
                    string n = (string)w.LocationName;
                    sb.Append($"\n  [{i}] path='{p}' name='{n}'");
                }
                catch { }
            }
            return sb.ToString();
        }
        catch (Exception ex) { return "ERR " + ex.Message; }
    }

    /// <summary>诊断：整窗口里所有 TabItem 的选中状态，用于定位当前激活标签。</summary>
    public static string TabItems(IntPtr hwnd)
        => Runner.Run(() =>
        {
            try
            {
                var root = AutomationElement.FromHandle(hwnd);
                if (root == null) return "null";
                var cond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem);
                var tabs = root.FindAll(TreeScope.Descendants, cond);
                var sb = new StringBuilder();
                sb.Append($"TabItem count={tabs.Count}");
                for (int i = 0; i < tabs.Count; i++)
                {
                    var t = tabs[i];
                    string n = (t.Current.Name ?? "").Trim();
                    bool sel = false;
                    try
                    {
                        if (t.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object p))
                            sel = ((SelectionItemPattern)p).Current.IsSelected;
                    }
                    catch { }
                    sb.Append($"\n  [{i}] '{n}' selected={sel}");
                }
                return sb.ToString();
            }
            catch (Exception ex) { return "ERR " + ex.Message; }
        }, 3000);

    /// <summary>某个 DefView 沿祖先链找到它归属的 ShellTabWindowClass 名字（即标签名）。</summary>
    private static string ViewOwnerNameCore(IntPtr view)
    {
        try
        {
            var el = AutomationElement.FromHandle(view);
            if (el == null) return string.Empty;
            for (int i = 0; i < 10 && el != null; i++)
            {
                string c = el.Current.ClassName ?? string.Empty;
                string n = (el.Current.Name ?? string.Empty).Trim();
                if (c == "ShellTabWindowClass") return n;
                el = TreeWalker.ControlViewWalker.GetParent(el);
            }
        }
        catch { }
        return string.Empty;
    }

    public static string ViewOwnerTabName(IntPtr view) => Runner.Run(() => ViewOwnerNameCore(view), 2000);

    /// <summary>诊断：某个 DefView 的完整祖先链。</summary>
    public static string ViewOwnerTab(IntPtr view)
        => Runner.Run(() =>
        {
            try
            {
                var el = AutomationElement.FromHandle(view);
                if (el == null) return "null";
                var parts = new List<string>();
                for (int i = 0; i < 10 && el != null; i++)
                {
                    string c = el.Current.ClassName ?? "";
                    string n = (el.Current.Name ?? "").Trim();
                    parts.Add($"{c}:'{n}'");
                    if (c == "ShellTabWindowClass") break;
                    el = TreeWalker.ControlViewWalker.GetParent(el);
                }
                return string.Join(" <- ", parts);
            }
            catch (Exception ex) { return "ERR " + ex.Message; }
        }, 3000);

    /// <summary>诊断：某个 DefView 的 UIA 属性（IsOffscreen / 项数 / 前几项名字）。多标签页用来区分激活标签。</summary>
    public static string ViewInfo(IntPtr view)
        => Runner.Run(() =>
        {
            try
            {
                var el = AutomationElement.FromHandle(view);
                if (el == null) return "null";
                bool off = el.Current.IsOffscreen;
                var items = ReadItems(el);
                string first = items.Count > 0 ? items[0].Name : "(none)";
                return $"offscreen={off} items={items.Count} first='{first}'";
            }
            catch (Exception ex) { return "ERR " + ex.Message; }
        }, 3000);

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

                // 先只看文件列表子树（导航窗格里的 List 不算），拿不到再退回整窗口。
                // 这里已经在 Runner 的 job 里，必须用 *Core 版本，不能再嵌套 Runner。
                var scope = ItemsViewScopeCore(hwnd, ActiveTabCore(hwnd)) ?? root;
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

    /// <summary>
    /// 诊断用：递归 dump 一个窗口的完整 UIA 树（名字 / 类名 / 矩形 / 可用 Pattern）。
    /// 用于排查 Seer 预览窗的媒体控件（播放 / 进度 / 静音）暴露情况。
    /// </summary>
    public static string DumpTree(IntPtr hwnd, int maxDepth = 8, int maxNodes = 200)
    {
        return Runner.Run(() =>
        {
            var sb = new System.Text.StringBuilder();
            try
            {
                var root = AutomationElement.FromHandle(hwnd);
                if (root == null) return "null";
                int count = 0;
                void Walk(AutomationElement el, int depth, string pad)
                {
                    if (depth > maxDepth || count >= maxNodes) return;
                    count++;
                    string n = "", c = "", t = "";
                    var pats = new List<string>();
                    try
                    {
                        n = (el.Current.Name ?? string.Empty).Trim();
                        c = el.Current.ClassName ?? string.Empty;
                        t = el.Current.ControlType?.ProgrammaticName ?? "?";
                        var all = el.GetSupportedPatterns();
                        foreach (var p in all) pats.Add(p.ProgrammaticName.Replace("PatternIdentifiers.Pattern", "").Replace("Pattern", ""));
                    }
                    catch { }
                    string rect = "";
                    try
                    {
                        var r = el.Current.BoundingRectangle;
                        rect = r.IsEmpty ? "" : $" [{(int)r.X},{(int)r.Y} {(int)r.Width}x{(int)r.Height}]";
                    }
                    catch { }
                    sb.AppendLine($"{pad}{t}:'{n}' {c}{rect} ({string.Join(",", pats)})");
                    try
                    {
                        var kids = el.FindAll(TreeScope.Children, Condition.TrueCondition);
                        foreach (AutomationElement k in kids) Walk(k, depth + 1, pad + "  ");
                    }
                    catch { }
                }
                Walk(root, 0, "");
                if (count >= maxNodes) sb.AppendLine($"... (截断，共 {count}+ 节点)");
            }
            catch (Exception ex)
            {
                sb.AppendLine("ERR " + ex.Message);
            }
            return sb.ToString();
        }, 8000);
    }
}
