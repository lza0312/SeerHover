using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace SeerHover;

/// <summary>
/// 覆盖在 Seer 音/视频预览窗原控制条「音量喇叭」上的小型控件条：[倍速图标][音量图标]。
///
/// 显隐与原控制条同逻辑（实测）：鼠标移到预览窗**下边缘、原控件高度范围内**时原条出现，
/// 离开即隐藏、悬停期间不空闲隐藏——本控件条用同一个底部激活区（ZoneH）驱动，
/// 40ms 快速通道只用 Win32 廉价调用（IsWindowVisible / GetWindowRect / GetCursorPos），
/// 300ms 慢通道才走 IPC 刷媒体类型。
///
/// 悬浮按钮时在控件条**正上方**弹出面板：面板水平中线对齐按钮中线（倍速面板在左半边上方、
/// 音量面板在右半边上方）；竖向滑块在上、数值在下——
/// 音量显示 100%（0-100%，滚轮 ±5%）、倍速显示 1.00X（滑块 0.25-3.00，滚轮 ±0.25 至 6.00）。
/// 鼠标移出面板/按钮约 80ms 后收起。
///
/// 坐标全部用**物理像素** + Native.SetWindowPos 直写，并吞掉 WM_DPICHANGED——
/// WinForms 在多 DPI 下的 Location/Bounds 缩放会把窗口挪到错误位置（实测 144 DPI 环境
/// 用 Location 定位偏到音量键左上方），不能依赖。
///
/// NOACTIVATE 窗口收不到 WM_MOUSEWHEEL / 点击命中也要物理坐标，全部走低级鼠标钩子。
/// 调速靠 Seer 的媒体快捷键（X = 降速、C = 升速），步进实测 0.2（2.0 显 1.8、6.0 显 5.0 反推）。
/// </summary>
internal sealed class MediaOverlayForm : Form
{
    private const int Pad = 3;
    private const int Btn = 26;                            // 按钮尺寸（物理 px）
    private const int Gap = 4;
    private const int BarW = Pad + Btn + Gap + Btn + Pad;  // 62
    private const int BarH = Btn + Pad * 2;                // 32
    private const int ZoneH = 56;                          // 底部激活区高度（原控制条区域）

    // 固定位置（物理 px，实测横版 1260x2160 与竖版窄窗 765x980 双样本一致）：
    // 条右缘距预览窗右缘 20、条垂直中心距底缘 28 —— 恰好盖住原静音喇叭 (右缘-40, 底缘-28)，
    // 且不遮挡时长显示。不做动态检测：横竖版窗口切换时检测易漂移，固定偏移双样本误差 ≤2px。
    public const int BarInsetRight = 12;     // 条右缘 ← 预览窗右缘
    public const int BarOffsetBottom = 28;   // 条垂直中心 ← 预览窗底缘

    // 原喇叭位置（供 --anchor-diag 报告对照；正式定位已改用上面两个常数）
    public static int AnchorFromRight = 40;
    public static int AnchorFromBottom = 28;

    // 按钮中心（客户区物理坐标）：倍速在左、音量在右（盖住原音量喇叭）
    private const int VolCx = Pad + Btn + Gap + Btn / 2;
    private const int VolCy = Pad + Btn / 2;
    private const int RateCx = Pad + Btn / 2;

    private readonly SeerV2 _v2;
    private readonly System.Windows.Forms.Timer _fast;   // 40ms：激活区显隐 / 物理定位 / 弹出面板
    private readonly System.Windows.Forms.Timer _slow;   // 300ms：IPC 刷媒体状态与窗口句柄
    private readonly MediaPopup _popup = new();

