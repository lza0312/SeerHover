using System.Text;

namespace SeerHover;

/// <summary>
/// 接管预览窗口的位置与尺寸。
///
/// 实测结论（Seer 4.7 商店版，150% DPI）：
/// 1. 窗口刚出现时是 765x645，随后会按内容（图片原始尺寸等）自适应一次；
/// 2. 如果我们在它自适应之前就 SetWindowPos，尺寸会被保留，不会再被改；
/// 3. 它有一个最小窗口尺寸（实测 765x645），比它小的设置会被立刻夹回，
///    以前反复设 900x620 就得到「620 ↔ 645」的持续抖动。
///
/// 所以策略是：出现即设一次 → 读回实际值（可能被夹）→ 用实际值再确认一次，
/// 最多 2 次；之后只做低频监听，发现被改才补一次，绝不循环硬设。
///
/// 尺寸策略三选一：
///   fixed = 用配置的宽高；
///   auto  = 按原图/原视频的像素比例，在原尺寸基础上尽可能大地显示（放不下才缩，太小才放大）；
///   seer  = 完全交给 Seer，只摆位置。
///
/// 位置：光标旁模式下会在「右 / 左 / 下 / 上」四个方向里挑一个能完整放下、且空间最大的方向，
/// 并保证窗口不压住鼠标（AvoidCursor）。
/// </summary>
internal sealed class GeometryController
{
    /// <summary>一次几何计算的结果：最终窗口尺寸 + 要发给 Seer 的位置。</summary>
    private readonly record struct GeometryResult(int W, int H, Native.RECT Rect);

    private readonly AppConfig _cfg;
    private readonly SeerIpc _ipc;
    private CancellationTokenSource? _cts;

    /// <summary>诊断开关：由 --geom-plan 打开，记录每个方向的计算过程。</summary>
    public static bool DebugPlans;
    public static string LastPlanDebug { get; private set; } = string.Empty;

    /// <summary>Seer 的最小窗口尺寸（首次被夹后学习到的值，物理像素）</summary>
    private static int _floorW;
    private static int _floorH;

    private const int DefaultMinW = 765;
    private const int DefaultMinH = 645;

    /// <summary>固定尺寸的硬上限：8K（7680×4320），4K / 5K2K 带鱼屏都在范围内</summary>
    public const int MaxFixedW = 7680;
    public const int MaxFixedH = 4320;

    /// <summary>固定尺寸的软下限：320×240（音频等类别的小窗预览）。</summary>
    public const int MinFixedW = 320;
    public const int MinFixedH = 240;

    public GeometryController(AppConfig cfg, SeerIpc ipc)
    {
        _cfg = cfg;
        _ipc = ipc;
    }

