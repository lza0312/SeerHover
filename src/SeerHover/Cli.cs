using System.Diagnostics;
using System.Text;
using System.Windows.Forms;

namespace SeerHover;

/// <summary>
/// 命令行诊断入口（--status / --items / --bench / --up-probe …）。
/// 全部是「独立进程跑一次就退出」的工具，和托盘主流程共享 Native / Uia / HoverResolver 的实现，
/// 所以能用真机复现问题，又不会污染常驻进程。
/// </summary>
internal static class Cli
{
    private static StreamWriter? _tee;

    public static int Run(Func<int> body, string name)
    {
        try
        {
            _tee = new StreamWriter(Path.Combine(Path.GetTempPath(), $"seerhover-{name}.log"), false, Encoding.UTF8)
            { AutoFlush = true };
        }
        catch { _tee = null; }

        try { return body(); }
        catch (Exception ex)
        {
            Out("EXCEPTION: " + ex);
            return 99;
        }
        finally
        {
            try { _tee?.Flush(); _tee?.Dispose(); } catch { }
        }
    }

    private static void Out(string s)
    {
        try { Console.WriteLine(s); } catch { }
        try { _tee?.WriteLine(s); } catch { }
    }

    /// <summary>诊断：--hide，直接让 Seer 收起预览窗（测试/残留窗口清理用）。</summary>
    public static int HideSeer()
    {
        var ipc = new SeerIpc();
        if (!ipc.IsRunning) { Out("seer not running"); return 2; }
        ipc.Hide();
        Thread.Sleep(120);
        Out($"visible after hide: {ipc.IsVisible}");
        ipc.Dispose();
        return 0;
    }

    public static int Status()
    {
        var ipc = new SeerIpc();
        Out("=== SeerHover status ===");
        Out($"Seer running : {ipc.IsRunning}");
        if (ipc.IsRunning)
        {
            Out($"Seer hwnd    : 0x{ipc.Window.ToInt64():X}");
            Out($"Seer version : {ipc.Version}");
            Out($"visible      : {ipc.IsVisible}");
        }
        var cfg = AppConfig.LoadOrCreate();
        Out($"config       : {AppConfig.Path}");
        Out($"dwell        : {cfg.DwellMs} ms, grace {cfg.HideGraceMs} ms, poll {cfg.PollMs} ms");
        Out($"geometry     : {cfg.Geometry.Mode} size={cfg.Geometry.SizeMode} {cfg.Geometry.Width}x{cfg.Geometry.Height}");
        return 0;
    }

    /// <summary>诊断：--up-probe [x y]，报告「这个点双击会不会被判定为空白处」。</summary>
    public static int UpProbe(string[] args)
    {
        int x, y;
        if (args.Length > 2 && int.TryParse(args[1], out int ax) && int.TryParse(args[2], out int ay))
        {
            x = ax; y = ay;
        }
        else
        {
            if (!Native.GetCursorPos(out var p)) { Out("cannot read cursor"); return 2; }
            x = p.X; y = p.Y;
        }
        Out(ExplorerUp.Probe(x, y));
        return 0;
    }