    private IntPtr _hwnd;
    private bool _mediaActive;
    private double _rate = 1.0;          // 初始值来自设置里的「默认倍速」（Seer 的 X/C 步进实测 0.2）
    private int _vol = 100;              // 初始值来自设置里的「默认音量」（会话音量硬上限 100%）
    private bool _muted;                 // Seer 会话静音态（PollState 每 300ms 刷新）
    private bool _volHover;
    private bool _rateHover;
    private int _outsideRuns;            // 光标既不在条上也不在面板上的连续快速跳数
    private Rectangle _physRect;         // 我们窗口的物理位置（WinForms Bounds 不可信）
    private Rectangle _volPhys;          // 按钮物理区（钩子 / OwnsCursor 用）
    private Rectangle _ratePhys;
    private Rectangle _seerPhys;         // 预览窗物理区（getState 上报，与真实屏幕一致；
                                         // 托盘进程的 GetWindowRect 被按进程 DPI 虚拟化过，不可信）

    private Native.HookProc? _wheelProc;   // 必须留住引用，否则委托被 GC 后钩子会崩
    private IntPtr _wheelHook;

    private static MediaOverlayForm? _instance;

    /// <summary>设置改了默认音量/倍速后刷新显示值（不改动当前实际音频）。</summary>
    public void SetDefaults(int vol, double rate)
    {
        _vol = Math.Clamp(vol, 0, 100);
        _rate = Math.Clamp(Math.Round(rate * 4) / 4.0, 0.25, 6.0);
        _popup.SyncValues(_vol, _rate);
        Invalidate();
    }

    /// <summary>光标是否落在我们控制条或弹出面板上（这些窗口 NOACTIVATE，不属于 Seer 进程，收窗判定要放行）。</summary>
    public static bool OwnsCursor(int x, int y)
    {
        var inst = _instance;
        if (inst == null || inst.IsDisposed) return false;
        Rectangle phys = inst._physRect;   // 局部拷贝消除 CS1690
        return phys.Contains(x, y) || inst._popup.BoundsContain(x, y);
    }

    public MediaOverlayForm(SeerV2 v2, int defaultVol = 100, double defaultRate = 1.0)
    {
        _v2 = v2;
        _vol = Math.Clamp(defaultVol, 0, 100);
        _rate = Math.Clamp(Math.Round(defaultRate * 4) / 4.0, 0.25, 6.0);
        _instance = this;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        Size = new Size(BarW, BarH);
        BackColor = Color.FromArgb(28, 28, 30);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw, true);
        TopMost = true;

        _fast = new System.Windows.Forms.Timer { Interval = 40 };
        _fast.Tick += (_, _) => SyncFast();
        _fast.Start();

        _slow = new System.Windows.Forms.Timer { Interval = 300 };
        _slow.Tick += (_, _) => PollState();
        _slow.Start();

        _popup.VolumeChanged += v => { _vol = v; AudioSession.SetVolume(v / 100f); };
        _popup.RateChanged += r => SetRate(r);

