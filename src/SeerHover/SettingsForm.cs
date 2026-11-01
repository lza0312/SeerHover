using System.Drawing;
using System.Windows.Forms;

namespace SeerHover;

/// <summary>设置窗口需要宿主（托盘）提供的几个能力。</summary>
internal sealed class SettingsHost
{
    public Func<string, string> Command { get; set; } = _ => string.Empty;
    public Func<bool> IsStartupEnabled { get; set; } = () => false;
    public Action<bool> SetStartup { get; set; } = _ => { };

    /// <summary>应用配置；第二个参数为 true 时不弹托盘气泡（「应用」按钮连续点按用）。</summary>
    public Action<AppConfig, bool> Apply { get; set; } = (_, _) => { };

    /// <summary>拖动「界面缩放」时的即时预览（不改配置，只换字体度量）。</summary>
    public Action<float> PreviewScale { get; set; } = _ => { };

    /// <summary>设置窗口关闭时调用：把缩放还原成「已保存配置」的比例（取消时要用）。</summary>
    public Action EndPreview { get; set; } = () => { };

    /// <summary>记住窗口大小（关窗时调用，直接落盘，不触发配置生效流程）。</summary>
    public Action<int, int> RememberSize { get; set; } = (_, _) => { };
}

/// <summary>
/// 图形化设置面板（所有 config.json 项都在这儿改，改完点保存生效）。
///
/// 布局说明：
/// - 窗口可自由改变大小 / 最大化，卡片与每一行都按当前宽度重排（见 Stack / CardPanel / SettingRow）。
/// - DPI：窗体禁用 WinForms 自动缩放（AutoScaleMode.None），改为手动按 DeviceDpi/96 缩放布局（S()）。
///   字体是磅值，GDI 会按目标 DC 的 DPI 自行放大，两边天然一致，不会出现「字大框小」的截断。
/// - 滚动条是自绘的（ScrollHost），系统原生滚动条无法跟随深色主题。
/// </summary>
internal sealed class SettingsForm : Form, IMessageFilter
{
    private const int MinPageW = 520;       // 96 DPI 逻辑
    private const int RowPad = 14;
    private const int CardMargin = 8;

    private readonly SettingsHost _host;
    private AppConfig _work;
    private float _ui = 1f;                 // DPI 缩放系数 = DeviceDpi / 96
    private float _scale = 1f;              // 界面缩放系数（设置项，1.0 = 100%）
    private readonly System.Windows.Forms.Timer _scaleTimer;

    private NavRail _rail = null!;
    private Panel _content = null!;
    private readonly List<ScrollHost> _pages = new();

    private int S(int v) => (int)Math.Round(v * _ui * _scale);

    public SettingsForm(AppConfig cfg, SettingsHost host)
    {
        _host = host;
        _work = cfg.Clone();
        _ui = DeviceDpi / 96f;
        _scale = _work.Scale;

        // 打开前先把主题定下来（config 里没记录时跟随系统），字号按设置里的缩放重建
        Theme.SetScale(_scale);
        Theme.Apply(_work.DarkMode ?? Theme.DetectSystemDark());

        // 拖动缩放滑杆时不要每一格都重建窗口，停手后再套用
        _scaleTimer = new System.Windows.Forms.Timer { Interval = 260 };
        _scaleTimer.Tick += (_, _) => { _scaleTimer.Stop(); ApplyScaleLive(); };

        Text = "SeerHover 设置";
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        KeyPreview = true;
        Icon = SystemIcons.Application;

        AutoScaleMode = AutoScaleMode.None;
        ApplyWindowSize();

        BuildChrome();
        BuildPages();

        Application.AddMessageFilter(this);
    }

