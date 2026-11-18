using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
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

    /// <summary>诊断：dump Seer 预览窗口的 UIA 元素树（排查媒体控件暴露情况）。--uia-seer</summary>
    public static int UiaSeer()
    {
        var ipc = new SeerIpc();
        if (!ipc.IsRunning) { Out("seer not running"); return 2; }
        IntPtr w = ipc.Window;
        Out($"hwnd=0x{w:X} visible={Native.IsWindowVisible(w)} rect={ipc.GetRect()}");
        Out(Uia.DumpTree(w));
        ipc.Dispose();
        return 0;
    }

    /// <summary>把窗口自身绘制进位图（被遮挡也有效）。失败返回 false。</summary>
    private static bool PrintWindow(IntPtr hwnd, Bitmap bmp)
    {
        try
        {
            using var g = Graphics.FromImage(bmp);
            IntPtr hdc = g.GetHdc();
            try { return Native.PrintWindow(hwnd, hdc, 2 /*PW_RENDERFULLCONTENT*/); }
            finally { g.ReleaseHdc(hdc); }
        }
        catch { return false; }
    }

    /// <summary>诊断：调用任意 Seer V2 RPC 方法并打印原始 result。用法：--v2-call &lt;method&gt; [json参数]</summary>
    public static int V2Call(string[] args)
    {
        if (args.Length < 2) { Out("usage: --v2-call <method> [json]"); return 2; }
        string method = args[1];
        Dictionary<string, object?>? p = null;
        if (args.Length > 2)
        {
            try { p = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(args[2]); }
            catch (Exception ex) { Out("bad json: " + ex.Message); return 2; }
        }
        using var v2 = new SeerV2();
        if (!v2.EnsureConnected()) { Out("v2 connect failed"); return 3; }
        Out(v2.RpcRaw(method, p) ?? "null");
        return 0;
    }

    public static int Status()
    {
        var ipc = new SeerIpc();
        Out("=== SeerHover status ===");
        using (var ico16 = Program.AppIcon(16))
        using (var ico64 = Program.AppIcon(64))
            Out($"app icon     : tray {ico16.Width}x{ico16.Height}, window {ico64.Width}x{ico64.Height}");
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

    /// <summary>V2 Named Pipe 自检：连接、读状态、带 rect 预览、回读几何、关闭。</summary>
    public static int V2Test(string? path)
    {
        using var v2 = new SeerV2();
        if (!v2.EnsureConnected()) { Out("v2 unavailable: " + v2.LastError); return 2; }
        var info = v2.GetInfo();
        Out($"v2 connected: app={info?.AppVersion} versionInt={info?.VersionInt} licensed={info?.Licensed}");
        Out($"tracking(v2)={v2.IsTracking()}");

        var rect = new Native.RECT { Left = 320, Top = 180, Right = 1320, Bottom = 880 }; // 1000x700
        if (path != null && File.Exists(path))
        {
            Out($"preview.open rect=({rect.Left},{rect.Top}) {rect.Width}x{rect.Height} path={path}");
            if (!v2.PreviewOpen(path, rect)) { Out("preview.open FAILED: " + v2.LastError); return 3; }
            Thread.Sleep(600);
            var st = v2.PreviewState();
            Out($"state: visible={st?.Visible} path='{st?.Path}' rect={st?.Rect}");
            v2.PreviewClose();
            Out("closed");
        }
        return 0;
    }

    /// <summary>诊断当前光标位置的完整解析链路（多标签页定位问题用）。</summary>
    public static int ResolveAt()
    {
        var cursor = new Native.POINT();
        Native.GetCursorPos(out cursor);
        Out($"cursor=({cursor.X},{cursor.Y})");
        IntPtr root = Native.RootAt(cursor, out _);
        Out($"root=0x{root.ToInt64():X} class='{Native.ClassOf(root)}' title='{Native.TitleOf(root)}'");
        var tab = Uia.ActiveTab(root);
        Out($"selectedTab=[{tab.Index}] '{tab.Name}'");
        var resolver = new HoverResolver(AppConfig.LoadOrCreate());
        Out($"folder(q)={resolver.DebugFolderPath(root)}");
        Out("tabs: " + (Uia.TabItems(root) ?? "(null)").Replace("\n", " | "));
        Out("shell: " + (Uia.ShellPaths(root) ?? "(null)").Replace("\n", " | "));
        var views = Native.FindAllClass(root, 8, "SHELLDLL_DefView");
        for (int i = 0; i < views.Count; i++)
            Out($"view[{i}]=0x{views[i].ToInt64():X} owner='{Uia.ViewOwnerTabName(views[i])}'");
        var item = ExplorerItemCache.Shared.HitTest(root, cursor);
        Out($"item={item?.Name ?? "(null)"} rect={item?.Rect}");
        var target = resolver.ResolveCore(cursor, root, out string why);
        Out($"resolve: why={why} target={target}");
        if (target != null) Out($"  path exists={File.Exists(target.Path)} dir={Directory.Exists(target.Path)}");
        return 0;
    }

    /// <summary>诊断：单独渲染一段 WrapLabel，验证折行绘制是否完整。</summary>
    public static int WrapTest(string[] args)
    {
        if (args.Length < 2) { Out("usage: --wrap-test <out.png> [text]"); return 2; }
        string text = args.Length > 2 ? args[2] : "设置窗口和托盘图标右键菜单的整体大小（字号、间距一起缩放）。拖动后立即预览，点「应用 / 确认」长期生效。";
        const int w = 357;
        using var label = new WrapLabel
        {
            Text = text,
            Font = new Font("Microsoft YaHei UI", 8.5f),
            ForeColor = Color.Gray,
            Width = w
        };
        int h = label.MeasureHeight(w);
        label.Height = h;
        Out($"h={h} lines={string.Join(" | ", label.Lines(w))}");
        using var bmp = new Bitmap(w, h);
        label.DrawToBitmap(bmp, new Rectangle(0, 0, w, h));
        bmp.Save(args[1], ImageFormat.Png);
        Out("saved: " + args[1]);
        return 0;
    }

    /// <summary>诊断：验证放大副本生成结果。--upscale <图片> <w> <h> [复制到的png]</summary>
    public static int Upscale(string[] args)
    {
        if (args.Length < 4 || !File.Exists(args[1]) ||
            !int.TryParse(args[2], out int w) || !int.TryParse(args[3], out int h))
        {
            Out("usage: --upscale <file> <w> <h> [copy-to.png]");
            return 2;
        }
        var src = MediaSize.Probe(args[1]);
        string? made = PreviewUpscaler.Get(args[1], w, h);
        Out($"source   : {src.W}x{src.H}");
        if (made == null)
        {
            Out("upscale  : not needed (source already >= target) or failed");
            return 0;
        }
        try
        {
            using var img = Image.FromFile(made);
            Out($"upscaled : {made}  {img.Width}x{img.Height}  {new FileInfo(made).Length} bytes");
        }
        catch (Exception ex) { Out("inspect failed: " + ex.Message); }
        if (args.Length > 4)
        {
            try { File.Copy(made, args[4], true); Out("copied   : " + args[4]); }
            catch (Exception ex) { Out("copy failed: " + ex.Message); }
        }
        return 0;
    }

    /// <summary>诊断：裁剪 PNG，便于放大检查文字。--crop <in> <out> x y w h</summary>
    public static int Crop(string[] args)
    {
        if (args.Length < 7) { Out("usage: --crop <in> <out> x y w h"); return 2; }
        try
        {
            using var src = new Bitmap(args[1]);
            int x = int.Parse(args[3]), y = int.Parse(args[4]), w = int.Parse(args[5]), h = int.Parse(args[6]);
            x = Math.Clamp(x, 0, Math.Max(0, src.Width - 1));
            y = Math.Clamp(y, 0, Math.Max(0, src.Height - 1));
            w = Math.Clamp(w, 1, src.Width - x);
            h = Math.Clamp(h, 1, src.Height - y);
            using var dst = new Bitmap(w * 2, h * 2);
            using (var g = Graphics.FromImage(dst))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, new Rectangle(0, 0, dst.Width, dst.Height), new Rectangle(x, y, w, h), GraphicsUnit.Pixel);
            }
            dst.Save(args[2], ImageFormat.Png);
            Out("saved: " + args[2]);
        }
        catch (Exception ex) { Out("crop failed: " + ex.Message); return 3; }
        return 0;
    }

    /// <summary>诊断：弹出设置窗并截图，检查文字是否被截断。用法：--settings-shot [out.png]</summary>
    public static int SettingsShot(string[] args)
    {
        string outPath = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "seer-settings.png");
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        int page = args.Length > 2 && int.TryParse(args[2], out int pi) ? pi : 0;
        var form = new SettingsForm(AppConfig.LoadOrCreate(), new SettingsHost()) { TopMost = true };
        form.SelectPageForDiagnostic(page);
        var timer = new System.Windows.Forms.Timer { Interval = 1200 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                if (Environment.GetEnvironmentVariable("SEER_DEBUG_ROWS") == "1")
                    form.DumpRows(page);
                using var bmp = new Bitmap(Math.Max(1, form.Width), Math.Max(1, form.Height));
                Native.SetForegroundWindow(form.Handle);
                Thread.Sleep(200);
                // 优先 PrintWindow：设置窗可能被别的置顶窗口挡住，直接抓屏幕会拍到别人的窗
                if (!PrintWindow(form.Handle, bmp)) 
                {
                    using (var g = Graphics.FromImage(bmp))
                        g.CopyFromScreen(form.Left, form.Top, 0, 0, bmp.Size);
                }
                bmp.Save(outPath, ImageFormat.Png);
                Out("saved: " + outPath);
            }
            catch (Exception ex) { Out("capture failed: " + ex.Message); }
            timer.Dispose();
            form.Close();
        };
        form.Shown += (_, _) => timer.Start();
        Application.Run(form);
        return 0;
    }

    /// <summary>诊断：打开媒体预览、把鼠标移进窗口让 Seer 原控制条出现，截底部条。用法：--bar-shot &lt;file&gt; [out.png] [stripH]</summary>
    public static int BarShot(string[] args)
    {
        if (args.Length < 2 || !File.Exists(args[1])) { Out("usage: --bar-shot <file> [out.png] [stripH]"); return 2; }
        string outPath = args.Length > 2 ? args[2] : Path.Combine(Path.GetTempPath(), "seer-bar.png");
        int stripH = args.Length > 3 && int.TryParse(args[3], out int sh) ? sh : 160;
        using var v2 = new SeerV2();
        if (!v2.EnsureConnected()) { Out("v2 unavailable: " + v2.LastError); return 2; }
        if (!v2.PreviewOpen(args[1])) { Out("preview.open failed: " + v2.LastError); return 3; }
        Thread.Sleep(1200);
        var st = v2.PreviewState();
        var r = st?.Rect ?? new Native.RECT();
        IntPtr w = new SeerIpc().Window;
        Native.GetWindowRect(w, out var wr);
        Out($"state visible={st?.Visible} rect={r} hwnd=0x{w:X} winRect={wr}");
        if (w == IntPtr.Zero) { v2.PreviewClose(); return 4; }
        if (r.IsEmpty) r = wr;
        // 鼠标移到预览窗口底部边缘内侧（不压边框）并持续小幅晃动，直到截图前一刻
        int cx = (r.Left + r.Right) / 2, cy = r.Bottom - 18;
        Native.SetCursorPos(cx - 60, cy);
        for (int i = 0; i < 20; i++)
        {
            Native.SetCursorPos(cx - 60 + (i * 15) % 120, cy + (i % 2) * 3 - 1);
            Thread.Sleep(80);
            if (i == 14) Thread.Sleep(300);   // 中途停一下让条弹出
        }
        try
        {
            int w2 = r.Width, h2 = Math.Min(stripH, r.Height);
            using var bmp = new Bitmap(w2, h2);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(r.Left, r.Bottom - h2, 0, 0, bmp.Size);
        bmp.Save(outPath, ImageFormat.Png);
        Out($"saved: {outPath} ({w2}x{h2}) copy");

        // 静止 3.5s 后再截一张：确认原控制条是否空闲自动隐藏
        Thread.Sleep(3500);
        string idlePath = Path.ChangeExtension(outPath, ".idle.png");
        using (var bmp2 = new Bitmap(w2, h2))
        using (var g2 = Graphics.FromImage(bmp2))
        {
            g2.CopyFromScreen(r.Left, r.Bottom - h2, 0, 0, bmp2.Size);
            bmp2.Save(idlePath, ImageFormat.Png);
        }
        Out("saved idle: " + idlePath);
        }
        catch (Exception ex) { Out("capture failed: " + ex.Message); }
        v2.PreviewClose();
        return 0;
    }

    /// <summary>诊断：不碰 v2 RPC——假设预览已被别的命令打开，只晃鼠标到底部并截底部条。用法：--overlay-probe [out.png] [stripH]</summary>
    public static int OverlayProbe(string[] args)
    {
        string outPath = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "overlay-probe.png");
        int stripH = args.Length > 2 && int.TryParse(args[2], out int sh) ? sh : 300;
        IntPtr w = new SeerIpc().Window;
        if (w == IntPtr.Zero || !Native.IsWindowVisible(w)) { Out("no visible seer window"); return 4; }
        Native.GetWindowRect(w, out var r);
        Out($"winRect={r}");
        int cx = (r.Left + r.Right) / 2, cy = r.Bottom - 18;
        Native.SetCursorPos(cx - 60, cy);
        for (int i = 0; i < 20; i++)
        {
            Native.SetCursorPos(cx - 60 + (i * 15) % 120, cy + (i % 2) * 3 - 1);
            Thread.Sleep(80);
        }
        // 默认截窗口底部条；也可自定义屏幕矩形：--overlay-probe out stripH x y w h
        int capX = r.Left, capY = r.Bottom - Math.Min(stripH, r.Height);
        int capW = r.Width, capH = Math.Min(stripH, r.Height);
        if (args.Length >= 7 && int.TryParse(args[3], out int ax) && int.TryParse(args[4], out int ay)
            && int.TryParse(args[5], out int aw) && int.TryParse(args[6], out int ah))
        { capX = ax; capY = ay; capW = aw; capH = ah; }
        using var bmp = new Bitmap(capW, capH);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(capX, capY, 0, 0, bmp.Size);
        bmp.Save(outPath, ImageFormat.Png);
        Out($"saved: {outPath} ({capW}x{capH} @ {capX},{capY})");
        return 0;
    }

    /// <summary>诊断：枚举音频会话，看 Seer 的会话在哪。用法：--audio-probe</summary>
    public static int AudioProbe()
    {
        Out(AudioSession.Probe());
        Out("SetVolume(0.5): " + (AudioSession.SetVolume(0.5f) ? "ok" : "FAILED (no session)"));
        Out(AudioSession.Probe());
        return 0;
    }

    /// <summary>诊断：开预览→晃鼠标到底部让原控制条出现→跑锚点检测算法并输出全部聚簇报告。
    /// 用法：--anchor-diag &lt;file&gt; [tag] [w h]（w/h 指定窗口尺寸模拟竖版等窄窗口）</summary>
    public static int AnchorDiag(string[] args)
    {
        if (args.Length < 2 || !File.Exists(args[1])) { Out("usage: --anchor-diag <file> [tag] [w h]"); return 2; }
        string tag = args.Length > 2 ? args[2] : "x";
        Native.RECT? rect = null;
        if (args.Length > 4 && int.TryParse(args[3], out int rw) && int.TryParse(args[4], out int rh))
            rect = new Native.RECT { Left = 2600, Top = 300, Right = 2600 + rw, Bottom = 300 + rh };
        string dump = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\work\anchor-" + tag + ".png"));
        using var v2 = new SeerV2();
        if (!v2.EnsureConnected()) { Out("v2 unavailable: " + v2.LastError); return 2; }
        if (!v2.PreviewOpen(args[1], rect)) { Out("preview.open failed: " + v2.LastError); return 3; }
        try
        {
            Thread.Sleep(1200);
            var st = v2.PreviewState();
            if (st is not { Visible: true } || st.Value.Rect is not { } sr || sr.IsEmpty) { Out("no state rect"); return 4; }
            var r = Rectangle.FromLTRB(sr.Left, sr.Top, sr.Right, sr.Bottom);
            Out($"rect={r}");

            // 鼠标移到底部边缘内侧晃动让原控制条弹出，然后停住让条保持
            int cy = r.Bottom - 18;
            Native.SetCursorPos(r.Right - 80, cy);
            for (int i = 0; i < 10; i++)
            {
                Native.SetCursorPos(r.Right - 80 + (i * 10) % 60, cy);
                Thread.Sleep(70);
            }
            Thread.Sleep(600);   // 让控制条完全淡入

            for (int pass = 1; pass <= 3; pass++)
            {
                var (ok, cx, cy2, report) = MediaOverlayForm.AnalyzeStrip(r, pass == 1 ? dump : null);
                Out($"--- pass {pass} ok={ok} anchor={cx},{cy2} ---");
                Out(report.ToString());
                Thread.Sleep(300);
            }
            Out("dump: " + dump);
        }
        finally { v2.PreviewClose(); }
        return 0;
    }

    /// <summary>诊断：弹一条气泡通知并截右下角屏幕，检查图标渲染。用法：--balloon-test</summary>
    public static int BalloonTest()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        var tray = new NotifyIcon
        {
            Icon = Program.AppIcon(16),
            Visible = true,
        };
        var form = new Form { WindowState = FormWindowState.Minimized, ShowInTaskbar = false };
        form.Shown += (_, _) =>
        {
            TrayBalloon.Show(tray, 5000, "SeerHover", "设置已保存并生效。", ToolTipIcon.Info);
            var t = new System.Windows.Forms.Timer { Interval = 2600 };
            t.Tick += (_, _) =>
            {
                t.Stop();
                var screen = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
                int w = 900, h = 320;
                using var bmp = new Bitmap(w, h);
                using (var g = Graphics.FromImage(bmp))
                    g.CopyFromScreen(screen.Right - w, screen.Bottom - h, 0, 0, bmp.Size);
                string outPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\work\balloon.png"));
                bmp.Save(outPath, ImageFormat.Png);
                Out("saved: " + outPath);
                Application.ExitThread();
            };
            t.Start();
        };
        Application.Run(form);
        tray.Dispose();
        return 0;
    }

    /// <summary>诊断：穷举系统 DLL GUID 找本机真实音频接口 IID。用法：--audio-discover</summary>
    public static int AudioDiscover()
    {
        AudioSession.Discover();
        return 0;
    }

    /// <summary>诊断：给当前可见的预览窗发一次 B 键（循环图片背景）。--bg-next</summary>
    public static int BgNext()
    {
        var ipc = new SeerIpc();
        IntPtr w = ipc.Window;
        if (w == IntPtr.Zero || !Native.IsWindowVisible(w)) { Out("no visible preview window"); return 2; }
        Native.SetForegroundWindow(w);
        Thread.Sleep(120);
        Native.keybd_event(0x42, 0, 0, UIntPtr.Zero);
        Native.keybd_event(0x42, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
        Out("sent B to preview window");
        return 0;
    }

    /// <summary>诊断：生成一张带透明通道的测试 PNG。--make-alpha <out.png> [w h]</summary>
    public static int MakeAlpha(string[] args)
    {
        string outPath = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "seer-alpha-test.png");
        int w = args.Length > 2 && int.TryParse(args[2], out int tw) ? tw : 800;
        int h = args.Length > 3 && int.TryParse(args[3], out int th) ? th : 600;
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var path = new System.Drawing.Drawing2D.GraphicsPath();
            path.AddEllipse(w / 6, h / 6, w * 2 / 3, h * 2 / 3);
            using var brush = new SolidBrush(Color.FromArgb(230, 48, 99, 220));
            g.FillPath(brush, path);
            using var pen = new Pen(Color.FromArgb(200, 255, 255, 255), 6);
            g.DrawPath(pen, path);
        }
        bmp.Save(outPath, ImageFormat.Png);
        Out("saved: " + outPath);
        return 0;
    }

    /// <summary>诊断：按 rect 打开音视频预览并聚焦，鼠标移入窗口后分时刻截图，检查媒体控件是否出现。</summary>
    public static int MediaShot(string[] args)
    {
        if (args.Length < 2 || !File.Exists(args[1]))
        {
            Out("usage: --media-shot <音视频文件> [输出前缀]");
            return 2;
        }
        string prefix = args.Length > 2 ? args[2] : Path.Combine(Path.GetTempPath(), "media");
        bool noRect = args.Any(a => string.Equals(a, "--norect", StringComparison.OrdinalIgnoreCase));
        var rect = new Native.RECT { Left = 600, Top = 100, Right = 2800, Bottom = 1500 };
        using var v2 = new SeerV2();
        if (!v2.EnsureConnected()) { Out("v2 unavailable: " + v2.LastError); return 2; }

        Native.GetCursorPos(out var saved);
        try
        {
            if (!v2.PreviewOpen(args[1], noRect ? null : rect, focus: true))
            { Out("preview.open failed: " + v2.LastError); return 3; }
            Thread.Sleep(1800);
            var st0 = v2.PreviewState();
            var wr = st0?.Rect ?? rect;
            Out($"window rect={wr.Width}x{wr.Height} @({wr.Left},{wr.Top})");
            // 模拟真实鼠标移动：分几步挪进窗口并小幅移动，触发 Qt 的 hover 事件
            int cx = (wr.Left + wr.Right) / 2, cy = wr.Bottom - 80;
            for (int i = 0; i < 6; i++)
            {
                Native.SetCursorPos(cx - 30 + i * 12, cy - 10 + i * 4);
                Thread.Sleep(120);
            }
            int prev = 0;
            foreach (int t in new[] { 300, 1000, 2200 })
            {
                Thread.Sleep(t - prev);
                prev = t;
                string p = $"{prefix}{(noRect ? "-norect" : "")}-{t}.png";
                using var bmp = new Bitmap(wr.Width, wr.Height);
                using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(wr.Left, wr.Top, 0, 0, bmp.Size);
                bmp.Save(p, ImageFormat.Png);
                Out($"saved(+{t}ms): {p}");
            }
        }
        finally
        {
            try { v2.PreviewClose(); } catch { }
            Native.SetCursorPos(saved.X, saved.Y);
        }
        return 0;
    }

    /// <summary>诊断：打开图片后连按 B 键循环背景，截图看有没有「透明」档。--bg-cycle <图片> [输出前缀]</summary>
    public static int BgCycle(string[] args)
    {
        if (args.Length < 2 || !File.Exists(args[1]))
        {
            Out("usage: --bg-cycle <图片> [输出前缀]");
            return 2;
        }
        string prefix = args.Length > 2 ? args[2] : Path.Combine(Path.GetTempPath(), "bg");
        var rect = new Native.RECT { Left = 600, Top = 100, Right = 1800, Bottom = 1100 };
        var ipc = new SeerIpc();
        using var v2 = new SeerV2();
        if (!v2.EnsureConnected()) { Out("v2 unavailable: " + v2.LastError); return 2; }

        try
        {
            if (!v2.PreviewOpen(args[1], rect, focus: true)) { Out("preview.open failed: " + v2.LastError); return 3; }
            Thread.Sleep(1500);
            IntPtr w = ipc.Window;
            Native.SetForegroundWindow(w);
            Thread.Sleep(200);
            for (int i = 0; i < 6; i++)
            {
                string p = $"{prefix}-{i}.png";
                using var bmp = new Bitmap(rect.Width, rect.Height);
                using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(rect.Left, rect.Top, 0, 0, bmp.Size);
                bmp.Save(p, ImageFormat.Png);
                Out($"saved({i}): {p}");
                Native.keybd_event(0x42, 0, 0, UIntPtr.Zero);              // B
                Native.keybd_event(0x42, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
                Thread.Sleep(600);
            }
        }
        finally
        {
            try { v2.PreviewClose(); } catch { }
        }
        return 0;
    }

    /// <summary>诊断：legacy 5000 路径 + 预摆位，采样窗口显示尺寸时间线。--legacy-test <文件> [x y w h]</summary>
    public static int LegacyTest(string[] args)
    {
        if (args.Length < 2 || !File.Exists(args[1]))
        {
            Out("usage: --legacy-test <file> [x y w h]");
            return 2;
        }
        var rect = new Native.RECT { Left = 600, Top = 100, Right = 3100, Bottom = 1900 };
        if (args.Length >= 6 && int.TryParse(args[2], out int rx) && int.TryParse(args[3], out int ry) &&
            int.TryParse(args[4], out int rw) && int.TryParse(args[5], out int rh))
            rect = new Native.RECT { Left = rx, Top = ry, Right = rx + rw, Bottom = ry + rh };

        var ipc = new SeerIpc();
        ipc.Hide();
        Thread.Sleep(300);

        IntPtr w = ipc.Window;
        Out($"hidden window: 0x{w.ToInt64():X} visible={Native.IsWindowVisible(w)}");
        bool preset = false;
        if (w != IntPtr.Zero && !Native.IsWindowVisible(w))
        {
            preset = Native.SetWindowPos(w, IntPtr.Zero, rect.Left, rect.Top, rect.Width, rect.Height,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            var cur = ipc.GetRect();
            Out($"preset={preset} rect now=({cur.Left},{cur.Top}) {cur.Width}x{cur.Height}");
        }

        var sw = Stopwatch.StartNew();
        ipc.Invoke(args[1]);
        string last = "";
        while (sw.ElapsedMilliseconds < 1200)
        {
            var r = ipc.GetRect();
            bool vis = ipc.IsVisibleFast;
            string s = $"{(vis ? "vis" : "hid")} {r.Width}x{r.Height} @({r.Left},{r.Top})";
            if (s != last) { Out($"+{sw.ElapsedMilliseconds,5}ms {s}"); last = s; }
            Thread.Sleep(4);
        }
        ipc.Hide();
        return 0;
    }

    /// <summary>诊断：复现「预览可见时切换文件」的尺寸时间线。--switch-test <文件A> <文件B></summary>
    public static int SwitchTest(string[] args)
    {
        if (args.Length < 3 || !File.Exists(args[1]) || !File.Exists(args[2]))
        {
            Out("usage: --switch-test <fileA> <fileB>");
            return 2;
        }
        using var v2 = new SeerV2();
        if (!v2.EnsureConnected()) { Out("v2 unavailable: " + v2.LastError); return 2; }

        var rectA = new Native.RECT { Left = 2300, Top = 400, Right = 3000, Bottom = 1400 };
        var rectB = new Native.RECT { Left = 600, Top = 100, Right = 3100, Bottom = 1900 };
        var ipc = new SeerIpc();
        ipc.Hide();
        Thread.Sleep(300);

        void Sample(string tag, int ms)
        {
            var sw = Stopwatch.StartNew();
            string last = "";
            while (sw.ElapsedMilliseconds < ms)
            {
                var r = ipc.GetRect();
                bool vis = ipc.IsVisibleFast;
                string s = $"{(vis ? "vis" : "hid")} {r.Width}x{r.Height} @({r.Left},{r.Top})";
                if (s != last) { Out($"+{sw.ElapsedMilliseconds,5}ms [{tag}] {s}"); last = s; }
                Thread.Sleep(4);
            }
        }

        Out($"open A: {Path.GetFileName(args[1])}");
        if (!v2.PreviewOpen(args[1], rectA)) { Out("open A failed: " + v2.LastError); return 3; }
        Sample("A", 1000);
        Out($"switch B: {Path.GetFileName(args[2])}");
        if (!v2.PreviewOpen(args[2], rectB)) { Out("open B failed: " + v2.LastError); return 3; }
        Sample("B", 1500);
        v2.PreviewClose();
        return 0;
    }

    /// <summary>
    /// 诊断：用指定 rect 打开预览并截下 Seer 窗口，看图片在窗口里是填满还是居中留黑边。
    /// 用法：--shot-preview <图片> [x y w h] [输出png]
    /// </summary>
    public static int ShotPreview(string[] args)
    {
        if (args.Length < 2 || !File.Exists(args[1]))
        {
            Out("usage: --shot-preview <file> [x y w h] [out.png]");
            return 2;
        }
        var rect = new Native.RECT { Left = 100, Top = 100, Right = 1100, Bottom = 800 };
        if (args.Length >= 6 && int.TryParse(args[2], out int x) && int.TryParse(args[3], out int y) &&
            int.TryParse(args[4], out int w) && int.TryParse(args[5], out int h))
            rect = new Native.RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };

        string outPath = args.Length >= 7 ? args[6] : Path.Combine(Path.GetTempPath(), "seer-preview-shot.png");
        using var v2 = new SeerV2();
        if (!v2.EnsureConnected()) { Out("v2 unavailable: " + v2.LastError); return 2; }
        if (!v2.PreviewOpen(args[1], rect)) { Out("preview.open failed: " + v2.LastError); return 3; }
        Thread.Sleep(1400);
        var st = v2.PreviewState();
        var r = st?.Rect ?? rect;
        Out($"state rect={r} loadState(available)={st?.Visible}");
        if (r.Width <= 0 || r.Height <= 0) { v2.PreviewClose(); return 4; }
        try
        {
            using var bmp = new Bitmap(r.Width, r.Height);
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Left, r.Top, 0, 0, bmp.Size);
            bmp.Save(outPath, ImageFormat.Png);
            Out("saved: " + outPath);
        }
        catch (Exception ex) { Out("capture failed: " + ex.Message); }
        v2.PreviewClose();
        return 0;
    }

    /// <summary>
    /// 诊断：打开媒体预览 → 聚焦 → 发按键 → 截图。验证 X/C 调速、M 静音、L/R 旋转等媒体快捷键。
    /// 用法：--media-key-test &lt;file&gt; &lt;keys&gt; [out.png]   keys 如 "L"、"CCC"、"XXX"、""（仅截图）
    /// </summary>
    public static int MediaKeyTest(string[] args)
    {
        if (args.Length < 3 || !File.Exists(args[1]))
        {
            Out("usage: --media-key-test <file> <keys> [out.png]");
            return 2;
        }
        string keys = args[2].ToUpperInvariant();
        string outPath = args.Length > 3 ? args[3] : Path.Combine(Path.GetTempPath(), "media-key.png");
        using var v2 = new SeerV2();
        if (!v2.EnsureConnected()) { Out("v2 unavailable: " + v2.LastError); return 2; }
        if (!v2.PreviewOpen(args[1])) { Out("preview.open failed: " + v2.LastError); return 3; }
        Thread.Sleep(1200);

        var st = v2.PreviewState();
        var r = st?.Rect ?? new Native.RECT();
        IntPtr w = new SeerIpc().Window;
        Out($"state visible={st?.Visible} rect={r} hwnd=0x{w:X}");
        if (w == IntPtr.Zero) { v2.PreviewClose(); return 4; }

        Native.SetForegroundWindow(w);
        Thread.Sleep(300);
        foreach (var ch in keys)
        {
            byte vk = (byte)(Native.VkKeyScanW(ch) & 0xFF);
            Native.keybd_event(vk, 0, 0, UIntPtr.Zero);
            Native.keybd_event(vk, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
            Thread.Sleep(120);
        }
        Thread.Sleep(900);

        try
        {
            using var bmp = new Bitmap(Math.Max(1, r.Width), Math.Max(1, r.Height));
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Left, r.Top, 0, 0, bmp.Size);
            bmp.Save(outPath, ImageFormat.Png);
            Out("saved: " + outPath);
        }
        catch (Exception ex) { Out("capture failed: " + ex.Message); }
        v2.PreviewClose();
        return 0;
    }

    /// <summary>诊断：给定图片和光标位置，打印几何计算结果（不真的弹窗）。</summary>
    public static int GeomPlan(string[] args)
    {
        if (args.Length < 2 || !File.Exists(args[1]))
        {
            Out("usage: --geom-plan <file> [x y]");
            return 2;
        }
        string path = args[1];
        Native.POINT cursor;
        if (args.Length >= 4 && int.TryParse(args[2], out int x) && int.TryParse(args[3], out int y))
            cursor = new Native.POINT(x, y);
        else Native.GetCursorPos(out cursor);

        var cfg = AppConfig.LoadOrCreate();
        var (srcW, srcH) = MediaSize.Probe(path);
        var target = new HoverTarget
        {
            Path = path,
            Name = Path.GetFileName(path),
            ItemRect = new Native.RECT
            {
                Left = cursor.X - 40, Top = cursor.Y - 40,
                Right = cursor.X + 40, Bottom = cursor.Y + 40
            }
        };
        GeometryController.DebugPlans = true;
        var rect = new GeometryController(cfg, new SeerIpc()).Plan(target, cursor);

        Out($"config      : {AppConfig.Path}");
        Out($"source      : {srcW}x{srcH}  ({Path.GetFileName(path)})");
        Out($"cursor      : {cursor}");
        var mi = Native.MonitorOf(cursor.X, cursor.Y);
        Out($"settings    : mode={cfg.Geometry.Mode} size={cfg.Geometry.SizeMode} gap={cfg.Geometry.Gap} ratio={cfg.Geometry.MaxScreenRatio} avoid={cfg.Geometry.AvoidCursor}");
        Out($"screen      : {mi.rcMonitor}");
        Out($"work        : {mi.rcWork}");
        Out($"plan rect   : {rect}");
        Out("candidates:\n" + GeometryController.LastPlanDebug);
        if (rect.HasValue && !rect.Value.IsEmpty)
        {
            int dx = Math.Min(Math.Abs(cursor.X - rect.Value.Left), Math.Abs(cursor.X - rect.Value.Right));
            int dy = Math.Min(Math.Abs(cursor.Y - rect.Value.Top), Math.Abs(cursor.Y - rect.Value.Bottom));
            Out($"aspect      : src={(srcW > 0 ? (double)srcW / srcH : 0):0.000} plan={(double)rect.Value.Width / rect.Value.Height:0.000}");
            Out($"cursor gaps : x={dx} y={dy} (covers cursor={cursor.X >= rect.Value.Left && cursor.X <= rect.Value.Right && cursor.Y >= rect.Value.Top && cursor.Y <= rect.Value.Bottom})");
        }
        return 0;
    }

    /// <summary>诊断：切换标签后立即回读光标解析结果，验证两个标签的 DefView / 路径是否配对。</summary>
    public static int SelectTab(string[] args)
    {
        if (args.Length < 2 || !int.TryParse(args[1], out int index))
        {
            Out("usage: --select-tab <index>   (0 = first tab)");
            return 2;
        }
        Native.GetCursorPos(out var cursor);
        IntPtr root = Native.RootAt(cursor, out _);
        if (root == IntPtr.Zero || !HoverResolver.IsExplorerRoot(root))
        {
            Out("cursor is not over an explorer window");
            return 2;
        }
        Out($"before: {Uia.ActiveTab(root)}");
        if (!Uia.SelectTab(root, index)) { Out("select tab failed"); return 3; }
        Thread.Sleep(500);
        ExplorerItemCache.Shared.Invalidate();
        Out("--- after switch ---");
        return ResolveAt();
    }

    /// <summary>诊断多标签页：列出每个资源管理器窗口的所有 DefView 及其 UIA 状态。</summary>
    public static int Tabs()
    {
        IntPtr top = IntPtr.Zero;
        int n = 0;
        while ((top = Native.FindWindowExW(IntPtr.Zero, top, "CabinetWClass", null)) != IntPtr.Zero)
        {
            string title = Native.TitleOf(top);
            Out($"== 0x{top.ToInt64():X} '{title}' visible={Native.IsWindowVisible(top)} ==");
            Out("  " + Uia.TabItems(top).Replace("\n", "\n  "));
            Out("  shell: " + Uia.ShellPaths(top).Replace("\n", "\n         "));
            var views = Native.FindAllClass(top, 8, "SHELLDLL_DefView");
            Out($"  DefViews={views.Count}");
            for (int i = 0; i < views.Count; i++)
            {
                var v = views[i];
                Native.GetWindowRect(v, out var vr);
                Out($"  [{i}] 0x{v.ToInt64():X} rect={vr} visible={Native.IsWindowVisible(v)}");
                Out($"      owner: {Uia.ViewOwnerTab(v)}");
                Out($"      uia: {Uia.ViewInfo(v)}");
            }
            n++;
        }
        if (n == 0) Out("no explorer windows");
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

    /// <summary>
    /// 诊断：生成 MJPG 编码的测试视频（横屏 1920x1080 / 竖屏 1080x1920），带活动色块便于肉眼确认播放进度。
    /// 用法：--make-test-video [输出目录]
    /// </summary>
    public static int MakeTestVideo(string[] args)
    {
        string dir = args.Length > 1 ? args[1] : Path.GetTempPath();
        try
        {
            MakeMjpegAvi(Path.Combine(dir, "sv-landscape.avi"), 1920, 1080, 120, 10);
            MakeMjpegAvi(Path.Combine(dir, "sv-portrait.avi"), 1080, 1920, 120, 10);
            return 0;
        }
        catch (Exception ex) { Out("failed: " + ex); return 4; }
    }

    private static void MakeMjpegAvi(string path, int w, int h, int frames, int fps)
    {
        var jpegs = new List<byte[]>();
        for (int i = 0; i < frames; i++)
        {
            using var bmp = new Bitmap(w, h);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(30 + i * 180 / frames, (i * 7) % 255, (i * 13) % 255));
                using var white = new SolidBrush(Color.White);
                g.FillRectangle(white, w / 4, h / 4, w / 2, h / 2);
                using var font = new Font("Arial", 60, FontStyle.Bold);
                g.DrawString($"{i + 1}/{frames}s", font, Brushes.Black, w / 2 - 100, h / 2 - 60);
                using var bar = new SolidBrush(Color.Yellow);
                g.FillRectangle(bar, 0, h - 30, w * (i + 1) / frames, 30);   // 底部进度条
            }
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Jpeg);
            jpegs.Add(ms.ToArray());
        }

        byte[] Chunk(string fourcc, byte[] data)
            => System.Text.Encoding.ASCII.GetBytes(fourcc)
               .Concat(BitConverter.GetBytes(data.Length)).Concat(data)
               .Concat(data.Length % 2 == 1 ? new byte[] { 0 } : Array.Empty<byte>()).ToArray();

        var strh = new List<byte>();
        strh.AddRange(System.Text.Encoding.ASCII.GetBytes("vids"));
        strh.AddRange(System.Text.Encoding.ASCII.GetBytes("MJPG"));
        strh.AddRange(BitConverter.GetBytes(0u));
        strh.AddRange(BitConverter.GetBytes((ushort)0)); strh.AddRange(BitConverter.GetBytes((ushort)0));
        strh.AddRange(BitConverter.GetBytes(0u));                       // initial frames
        strh.AddRange(BitConverter.GetBytes(1u));                       // scale
        strh.AddRange(BitConverter.GetBytes((uint)fps));                // rate
        strh.AddRange(BitConverter.GetBytes(0u));                       // start
        strh.AddRange(BitConverter.GetBytes((uint)frames));             // length
        strh.AddRange(BitConverter.GetBytes(0u));                       // buffer
        strh.AddRange(BitConverter.GetBytes(0xFFFFFFFFu));              // quality
        strh.AddRange(BitConverter.GetBytes(0u));                       // sample size
        strh.AddRange(new byte[8]);                                     // rcFrame

        var bih = new List<byte>();
        bih.AddRange(BitConverter.GetBytes(40));
        bih.AddRange(BitConverter.GetBytes(w));
        bih.AddRange(BitConverter.GetBytes(h));
        bih.AddRange(BitConverter.GetBytes((ushort)1));
        bih.AddRange(BitConverter.GetBytes((ushort)24));
        bih.AddRange(BitConverter.GetBytes(0x47504A4Du));   // biCompression = 'MJPG'（0 会被当成 rawvideo）
        bih.AddRange(BitConverter.GetBytes(0u));
        bih.AddRange(BitConverter.GetBytes(0u)); bih.AddRange(BitConverter.GetBytes(0u));
        bih.AddRange(BitConverter.GetBytes(0u)); bih.AddRange(BitConverter.GetBytes(0u));

        var avih = new List<byte>();
        avih.AddRange(BitConverter.GetBytes((uint)(1_000_000 / fps)));  // microsec per frame
        avih.AddRange(BitConverter.GetBytes(0u)); avih.AddRange(BitConverter.GetBytes(0u));
        avih.AddRange(BitConverter.GetBytes(0x10u));                    // HASINDEX
        avih.AddRange(BitConverter.GetBytes((uint)frames));
        avih.AddRange(BitConverter.GetBytes(0u)); avih.AddRange(BitConverter.GetBytes(1u));
        avih.AddRange(BitConverter.GetBytes(0u)); avih.AddRange(BitConverter.GetBytes((uint)w));
        avih.AddRange(BitConverter.GetBytes((uint)h));
        avih.AddRange(new byte[16]);

        var movi = new MemoryStream();
        var idx = new MemoryStream();
        var offsets = new List<int>();
        long[] moviStart = { 0 };
        // 先算偏移：movi 数据区起点相对 idx1 的基准（标准做法：offset 相对 movi list 数据起点）
        int headerSize = 4 + 8 + 4 + 8 + 4 + avih.Count + 4 + 8 + 4 + 8 + 4 + strh.Count + 4 + 8 + 4 + bih.Count; // hdrl
        long dataPos = 4 + 8 + 4 + 8 + 4 + avih.Count + 4 + 8 + 4 + 8 + 4 + strh.Count + 4 + 8 + 4 + bih.Count + 8 + 4 + 8 + 4;
        foreach (var j in jpegs) { offsets.Add((int)dataPos); dataPos += 8 + j.Length + (j.Length % 2); }
        foreach (var j in jpegs) movi.Write(Chunk("00dc", j));
        foreach (var j in jpegs)
        {
            idx.Write(System.Text.Encoding.ASCII.GetBytes("00dc"));
            idx.Write(BitConverter.GetBytes(0x10u));
            idx.Write(BitConverter.GetBytes((uint)j.Length));
            idx.Write(BitConverter.GetBytes((uint)offsets[Array.IndexOf(jpegs.ToArray(), j)] - 4 + 8));
        }

        var hdrl = Chunk("LIST", System.Text.Encoding.ASCII.GetBytes("hdrl")
            .Concat(Chunk("avih", avih.ToArray()))
            .Concat(Chunk("LIST", System.Text.Encoding.ASCII.GetBytes("strl")
                .Concat(Chunk("strh", strh.ToArray()))
                .Concat(Chunk("strf", bih.ToArray())).ToArray())).ToArray());
        var moviList = Chunk("LIST", System.Text.Encoding.ASCII.GetBytes("movi").Concat(movi.ToArray()).ToArray());

        using var fs = File.Create(path);
        var body = System.Text.Encoding.ASCII.GetBytes("AVI ")
            .Concat(hdrl).Concat(moviList).Concat(Chunk("idx1", idx.ToArray())).ToArray();
        fs.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        fs.Write(BitConverter.GetBytes(body.Length));
        fs.Write(body);
        Out($"wrote {path} ({new FileInfo(path).Length} bytes) {w}x{h}x{frames}@{fps}");
    }
}