    /// <summary>诊断：--bench [n]，量一下 UIA 扫描 / 缓存命中 / 空闲轮询的单次成本。</summary>
    public static int Bench(string[] args)
    {
        int n = args.Length > 1 && int.TryParse(args[1], out var k) ? Math.Clamp(k, 1, 20) : 5;
        IntPtr win = FindExplorerWindow();
        if (win == IntPtr.Zero) { Out("no explorer window (open one first)"); return 2; }
        Out($"window 0x{win.ToInt64():X} '{Native.TitleOf(win)}'");

        for (int i = 0; i < n; i++)
        {
            var sw = Stopwatch.StartNew();
            var items = Uia.EnumItems(win);
            Out($"uia scan #{i + 1}: {items?.Count ?? -1} items in {sw.Elapsed.TotalMilliseconds:0.0} ms");
        }

        // 拆分：文件列表子树 vs 整窗口（确认「只扫子树」真的省了时间）
        IntPtr view = Native.ItemsViewOf(win);
        Out($"items view: 0x{view.ToInt64():X} class='{Native.ClassOf(view)}'");
        Out($"view uia   : {Uia.ElementInfo(view)}");
        var sub = Uia.ScanScope(view);
        Out($"subtree scan: {sub.Count} items in {sub.Ms:0.0} ms");
        var all = Uia.ScanScope(win);
        Out($"window scan : {all.Count} items in {all.Ms:0.0} ms");

        Native.GetWindowRect(win, out var wr);
        var probe = new Native.POINT(wr.Left + 40, wr.Top + 200);
        ExplorerItemCache.Shared.HitTest(win, probe);   // 预热

        // 单项原始成本：找出每步操作里最贵的那一环
        var items2 = Uia.EnumItems(win);
        var onItem = items2 is { Count: > 0 }
            ? new Native.POINT((items2[0].Rect.Left + items2[0].Rect.Right) / 2,
                               (items2[0].Rect.Top + items2[0].Rect.Bottom) / 2)
            : probe;
        var resolver = new HoverResolver(AppConfig.LoadOrCreate());
        Time("TitleOf(hwnd)", 300, () => Native.TitleOf(win));
        Time("GetWindowRect", 500, () => Native.GetWindowRect(win, out _));
        Time("WindowFromPoint", 500, () => Native.RootAt(probe, out _));
        Time("InfoOf(hwnd)", 500, () => Native.InfoOf(win));
        Time("ItemHit", 300, () => ExplorerItemCache.Shared.HitTest(win, onItem));
        Time("Resolve(点中项)", 300, () => resolver.ResolveCore(onItem, out _));

        var sw2 = Stopwatch.StartNew();
        for (int i = 0; i < 200; i++) ExplorerItemCache.Shared.HitTest(win, probe);
        Out($"cache hit x200: {sw2.Elapsed.TotalMilliseconds:0.0} ms ({sw2.Elapsed.TotalMilliseconds / 200:0.000} ms/次)");

        sw2.Restart();
        for (int i = 0; i < 1000; i++)
        {
            Native.GetCursorPos(out _);
            Native.IsKeyDown(Native.VK_LBUTTON);
        }
        Out($"idle syscalls x1000: {sw2.Elapsed.TotalMilliseconds:0.0} ms ({sw2.Elapsed.TotalMilliseconds / 1000:0.000} ms/次)");
        return 0;
    }