    /// <summary>默认尺寸 / 上次关闭时记住的尺寸（夹到屏幕工作区内）。记忆值按 100% 缩放存储。</summary>
    private void ApplyWindowSize()
    {
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        int maxW = Math.Max(S(640), wa.Width - S(40));
        int maxH = Math.Max(S(420), wa.Height - S(40));

        int w = _work.SettingsWidth > 0 ? (int)Math.Round(_work.SettingsWidth * _scale) : S(900);
        int h = _work.SettingsHeight > 0 ? (int)Math.Round(_work.SettingsHeight * _scale) : S(780);

        // 最小值也受屏幕限制：1080p + 160% 缩放时别把窗口撑出屏幕
        MinimumSize = new Size(Math.Min(S(770), maxW), Math.Min(S(520), maxH));
        ClientSize = new Size(Math.Clamp(w, MinimumSize.Width, maxW), Math.Clamp(h, MinimumSize.Height, maxH));
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.SetDarkTitleBar(Handle, Theme.IsDark);

        // 句柄落在另一块 DPI 不同的屏幕上时按新 DPI 重建一次，保证比例正确
        float ui = DeviceDpi / 96f;
        if (Math.Abs(ui - _ui) > 0.01f)
        {
            _ui = ui;
            ApplyWindowSize();
            RebuildAll(_rail?.SelectedIndex ?? 0);
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Application.RemoveMessageFilter(this);
        _scaleTimer.Stop();
        _scaleTimer.Dispose();
        // 缩放可能只是预览过（点了取消），这里还原成已保存配置的比例
        try { _host.EndPreview(); } catch { }
        base.OnFormClosed(e);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // 记住尺寸（最大化时记 RestoreBounds，否则下次打开还是最大化）；按 100% 缩放记，换缩放后窗口大小才合理
        var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        try { _host.RememberSize((int)Math.Round(b.Width / _scale), (int)Math.Round(b.Height / _scale)); } catch { }
        base.OnFormClosing(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // 正在输入框里打字时，Esc 只应还原输入，不该关窗口
        bool typing = ActiveControl is TextBoxBase tb && (tb.Parent is NumberField || tb.Parent is TextArea);
        if (e.KeyCode == Keys.Escape && !typing) { Close(); e.Handled = true; }
        base.OnKeyDown(e);
    }

    // ── 滚轮：WinForms 把 WM_MOUSEWHEEL 发给焦点控件，子控件不转发，
    //    这里统一截下来交给当前页的自绘滚动容器（多行文本框转给它自己）。
    bool IMessageFilter.PreFilterMessage(ref Message m)
    {
        const int WM_MOUSEWHEEL = 0x020A;
        if (m.Msg != WM_MOUSEWHEEL || !IsHandleCreated) return false;

        int delta = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
        if (delta == 0) return false;

        var pos = Cursor.Position;

        // 光标在输入框上：数值框改步进，多行框转给 Edit 自己滚
        var under = Control.FromChildHandle(Native.WindowFromPoint(new Native.POINT { X = pos.X, Y = pos.Y }));
        if (under is TextBoxBase tb)
        {
            if (tb.Parent is NumberField nf) nf.StepBy(delta);
            else
            {
                Native.SendMessageW(tb.Handle, WM_MOUSEWHEEL, m.WParam, m.LParam);
                (tb.Parent as TextArea)?.RefreshBar();
            }
            return true;
        }
        var taHost = under as TextArea ?? under?.Parent as TextArea;
        if (taHost != null)
        {
            Native.SendMessageW(taHost.Box.Handle, WM_MOUSEWHEEL, m.WParam, m.LParam);
            taHost.RefreshBar();
            return true;
        }

        var host = _pages.FirstOrDefault(p => p.Visible);
        if (host == null) return false;
        if (!host.RectangleToScreen(host.ClientRectangle).Contains(pos)) return false;

        host.ScrollBy(-Math.Sign(delta) * S(56) * 3);
        return true;
    }

    // ── 骨架 ────────────────────────────────────────────────────────

    private void BuildChrome()
    {
        SuspendLayout();

        _content = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Bg,
            Padding = new Padding(S(20), S(14), S(20), S(10))
        };

        _rail = new NavRail
        {
            Dock = DockStyle.Left,
            Width = S(184),
            SubHeader = "悬停预览 · v" + (typeof(SettingsForm).Assembly.GetName().Version?.ToString(3) ?? "1.0.0")
        };
        _rail.Items.AddRange(new[] { "常规", "窗口", "过滤", "高级" });
        _rail.SelectedIndexChanged += (_, _) => ShowPage(_rail.SelectedIndex);

        var bottom = BuildBottomBar();

        Controls.Add(_content);
        Controls.Add(_rail);
        Controls.Add(bottom);

        ResumeLayout();
    }

    private Panel BuildBottomBar()
    {
        var bar = new Panel { Dock = DockStyle.Bottom, Height = S(64), BackColor = Theme.Surface };
        bar.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawLine(pen, 0, 0, bar.Width, 0);
        };

        var reset = new PillButton("恢复默认", false) { Size = new Size(S(104), S(34)), Location = new Point(S(22), S(15)) };
        reset.Click += (_, _) => ResetDefaults();

        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            Width = S(330),
            FlowDirection = FlowDirection.RightToLeft,   // 先加的靠右：确认 / 取消 / 应用
            WrapContents = false,
            BackColor = Theme.Surface,
            Padding = new Padding(0, S(15), S(18), 0)
        };