    public void Cancel()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts == null) return;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// 只算目标几何、不碰窗口。供 V2 的 preview.open(rect) 在触发预览前同步取 rect：
    /// rect 必须和路径一起发出去，才能禁掉 Seer 的 content-fit 自动缩放。
    /// 返回 null 表示该类别走 seer 尺寸（完全交给 Seer）。
    /// </summary>
    public Native.RECT? Plan(HoverTarget target, Native.POINT cursor)
    {
        if (EffectiveSizeMode(_cfg.Geometry, target.Path) == "seer") return null;
        return Compute(target, cursor, default, preSet: true).Rect;
    }

    /// <summary>
    /// 该文件最终生效的尺寸策略：先查按类别的覆盖（Geometry.CategoryModes），
    /// 没有覆盖或覆盖值为空时回落到全局 SizeMode。
    /// </summary>
    public static string EffectiveSizeMode(GeometryConfig cfg, string path)
    {
        if (cfg.CategoryModes != null &&
            cfg.CategoryModes.TryGetValue(MediaKind.Of(path), out var m) &&
            !string.IsNullOrWhiteSpace(m))
        {
            return m.Trim().ToLowerInvariant() switch
            {
                "auto" => "auto",
                "seer" => "seer",
                "fixed" => "fixed",
                _ => (cfg.SizeMode ?? "fixed").ToLowerInvariant()
            };
        }
        return (cfg.SizeMode ?? "fixed").ToLowerInvariant();
    }

    /// <summary>该文件最终生效的「固定尺寸」宽高：按类别覆盖（CategorySizes），缺失用全局 Width/Height。</summary>
    public static (int W, int H) EffectiveFixedSize(GeometryConfig cfg, string path)
    {
        int w = cfg.Width, h = cfg.Height;
        if (cfg.CategorySizes != null &&
            cfg.CategorySizes.TryGetValue(MediaKind.Of(path), out var s) &&
            s is { Length: 2 } && s[0] > 0 && s[1] > 0)
        {
            w = s[0];
            h = s[1];
        }
        return (w, h);
    }

    /// <summary> unify：V2 的 rect 与旧版 SetWindowPos 共用同一套计算，避免两条路径尺寸/位置不一致。 </summary>
    private GeometryResult Compute(HoverTarget target, Native.POINT cursor, Native.RECT current, bool preSet)
    {
        var cfg = _cfg.Geometry;
        string sizeMode = EffectiveSizeMode(cfg, target.Path);
        string mode = (cfg.Mode ?? "cursor").ToLowerInvariant();

        var mi = Native.MonitorOf(cursor.X, cursor.Y);
        var screen = mi.rcMonitor.IsEmpty ? mi.rcWork : mi.rcMonitor;
        var work = mi.rcWork.IsEmpty ? mi.rcMonitor : mi.rcWork;

        int minW = _floorW > 0 ? _floorW : DefaultMinW;
        int minH = _floorH > 0 ? _floorH : DefaultMinH;

        if (sizeMode == "seer")
        {
            int sw = current.Width > 0 ? current.Width : minW;
            int sh = current.Height > 0 ? current.Height : minH;
            return new GeometryResult(sw, sh, Place(target, current, work, cfg, sw, sh, cursor));
        }

        // 「光标旁 + 原比例最大化」：尺寸必须先知道往哪边弹，才能按那边的真实可用空间放大，
        // 否则先按整屏算尺寸再挑方向，就会出现「右下角鼠标、左上空间很大却仍用小窗」。
        var (cfgW, cfgH) = EffectiveFixedSize(cfg, target.Path);
        if (sizeMode == "auto" && mode != "fixed" && mode != "corner")
        {
            int srcW = 0, srcH = 0;
            (srcW, srcH) = MediaSize.Probe(target.Path);
            if ((srcW <= 0 || srcH <= 0) && Logger.IsVerbose)
                Logger.Info($"geometry: source size unavailable for {target.Name}");
            return PlanAuto(cursor, work, screen, cfg, srcW, srcH, minW, minH, cfgW, cfgH);
        }

        int w = 0, h = 0;
        if (sizeMode == "auto") (w, h) = MediaSize.Probe(target.Path);
        var (fw, fh) = TargetSize(cfg, sizeMode, cfgW, cfgH, w, h, screen, minW, minH, preSet ? default : current);
        return new GeometryResult(fw, fh, Place(target, current, work, cfg, fw, fh, cursor));
    }

    public void Apply(HoverTarget target, Native.POINT cursor)
    {
        Cancel();
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _cts, cts);
        var token = cts.Token;

        _ = Task.Run(() =>
        {
            try { Run(target, cursor, token); }
            catch (Exception ex) { Logger.Error("geometry failed: " + ex.Message); }
            finally
            {
                Interlocked.CompareExchange(ref _cts, null, cts);
                cts.Dispose();
            }
        });
    }

    private void Run(HoverTarget target, Native.POINT cursor, CancellationToken token)
    {
        // 定位窗口只要几百毫秒，但需要 1ms 定时精度（WaitForWindow / 回拉采样）。
        // 只在这段时间里提高精度，进程闲着时不再让整机跑在 1ms 定时上（更省电、更少唤醒）。
        using var _ = Native.TimerResolution.Begin(1);

        var cfg = _cfg.Geometry;
        string sizeMode = (cfg.SizeMode ?? "fixed").ToLowerInvariant();
        bool controlSize = sizeMode != "seer";

        // 0) 抢在 Seer 显示之前设一次：窗口对象在显示前就存在，提前设好尺寸位置，
        //    它显示时就不会先按图片原始尺寸铺开再被我们缩回来。
        if (controlSize && _ipc.Window != IntPtr.Zero)
        {
            var pre = Compute(target, cursor, default, preSet: true).Rect;
            Native.SetWindowPos(_ipc.Window, IntPtr.Zero, pre.Left, pre.Top,
                pre.Right - pre.Left, pre.Bottom - pre.Top,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }

        // 1) 等窗口出现。必须出现即设：Seer 会在显示后几十毫秒内按内容铺一次原始尺寸
        //    （图片能铺到 1794x1794），抢在它之前设好，就不会看到"先大后小"。
        var first = WaitForWindow(token, 1200);
        if (first.IsEmpty) return;

        var mi = Native.MonitorOf(cursor.X, cursor.Y);
        var screen = mi.rcMonitor.IsEmpty ? mi.rcWork : mi.rcMonitor;   // 尺寸上限按整块屏幕算（不是去掉任务栏的工作区）
        var work = mi.rcWork.IsEmpty ? mi.rcMonitor : mi.rcWork;        // 摆放仍按工作区
        int minW = _floorW > 0 ? _floorW : DefaultMinW;
        int minH = _floorH > 0 ? _floorH : DefaultMinH;

        var plan = Compute(target, cursor, first, preSet: false);
        int width = plan.W, height = plan.H;
        var rect = plan.Rect;

        // 3) 设定目标几何，并用 5ms 的高频采样盯着。
        //    Seer 在内容加载完成后会再撑一次（图片会撑到原始尺寸），
        //    20ms 内拉回后用户基本看不到那一帧。
        MoveTo(rect.Left, rect.Top, width, height);
        int sets = 1;
        int misses = 0;
        int stable = 0;

        for (int i = 0; i < 40; i++)
        {
            // 用 Sleep + 取消标记代替 token.WaitHandle（后者每次预览都要建一个内核事件句柄）
            if (token.IsCancellationRequested) return;
            Thread.Sleep(5);
            if (token.IsCancellationRequested) return;
            if (!_ipc.IsWindowAlive || !_ipc.IsVisibleFast) return;

            var r = _ipc.GetRect();
            if (r.IsEmpty) continue;

            bool ok = Math.Abs(r.Width - width) <= 4 && Math.Abs(r.Height - height) <= 4 &&
                      Math.Abs(r.Left - rect.Left) <= 4 && Math.Abs(r.Top - rect.Top) <= 4;

            if (ok)
            {
                if (++stable >= 12) break;   // 连续 60ms 不动 = 稳了
                continue;
            }

            stable = 0;
            misses++;

            // 差异很小 → Seer 的最小尺寸夹取，记住并采纳，别再顶
            bool near = r.Width <= width * 1.25 && r.Height <= height * 1.25;
            if (near && r.Width >= width && r.Height >= height)
            {
                if (r.Width > width) _floorW = r.Width;
                if (r.Height > height) _floorH = r.Height;
                plan = Compute(target, cursor, r, preSet: false);
                width = plan.W;
                height = plan.H;
                rect = plan.Rect;
                if (Logger.IsVerbose) Logger.Info($"geometry adopt floor: {width}x{height}");
                MoveTo(rect.Left, rect.Top, width, height);
                sets++;
                continue;
            }

            if (misses > 4)
            {
                if (Logger.IsVerbose) Logger.Info($"geometry give up at {r.Width}x{r.Height}");
                break;
            }

            if (Logger.IsVerbose) Logger.Info($"geometry pullback #{misses}: {r.Width}x{r.Height} -> {width}x{height}");
            MoveTo(rect.Left, rect.Top, width, height);
            sets++;
        }

        if (Logger.IsVerbose) Logger.Info($"geometry settled: {width}x{height} at {rect.Left},{rect.Top} (sets={sets})");
    }

    // ── V2 路径：预摆位 + 盯梢，抹掉「先小后大」的那一帧 ─────────────

    /// <summary>
    /// Seer 的预览窗口对象在显示之前就已存在。V2 走 preview.open(rect) 时 Seer
    /// 内部异步应用 rect，窗口会先按上次的尺寸露一帧再变过来（大图/视频很明显）。
    /// 趁它还没显示，先同步把窗口摆到目标 rect —— 显示出来就是最终大小。
    /// </summary>
    public void PrePosition(Native.RECT rect)
    {
        try
        {
            IntPtr w = _ipc.Window;
            if (w == IntPtr.Zero || Native.IsWindowVisible(w)) return;
            Native.SetWindowPos(w, IntPtr.Zero, rect.Left, rect.Top, rect.Width, rect.Height,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            if (Logger.IsVerbose) Logger.Info($"geometry preposition: {rect.Width}x{rect.Height} at {rect.Left},{rect.Top}");
        }
        catch { }
    }

    /// <summary>
    /// V2 打开后短盯一会：出现即再钉一次，之后 5ms 采样，Seer 若把窗口改走就拉回目标 rect；
    /// 被 Seer 最小窗夹大（在目标 1.25 倍内）则接受，不硬顶。
    /// </summary>
    public void Watch(Native.RECT rect)
    {
        Cancel();
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _cts, cts);
        var token = cts.Token;

        _ = Task.Run(() =>
        {
            try
            {
                using var _t = Native.TimerResolution.Begin(1);

                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 1200)
                {
                    if (token.IsCancellationRequested) return;
                    if (_ipc.IsVisibleFast) break;
                    Thread.Sleep(2);
                }
                if (token.IsCancellationRequested || !_ipc.IsVisibleFast) return;

                MoveTo(rect.Left, rect.Top, rect.Width, rect.Height);
                int stable = 0, misses = 0;
                for (int i = 0; i < 40; i++)
                {
                    if (token.IsCancellationRequested) return;
                    Thread.Sleep(5);
                    if (!_ipc.IsWindowAlive || !_ipc.IsVisibleFast) return;

                    var r = _ipc.GetRect();
                    if (r.IsEmpty) continue;
                    bool ok = Math.Abs(r.Width - rect.Width) <= 4 && Math.Abs(r.Height - rect.Height) <= 4 &&
                              Math.Abs(r.Left - rect.Left) <= 4 && Math.Abs(r.Top - rect.Top) <= 4;
                    if (ok)
                    {
                        if (++stable >= 12) break;
                        continue;
                    }

                    // 差异很小且比目标大 → Seer 的最小窗夹取，接受，别再顶
                    bool near = r.Width <= rect.Width * 1.25 && r.Height <= rect.Height * 1.25;
                    if (near && r.Width >= rect.Width && r.Height >= rect.Height)
                    {
                        if (Logger.IsVerbose) Logger.Info($"geometry watch adopt floor: {r.Width}x{r.Height}");
                        break;
                    }

                    stable = 0;
                    if (++misses > 4) break;
                    MoveTo(rect.Left, rect.Top, rect.Width, rect.Height);
                }
                if (Logger.IsVerbose) Logger.Info("geometry watch done");
            }
            catch (Exception ex) { Logger.Error("geometry watch failed: " + ex.Message); }
            finally
            {
                Interlocked.CompareExchange(ref _cts, null, cts);
                cts.Dispose();
            }
        });
    }

    // ── 尺寸 ────────────────────────────────────────────────────────

    /// <summary>
    /// 光标旁 + 原比例最大化：一次算完「往哪边弹」和「弹多大」。
    ///
    /// 四个方向各自先算出从光标（严格留出 Gap 像素）到工作区边缘的可用矩形，
    /// 再用同一个缩放系数把原始比例放进这个矩形 —— 宽高一起缩放，绝不分别夹取，
    /// 否则宽高比一变，Seer 就会在图片两侧/上下补黑边。
    /// 最终按「是否满足最小窗 + 预览面积 + 可用空间面积」挑最优方向。
    /// </summary>
    private static GeometryResult PlanAuto(Native.POINT cursor, Native.RECT work, Native.RECT screen,
        GeometryConfig cfg, int srcW, int srcH, int minW, int minH, int cfgW, int cfgH)
    {
        var debug = DebugPlans ? new StringBuilder() : null;
        bool known = srcW > 0 && srcH > 0;
        double bw = known ? srcW : Math.Max(1, cfgW > 0 ? cfgW : minW);
        double bh = known ? srcH : Math.Max(1, cfgH > 0 ? cfgH : minH);

        double ratio = cfg.MaxScreenRatio > 0 ? cfg.MaxScreenRatio : 1.0;
        int capW = Math.Max(200, (int)(screen.Width * ratio));
        int capH = Math.Max(150, (int)(screen.Height * ratio));
        int gap = Math.Max(0, cfg.Gap);
        double floorScale = Math.Max(minW / bw, minH / bh);
        // Seer V2 的 rect 不会放大图片：窗口比原图大时只按 1:1 居中，四周补黑边。
        // 打开 UpscaleToFill 时允许窗口放大（HoverController 会先把图片重采样成临时大图，
        // Seer 对大图会缩小填满，视觉上就是原比例放大）；关闭时严格不超过原图像素。
        double screenScale = Math.Min(capW / bw, capH / bh);
        if (!cfg.UpscaleToFill) screenScale = Math.Min(1.0, screenScale);

        // (是否水平方向, 近端坐标, 远端坐标, 方向：+1 = 右/下)
        var dirs = new (bool Horizontal, int Near, int Far, int Sign)[]
        {
            (true,  cursor.X + gap, work.Right,  +1),   // 右
            (true,  work.Left,      cursor.X - gap, -1), // 左
            (false, cursor.Y + gap, work.Bottom, +1),   // 下
            (false, work.Top,       cursor.Y - gap, -1), // 上
        };

        bool found = false;
        GeometryResult best = default;
        double bestScore = double.MinValue;

        foreach (var d in dirs)
        {
            int span = d.Far - d.Near;                              // 弹出方向上的可用长度
            int cross = d.Horizontal ? work.Height : work.Width;    // 垂直方向整个工作区都可用
            if (span < 60 || cross < 60) continue;

            // 水平方向：span 比宽、cross 比高；垂直方向正好相反
            double spanSide = d.Horizontal ? bw : bh;
            double crossSide = d.Horizontal ? bh : bw;
            double scale = Math.Min(screenScale, Math.Min(span / spanSide, cross / crossSide));
            if (scale <= 0) continue;

            int w = Math.Clamp((int)Math.Round(bw * scale), 1, capW);
            int h = Math.Clamp((int)Math.Round(bh * scale), 1, capH);
            if (w < 2 || h < 2) continue;

            // 峰值优先：能满足 Seer 最小窗的方向 >> 预览面积大的方向 >> 剩余空间大的方向
            double score = (scale >= floorScale ? 1e12 : 0)
                         + (double)w * h
                         + (double)span * cross / 1e6;
            if (found && score <= bestScore) continue;

            int left = d.Horizontal
                ? (d.Sign > 0 ? cursor.X + gap : cursor.X - gap - w)
                : cursor.X - w / 2;
            int top = d.Horizontal
                ? cursor.Y - h / 2
                : (d.Sign > 0 ? cursor.Y + gap : cursor.Y - gap - h);
            ClampToWork(ref left, ref top, w, h, work);

            // 已经留了 gap，这里用 gap-1 做安全垫，避免「刚好贴着 28px」被判成压住鼠标又挪走
            if (cfg.AvoidCursor)
                NudgeAwayFromCursor(ref left, ref top, w, h, cursor, work, Math.Max(10, gap - 1));
            ClampToWork(ref left, ref top, w, h, work);
            debug?.AppendLine($"  h={d.Horizontal} sign={d.Sign:+0;-0} span={span} cross={cross} scale={scale:0.000} {w}x{h} score={score:0.0} at {left},{top}");

            best = new GeometryResult(w, h,
                new Native.RECT { Left = left, Top = top, Right = left + w, Bottom = top + h });
            bestScore = score;
            found = true;
        }

        if (debug != null)
        {
            debug.Insert(0, $"src={bw:0}x{bh:0} screenScale={screenScale:0.000} floorScale={floorScale:0.000}\n");
            LastPlanDebug = debug.ToString();
        }
        if (found) return best;

        // 工作区太小（极端缩放 / 单屏手机投影）：退回工作区左上角，保持比例不夹成黑框
        int fw = Math.Clamp((int)Math.Round(bw), 1, capW);
        int fh = Math.Clamp((int)Math.Round(bh), 1, capH);
        int l0 = work.Left, t0 = work.Top;
        ClampToWork(ref l0, ref t0, fw, fh, work);
        return new GeometryResult(fw, fh,
            new Native.RECT { Left = l0, Top = t0, Right = l0 + fw, Bottom = t0 + fh });
    }

    private static (int W, int H) TargetSize(GeometryConfig cfg, string sizeMode, int cfgW, int cfgH,
        int srcW, int srcH, Native.RECT screen, int minW, int minH, Native.RECT current)
    {
        if (sizeMode == "seer")
            return (current.Width, current.Height);

        int scrW = Math.Max(320, screen.Width);
        int scrH = Math.Max(240, screen.Height);

        // 原比例最大化：受「最大占屏比」限制（按整块屏幕，不是工作区）
        if (sizeMode == "auto" && srcW > 0 && srcH > 0)
        {
            int maxW = Math.Max(200, (int)(scrW * cfg.MaxScreenRatio));
            int maxH = Math.Max(150, (int)(scrH * cfg.MaxScreenRatio));
            double avail = Math.Min((double)maxW / srcW, (double)maxH / srcH);       // 可用空间允许的缩放
            double floorS = Math.Max((double)minW / srcW, (double)minH / srcH);      // 不小于 Seer 最小窗口
            // 原尺寸放得下就用原尺寸；放不下按可用空间缩；原尺寸比最小窗口还小就放大到最小窗口（仍然放不下则听可用空间）
            double scale = Math.Min(avail, Math.Max(Math.Min(1.0, avail), floorS));
            int w = (int)Math.Round(srcW * scale);
            int h = (int)Math.Round(srcH * scale);
            // 必须整体等比放大到最小窗；分别夹取宽高会把比例夹歪，Seer 就补黑边
            if ((w < minW || h < minH) && w > 0 && h > 0)
            {
                double up = Math.Max((double)minW / w, (double)minH / h);
                if (w * up <= maxW && h * up <= maxH)
                {
                    w = (int)Math.Round(w * up);
                    h = (int)Math.Round(h * up);
                }
            }
            return (Math.Clamp(w, 1, maxW), Math.Clamp(h, 1, maxH));
        }

        // 固定尺寸：只受「屏幕尺寸」和 8K 上限约束，不再被占屏比压小，
        // 这样 4K 屏可以把高度直接设成 2160（铺满整屏）。
        // 下限 320x240（音频等类别想要小窗预览）；Seer 若坚持自己的最小窗会夹回，
        // 盯梢逻辑会把被夹大的值当作实际值采纳，不会抖动。
        int capW = Math.Max(minW, Math.Min(scrW, MaxFixedW));
        int capH = Math.Max(minH, Math.Min(scrH, MaxFixedH));

        int fw = cfgW > 0 ? cfgW : current.Width;
        int fh = cfgH > 0 ? cfgH : current.Height;
        fw = Math.Clamp(fw, Math.Min(MinFixedW, capW), capW);
        fh = Math.Clamp(fh, Math.Min(MinFixedH, capH), capH);
        return (fw, fh);
    }

    // ── 位置 ────────────────────────────────────────────────────────

    private static Native.RECT Place(HoverTarget target, Native.RECT current, Native.RECT work,
        GeometryConfig cfg, int w, int h, Native.POINT cursor)
    {
        if (w <= 0) w = Math.Max(200, current.Width);
        if (h <= 0) h = Math.Max(150, current.Height);

        int left, top;
        switch ((cfg.Mode ?? "cursor").ToLowerInvariant())
        {
            case "fixed":
                left = cfg.X;
                top = cfg.Y;
                break;
            case "corner":
                switch ((cfg.Corner ?? "topright").ToLowerInvariant())
                {
                    case "topleft":
                        left = work.Left + cfg.Margin; top = work.Top + cfg.Margin; break;
                    case "bottomleft":
                        left = work.Left + cfg.Margin; top = work.Bottom - h - cfg.Margin; break;
                    case "bottomright":
                        left = work.Right - w - cfg.Margin; top = work.Bottom - h - cfg.Margin; break;
                    default:
                        left = work.Right - w - cfg.Margin; top = work.Top + cfg.Margin; break;
                }
                break;
            default:
                return PlaceNearItem(target, work, cfg, w, h, cursor);
        }

        ClampToWork(ref left, ref top, w, h, work);
        if (cfg.AvoidCursor) NudgeAwayFromCursor(ref left, ref top, w, h, cursor, work, Math.Max(10, cfg.Gap));
        ClampToWork(ref left, ref top, w, h, work);

        return new Native.RECT { Left = left, Top = top, Right = left + w, Bottom = top + h };
    }

    /// <summary>
    /// 光标旁定位：在四个方向里挑「能完整放下 且 剩余空间最大」的一个，
    /// 窗口沿垂直于弹出方向与文件项对齐，最后再保证不压住鼠标。
    /// </summary>
    private static Native.RECT PlaceNearItem(HoverTarget target, Native.RECT work, GeometryConfig cfg,
        int w, int h, Native.POINT cursor)
    {
        var anchor = target.ItemRect.Width > 0 && target.ItemRect.Height > 0
            ? target.ItemRect
            : new Native.RECT { Left = cursor.X - 2, Top = cursor.Y - 2, Right = cursor.X + 2, Bottom = cursor.Y + 2 };

        int gap = Math.Max(0, cfg.Gap);

        var cands = new (Native.RECT Space, int Left, int Top)[]
        {
            // 右侧：空间 = 文件项右边到工作区右边
            (new Native.RECT { Left = anchor.Right + gap, Top = work.Top, Right = work.Right, Bottom = work.Bottom },
                anchor.Right + gap, anchor.Top),
            // 左侧
            (new Native.RECT { Left = work.Left, Top = work.Top, Right = anchor.Left - gap, Bottom = work.Bottom },
                anchor.Left - gap - w, anchor.Top),
            // 下方
            (new Native.RECT { Left = work.Left, Top = anchor.Bottom + gap, Right = work.Right, Bottom = work.Bottom },
                anchor.Left, anchor.Bottom + gap),
            // 上方
            (new Native.RECT { Left = work.Left, Top = work.Top, Right = work.Right, Bottom = anchor.Top - gap },
                anchor.Left, anchor.Top - gap - h),
        };

        bool has = false;
        (Native.RECT Space, int Left, int Top) best = default;
        double bestScore = double.MinValue;

        foreach (var c in cands)
        {
            if (c.Space.Width < 60 || c.Space.Height < 60) continue;
            bool fits = c.Space.Width >= w && c.Space.Height >= h;
            double area = (double)c.Space.Width * c.Space.Height;
            double score = (fits ? 1e15 : 0) + area;   // 能完整放下优先，其次空间越大越好
            if (!has || score > bestScore) { best = c; bestScore = score; has = true; }
        }

        if (!has)
        {
            int l0 = work.Left, t0 = work.Top;
            ClampToWork(ref l0, ref t0, w, h, work);
            return new Native.RECT { Left = l0, Top = t0, Right = l0 + w, Bottom = t0 + h };
        }

        int left = best.Left, top = best.Top;
        ClampToWork(ref left, ref top, w, h, work);

        if (cfg.AvoidCursor)
            NudgeAwayFromCursor(ref left, ref top, w, h, cursor, work, Math.Max(10, gap));

        ClampToWork(ref left, ref top, w, h, work);
        return new Native.RECT { Left = left, Top = top, Right = left + w, Bottom = top + h };
    }

    private static void ClampToWork(ref int left, ref int top, int w, int h, Native.RECT work)
    {
        if (left + w > work.Right) left = work.Right - w;
        if (top + h > work.Bottom) top = work.Bottom - h;
        if (left < work.Left) left = work.Left;
        if (top < work.Top) top = work.Top;
    }

    /// <summary>窗口如果压住鼠标（含 pad 缓冲），往四个方向里挪最近且仍然不压鼠标的位置。</summary>
    private static void NudgeAwayFromCursor(ref int left, ref int top, int w, int h,
        Native.POINT cursor, Native.RECT work, int pad)
    {
        if (!HitsCursor(left, top, w, h, cursor, pad)) return;

        int bestL = left, bestT = top, bestDist = int.MaxValue;
        bool found = false;

        var options = new (int L, int T)[]
        {
            (cursor.X + pad, top),
            (cursor.X - pad - w, top),
            (left, cursor.Y + pad),
            (left, cursor.Y - pad - h),
        };

        foreach (var (l0, t0) in options)
        {
            int l = l0, t = t0;
            ClampToWork(ref l, ref t, w, h, work);
            if (HitsCursor(l, t, w, h, cursor, pad)) continue;
            int d = Math.Abs(l - left) + Math.Abs(t - top);
            if (d < bestDist) { bestDist = d; bestL = l; bestT = t; found = true; }
        }

        if (found) { left = bestL; top = bestT; }
    }

    private static bool HitsCursor(int left, int top, int w, int h, Native.POINT c, int pad) =>
        c.X >= left - pad && c.X <= left + w + pad && c.Y >= top - pad && c.Y <= top + h + pad;

    // ── 窗口交互 ────────────────────────────────────────────────────

    private void MoveTo(int left, int top, int w, int h) =>
        Native.SetWindowPos(_ipc.Window, IntPtr.Zero, left, top, w, h,
            Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);

    private Native.RECT WaitForWindow(CancellationToken token, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (token.IsCancellationRequested) return default;
            if (_ipc.IsVisibleFast)
            {
                var r = _ipc.GetRect();
                if (!r.IsEmpty) return r;
            }
            Thread.Sleep(2);
        }
        return default;
    }
}
