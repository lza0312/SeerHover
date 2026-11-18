using System.Drawing;
using System.Windows.Forms;

namespace SeerHover;

/// <summary>设置窗口需要宿主（托盘）提供的几个能力。</summary>
internal sealed class SettingsHost
{
    public Func<string, string> Command { get; set; } = _ => string.Empty;
    public Func<bool> IsStartupEnabled { get; set; } = () => false;
    public Action<bool> SetStartup { get; set; } = _ => { };

    /// <summary>
    /// 应用配置；第二个参数为 true 时不弹托盘气泡（「应用」按钮连续点按用）。
    /// 返回 false 表示配置没能落盘，设置窗口会保持打开并提示用户。
    /// </summary>
    public Func<AppConfig, bool, bool> Apply { get; set; } = (_, _) => true;

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
        Icon = Program.AppIcon(64);

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

        // 左侧栏调窄：宽度 = 顶部 "SeerHover" 标题宽度 + 少量边距；版本号挪到「悬停预览」下面
        int railW = Math.Max(S(110), TextRenderer.MeasureText("SeerHover", Theme.H1).Width + S(36));
        _rail = new NavRail
        {
            Dock = DockStyle.Left,
            Width = railW,
            SubHeader = "悬停预览",
            SubHeader2 = "v" + (typeof(SettingsForm).Assembly.GetName().Version?.ToString(3) ?? "1.0.0")
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

    /// <summary>诊断用：截图指定页。--settings-shot 通过它逐页检查文字。</summary>
    public void SelectPageForDiagnostic(int index)
    {
        if (index < 0 || index >= _pages.Count) return;
        _rail.SelectedIndex = index;
        ShowPage(index);
        if (Environment.GetEnvironmentVariable("SEER_DEBUG_ROWS") == "1") DumpRows(index);
    }

    /// <summary>诊断：打印某页全部行的子控件布局（SEER_DEBUG_ROWS=1）。</summary>
    public void DumpRows(int index)
    {
        void Dump(Control c, string indent)
        {
            Console.WriteLine($"{indent}{c.GetType().Name} bounds={c.Bounds} visible={c.Visible}");
            foreach (Control k in c.Controls) Dump(k, indent + "  ");
        }
        Dump(_pages[index], "");
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

    /// <summary>应用按钮：写入并生效，窗口不关；确认按钮：写入 + 关闭。落盘失败时保持窗口打开。</summary>
    private void ApplySettings(bool close)
    {
        _work.Normalize();
        if (!_host.Apply(_work, !close))
        {
            MessageBox.Show(this,
                "设置已在本次运行中生效，但写入 config.json 失败，重启后会丢失。\n" +
                "请检查程序目录是否可写（或是否被只读/权限策略限制），然后重试。\n\n" +
                "详细原因见托盘菜单「打开日志」。",
                "SeerHover - 保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
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
            ToggleRow("开机自启", "登录 Windows 后在托盘里自动运行。", _host.IsStartupEnabled(), v => _host.SetStartup(v)),
            ToggleRow("双击空白处回到上一层",
                "在资源管理器文件列表的空白处双击，等同于按 Alt+↑ 返回上一层文件夹；双击文件/文件夹本身不受影响。",
                _work.DoubleClickUp, v => _work.DoubleClickUp = v));

        stack.Gap(16);
        stack.Section("外观");
        stack.Card(
            ToggleRow("深色模式", "设置窗口与托盘菜单的配色；首次打开时跟随系统，切换后会记住选择。",
                Theme.IsDark, SetDarkMode),
            SliderRow("界面缩放", "整体缩放设置窗和托盘菜单。",
                80, 160, 5, _work.UiScale, "%", SetUiScale));

        stack.Gap(16);
        stack.Section("媒体控件");
        stack.Card(
            SliderRow("默认音量", "悬停预览音/视频时控件条的初始音量（只改 Seer 自己的会话音量，不影响系统）。", 0, 100, 5,
                _work.MediaVolume, "%", v => _work.MediaVolume = v),
            SliderRow("默认倍速", "100% = 1.00 倍速；范围 20%–600%（0.20–6.00 倍），按 20% 步进。", 20, 600, 20,
                (int)Math.Round(_work.MediaRate * 100), "%", v => _work.MediaRate = Math.Round(v / 20.0) * 0.2));

        stack.Gap(16);
        stack.Section("时机");
        stack.Card(
            SliderRow("悬停触发延迟", "停在文件上多久后弹出预览。",
                100, 1500, 10, _work.DwellMs, "ms", v => _work.DwellMs = v),
            SliderRow("离开后收起延迟", "光标离开文件后多久收回预览窗。", 0, 800, 10, _work.HideGraceMs, "ms", v => _work.HideGraceMs = v));

        stack.Gap(16);
        stack.Section("抑制");
        stack.Card(
            ToggleRow("按住 Ctrl / Shift / Alt 时不触发", "避免和框选、多选、拖拽操作冲突。", _work.SuppressOnModifiers, v => _work.SuppressOnModifiers = v),
            ToggleRow("点击文件时收起预览", "点一下就收窗，不用等光标移开。", _work.HideOnClick, v => _work.HideOnClick = v));

        stack.Finish();
        return page;
    }

    private ScrollHost BuildWindowPage()
    {
        var (page, stack) = NewPage();

        // 当前屏幕（物理像素）：写进说明里，方便直接填「铺满整屏」的数值

        stack.Section("位置");

        // 条件显示：弹出位置选「屏幕角落」才显示角落/边距，选「固定坐标」才显示 X/Y，
        // 选「光标旁」时这两组不生效的设定项直接隐藏，页面更短。
        SettingRow cornerRow = null!, marginRow = null!, xRow = null!, yRow = null!;
        void UpdatePlaceRows(int modeIndex)
        {
            // 只写 RowVisible：页面隐藏期间 WinForms 的 Visible 不可靠，Reflow 会用 RowVisible 同步 Visible。
            cornerRow.RowVisible = marginRow.RowVisible = modeIndex == 1;   // 屏幕角落
            xRow.RowVisible = yRow.RowVisible = modeIndex == 2;             // 固定坐标
            stack.Reflow();
        }

        stack.Card(
            SegRow("弹出位置", "「光标旁」会挑空间更大的方向。",
                new[] { "光标旁", "屏幕角落", "固定坐标" }, ModeIndex(_work.Geometry.Mode),
                i => _work.Geometry.Mode = ModeValue(i), UpdatePlaceRows),
            ToggleRow("不遮挡鼠标", "自动避开鼠标和文件图标所在的位置；空间不够时仍会贴边。",
                _work.Geometry.AvoidCursor, v => _work.Geometry.AvoidCursor = v),
            SliderRow("与光标的间距", "窗口边缘到鼠标的最小距离。", 0, 80, 2, _work.Geometry.Gap, "px", v => _work.Geometry.Gap = v),
            cornerRow = SegRow("角落", "仅「屏幕角落」模式生效。", new[] { "左上", "右上", "左下", "右下" },
                CornerIndex(_work.Geometry.Corner), i => _work.Geometry.Corner = CornerValue(i)),
            marginRow = SliderRow("屏幕边距", "窗口到屏幕边缘的距离。", 0, 120, 4, _work.Geometry.Margin, "px", v => _work.Geometry.Margin = v),
            xRow = SliderRow("固定坐标 X", "仅固定坐标模式生效，可为负数。", -7680, 7680, 10, _work.Geometry.X, "px", v => _work.Geometry.X = v),
            yRow = SliderRow("固定坐标 Y", "仅固定坐标模式生效，可为负数。", -4320, 4320, 10, _work.Geometry.Y, "px", v => _work.Geometry.Y = v));

        UpdatePlaceRows(ModeIndex(_work.Geometry.Mode));

        stack.Gap(16);
        stack.Section("尺寸");
        stack.Note("每类文件单独选尺寸策略；选「固定」出现宽 / 高滑杆，选「比例最大化」出现放大填满与占屏比。");

        // 一张卡装下全部：分类之间细线分隔（同位置卡内样式），分类与它的参数行之间无分隔。
        var fixedRows = new Dictionary<string, SettingRow>();
        var ratioRows = new Dictionary<string, SettingRow>();
        var catRows = new List<SettingRow>();
        foreach (var kind in MediaKind.Categories)
        {
            catRows.Add(CategoryRow(kind, KindFormats(kind),
                CurrentCategoryIndex(kind),
                i => SetCategoryMode(kind, i),
                _ => UpdateCategoryRows()));
            var ratioRow = RatioParamsRow();
            ratioRow.GroupWithPrev = true;    // 参数行跟它的分类行同组
            ratioRows[kind] = ratioRow;
            catRows.Add(ratioRow);
            var fixedRow = CategoryFixedRow(kind);
            fixedRow.GroupWithPrev = true;
            catRows.Add(fixedRows[kind] = fixedRow);
        }
        stack.Card(catRows.ToArray());
        UpdateCategoryRows();

        void UpdateCategoryRows()
        {
            // 与 CurrentCategoryIndex 同一套有效模式判断：未显式设置键的分类沿用全局尺寸策略
            string Effective(string kind) =>
                CurrentCategoryMode(kind) ?? (_work.Geometry.SizeMode ?? "fixed").Trim().ToLowerInvariant();

            foreach (var (kind, row) in fixedRows)
                row.RowVisible = Effective(kind) == "fixed";
            foreach (var (kind, row) in ratioRows)
                row.RowVisible = Effective(kind) == "auto";
            stack.Reflow();
        }

        stack.Finish();
        return page;
    }

    private static string KindFormats(string kind) => kind switch
    {
        MediaKind.Image => "jpg · png · gif · webp · heic · psd · svg · raw",
        MediaKind.Video => "mp4 · mkv · mov · avi · wmv · webm · flv",
        MediaKind.Audio => "mp3 · wav · flac · aac · ogg · m4a · ape",
        MediaKind.Web => "html · mhtml · url 链接",
        MediaKind.Text => "txt · md · json · xml · 代码与字幕",
        MediaKind.Archive => "zip · rar · 7z · tar · iso",
        MediaKind.Document => "pdf · office · epub · djvu",
        MediaKind.Folder => "目录（开启「文件夹也预览」后生效）",
        _ => "未登记的扩展名一律归到这里"
    };

    /// <summary>
    /// 分类选「比例最大化」时显示的参数行（跟随分类行，GroupWithPrev）：
    /// 左窄 = 放大填满窗口（开关），右宽 = 最大占屏比（滑杆 + 输入框）。
    /// 两个值是全局配置（Geometry.UpscaleToFill / MaxScreenRatio），多个分类行显示的是同一组值，
    /// 重排时把控件显示同步到全局，改任意一行其它行跟着变。
    /// </summary>
    private SettingRow RatioParamsRow()
    {
        var t = TitleLabel("放大填满窗口");
        var d = DescLabel("小尺寸文件生成临时放大副本，Seer 才会把窗口填满。");
        var sw = new ToggleSwitch { Checked = _work.Geometry.UpscaleToFill };
        var nf = new NumberField(Math.Clamp((int)Math.Round(_work.Geometry.MaxScreenRatio * 100), 30, 100), 30, 100, 5, "%");
        var sl = new SliderControl { Minimum = 30, Maximum = 100, Step = 5, Value = nf.Value };

        void ApplyGlobal()
        {
            _work.Geometry.UpscaleToFill = sw.Checked;
            _work.Geometry.MaxScreenRatio = sl.Value / 100.0;
        }

        bool sync = false;
        sl.ValueChanged += (_, _) => { if (sync) return; sync = true; nf.SetValueFromCode(sl.Value); sync = false; ApplyGlobal(); };
        nf.ValueChanged += (_, _) => { if (sync) return; sync = true; sl.Value = nf.Value; nf.SetValueFromCode(sl.Value); sync = false; ApplyGlobal(); };
        sw.CheckedChanged += (_, _) => ApplyGlobal();

        var row = new SettingRow((r, w) =>
        {
            // 多个分类都选「比例最大化」时会有多行参数行：每次重排把显示值对齐全局
            sync = true;
            sw.Checked = _work.Geometry.UpscaleToFill;
            sl.Value = Math.Clamp((int)Math.Round(_work.Geometry.MaxScreenRatio * 100), 30, 100);
            nf.SetValueFromCode(sl.Value);
            sync = false;

            int swW = S(46), swH = S(26);
            int fieldW = Math.Clamp(S(96), S(84), S(132)), fieldH = S(28);

            // 左：标题 + 开关（窄），右：滑杆 + 输入框（宽）
            int leftW = S(150);
            int titleH = S(28);
            int descH = Math.Min(d.MeasureHeight(leftW), S(56));   // 最多两行，多余裁掉
            int blockH = titleH + (descH > 0 ? S(4) + descH : 0);
            int h = Math.Max(S(64), blockH + S(14));
            r.Height = h;

            int ty = Math.Max(S(6), (h - blockH) / 2);
            t.Bounds = new Rectangle(0, ty, leftW, titleH);
            d.Visible = descH > 0;
            if (descH > 0) d.Bounds = new Rectangle(0, ty + titleH + S(2), leftW, descH);
            sw.Size = new Size(swW, swH);
            sw.Location = new Point(leftW + S(8), (h - swH) / 2);

            int rightW = w - leftW - swW - S(36);
            int sx = w - rightW;
            int sy = (h - fieldH) / 2;
            sl.SetBounds(sx, sy + (fieldH - S(26)) / 2, Math.Min(S(230), rightW - fieldW - S(10)), S(26));
            nf.SetBounds(sx + Math.Min(S(230), rightW - fieldW - S(10)) + S(10), sy, fieldW, fieldH);
        });

        row.Controls.Add(t);
        row.Controls.Add(d);
        row.Controls.Add(sw);
        row.Controls.Add(sl);
        row.Controls.Add(nf);
        return row;
    }

    /// <summary>
    /// 分类策略行：说明是对应分类的支持格式；说明一行放不下就整段隐藏（不折行、不覆盖右侧选择框）。
    /// </summary>
    private SettingRow CategoryRow(string kind, string formats, int index, Action<int> set, Action<int> afterChange)
    {
        var t = TitleLabel(MediaKind.Label(kind));
        var d = DescLabel(formats);
        var seg = new Segmented { Options = CategoryOptions, SelectedIndex = Math.Clamp(index, 0, CategoryOptions.Length - 1) };
        seg.SelectedIndexChanged += (_, _) =>
        {
            set(seg.SelectedIndex);
            afterChange?.Invoke(seg.SelectedIndex);
        };

        var row = new SettingRow((r, w) =>
        {
            int segW = S(232);   // 三个短标签足够，右侧选择框做窄
            int labelW = w - segW - S(18);
            // 用 MeasureHeight（与实际折行渲染同一套测量）判断：折两行以内显示，更多 = 超出显示范围整段隐藏。
            // TextRenderer.MeasureText 对混排中文会用保守度量误判超宽，不能用来判。
            int descH = string.IsNullOrEmpty(d.Text) ? 0 : d.MeasureHeight(labelW);
            d.Visible = descH > 0 && descH <= S(56);
            if (!d.Visible) descH = 0;

            int titleH = S(28);
            int blockH = titleH + (descH > 0 ? S(4) + descH : 0);
            int h = Math.Max(S(62), blockH + S(16));
            r.Height = h;
            int y = Math.Max(S(6), (h - blockH) / 2);
            t.Bounds = new Rectangle(0, y, labelW, titleH);
            if (descH > 0) d.Bounds = new Rectangle(0, y + titleH + S(2), labelW, descH);
            seg.SetBounds(w - segW, (h - S(34)) / 2, segW, S(34));
        });

        row.Controls.Add(t);
        row.Controls.Add(d);
        row.Controls.Add(seg);
        return row;
    }

    private static readonly string[] CategoryOptions = { "固定", "比例最大化", "Seer" };

    private string? CurrentCategoryMode(string kind) =>
        _work.Geometry.CategoryModes != null && _work.Geometry.CategoryModes.TryGetValue(kind, out var v) ? v : null;

    private static int Mode3(string? mode) => mode switch
    {
        "fixed" => 0,
        "auto" => 1,
        "seer" => 2,
        _ => -1
    };

    /// <summary>类别未设置时沿用全局「尺寸策略」的当前值显示（配置里没有 UI 可改全局了，仅作兜底展示）。</summary>
    private int CurrentCategoryIndex(string kind)
    {
        int m = Mode3(CurrentCategoryMode(kind));
        if (m >= 0) return m;
        return Mode3((_work.Geometry.SizeMode ?? "fixed").Trim().ToLowerInvariant()) switch
        {
            1 => 1,
            2 => 2,
            _ => 0
        };
    }

    private void SetCategoryMode(string kind, int index)
    {
        var dict = _work.Geometry.CategoryModes ??=
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        dict[kind] = index switch
        {
            1 => "auto",
            2 => "seer",
            _ => "fixed"
        };
        if (dict.Count == 0) _work.Geometry.CategoryModes = null;
    }

    private (int W, int H) CurrentCategorySize(string kind)
    {
        if (_work.Geometry.CategorySizes != null &&
            _work.Geometry.CategorySizes.TryGetValue(kind, out var s) && s is { Length: 2 } && s[0] > 0 && s[1] > 0)
            return (s[0], s[1]);
        return (_work.Geometry.Width, _work.Geometry.Height);
    }

    private void SaveCategorySize(string kind, int w, int h)
    {
        var dict = _work.Geometry.CategorySizes ??= new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
        dict[kind] = new[] { w, h };
    }

    /// <summary>
    /// 类别「固定」尺寸行：宽 [滑杆][输入] 高 [滑杆][输入]，只在所属类别选「固定」时显示。
    /// 下限放宽到 320x240（Seer 若坚持自己的最小窗会自动夹回，盯梢逻辑会采纳实际值）。
    /// </summary>
    private SettingRow CategoryFixedRow(string kind)
    {
        const int minW = 320, minH = 240, maxW = 7680, maxH = 4320;
        var (w0, h0) = CurrentCategorySize(kind);
        var tW = new WrapLabel { Text = "宽", Font = Theme.BodyStrong, ForeColor = Theme.Muted, BackColor = Color.Transparent };
        var tH = new WrapLabel { Text = "高", Font = Theme.BodyStrong, ForeColor = Theme.Muted, BackColor = Color.Transparent };
        var sW = new SliderControl { Minimum = minW, Maximum = maxW, Step = 5, Value = Math.Clamp(w0, minW, maxW) };
        var sH = new SliderControl { Minimum = minH, Maximum = maxH, Step = 5, Value = Math.Clamp(h0, minH, maxH) };
        var nW = new NumberField(sW.Value, minW, maxW, 5, "px");
        var nH = new NumberField(sH.Value, minH, maxH, 5, "px");

        bool sync = false;
        void Save() => SaveCategorySize(kind, nW.Value, nH.Value);
        nW.ValueChanged += (_, _) => { if (sync) return; sync = true; sW.Value = nW.Value; nW.SetValueFromCode(sW.Value); sync = false; Save(); };
        sW.ValueChanged += (_, _) => { if (sync) return; sync = true; nW.SetValueFromCode(sW.Value); sync = false; Save(); };
        nH.ValueChanged += (_, _) => { if (sync) return; sync = true; sH.Value = nH.Value; nH.SetValueFromCode(sH.Value); sync = false; Save(); };
        sH.ValueChanged += (_, _) => { if (sync) return; sync = true; nH.SetValueFromCode(sH.Value); sync = false; Save(); };

        var row = new SettingRow((r, w) =>
        {
            int fieldW = Math.Clamp(S(104), S(84), S(132));
            int fieldH = S(28);
            int lblW = S(28);
            int sliderW = Math.Max(S(120), (w - 2 * (lblW + fieldW) - S(72)) / 2);

            int h = Math.Max(S(58), fieldH + S(24));
            r.Height = h;
            int y = (h - fieldH) / 2;
            int sy = (h - S(26)) / 2;

            int x = S(10);
            tW.Bounds = new Rectangle(x, y, lblW, fieldH);
            sW.SetBounds(x + lblW + S(6), sy, sliderW, S(26));
            nW.SetBounds(x + lblW + S(6) + sliderW + S(8), y, fieldW, fieldH);

            int x2 = x + lblW + S(6) + sliderW + S(8) + fieldW + S(30);
            tH.Bounds = new Rectangle(x2, y, lblW, fieldH);
            sH.SetBounds(x2 + lblW + S(6), sy, sliderW, S(26));
            nH.SetBounds(x2 + lblW + S(6) + sliderW + S(8), y, fieldW, fieldH);
        });

        row.Controls.Add(tW);
        row.Controls.Add(sW);
        row.Controls.Add(nW);
        row.Controls.Add(tH);
        row.Controls.Add(sH);
        row.Controls.Add(nH);
        return row;
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
            SliderRow("回查延迟", "太短容易误判。", 300, 1500, 50, _work.VerifyDelayMs, "ms", v => _work.VerifyDelayMs = v));

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

    private static WrapLabel TitleLabel(string title) => new()
    {
        Text = title,
        Font = Theme.BodyStrong,
        ForeColor = Theme.Text,
        BackColor = Color.Transparent
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
    private (int BlockH, int Y) LayoutText(SettingRow row, WrapLabel t, WrapLabel d, int labelW, int minRowH, bool top)
    {
        // 标题留 28 逻辑像素（原 22 在 150% DPI + 界面缩放下会把粗体汉字上半截裁掉）
        int titleH = S(28);
        int w = Math.Max(S(60), labelW);
        // 不能看 d.Visible：父页隐藏时 WinForms 的 Visible 也返回 false，
        // 会把说明当成不存在、标题重新居中下移，说明留在旧位置被盖掉上半截。
        int descH = !string.IsNullOrEmpty(d.Text) ? d.MeasureHeight(w) : 0;
        int blockH = titleH + (descH > 0 ? S(4) + descH : 0);
        int h = Math.Max(minRowH, blockH + S(16));
        row.Height = h;

        int y = top ? S(6) : Math.Max(S(6), (h - blockH) / 2);
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

    private SettingRow SegRow(string title, string desc, string[] options, int index, Action<int> set, Action<int>? afterChange = null)
    {
        var t = TitleLabel(title);
        var d = DescLabel(desc);
        var seg = new Segmented
        {
            Options = options,
            SelectedIndex = Math.Clamp(index, 0, options.Length - 1)
        };
        seg.SelectedIndexChanged += (_, _) =>
        {
            set(seg.SelectedIndex);
            afterChange?.Invoke(seg.SelectedIndex);
        };

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
                // 分段区按选项文字量宽度，避免「原比例最大化」这类长标签被省略号截断
                int segW = Math.Min(S(440), Math.Max(S(250), (int)(w * 0.5)));
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