    private static void Time(string label, int n, Action body)
    {
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < n; i++) body();
        Out($"{label,-20} x{n}: {sw.Elapsed.TotalMilliseconds:0.0} ms ({sw.Elapsed.TotalMilliseconds / n:0.0000} ms/次)");
    }

    /// <summary>列出枚举到的列表项，并自检「按各自矩形中心回查」是否命中自己。</summary>
    public static int Items()
    {
        IntPtr win = FindExplorerWindow();
        if (win == IntPtr.Zero) { Out("no explorer window"); return 2; }
        Native.GetWindowRect(win, out var wr);
        Out($"explorer 0x{win.ToInt64():X} rect={wr}");

        var resolver = new HoverResolver(AppConfig.LoadOrCreate());
        Out($"folder: '{resolver.DebugFolderPath(win)}'");

        var items = Uia.EnumItems(win);
        if (items == null) { Out("enum returned null"); return 3; }
        Out($"items={items.Count}");
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            int cx = (it.Rect.Left + it.Rect.Right) / 2;
            int cy = (it.Rect.Top + it.Rect.Bottom) / 2;
            var back = resolver.Resolve(new Native.POINT(cx, cy));
            string verdict = back == null ? "(null)" : (back.Name == it.Name ? "self" : "WRONG:" + back.Name);
            Out($"  [{i,2}] '{it.Name}' rect={it.Rect} center={cx},{cy} -> {verdict}");
        }
        return 0;
    }

    /// <summary>探测图片/视频的原始像素尺寸（验证「原比例最大化」取到的源尺寸）。</summary>
    public static int MediaInfo(string? path)
    {
        if (path == null || !File.Exists(path)) { Out("usage: --media <file>"); return 2; }
        var sw = Stopwatch.StartNew();
        var (w, h) = MediaSize.Probe(path);
        sw.Stop();
        Out($"{Path.GetFileName(path)}: {w}x{h}  ({sw.ElapsedMilliseconds}ms)");
        return w > 0 ? 0 : 3;
    }

    /// <summary>发送指定路径并回读 Seer 实际预览的文件，验证 IPC 载荷是否被采用。</summary>
    public static int IpcTest(string? path)
    {
        var ipc = new SeerIpc();
        if (!ipc.IsRunning) { Out("Seer not running"); return 2; }
        Out($"version={ipc.Version} visible(before)={ipc.IsVisible} tracking={ipc.IsTrackingEnabled}");

        if (path != null && File.Exists(path))
        {
            Out($"invoke: {path}");
            ipc.Invoke(path);
            for (int i = 0; i < 6; i++)
            {
                Thread.Sleep(250);
                Out($"  t+{(i + 1) * 250}ms current='{ipc.GetCurrentFile()}'");
            }
        }
        else
        {
            Out("no valid path given, only reporting current=" + ipc.GetCurrentFile());
        }
        return 0;
    }

    public static int FixTracking()
    {
        var files = SeerSettings.CandidateFiles();
        Out($"candidates={files.Count}");
        foreach (var f in files) Out($"  {f} tracking={SeerSettings.ReadTracking(f)?.ToString() ?? "(none)"}");
        var changed = SeerSettings.DisableTracking();
        foreach (var c in changed) Out("patched: " + c);
        Out(changed.Count > 0 ? "RESULT: patched - 重启 Seer 后生效" : "RESULT: nothing to change");
        return 0;
    }

    public static int KeepWindow()
    {
        var files = SeerSettings.CandidateFiles();
        Out($"candidates={files.Count}");
        foreach (var f in files) Out($"  {f} keep_mode={SeerSettings.ReadKeepMode(f)?.ToString() ?? "(none)"}");
        var changed = SeerSettings.EnableKeepWindow();
        foreach (var c in changed) Out("patched: " + c);
        Out(changed.Count > 0 ? "RESULT: patched - 重启 Seer 后生效" : "RESULT: already enabled");
        return 0;
    }

    public static int RestoreWindow()
    {
        var changed = SeerSettings.DisableKeepWindow();
        foreach (var c in changed) Out("patched: " + c);
        Out(changed.Count > 0 ? "RESULT: 已恢复 Seer 默认（窗口随内容变化），重启 Seer 生效" : "RESULT: nothing to change");
        return 0;
    }

    public static int RestoreTracking()
    {
        var restored = SeerSettings.RestoreBackup();
        foreach (var r in restored) Out("restored: " + r);        Out(restored.Count > 0 ? "RESULT: restored - 重启 Seer 后生效" : "RESULT: no backup found");
        return 0;
    }

    /// <summary>列出 Seer 的所有顶层窗口及其当前预览文件。</summary>
    public static int SeerWindows()
    {
        var ipc = new SeerIpc();
        Native.EnumWindows((h, l) =>
        {
            if (!string.Equals(Native.ProcessNameOf(h), "Seer", StringComparison.OrdinalIgnoreCase)) return true;
            string cls = Native.ClassOf(h);
            if (!cls.Contains("Seer")) return true;
            Native.GetWindowRect(h, out var r);
            var sb = new StringBuilder(400);
            Native.GetWindowTextW(h, sb, sb.Capacity);
            string cur = ipc.GetCurrentFileFor(h);
            Out($"0x{h.ToInt64():X} cls={cls} visible={Native.IsWindowVisible(h)} rect={r} title='{sb}' current='{cur}'");
            return true;
        }, IntPtr.Zero);
        return 0;
    }

    /// <summary>复现「先点选 A，再悬停 B」：A 选中后发送 B，观察 Seer 实际预览谁。</summary>
    public static int TrackTest(string mode)
    {
        IntPtr win = FindExplorerWindow();
        if (win == IntPtr.Zero) { Out("no explorer window"); return 2; }
        var items = Uia.EnumItems(win);
        if (items == null || items.Count < 6) { Out("not enough items"); return 2; }

        string folder = new HoverResolver(AppConfig.LoadOrCreate()).DebugFolderPath(win);
        string a = items[1].Name, b = items[5].Name;
        Out($"folder='{folder}' mode={mode}");
        Out($"select A='{a}'  then invoke B='{b}'");

        Native.SetForegroundWindow(win);
        Thread.Sleep(250);
        Out($"select A -> {Uia.SelectItem(win, a)}");
        Thread.Sleep(300);

        var ipc = new SeerIpc();
        string pathB = Path.Combine(folder, b);
        string pathA = Path.Combine(folder, a);
        Out($"tracking={ipc.IsTrackingEnabled}");

        switch (mode)
        {
            case "twice":
                ipc.Invoke(pathB);
                Thread.Sleep(700);
                ipc.Invoke(pathB);
                break;
            case "pin":
                ipc.InvokePin(pathB);
                break;
            case "sep":
                ipc.Send(5001, pathB);
                break;
            case "standalone":
                ipc.InvokeStandalone(pathB);
                break;
            default:
                ipc.Invoke(pathB);
                break;
        }
        Out($"invoked B: {pathB}");

        for (int i = 0; i < 8; i++)
        {
            Thread.Sleep(250);
            string cur = ipc.GetCurrentFile();
            string who = PrefixMatch(cur, pathB) ? "B(正确)" : PrefixMatch(cur, pathA) ? "A(跳回选中项)" : "其他";
            Out($"  t+{(i + 1) * 250}ms {who} '{cur}'");
        }
        ipc.Hide();
        return 0;
    }

    /// <summary>
    /// 复现用户场景：先点选 A，再悬停 B，检查 Seer 预览的是不是 B。
    /// 需要托盘进程在跑（由它做悬停触发）。
    /// </summary>
    public static int E2ESelected()
    {
        var resolver0 = new HoverResolver(AppConfig.LoadOrCreate());
        var (win, folder, fileCount) = FindFileWindow(resolver0);
        if (win == IntPtr.Zero)
        {
            Out("no explorer window showing a folder with files（请打开一个含文件的文件夹）");
            return 2;
        }
        Out($"window 0x{win.ToInt64():X} folder='{folder}' files={fileCount}");

        var items = Uia.EnumItems(win);
        if (items == null || items.Count < 8) { Out("not enough items"); return 2; }

        // 只挑真实存在、且当前可见（没被滚动到视口外）的文件项
        Native.GetWindowRect(win, out var wr);
        var files = items
            .Where(i => File.Exists(Path.Combine(folder, i.Name)))
            .Where(i => i.Rect.Top > wr.Top + 60 && i.Rect.Bottom < wr.Bottom - 10 &&
                        i.Rect.Left > wr.Left && i.Rect.Right < wr.Right)
            .ToList();
        if (files.Count < 3) { Out("not enough visible file items"); return 2; }

        var targetB = files[2];
        var targetA = items.First(i => !string.Equals(i.Name, targetB.Name, StringComparison.OrdinalIgnoreCase));
        string a = targetA.Name, b = targetB.Name;
        string pathB = Path.Combine(folder, b);

        Native.SetForegroundWindow(win);
        Thread.Sleep(250);
        Out($"select A='{a}' -> {Uia.SelectItem(win, a)}");
        Thread.Sleep(200);

        var t = targetB;
        int cx = (t.Rect.Left + t.Rect.Right) / 2;
        int cy = (t.Rect.Top + t.Rect.Bottom) / 2;
        Out($"hover B='{b}' at {cx},{cy}");

        Native.GetCursorPos(out var saved);
        Native.SetCursorPos(cx + 260, cy + 220);
        Thread.Sleep(600);
        Native.SetCursorPos(cx, cy);
        Thread.Sleep(1800);

        var ipc = new SeerIpc();
        string cur = ipc.GetCurrentFile();
        bool ok = PrefixMatch(cur, pathB);
        Out($"seer current='{cur}' want='{pathB}' -> {(ok ? "B(正确)" : "WRONG")}");

        Native.SetCursorPos(saved.X, saved.Y);
        Thread.Sleep(500);
        ipc.Hide();
        Out(ok ? "RESULT: PASS - 悬停预览显示的是悬停的文件" : "RESULT: FAIL - 预览的不是悬停文件");
        return ok ? 0 : 3;
    }

    private static bool PrefixMatch(string got, string expect)
    {
        string g = got.Replace('/', '\\');
        string e = expect.Replace('/', '\\');
        if (g.Length == 0) return false;
        return e.StartsWith(g, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>连续输出光标下的识别结果，用于人工校验准确率。</summary>
    public static int Probe()
    {
        var cfg = AppConfig.LoadOrCreate();
        var resolver = new HoverResolver(cfg);
        Out("probe mode: 把鼠标移到文件上（Ctrl+C 结束）");
        string? last = null;
        while (true)
        {
            Native.GetCursorPos(out var p);
            var t = resolver.Resolve(p);
            string now = t == null ? "(none)" : t.Path;
            if (now != last)
            {
                last = now;
                Out($"{DateTime.Now:HH:mm:ss.fff} {p} {now}");
            }
            Thread.Sleep(60);
        }
    }

    private static string SizeOf(string line)
    {
        var m = System.Text.RegularExpressions.Regex.Match(line, @"(\d+)x(\d+) @");
        return m.Success ? m.Groups[1].Value + "x" + m.Groups[2].Value : line;
    }

    /// <summary>量化「跳动」：悬停后连续采样窗口尺寸，记录变化序列。需要托盘进程在跑。</summary>
    public static int GeomMonitor()
    {
        var resolver = new HoverResolver(AppConfig.LoadOrCreate());
        var (win, folder, _) = FindFileWindow(resolver);
        if (win == IntPtr.Zero) { Out("no explorer window with files"); return 2; }

        var items = Uia.EnumItems(win);
        if (items == null) { Out("no items"); return 2; }

        Native.GetWindowRect(win, out var wr);
        var files = items
            .Where(i => File.Exists(Path.Combine(folder, i.Name)))
            .Where(i => i.Rect.Top > wr.Top + 60 && i.Rect.Bottom < wr.Bottom - 10 &&
                        i.Rect.Left > wr.Left && i.Rect.Right < wr.Right)
            .ToList();
        if (files.Count == 0) { Out("no visible file item"); return 2; }

        var t = files[Math.Min(2, files.Count - 1)];
        int cx = (t.Rect.Left + t.Rect.Right) / 2;
        int cy = (t.Rect.Top + t.Rect.Bottom) / 2;

        var ipc = new SeerIpc();
        ipc.Hide();
        Thread.Sleep(300);

        Native.GetCursorPos(out var saved);
        // 先把光标挪开再挪回来，否则托盘会认为"光标没动"而不重新触发
        Native.SetCursorPos(cx + 260, cy + 220);
        Thread.Sleep(700);
        Native.SetCursorPos(cx, cy);

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 4000 && !ipc.IsVisibleFast) Thread.Sleep(5);
        if (!ipc.IsVisibleFast) { Out("seer did not appear（托盘程序没在跑？）"); Native.SetCursorPos(saved.X, saved.Y); return 3; }

        var seq = new List<string>();
        string last = "";
        var t2 = Stopwatch.StartNew();
        while (t2.ElapsedMilliseconds < 5000)
        {
            var r = ipc.GetRect();
            if (!r.IsEmpty)
            {
                string vis = ipc.IsVisibleFast ? "可见" : "隐藏";
                string s = $"{vis} {r.Width}x{r.Height} @({r.Left},{r.Top})";
                if (s != last) { seq.Add($"+{t2.ElapsedMilliseconds,4}ms  {s}"); last = s; }
            }
            Thread.Sleep(8);
        }

        Out($"出现耗时 {sw.ElapsedMilliseconds}ms，之后 2.5s 内的变化序列：");
        foreach (var s in seq) Out("   " + s);

        // 只把「尺寸变化」算作跳动；位置变化可能只是光标经过不同文件触发了新预览
        int sizeChanges = 0, posChanges = 0;
        for (int i = 1; i < seq.Count; i++)
        {
            if (SizeOf(seq[i - 1]) != SizeOf(seq[i])) sizeChanges++; else posChanges++;
        }

        Out("");
        Out($"尺寸变化 {sizeChanges} 次，位置变化 {posChanges} 次");
        Out(sizeChanges == 0
            ? "RESULT: PASS - 预览尺寸全程未变"
            : $"RESULT: FAIL - 尺寸变化 {sizeChanges} 次，仍在跳");

        Native.SetCursorPos(saved.X, saved.Y);
        return sizeChanges == 0 ? 0 : 3;
    }

    /// <summary>探测 Seer 是否接受外部设定的窗口尺寸：每次只设一次，分时读回看它会不会改回去。</summary>
    public static int GeomProbe(string[] args)
    {
        var resolver = new HoverResolver(AppConfig.LoadOrCreate());
        var (win, folder, _) = FindFileWindow(resolver);
        if (win == IntPtr.Zero) { Out("no explorer window with files（请打开一个含文件的文件夹）"); return 2; }

        var items = Uia.EnumItems(win);
        if (items == null) { Out("no items"); return 2; }

        string? path = null;
        foreach (var it in items)
        {
            string c = Path.Combine(folder, it.Name);
            if (File.Exists(c)) { path = c; break; }
        }
        if (path == null) { Out("no file item"); return 2; }

        var ipc = new SeerIpc();
        bool early = args is { Length: > 1 } && args[1] == "early";
        ipc.Invoke(path);
        Out($"invoke '{path}'");

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 3000 && !ipc.IsVisible) Thread.Sleep(15);
        if (!ipc.IsVisibleFast) { Out("seer window not visible"); return 3; }

        Out($"appeared {ipc.GetRect()}");
        Thread.Sleep(1000);
        var natural = ipc.GetRect();
        Out($"settled  {natural}   <- Seer 自己算出的尺寸");
        Out("");

        if (early)
        {
            // 窗口一出现就设目标尺寸，看内容加载完会不会覆盖
            const int tw = 900, th = 700;
            var r = ipc.GetRect();
            Native.SetWindowPos(ipc.Window, IntPtr.Zero, r.Left, r.Top, tw, th,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            Out($"early set {tw}x{th} @0ms -> {ipc.GetRect()}");
            int lastW = 0, lastH = 0;
            for (int i = 1; i <= 14; i++)
            {
                Thread.Sleep(150);
                var c = ipc.GetRect();
                if (c.Width != lastW || c.Height != lastH)
                {
                    Out($"  +{i * 150}ms {c.Width}x{c.Height} {(c.Width == tw && c.Height == th ? "(目标)" : "(被改)")}");
                    lastW = c.Width;
                    lastH = c.Height;
                }
            }
            ipc.Hide();
            return 0;
        }

        var trials = new (int W, int H)[] { (900, 620), (900, 500), (1100, 700), (1200, 800), (700, 900) };
        foreach (var (w, h) in trials)
        {
            if (!ipc.IsWindowAlive) { Out("window gone"); break; }
            var before = ipc.GetRect();
            Native.SetWindowPos(ipc.Window, IntPtr.Zero, before.Left, before.Top, w, h,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);

            var r0 = ipc.GetRect();
            Thread.Sleep(120);
            var r1 = ipc.GetRect();
            Thread.Sleep(180);
            var r2 = ipc.GetRect();
            Thread.Sleep(400);
            var r3 = ipc.GetRect();

            string verdict = (r3.Width == w && r3.Height == h) ? "接受" : "被改回";
            Out($"set {w}x{h} -> now {r0.Width}x{r0.Height} | +120ms {r1.Width}x{r1.Height} | +300ms {r2.Width}x{r2.Height} | +700ms {r3.Width}x{r3.Height}  [{verdict}]");
        }

        ipc.Hide();
        return 0;
    }

    /// <summary>自动验证：找资源管理器窗口 → 网格扫描 → 报告识别到的路径。</summary>
    public static int SelfTest()
    {
        var cfg = AppConfig.LoadOrCreate();
        var resolver = new HoverResolver(cfg);

        IntPtr win = FindExplorerWindow();
        bool opened = false;
        if (win == IntPtr.Zero)
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (!Directory.Exists(folder)) folder = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            Out("no explorer window, opening: " + folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
            opened = true;
            for (int i = 0; i < 45 && win == IntPtr.Zero; i++)
            {
                Thread.Sleep(120);
                win = FindExplorerWindow();
            }
        }

        if (win == IntPtr.Zero)
        {
            Out("RESULT: FAIL - no usable explorer window");
            return 2;
        }

        Native.SetForegroundWindow(win);
        Thread.Sleep(400);
        Native.GetWindowRect(win, out var wr);
        Out($"explorer hwnd=0x{win.ToInt64():X} rect={wr}");
        Out(Uia.DumpListItems(win));

        Native.GetCursorPos(out var saved);
        string? found = null;
        int tried = 0;
        int occluded = 0;

        for (int y = wr.Top + 90; y < wr.Bottom - 40 && found == null; y += 45)
        {
            for (int x = wr.Left + 70; x < wr.Right - 40; x += 110)
            {
                Native.SetCursorPos(x, y);
                Thread.Sleep(20);

                // 被别的窗口挡住的位置直接跳过（真实使用中不会出现）
                var under = Native.RootOf(Native.WindowFromPoint(new Native.POINT(x, y)));
                if (under != win)
                {
                    occluded++;
                    continue;
                }

                HoverTarget? t;
                string why;
                try { t = resolver.ResolveCore(new Native.POINT(x, y), out why); }
                catch (Exception ex) { Out($"resolve threw: {ex.Message}"); t = null; why = "exception"; }
                tried++;
                if (tried <= 4) Out($"  probe {x},{y} -> {why}");
                if (t != null && File.Exists(t.Path))
                {
                    found = t.Path;
                    Out($"hit at {x},{y} -> {t.Path}");
                    break;
                }
                if (tried > 240) break;
            }
        }

        Native.SetCursorPos(saved.X, saved.Y);

        if (opened)
        {
            try { Native.PostMessageW(win, 0x0010, IntPtr.Zero, IntPtr.Zero); } catch { }
        }

        if (found != null)
        {
            Out($"RESULT: PASS - real path resolved, probes={tried}, occluded(skipped)={occluded}");
            return 0;
        }
        Out($"RESULT: FAIL - no file resolved after {tried} probes (skipped {occluded} occluded points)");
        return 3;
    }

    public static int Diag()
    {
        var cfg = AppConfig.LoadOrCreate();
        var resolver = new HoverResolver(cfg);

        Out("=== explorer windows ===");
        Native.EnumWindows((h, l) =>
        {
            if (!Native.IsWindowVisible(h)) return true;
            string cls = Native.ClassOf(h);
            if (cls != "CabinetWClass" && cls != "Progman" && cls != "WorkerW") return true;
            Native.GetWindowRect(h, out var r);
            var sb = new StringBuilder(512);
            Native.GetWindowTextW(h, sb, sb.Capacity);
            Out($"hwnd=0x{h.ToInt64():X} class={cls} rect={r} title='{sb}'");
            return true;
        }, IntPtr.Zero);

        IntPtr win = FindExplorerWindow();
        if (win == IntPtr.Zero) { Out("no explorer window"); return 2; }
        Native.SetForegroundWindow(win);
        Thread.Sleep(350);
        Native.GetWindowRect(win, out var wr);
        Out($"target hwnd=0x{win.ToInt64():X} rect={wr}");
        Out($"folder path: '{resolver.DebugFolderPath(win)}'");

        Out("=== uia samples ===");
        int shown = 0;
        for (int y = wr.Top + 80; y < wr.Bottom - 30 && shown < 12; y += 40)
        {
            for (int x = wr.Left + 60; x < wr.Right - 30 && shown < 12; x += 90)
            {
                IntPtr under = Native.WindowFromPoint(new Native.POINT(x, y));
                IntPtr root = Native.RootOf(under);
                string? info = Uia.Describe(x, y);
                if (info != null)
                {
                    Out($"{x},{y} under={Native.ClassOf(under)}/{Native.ProcessNameOf(root)} uia: {info}");
                    shown++;
                }
            }
        }
        if (shown == 0) Out("UIA returned nothing in the whole scan area");
        return 0;
    }

    /// <summary>端到端：把光标放到某个文件项上，检查 Seer 是否被悬停唤起、离开后是否收窗。需要托盘进程在跑。</summary>
    public static int E2E()
    {
        IntPtr win = FindExplorerWindow();
        if (win == IntPtr.Zero) { Out("no explorer window"); return 2; }
        var items = Uia.EnumItems(win);
        if (items == null || items.Count == 0) { Out("no items enumerated"); return 2; }

        var target = items.Where(i => i.Name.Contains('.')).OrderByDescending(i => i.Rect.Width * i.Rect.Height).FirstOrDefault()
                     ?? items[0];
        int cx = (target.Rect.Left + target.Rect.Right) / 2;
        int cy = (target.Rect.Top + target.Rect.Bottom) / 2;
        Out($"target item '{target.Name}' rect={target.Rect} center={cx},{cy}");

        var ipc = new SeerIpc();
        Out($"seer running={ipc.IsRunning} visible(before)={ipc.IsVisible}");

        Native.GetCursorPos(out var saved);
        Native.SetCursorPos(cx + 260, cy + 220);
        Thread.Sleep(600);
        Native.SetCursorPos(cx, cy);
        Out("cursor placed, waiting 1600ms for dwell + geometry...");
        Thread.Sleep(1600);

        bool visible = ipc.IsVisibleFast;
        var rect = ipc.GetRect();
        Out($"visible(after hover)={visible} seerRect={rect}");

        // 移到窗口外（不是文件项）再看是否收窗
        Native.SetCursorPos(20, 20);
        Out("cursor moved outside, waiting 900ms for hide grace...");
        Thread.Sleep(900);
        bool hidden = !ipc.IsVisibleFast;
        Out($"hidden(after leave)={hidden}");

        Native.SetCursorPos(saved.X, saved.Y);
        ipc.Hide();
        bool pass = visible && hidden;
        Out(pass ? "RESULT: PASS - hover triggered preview and leaving dismissed it"
                 : $"RESULT: FAIL - visible={visible} hiddenAfterLeave={hidden}");
        return pass ? 0 : 3;
    }

    public static int UiaTree()
    {
        IntPtr win = FindExplorerWindow();
        if (win == IntPtr.Zero) { Out("no explorer window"); return 2; }
        Out($"explorer hwnd=0x{win.ToInt64():X}");
        Out(Uia.DumpListItems(win));
        return 0;
    }

    /// <summary>挑一个确实显示文件（而不是虚拟目录/空目录）的资源管理器窗口，供自动用例使用。</summary>
    private static (IntPtr Hwnd, string Folder, int FileCount) FindFileWindow(HoverResolver resolver)
    {
        IntPtr best = IntPtr.Zero;
        string bestFolder = string.Empty;
        int bestCount = -1;

        Native.EnumWindows((h, l) =>
        {
            if (!Native.IsWindowVisible(h)) return true;
            if (Native.ClassOf(h) != "CabinetWClass") return true;
            Native.GetWindowRect(h, out var r);
            if (r.Width < 400 || r.Height < 300) return true;

            string folder = resolver.DebugFolderPath(h);
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return true;

            int count;
            try { count = Directory.GetFiles(folder).Length; }
            catch { return true; }

            if (count > bestCount) { bestCount = count; best = h; bestFolder = folder; }
            return true;
        }, IntPtr.Zero);

        return (best, bestFolder, Math.Max(0, bestCount));
    }

    private static IntPtr FindExplorerWindow()
    {
        IntPtr result = IntPtr.Zero;
        Native.EnumWindows((h, l) =>
        {
            if (!Native.IsWindowVisible(h)) return true;
            if (Native.ClassOf(h) != "CabinetWClass") return true;
            Native.GetWindowRect(h, out var r);
            if (r.Width < 400 || r.Height < 300) return true;
            result = h;
            return false;
        }, IntPtr.Zero);
        return result;
    }

    public static int InstallStartup()
    {
        string exe = Environment.ProcessPath ?? Application.ExecutablePath;
        string dir = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory;
        string link = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "SeerHover.lnk");
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) { Out("WScript.Shell unavailable"); return 4; }
            dynamic shell = Activator.CreateInstance(t)!;
            dynamic sc = shell.CreateShortcut(link);
            sc.TargetPath = exe;
            sc.WorkingDirectory = dir;
            sc.WindowStyle = 7;
            sc.Description = "Seer 悬停预览";
            sc.Save();
            Out("startup shortcut created: " + link);
            return 0;
        }
        catch (Exception ex)
        {
            Out("failed: " + ex.Message);
            return 4;
        }
    }

    public static int UninstallStartup()
    {
        string link = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "SeerHover.lnk");
        try
        {
            if (File.Exists(link)) File.Delete(link);
            Out("startup shortcut removed");
            return 0;
        }
        catch (Exception ex)
        {
            Out("failed: " + ex.Message);
            return 4;
        }
    }
}