        InstallWheelHook();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000      // WS_EX_NOACTIVATE：不抢前台，按键留给 Seer
                       | 0x00000080;     // WS_EX_TOOLWINDOW：不出现在 Alt+Tab
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x02E0) return;   // WM_DPICHANGED：位置/尺寸全走物理 SetWindowPos，禁止 WinForms 重排
        base.WndProc(ref m);
    }

    // ---------- 显隐 / 定位 ----------

    /// <summary>300ms：IPC 拿预览状态（媒体类型 / 路径 / rect）与 Seer 窗口句柄。</summary>
    private void PollState()
    {
        try
        {
            _hwnd = new SeerIpc().Window;
            var st = _v2.PreviewState();
            _mediaActive = st is { Visible: true } &&
                           MediaKind.Of(st.Value.Path) is MediaKind.Audio or MediaKind.Video;
            if (st is { Visible: true } && st.Value.Rect is { } rc && !rc.IsEmpty)
                _seerPhys = Rectangle.FromLTRB(rc.Left, rc.Top, rc.Right, rc.Bottom);
            _muted = AudioSession.GetMute() ?? _muted;
            if (!_mediaActive && Visible) { Hide(); _popup.Hide(); }
        }
        catch { /* IPC 偶发失败：下一轮再试 */ }
    }

    /// <summary>40ms：只做 Win32 廉价调用。激活区显隐 + 物理定位 + 弹出面板。</summary>
    private void SyncFast()
    {
        try
        {
            if (!_mediaActive || _hwnd == IntPtr.Zero || !Native.IsWindowVisible(_hwnd) || _seerPhys.IsEmpty)
            {
                if (Visible) { Hide(); _popup.Hide(); }
                _physRect = Rectangle.Empty;
                return;
            }
            var r = _seerPhys;

            Native.GetCursorPos(out var cur);
            bool inZone = cur.X >= r.Left && cur.X < r.Right &&
                          cur.Y >= r.Bottom - ZoneH && cur.Y < r.Bottom;
            bool overOurs = _physRect.Contains(cur.X, cur.Y) || _popup.BoundsContain(cur.X, cur.Y);
            if (!inZone && !overOurs)
            {
                if (Visible) { Hide(); _popup.Hide(); }
                return;
            }

            // 固定位置：条右缘 = 窗右缘-20、条垂直中心 = 窗底缘-28（盖住原静音喇叭、不挡时长）
            int x = r.Right - BarInsetRight - BarW;
            int y = r.Bottom - BarOffsetBottom - BarH / 2;
            if (!Visible) Show();
            Native.SetWindowPos(Handle, (IntPtr)(-1) /*HWND_TOPMOST*/, x, y, BarW, BarH,
                0x0010 /*SWP_NOACTIVATE*/);   // 面板打开时也不降 z：面板自己每跳 TOPMOST（见 ShowAt 后的保持逻辑）
            _physRect = new Rectangle(x, y, BarW, BarH);
            _volPhys = new Rectangle(x + Pad + Btn + Gap, y + Pad, Btn, Btn);
            _ratePhys = new Rectangle(x + Pad, y + Pad, Btn, Btn);

            UpdateHover(cur);
        }
        catch { /* 偶发失败：下一轮再试 */ }
    }

    /// <summary>
    /// 锚点分析（静态，供正式检测与 --anchor-diag 诊断共用）：
    /// 截 r 底部 StripH 高的条，逐列统计亮像素、聚簇，取最右侧宽度像图标的簇 = 喇叭/静音喇叭。
    /// 实测（横版 1260x2160 与竖版窄窗 765x980 双样本一致）：
    /// Seer 控制条是紧凑单行 [文件][▶][进度条][🔇]，静音喇叭中心固定在 (右缘-40, 底缘-28)。
    /// 条带 48px 只盖条行——再高会把条上方视频画面的亮块也拍进来污染聚簇。
    /// 注意：正式检测前调用方必须先藏起本控件条与弹出面板，否则拍到自己的图标。
    /// </summary>
    public static (bool Ok, int Cx, int Cy, string Report) AnalyzeStrip(Rectangle r, string? dumpPath)
    {
        var report = new System.Text.StringBuilder();
        const int StripH = 48;
        try
        {
            int stripTop = r.Bottom - StripH;
            int w = Math.Min(r.Width, 1600);
            int x0 = r.Right - w;
            using var bmp = new Bitmap(w, StripH);
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(x0, stripTop, 0, 0, bmp.Size);
            if (dumpPath != null) bmp.Save(dumpPath, System.Drawing.Imaging.ImageFormat.Png);

            // 逐列统计亮像素（图标为浅色，控制条底为深色）；质心按像素数加权
            var cols = new List<(int X, long SumY, int N)>();
            var bits = bmp.LockBits(new Rectangle(0, 0, w, StripH), System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                unsafe
                {
                    byte* basePtr = (byte*)bits.Scan0;
                    int stride = bits.Stride;
                    for (int x = 0; x < w; x++)
                    {
                        long sumY = 0; int n = 0;
                        for (int y = 0; y < StripH; y++)
                        {
                            byte* p = basePtr + y * stride + x * 4;
                            if (p[0] > 150 && p[1] > 150 && p[2] > 150) { sumY += y; n++; }
                        }
                        if (n >= 2) cols.Add((x, sumY, n));
                    }
                }
            }
            finally { bmp.UnlockBits(bits); }
            report.AppendLine($"strip {w}x{StripH}@{x0},{stripTop} brightCols={cols.Count}");

            if (cols.Count == 0) return (false, 0, 0, report.ToString());

            // 相邻亮列聚簇（列间隙 ≤5px 视为同一图标）；质心 = Σ(坐标×像素)/Σ像素
            var clusters = new List<(int X0, int X1, double Cx, double Cy, int N)>();
            long sx = 0, sy = 0, sn = 0;
            int s0 = cols[0].X, s1 = cols[0].X;
            int prev = cols[0].X;
            void Flush()
            {
                if (sn > 0) clusters.Add((s0, s1, (double)sx / sn, stripTop + (double)sy / sn, (int)sn));
            }
            foreach (var (cx, sumY, n) in cols)
            {
                if (cx - prev > 5) { Flush(); s0 = cx; sx = 0; sy = 0; sn = 0; }
                s1 = cx;
                sx += (long)cx * n; sy += sumY; sn += n;
                prev = cx;
            }
            Flush();

            foreach (var c in clusters.TakeLast(12))
                report.AppendLine($"  cluster x[{c.X0 + x0}..{c.X1 + x0}] w={c.X1 - c.X0} cx={(int)Math.Round(c.Cx)} cy={(int)Math.Round(c.Cy)} n={c.N}");

            // 宽度像图标的簇（喇叭图标 ~15-30px；排除文本/进度条等超宽簇），取最右侧
            var cand = clusters.Where(c => c.X1 - c.X0 >= 6 && c.X1 - c.X0 <= 48).ToList();
            if (cand.Count == 0) { report.AppendLine("no icon-sized cluster"); return (false, 0, 0, report.ToString()); }
            var best = cand[^1];
            int fcx = (int)Math.Round(best.Cx) + x0;
            int fcy = (int)Math.Round(best.Cy);
            report.AppendLine($"picked cx={fcx} cy={fcy}  (fallback would be {r.Right - AnchorFromRight},{r.Bottom - AnchorFromBottom})");
            return (true, fcx, fcy, report.ToString());
        }
        catch (Exception ex)
        {
            report.AppendLine("ERR: " + ex.Message);
            return (false, 0, 0, report.ToString());
        }
    }

    /// <summary>悬浮高亮 + 弹出面板（40ms 通道）。移出面板与按钮 2 跳（~80ms）后收起。</summary>
    private void UpdateHover(Native.POINT cur)
    {
        bool inVol = _volPhys.Contains(cur.X, cur.Y);
        bool inRate = _ratePhys.Contains(cur.X, cur.Y);
        bool inside = _physRect.Contains(cur.X, cur.Y);
        bool overPopup = _popup.BoundsContain(cur.X, cur.Y);

        if (inVol != _volHover || inRate != _rateHover)
        {
            _volHover = inVol;
            _rateHover = inRate;
            Invalidate();
        }

        if (inVol && !_popup.IsHandlingVolume)
        {
            _outsideRuns = 0;
            _popup.ShowAt(1, _vol, _volPhys.Left + Btn / 2, _physRect.Top, _seerPhys);
        }
        else if (inRate && !_popup.IsHandlingRate)
        {
            _outsideRuns = 0;
            _popup.ShowAt(2, _rate, _ratePhys.Left + Btn / 2, _physRect.Top, _seerPhys);
        }
        else if (inside || overPopup)
        {
            _outsideRuns = 0;
        }
        else if (++_outsideRuns >= 2 && _popup.Visible)
        {
            _popup.Hide();   // 移出面板：~80ms 内收起，2 跳宽限只用于跨按钮与面板之间的缝隙
        }
    }

    // ---------- 低级鼠标钩子：滚轮 / 点击（NOACTIVATE 窗口收不到滚轮消息） ----------

    private void InstallWheelHook()
    {
        _wheelProc = WheelHookProc;
        _wheelHook = Native.SetWindowsHookExW(Native.WH_MOUSE_LL, _wheelProc, Native.GetModuleHandleW(null), 0);
        if (_wheelHook == IntPtr.Zero)
            Logger.Error("media wheel hook install failed: " + Marshal.GetLastWin32Error());
    }

    /// <summary>只读 MSLLHOOKSTRUCT 字段，不碰 UI——重活 BeginInvoke 丢回 UI 线程。吞掉已处理的事件。</summary>
    private IntPtr WheelHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && Visible && !_physRect.IsEmpty)
            {
                int msg = (int)wParam;
                int x = Marshal.ReadInt32(lParam, 0);
                int y = Marshal.ReadInt32(lParam, 4);

                if (msg == Native.WM_MOUSEWHEEL)
                {
                    short delta = (short)((Marshal.ReadInt32(lParam, 8) >> 16) & 0xFFFF);  // mouseData 高字
                    bool up = delta > 0;
                    if (_popup.BoundsContain(x, y))
                    {
                        BeginInvoke(() => _popup.Wheel(up)); return (IntPtr)1;
                    }
                    if (_volPhys.Contains(x, y)) { BeginInvoke(() => WheelVolume(up)); return (IntPtr)1; }
                    if (_ratePhys.Contains(x, y)) { BeginInvoke(() => WheelRate(up)); return (IntPtr)1; }
                }
                else if (msg == Native.WM_LBUTTONDOWN)
                {
                    if (_ratePhys.Contains(x, y))
                    {
                        // 直接点倍速图标：1x → 2x，其它 → 1x
                        BeginInvoke(() => SetRate(Math.Abs(_rate - 1.0) < 0.001 ? 2.0 : 1.0));
                        return (IntPtr)1;
                    }
                    if (_volPhys.Contains(x, y))
                    {
                        // 直接点音量图标 = 切换静音
                        BeginInvoke(() =>
                        {
                            bool cur = AudioSession.GetMute() ?? false;
                            _muted = !cur;
                            if (!AudioSession.SetMute(_muted)) Logger.Error("mute toggle failed (no seer audio session)");
                            Invalidate();
                        });
                        return (IntPtr)1;
                    }
                }
            }
        }
        catch { /* 钩子里绝不抛 */ }
        return Native.CallNextHookEx(_wheelHook, nCode, wParam, lParam);
    }

    /// <summary>悬浮音量图标滚轮：5% 步进（会话音量硬上限 100%）。</summary>
    private void WheelVolume(bool up)
    {
        _vol = Math.Clamp(_vol + (up ? 5 : -5), 0, 100);
        bool ok = AudioSession.SetVolume(_vol / 100f);
        if (!ok) Logger.Error($"volume set failed (no seer audio session) vol={_vol}");
        _popup.SyncValues(_vol, _rate);
    }

    /// <summary>悬浮倍速图标滚轮：0.2 步进（0.2-6.0，与 Seer 的 X/C 键步进一致）。</summary>
    private void WheelRate(bool up) => SetRate(_rate + (up ? 0.2 : -0.2));

    // ---------- 绘制 ----------

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = new GraphicsPath();
        int r = 6;
        path.AddArc(0, 0, r * 2, r * 2, 180, 90);
        path.AddArc(Width - r * 2, 0, r * 2, r * 2, 270, 90);
        path.AddArc(Width - r * 2, Height - r * 2, r * 2, r * 2, 0, 90);
        path.AddArc(0, Height - r * 2, r * 2, r * 2, 90, 90);
        path.CloseFigure();
        using (var bg = new SolidBrush(Color.FromArgb(216, 28, 28, 30)))
            g.FillPath(bg, path);
        using var pen = new Pen(Color.FromArgb(70, 255, 255, 255));
        g.DrawPath(pen, path);

        var f = new Font("Segoe MDL2 Assets", 10f);
        TextRenderer.DrawText(g, _muted ? "\uE74F" : "\uE767", f,
            new Rectangle(Pad + Btn + Gap, Pad, Btn, Btn), _volHover ? Color.White : Color.FromArgb(190, 255, 255, 255),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, "\uEC4A", f,
            new Rectangle(Pad, Pad, Btn, Btn), _rateHover ? Color.White : Color.FromArgb(190, 255, 255, 255),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    /// <summary>通过 Seer 的媒体快捷键调速（X = 降、C = 升），假定每步 0.25。</summary>
    private void SetRate(double target)
    {
        target = Math.Clamp(Math.Round(target * 5) / 5.0, 0.2, 6.0);
        if (Math.Abs(target - _rate) < 0.001) return;
        IntPtr w = new SeerIpc().Window;
        if (w == IntPtr.Zero) return;
        Native.SetForegroundWindow(w);
        Thread.Sleep(40);
        char key = target > _rate ? 'C' : 'X';
        int steps = (int)Math.Round(Math.Abs(target - _rate) / 0.2);
        for (int i = 0; i < steps; i++)
        {
            byte vk = (byte)(Native.VkKeyScanW(key) & 0xFF);
            Native.keybd_event(vk, 0, 0, UIntPtr.Zero);
            Native.keybd_event(vk, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
            Thread.Sleep(60);
        }
        _rate = target;
        _popup.SyncValues(_vol, _rate);
        Invalidate();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _fast.Stop();
        _slow.Stop();
        if (_wheelHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_wheelHook);
        _wheelHook = IntPtr.Zero;
        if (_instance == this) _instance = null;
        base.OnHandleDestroyed(e);
    }

    /// <summary>
    /// 弹出面板：竖向滑块在上、数值在下（音量 100% / 倍速 1.00X）。
    /// NOACTIVATE；位置/尺寸全走物理 SetWindowPos（与控件条同一套坐标）。
    /// 滑块拖动直接改值；滚轮由宿主的钩子转调 Wheel()。
    /// </summary>
    private sealed class MediaPopup : Form
    {
        public event Action<int>? VolumeChanged;    // 0-100
        public event Action<double>? RateChanged;   // 0.25-6.00

        public const int PhysW = 52;
        public const int PhysH = 184;
        private const int LabelH = 30;

        public bool IsHandlingVolume => Mode == 1;
        public bool IsHandlingRate => Mode == 2;
        private int Mode;   // 0=隐藏 1=音量 2=倍速

        private int _vol = 100;          // 0-100
        private double _rate = 1.0;      // 0.25-6.00
        private bool _dragging;
        private Rectangle _physRect;

        private Rectangle TrackRect => new(10, 10, PhysW - 20, PhysH - LabelH - 18);

        public MediaPopup()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            Size = new Size(PhysW, PhysH);
            BackColor = Color.FromArgb(28, 28, 30);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.ResizeRedraw, true);
            TopMost = true;
            Cursor = Cursors.Hand;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 | 0x00000080;
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x02E0) return;   // WM_DPICHANGED：同宿主，禁重排
            base.WndProc(ref m);
        }

        /// <summary>弹出（物理 px）：面板水平中心对齐按钮中心，底边贴控件条上沿，夹在预览窗内。</summary>
        public void ShowAt(int mode, object value, int btnCenterX, int barTopY, Rectangle seer)
        {
            Mode = mode;
            if (mode == 1) _vol = (int)value;
            else _rate = (double)value;

            int px = Math.Clamp(btnCenterX - PhysW / 2, seer.Left + 4, Math.Max(seer.Left + 4, seer.Right - PhysW - 4));
            int py = Math.Clamp(barTopY - PhysH, seer.Top + 4, Math.Max(seer.Top + 4, seer.Bottom - PhysH - 4));
            if (!Visible) Show();
            Native.SetWindowPos(Handle, (IntPtr)(-1) /*HWND_TOPMOST*/, px, py, PhysW, PhysH, 0x0010 /*NOACTIVATE*/);
            _physRect = new Rectangle(px, py, PhysW, PhysH);
            Invalidate();
        }

        public new void Hide()
        {
            Mode = 0;
            _dragging = false;
            base.Hide();
        }

        /// <summary>滚轮（宿主钩子转调）：音量 ±5，倍速 ±0.25。</summary>
        public void Wheel(bool up)
        {
            if (Mode == 1)
            {
                _vol = Math.Clamp(_vol + (up ? 5 : -5), 0, 100);
                VolumeChanged?.Invoke(_vol);
            }
            else if (Mode == 2)
            {
                _rate = Math.Clamp(Math.Round((_rate + (up ? 0.2 : -0.2)) * 5) / 5.0, 0.2, 6.0);
                RateChanged?.Invoke(_rate);
            }
            Invalidate();
        }

        /// <summary>滚轮 / 快捷键改值后同步显示。</summary>
        public void SyncValues(int vol, double rate)
        {
            _vol = vol;
            _rate = rate;
            Invalidate();
        }

        public bool BoundsContain(int x, int y) => Visible && _physRect.Contains(x, y);

        // ---- 竖向滑块 ----

        private double Normalized
        {
            get
            {
                var t = TrackRect;
                if (Mode == 1) return _vol / 100.0;
                return (_rate - 0.2) / (3.0 - 0.2);   // 滑块范围 0.2-3.0
            }
        }

        private void SetFromY(int y)
        {
            var t = TrackRect;
            double n = Math.Clamp((t.Bottom - y) / (double)t.Height, 0.0, 1.0);
            if (Mode == 1)
            {
                int v = (int)Math.Round(n * 100);
                if (v != _vol) { _vol = v; VolumeChanged?.Invoke(v); }
            }
            else
            {
                double r = Math.Round((0.2 + n * (3.0 - 0.2)) * 100) / 100.0;
                if (Math.Abs(r - _rate) > 0.0001) _rate = r;   // 倍速拖动只更新显示，松手才发键（OnMouseUp）
            }
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left && TrackRect.Contains(e.Location))
            {
                _dragging = true;
                SetFromY(e.Y);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging && e.Button == MouseButtons.Left) SetFromY(e.Y);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_dragging && Mode == 2) RateChanged?.Invoke(_rate);   // 松手才发调速键，避免拖动过程狂发
            _dragging = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var bg = new SolidBrush(BackColor))
                g.FillRectangle(bg, ClientRectangle);
            using var pen = new Pen(Color.FromArgb(70, 255, 255, 255));
            g.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);

            var t = TrackRect;
            int cx = t.Left + t.Width / 2;
            // 滑轨
            using (var trackPen = new Pen(Color.FromArgb(90, 255, 255, 255), 4f))
                g.DrawLine(trackPen, cx, t.Top, cx, t.Bottom);
            // 已选段（自下而上）
            double n = Normalized;
            int thumbY = t.Bottom - (int)Math.Round(n * t.Height);
            using (var fillPen = new Pen(Color.FromArgb(120, 160, 255), 4f))
                g.DrawLine(fillPen, cx, thumbY, cx, t.Bottom);
            // 滑块圆点
            using (var thumb = new SolidBrush(Color.White))
                g.FillEllipse(thumb, cx - 6, thumbY - 6, 12, 12);

            // 底部数值
            string label = Mode == 1 ? $"{_vol}%" : $"{_rate:0.00}X";
            TextRenderer.DrawText(g, label, new Font("Segoe UI", 10f, FontStyle.Bold),
                new Rectangle(0, PhysH - LabelH, PhysW, LabelH), Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}
