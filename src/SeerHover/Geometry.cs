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
    private readonly AppConfig _cfg;
    private readonly SeerIpc _ipc;
    private CancellationTokenSource? _cts;

    /// <summary>Seer 的最小窗口尺寸（首次被夹后学习到的值，物理像素）</summary>
    private static int _floorW;
    private static int _floorH;

    private const int DefaultMinW = 765;
    private const int DefaultMinH = 645;

    /// <summary>固定尺寸的硬上限：8K（7680×4320），4K / 5K2K 带鱼屏都在范围内</summary>
    public const int MaxFixedW = 7680;
    public const int MaxFixedH = 4320;

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

        int minW = _floorW > 0 ? _floorW : DefaultMinW;
        int minH = _floorH > 0 ? _floorH : DefaultMinH;

        // 原比例最大化：取源文件像素尺寸（图片走 WIC，视频走 Shell 属性；命中缓存时零开销）
        int srcW = 0, srcH = 0;
        if (controlSize && sizeMode == "auto")
        {
            (srcW, srcH) = MediaSize.Probe(target.Path);
            if (srcW > 0 && Logger.IsVerbose) Logger.Info($"source size: {srcW}x{srcH} for {target.Name}");
        }

        // 0) 抢在 Seer 显示之前设一次：窗口对象在显示前就存在，提前设好尺寸位置，
        //    它显示时就不会先按图片原始尺寸铺开再被我们缩回来。
        if (controlSize && _ipc.Window != IntPtr.Zero)
        {
            var mi0 = Native.MonitorOf(cursor.X, cursor.Y);
            var scr0 = mi0.rcMonitor.IsEmpty ? mi0.rcWork : mi0.rcMonitor;
            var work0 = mi0.rcWork.IsEmpty ? mi0.rcMonitor : mi0.rcWork;
            var (pw, ph) = TargetSize(cfg, sizeMode, srcW, srcH, scr0, minW, minH,
                new Native.RECT { Left = 0, Top = 0, Right = Math.Max(minW, cfg.Width), Bottom = Math.Max(minH, cfg.Height) });
            var pre = Place(target, new Native.RECT(), work0, cfg, pw, ph, cursor);
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

        var (width, height) = TargetSize(cfg, sizeMode, srcW, srcH, screen, minW, minH, first);

        var rect = Place(target, first, work, cfg, width, height, cursor);

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
                width = Math.Clamp(r.Width, minW, Math.Max(minW, Math.Min(screen.Width, MaxFixedW)));
                height = Math.Clamp(r.Height, minH, Math.Max(minH, Math.Min(screen.Height, MaxFixedH)));
                rect = Place(target, r, work, cfg, width, height, cursor);
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

    // ── 尺寸 ────────────────────────────────────────────────────────

    private static (int W, int H) TargetSize(GeometryConfig cfg, string sizeMode, int srcW, int srcH,
        Native.RECT screen, int minW, int minH, Native.RECT current)
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
            int cw = Math.Clamp(w, Math.Min(minW, maxW), maxW);
            int ch = Math.Clamp(h, Math.Min(minH, maxH), maxH);
            return (cw, ch);
        }

        // 固定尺寸：只受「屏幕尺寸」和 8K 上限约束，不再被占屏比压小，
        // 这样 4K 屏可以把高度直接设成 2160（铺满整屏）。
        int capW = Math.Max(minW, Math.Min(scrW, MaxFixedW));
        int capH = Math.Max(minH, Math.Min(scrH, MaxFixedH));

        int fw = cfg.Width > 0 ? cfg.Width : current.Width;
        int fh = cfg.Height > 0 ? cfg.Height : current.Height;
        fw = Math.Clamp(fw, Math.Min(minW, capW), capW);
        fh = Math.Clamp(fh, Math.Min(minH, capH), capH);
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