        var confirm = new PillButton("确认") { Size = new Size(S(96), S(34)), Margin = new Padding(S(10), 0, 0, 0) };
        confirm.Click += (_, _) => ApplySettings(close: true);
        var cancel = new PillButton("取消", false) { Size = new Size(S(88), S(34)), Margin = new Padding(S(10), 0, 0, 0) };
        cancel.Click += (_, _) => Close();
        var apply = new PillButton("应用", false) { Size = new Size(S(88), S(34)), Margin = new Padding(0) };
        apply.Click += (_, _) => ApplySettings(close: false);

        flow.Controls.Add(confirm);
        flow.Controls.Add(cancel);
        flow.Controls.Add(apply);

        bar.Controls.Add(flow);
        bar.Controls.Add(reset);
        return bar;
    }

    private void BuildPages()
    {
        _pages.Clear();
        _pages.Add(BuildGeneralPage());
        _pages.Add(BuildWindowPage());
        _pages.Add(BuildFilterPage());
        _pages.Add(BuildAdvancedPage());

        foreach (var p in _pages)
        {
            p.Visible = false;
            _content.Controls.Add(p);
        }
        ShowPage(_rail.SelectedIndex < 0 ? 0 : _rail.SelectedIndex);
    }

    private void ShowPage(int index)
    {
        if (index < 0 || index >= _pages.Count) return;
        for (int i = 0; i < _pages.Count; i++) _pages[i].Visible = i == index;
        _pages[index].ScrollToTop();
        if (_rail.SelectedIndex != index) _rail.SelectedIndex = index;
    }

    /// <summary>换主题后整窗重建（自绘控件不会自己刷新颜色）。</summary>
    private void RebuildAll(int page)
    {
        SuspendLayout();
        var old = Controls.Cast<Control>().ToList();
        Controls.Clear();
        foreach (var c in old) c.Dispose();

        BuildChrome();
        BuildPages();
        Font = Theme.Body;
        _rail.SelectedIndex = Math.Clamp(page, 0, _pages.Count - 1);
        ShowPage(_rail.SelectedIndex);
        ResumeLayout(true);
        Native.SetDarkTitleBar(Handle, Theme.IsDark);
        ClearInitialFocus();
        Invalidate(true);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ClearInitialFocus();
    }

    /// <summary>
    /// 打开 / 重建时别把焦点丢在数值框里（会整段选中，看起来像被锁住）。
    /// 焦点交回窗体本身，用户点哪儿算哪儿。
    /// </summary>
    private void ClearInitialFocus()
    {
        if (ActiveControl is TextBoxBase tb && tb.Parent is NumberField) ActiveControl = null;
    }

    /// <summary>
    /// 「界面缩放」停手后调用：换字体 → 窗口按同比例放大/缩小（否则看着像变小了）→ 整窗重建。
    /// 重建期间鼠标捕获已经释放（滑杆 MouseUp 之后才触发），不会打断拖动。
    /// </summary>
    private void ApplyScaleLive()
    {
        float next = _work.Scale;
        if (Math.Abs(next - _scale) < 0.005f) return;

        float ratio = next / _scale;
        _scale = next;
        Theme.SetScale(_scale);
        try { _host.PreviewScale(_scale); } catch { }

        var wa = Screen.FromControl(this).WorkingArea;
        int maxW = Math.Max(S(640), wa.Width - S(40));
        int maxH = Math.Max(S(420), wa.Height - S(40));
        int minW = Math.Min(S(770), maxW);
        int minH = Math.Min(S(520), maxH);

        MinimumSize = new Size(minW, minH);
        ClientSize = new Size(
            Math.Clamp((int)Math.Round(ClientSize.Width * ratio), minW, maxW),
            Math.Clamp((int)Math.Round(ClientSize.Height * ratio), minH, maxH));

        RebuildAll(_rail.SelectedIndex);
    }

    private void SetDarkMode(bool dark)
    {
        _work.DarkMode = dark;
        Theme.Apply(dark);
        int page = _rail.SelectedIndex;
        BeginInvoke(new Action(() => RebuildAll(page)));
    }

    /// <summary>缩放滑杆：先记下值，停手 260ms 后再重建窗口（拖动中途重建会丢鼠标捕获）。</summary>
    private void SetUiScale(int percent)
    {
        _work.UiScale = Math.Clamp(percent, 80, 160);
        _scaleTimer.Stop();
        _scaleTimer.Start();
    }

    private void ResetDefaults()
    {
        var dlg = MessageBox.Show(this, "把全部设置恢复为默认值？\n（不会立即写入，仍需点“应用”或“确认”）",
            "SeerHover", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (dlg != DialogResult.OK) return;

        _work = new AppConfig();
        _scale = _work.Scale;
        Theme.SetScale(_scale);
        Theme.Apply(_work.DarkMode ?? Theme.DetectSystemDark());
        RebuildAll(0);
    }

    /// <summary>应用按钮：写入并生效，窗口不关；确认按钮：写入 + 关闭。</summary>
    private void ApplySettings(bool close)
    {
        _work.Normalize();
        _host.Apply(_work, !close);
        if (close) Close();
    }

    // ── 页面 ────────────────────────────────────────────────────────

    private ScrollHost BuildGeneralPage()
    {
        var (page, stack) = NewPage();

        stack.Section("基本");
        stack.Card(
            ToggleRow("启用悬停预览", "总开关，关掉后等同于暂停。", _work.Enabled, v => _work.Enabled = v),
            ToggleRow("文件夹也预览", "默认只对文件生效；开启后悬停文件夹同样弹出预览。", _work.PreviewFolders, v => _work.PreviewFolders = v),
            ToggleRow("开机自启", "登录 Windows 后在托盘里自动运行。", _host.IsStartupEnabled(), v => _host.SetStartup(v)));

        stack.Gap(16);
        stack.Section("外观");
        stack.Card(
            ToggleRow("深色模式", "设置窗口与托盘菜单的配色；首次打开时跟随系统，切换后会记住选择。",
                Theme.IsDark, SetDarkMode),
            SliderRow("界面缩放", "设置窗口和托盘图标右键菜单的整体大小（字号、间距一起缩放）。拖动后立即预览，点「应用 / 确认」长期生效。",
                80, 160, 5, _work.UiScale, "%", SetUiScale));

        stack.Gap(16);
        stack.Section("时机");
        stack.Card(
            SliderRow("悬停触发延迟", "光标停在同一个文件上多久后弹出预览。调小更跟手，调大更不容易误触。",
                100, 1500, 10, _work.DwellMs, "ms", v => _work.DwellMs = v),
            SliderRow("离开后收起延迟", "光标离开文件后多久收回预览窗。", 0, 800, 10, _work.HideGraceMs, "ms", v => _work.HideGraceMs = v));

        stack.Gap(16);
        stack.Section("抑制");
        stack.Card(
            ToggleRow("按住 Ctrl / Shift / Alt 时不触发", "避免和框选、多选、拖拽操作冲突。", _work.SuppressOnModifiers, v => _work.SuppressOnModifiers = v),
            ToggleRow("点击文件时收起预览", "点一下就收窗，不用等光标移开。", _work.HideOnClick, v => _work.HideOnClick = v));

        stack.Gap(16);
        stack.Section("鼠标");
        stack.Card(
            ToggleRow("双击空白处回到上一层",
                "在资源管理器文件列表的空白处双击，等同于按 Alt+↑ 返回上一层文件夹；双击文件/文件夹本身不受影响。",
                _work.DoubleClickUp, v => _work.DoubleClickUp = v));

        stack.Finish();
        return page;
    }

    private ScrollHost BuildWindowPage()
    {
        var (page, stack) = NewPage();

        // 当前屏幕（物理像素）：写进说明里，方便直接填「铺满整屏」的数值
        var mon = Native.MonitorOf(Cursor.Position.X, Cursor.Position.Y).rcMonitor;
        string scrText = mon.IsEmpty ? "未知" : $"{mon.Width}×{mon.Height}";

        stack.Section("位置");
        stack.Card(
            SegRow("弹出位置", "预览窗口出现在哪里。「光标旁」会自动挑空间更大的方向。",
                new[] { "光标旁", "屏幕角落", "固定坐标" }, ModeIndex(_work.Geometry.Mode), i => _work.Geometry.Mode = ModeValue(i)),
            ToggleRow("不遮挡鼠标", "自动避开鼠标和文件图标所在的位置；空间不够时仍会贴边。",
                _work.Geometry.AvoidCursor, v => _work.Geometry.AvoidCursor = v),
            SliderRow("与文件的间距", "窗口和文件图标之间留的空隙。", 0, 80, 2, _work.Geometry.Gap, "px", v => _work.Geometry.Gap = v),
            SegRow("角落", "仅「屏幕角落」模式生效。", new[] { "左上", "右上", "左下", "右下" },
                CornerIndex(_work.Geometry.Corner), i => _work.Geometry.Corner = CornerValue(i)),
            SliderRow("屏幕边距", "窗口到屏幕边缘的距离。", 0, 120, 4, _work.Geometry.Margin, "px", v => _work.Geometry.Margin = v),
            SliderRow("固定坐标 X", "仅「固定坐标」模式生效；多显示器左侧可以是负数。", -7680, 7680, 10, _work.Geometry.X, "px", v => _work.Geometry.X = v),
            SliderRow("固定坐标 Y", "仅「固定坐标」模式生效；多显示器上方可以是负数。", -4320, 4320, 10, _work.Geometry.Y, "px", v => _work.Geometry.Y = v));

        stack.Gap(16);
        stack.Section("尺寸");
        stack.Card(
            SegRow("尺寸策略", "「原比例最大化」按图片/视频的原始比例显示：放得下就用原尺寸，放不下按屏幕缩小。",
                new[] { "固定尺寸", "原比例最大化", "跟随 Seer" }, SizeModeIndex(_work.Geometry.SizeMode), i => _work.Geometry.SizeMode = SizeModeValue(i)),
            SliderRow("宽度", $"仅「固定尺寸」使用。可直接输入精确像素（上限 7680，兼容 8K / 带鱼屏）。当前屏幕 {scrText}。",
                765, 7680, 5, Math.Max(765, _work.Geometry.Width), "px", v => _work.Geometry.Width = v),
            SliderRow("高度", $"仅「固定尺寸」使用。可直接输入（上限 4320）。想铺满屏幕高度就填 {(mon.IsEmpty ? 2160 : mon.Height)}。",
                645, 4320, 5, Math.Max(645, _work.Geometry.Height), "px", v => _work.Geometry.Height = v),
            SliderRow("最大占屏比", "「原比例最大化」时窗口最多占屏幕的比例；固定尺寸不受它限制。",
                30, 100, 5, (int)Math.Round(_work.Geometry.MaxScreenRatio * 100), "%", v => _work.Geometry.MaxScreenRatio = v / 100.0));

        stack.Finish();
        return page;
    }

    private ScrollHost BuildFilterPage()
    {
        var (page, stack) = NewPage();

        stack.Section("排除");
        stack.Card(
            TextRow("不预览的扩展名",
                "用逗号、空格或换行分隔，例如：psd, ai, mp4（不含点，会自动转小写）。",
                string.Join(", ", _work.ExcludeExtensions), 120,
                text => _work.ExcludeExtensions = SplitList(text, true)),
            TextRow("不预览的路径",
                "每行一个目录前缀，例如：D:\\游戏 或 \\\\nas\\视频（不区分大小写）。",
                string.Join("\r\n", _work.ExcludePaths), 120,
                text => _work.ExcludePaths = SplitList(text, false)));

        stack.Gap(10);
        stack.Note("提示：路径按前缀匹配；扩展名和路径都在识别到文件后立即判断，命中就不弹预览。");

        stack.Finish();
        return page;
    }

    private ScrollHost BuildAdvancedPage()
    {
        var (page, stack) = NewPage();

        stack.Section("校验");
        stack.Card(
            ToggleRow("回查预览的是否同一个文件", "Seer 若开着「跟随选中项」会把画面切走，开启回查会自动收窗。",
                _work.VerifyPreview, v => _work.VerifyPreview = v),
            SliderRow("回查延迟", "要大于 Seer 内部刷新间隔，太短容易误判。", 300, 1500, 50, _work.VerifyDelayMs, "ms", v => _work.VerifyDelayMs = v));

        stack.Gap(16);
        stack.Section("性能与日志");
        stack.Card(
            SliderRow("轮询间隔", "越小越跟手，CPU 占用略高。", 25, 200, 5, _work.PollMs, "ms", v => _work.PollMs = v),
            ToggleRow("记录详细日志", "排查问题时可开，日常建议关闭。", _work.VerboseLog, v => _work.VerboseLog = v));

        stack.Gap(16);
        stack.Section("Seer 联动");
        stack.Card(
            ButtonRow(
                ("修复「跟随选中项」", () => _host.Command("fix-tracking")),
                ("恢复「跟随选中项」", () => _host.Command("restore-tracking")),
                ("开启「固定窗口尺寸」", () => _host.Command("keep-window")),
                ("恢复窗口自适应", () => _host.Command("restore-window")),
                ("打开配置文件", () => _host.Command("open-config")),
                ("打开日志", () => _host.Command("open-log"))));

        stack.Finish();
        return page;
    }

    // ── 行构件 ──────────────────────────────────────────────────────

    private static Label TitleLabel(string title) => new()
    {
        Text = title,
        AutoSize = false,
        Font = Theme.BodyStrong,
        ForeColor = Theme.Text,
        BackColor = Color.Transparent,
        TextAlign = ContentAlignment.MiddleLeft,
        UseMnemonic = false
    };

    private static WrapLabel DescLabel(string desc) => new()
    {
        Text = desc,
        Font = Theme.Small,
        ForeColor = Theme.Muted,
        BackColor = Color.Transparent,
        Visible = !string.IsNullOrEmpty(desc)
    };

    /// <summary>摆放标题 / 说明（说明按实际换行行数撑高），并设置行高。返回（说明块高度, 块起始 y）。</summary>
    private (int BlockH, int Y) LayoutText(SettingRow row, Label t, WrapLabel d, int labelW, int minRowH, bool top)
    {
        int titleH = S(22);
        int w = Math.Max(S(60), labelW);
        int descH = d.Visible ? d.MeasureHeight(w) : 0;
        int blockH = titleH + (descH > 0 ? S(2) + descH : 0);
        int h = Math.Max(minRowH, blockH + S(12));
        row.Height = h;

        int y = top ? S(4) : Math.Max(S(2), (h - blockH) / 2);
        t.Bounds = new Rectangle(0, y, w, titleH);
        if (descH > 0) d.Bounds = new Rectangle(0, y + titleH + S(2), w, descH);
        return (blockH, y);
    }

    private SettingRow ToggleRow(string title, string desc, bool value, Action<bool> set)
    {
        var t = TitleLabel(title);
        var d = DescLabel(desc);
        var sw = new ToggleSwitch { Checked = value };

        var row = new SettingRow((r, w) =>
        {
            int swW = S(46), swH = S(26), gap = S(18);
            LayoutText(r, t, d, w - swW - gap, S(62), false);
            sw.Size = new Size(swW, swH);
            sw.Location = new Point(w - swW, (r.Height - swH) / 2);
        });

        sw.CheckedChanged += (_, _) => set(sw.Checked);
        row.Controls.Add(t);
        row.Controls.Add(d);
        row.Controls.Add(sw);

        EventHandler hit = (_, _) => sw.Checked = !sw.Checked;
        row.Click += hit;
        t.Click += hit;
        d.Click += hit;
        return row;
    }

    private SettingRow SliderRow(string title, string desc, int min, int max, int step, int value, string suffix, Action<int> set)
    {
        var t = TitleLabel(title);
        var d = DescLabel(desc);
        var nf = new NumberField(Math.Clamp(value, min, max), min, max, step, suffix);
        var sl = new SliderControl
        {
            Minimum = min,
            Maximum = max,
            Step = step,
            Value = Math.Clamp(value, min, max)
        };

        bool sync = false;
        // 手动输入：滑杆跟着走（滑杆会按 step 取整，回写保持一致）
        nf.ValueChanged += (_, _) =>
        {
            if (sync) return;
            sync = true;
            sl.Value = nf.Value;
            nf.SetValueFromCode(sl.Value);
            sync = false;
            set(sl.Value);
        };
        // 拖动滑杆：数值框跟着走（连聚焦中的输入框也要刷新，否则失焦时会把旧值写回去）
        sl.ValueChanged += (_, _) =>
        {
            if (sync) return;
            sync = true;
            nf.SetValueFromCode(sl.Value);
            sync = false;
            set(sl.Value);
        };

        var row = new SettingRow((r, w) =>
        {
            int fieldW = Math.Clamp(S(104), S(84), S(132));
            int fieldH = S(28);

            if (w < S(440))
            {
                // 窄窗口：数值框右对齐独占一行，滑杆在下面占满整行
                var (blockH, _) = LayoutText(r, t, d, w, 0, true);
                int y = blockH + S(10);
                nf.SetBounds(w - fieldW, y, fieldW, fieldH);
                sl.SetBounds(0, y + fieldH + S(6), w, S(26));
                r.Height = y + fieldH + S(6) + S(26) + S(10);
            }
            else
            {
                int totalW = Math.Clamp((int)(w * 0.48), S(230), S(360));
                int sliderW = Math.Max(S(110), totalW - fieldW - S(10));
                LayoutText(r, t, d, w - totalW - S(18), S(76), false);
                int cy = r.Height / 2;
                nf.SetBounds(w - fieldW, cy - fieldH / 2, fieldW, fieldH);
                sl.SetBounds(w - fieldW - S(10) - sliderW, cy - S(13), sliderW, S(26));
            }
        });

        row.Controls.Add(t);
        row.Controls.Add(d);
        row.Controls.Add(nf);
        row.Controls.Add(sl);
        return row;
    }

    private SettingRow SegRow(string title, string desc, string[] options, int index, Action<int> set)
    {
        var t = TitleLabel(title);
        var d = DescLabel(desc);
        var seg = new Segmented
        {
            Options = options,
            SelectedIndex = Math.Clamp(index, 0, options.Length - 1)
        };
        seg.SelectedIndexChanged += (_, _) => set(seg.SelectedIndex);

        var row = new SettingRow((r, w) =>
        {
            if (w < S(440))
            {
                var (blockH, _) = LayoutText(r, t, d, w, 0, true);
                int y = blockH + S(12);
                seg.SetBounds(0, y, w, S(34));
                r.Height = y + S(34) + S(12);
            }
            else
            {
                int segW = Math.Min(S(350), Math.Max(S(250), (int)(w * 0.48)));
                LayoutText(r, t, d, w - segW - S(18), S(72), false);
                seg.SetBounds(w - segW, (r.Height - S(34)) / 2, segW, S(34));
            }
        });

        row.Controls.Add(t);
        row.Controls.Add(d);
        row.Controls.Add(seg);
        return row;
    }

    private SettingRow TextRow(string title, string desc, string text, int boxHeightLogical, Action<string> set)
    {
        var t = TitleLabel(title);
        var d = DescLabel(desc);
        int boxH = S(boxHeightLogical);
        var box = new TextArea(text, boxH);
        box.Box.TextChanged += (_, _) => set(box.Box.Text);

        var row = new SettingRow((r, w) =>
        {
            var (blockH, _) = LayoutText(r, t, d, w, 0, true);
            int y = blockH + S(10);
            box.SetBounds(0, y, w, boxH);
            r.Height = y + boxH + S(8);
        });

        row.Controls.Add(t);
        row.Controls.Add(d);
        row.Controls.Add(box);
        return row;
    }

    private SettingRow ButtonRow(params (string Label, Func<string> Run)[] items)
    {
        var buttons = new PillButton[items.Length];
        for (int i = 0; i < items.Length; i++)
        {
            var (label, run) = items[i];
            var btn = new PillButton(label, false) { Font = Theme.Body };
            btn.Click += (_, _) =>
            {
                string msg;
                try { msg = run(); }
                catch (Exception ex) { msg = "执行失败：" + ex.Message; }
                if (!string.IsNullOrWhiteSpace(msg))
                    MessageBox.Show(this, msg, "SeerHover", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            buttons[i] = btn;
        }

        var row = new SettingRow((r, w) =>
        {
            int per = w >= S(520) ? 3 : w >= S(320) ? 2 : 1;
            int gapX = S(10), gapY = S(10);
            int btnW = Math.Max(S(80), (w - gapX * (per - 1)) / per);

            for (int i = 0; i < buttons.Length; i++)
            {
                int cx = i % per, cy = i / per;
                buttons[i].SetBounds(cx * (btnW + gapX), S(8) + cy * (S(34) + gapY), btnW, S(34));
            }

            int rows = (buttons.Length + per - 1) / per;
            r.Height = S(8) + rows * (S(34) + gapY) + S(2);
        });

        foreach (var b in buttons) row.Controls.Add(b);
        return row;
    }

    // ── 工具 ────────────────────────────────────────────────────────

    private (ScrollHost Page, Stack Stack) NewPage()
    {
        var host = new ScrollHost { Dock = DockStyle.Fill, BackColor = Theme.Bg };
        var stack = new Stack(host, S(RowPad), S(CardMargin), S(MinPageW), S);
        host.ViewportChanged += (_, _) => stack.Reflow();
        return (host, stack);
    }

    private static int ModeIndex(string mode) => mode switch
    {
        "corner" => 1,
        "fixed" => 2,
        _ => 0
    };

    private static string ModeValue(int i) => i switch { 1 => "corner", 2 => "fixed", _ => "cursor" };

    private static int SizeModeIndex(string mode) => mode switch
    {
        "auto" => 1,
        "seer" => 2,
        _ => 0
    };

    private static string SizeModeValue(int i) => i switch { 1 => "auto", 2 => "seer", _ => "fixed" };

    private static int CornerIndex(string corner) => corner switch
    {
        "topleft" => 0,
        "bottomleft" => 2,
        "bottomright" => 3,
        _ => 1
    };

    private static string CornerValue(int i) => i switch { 0 => "topleft", 2 => "bottomleft", 3 => "bottomright", _ => "topright" };

    private static List<string> SplitList(string text, bool singleLine)
    {
        var seps = singleLine ? new[] { ',', ';', ' ', '\t', '\r', '\n' } : new[] { '\r', '\n', ';' };
        return text.Split(seps, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 页内竖向堆叠器：按当前宽度摆放卡片 / 分组标题 / 提示文字。
    /// 记录每个控件的「前置间距」，窗口变宽变窄时只需按记录重算 y 与宽度。
    /// </summary>
    private sealed class Stack
    {
        private readonly ScrollHost _host;
        private readonly Func<int, int> _px;
        private readonly int _pad;
        private readonly int _margin;
        private readonly int _minWidth;
        private readonly List<(Control C, int Gap)> _items = new();
        private int _gap;

        public Stack(ScrollHost host, int pad, int margin, int minWidth, Func<int, int> px)
        {
            _host = host;
            _pad = pad;
            _margin = margin;
            _minWidth = minWidth;
            _px = px;
            _gap = px(2);
        }

        private void Add(Control c)
        {
            _items.Add((c, _gap));
            _host.Content.Controls.Add(c);
            _gap = 0;
        }

        public void Section(string text)
        {
            var l = new Label
            {
                Text = text,
                AutoSize = false,
                Font = Theme.Small,
                ForeColor = Theme.Muted,
                BackColor = Theme.Bg,
                Height = _px(18),
                TextAlign = ContentAlignment.BottomLeft,
                UseMnemonic = false
            };
            Add(l);
            _gap += _px(8);
        }

        public void Card(params Control[] rows)
        {
            var card = new CardPanel { PadH = _pad, PadV = _px(6) };
            foreach (var r in rows) card.AddRow(r);
            Add(card);
        }

        public void Note(string text)
        {
            var l = new Label
            {
                Text = text,
                AutoSize = false,
                Font = Theme.Small,
                ForeColor = Theme.Muted,
                BackColor = Theme.Bg,
                Height = _px(36),
                UseMnemonic = false
            };
            Add(l);
        }

        public void Gap(int hLogical) => _gap += _px(hLogical);

        public void Finish() => Reflow();

        /// <summary>按当前可视宽度重排整页。</summary>
        public void Reflow()
        {
            int avail = _host.ClientSize.Width - _host.BarWidth - _margin * 2;
            int w = Math.Max(_minWidth, avail);

            _host.SuspendLayout();
            int y = _px(2);
            foreach (var (c, gap) in _items)
            {
                y += gap;
                if (c is CardPanel card)
                {
                    card.Reflow(w);
                    card.Left = _margin;
                    card.Top = y;
                }
                else
                {
                    c.SetBounds(_margin + _pad / 3, y, Math.Max(_px(60), w - _pad / 2), c.Height);
                }
                y += c.Height;
            }
            _host.SetContentHeight(y + _px(8));
            _host.ResumeLayout(true);
        }
    }
}
