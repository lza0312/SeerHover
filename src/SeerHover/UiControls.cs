using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SeerHover;

/// <summary>确定自动换行的静态文本（Label 固定尺寸下不保证换行，这里明确用 WordBreak 绘制）。</summary>
internal sealed class WrapLabel : Control
{
    public WrapLabel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        TabStop = false;
    }

    /// <summary>给定宽度下需要的高度（设备像素），用于按内容撑行高。</summary>
    public int MeasureHeight(int width)
    {
        if (string.IsNullOrEmpty(Text)) return 0;
        var size = TextRenderer.MeasureText(Text, Font, new Size(Math.Max(10, width), int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        return size.Height;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor,
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.Top);
    }
}

/// <summary>
/// 设置行：宽度变化时由 CardPanel 调用 Arrange(w) 重排自身内容。
/// 所有内部控件的坐标都在 Arrange 里算，不依赖 WinForms 布局引擎。
/// </summary>
internal sealed class SettingRow : Panel
{
    private readonly Action<SettingRow, int> _arrange;

    public SettingRow(Action<SettingRow, int> arrange)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Surface;
        _arrange = arrange;
    }

    public void Arrange(int width)
    {
        if (width <= 0) return;
        _arrange(this, width);
    }
}

/// <summary>
/// 自带细滚动条的滚动容器。
/// WinForms 原生 AutoScroll 滚动条是系统绘制的，深色模式下会露出一条亮白条，
/// 所以这里不用 AutoScroll：内容放在子面板里，由 OnPaint 自绘主题色滚动条。
/// </summary>
internal sealed class ScrollHost : Panel
{
    private readonly Panel _content;
    private int _offset;
    private int _dragY0 = -1;
    private int _dragOff0;
    private bool _hoverBar;

    /// <summary>可视区尺寸变化（需要重新排版内容）时触发。</summary>
    public event EventHandler? ViewportChanged;

    public Panel Content => _content;

    public ScrollHost()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bg;
        _content = new Panel { BackColor = Theme.Bg };
        Controls.Add(_content);
    }

    /// <summary>滚动条占用的宽度（设备像素）。始终预留，避免滚动条出现/消失导致宽度来回跳。</summary>
    public int BarWidth => (int)Math.Ceiling(12 * Draw.Scale(this));

    public int MaxScroll => Math.Max(0, _content.Height - Height);

    protected override void OnClientSizeChanged(EventArgs e)
    {
        base.OnClientSizeChanged(e);
        ApplyLayout();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>内部布局算完后回调：报告内容总高度。</summary>
    public void SetContentHeight(int height)
    {
        int h = Math.Max(Height, height);
        if (_content.Height != h) _content.Height = h;
        if (_offset > MaxScroll) _offset = MaxScroll;
        ApplyLayout();
    }

    public void ScrollToTop()
    {
        _offset = 0;
        ApplyLayout();
    }

    public void ScrollBy(int dy)
    {
        int max = MaxScroll;
        if (max <= 0) return;
        int v = Math.Clamp(_offset + dy, 0, max);
        if (v == _offset) return;
        _offset = v;
        _content.Top = -_offset;
        Invalidate();
    }

    private void ApplyLayout()
    {
        int w = Math.Max(120, Width - BarWidth);
        if (_content.Width != w)
            _content.SetBounds(0, -_offset, w, Math.Max(Height, _content.Height));
        else if (_content.Top != -_offset)
            _content.Top = -_offset;
        Invalidate();
    }

    private (float Top, float Height, float Span) Thumb()
    {
        float s = Draw.Scale(this);
        float trackTop = 3 * s;
        float trackH = Math.Max(1, Height - 6 * s);
        int max = MaxScroll;
        float frac = max <= 0 ? 1f : Math.Min(1f, Height / (float)Math.Max(1, _content.Height));
        float th = Math.Max(30 * s, trackH * frac);
        if (th > trackH) th = trackH;
        float span = Math.Max(0, trackH - th);
        float top = trackTop + (max > 0 ? span * (_offset / (float)max) : 0);
        return (top, th, span);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using (var bg = new SolidBrush(Theme.Bg)) g.FillRectangle(bg, ClientRectangle);

        if (MaxScroll <= 0) return;

        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = Draw.Scale(this);
        float w = 6 * s;
        float x = Width - BarWidth + (BarWidth - w) / 2f;
        var (top, height, _) = Thumb();

        var color = _dragY0 >= 0 ? Theme.Accent : _hoverBar ? Theme.Muted : Theme.Track;
        Draw.FillRound(g, new RectangleF(x, top, w, height), w / 2f, color);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        ScrollBy(-Math.Sign(e.Delta) * (int)(56 * Draw.Scale(this)));
        base.OnMouseWheel(e);
    }

    private bool InBar(Point p) => p.X >= Width - BarWidth && MaxScroll > 0;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragY0 >= 0)
        {
            var (_, _, span) = Thumb();
            if (span > 0)
            {
                double v = _dragOff0 + (e.Y - _dragY0) * (double)MaxScroll / span;
                int next = Math.Clamp((int)Math.Round(v), 0, MaxScroll);
                if (next != _offset)
                {
                    _offset = next;
                    _content.Top = -_offset;
                    Invalidate();
                }
            }
            return;
        }

        bool hover = InBar(e.Location);
        if (hover != _hoverBar)
        {
            _hoverBar = hover;
            Cursor = hover ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hoverBar)
        {
            _hoverBar = false;
            Cursor = Cursors.Default;
            Invalidate();
        }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && InBar(e.Location))
        {
            var (top, height, _) = Thumb();
            if (e.Y >= top && e.Y <= top + height)
            {
                _dragY0 = e.Y;
                _dragOff0 = _offset;
                Capture = true;
            }
            else
            {
                ScrollBy(e.Y < top ? -Height * 8 / 10 : Height * 8 / 10);
            }
            Invalidate();
            return;
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_dragY0 >= 0)
        {
            _dragY0 = -1;
            Capture = false;
            Invalidate();
        }
        base.OnMouseUp(e);
    }
}

/// <summary>扁平开关。</summary>
internal sealed class ToggleSwitch : Control
{
    private bool _checked;
    public event EventHandler? CheckedChanged;

    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        Size = new Size(46, 26);
        Cursor = Cursors.Hand;
        TabStop = false;
    }

    protected override void OnClick(EventArgs e)
    {
        Checked = !Checked;
        base.OnClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = Draw.Scale(this);
        float pad = 2 * s;
        float h = Height - pad * 2;
        float w = Math.Min(Width - pad * 2, h * 1.85f);
        float x = Width - pad - w;
        Draw.FillRound(g, new RectangleF(x, pad, w, h), h / 2f, _checked ? Theme.Accent : Theme.Track);

        float knob = h - 4 * s;
        float kx = _checked ? x + w - knob - 2 * s : x + 2 * s;
        using var b = new SolidBrush(Color.White);
        g.FillEllipse(b, kx, pad + 2 * s, knob, knob);
    }
}

/// <summary>细滑杆（自定义绘制，比 TrackBar 好看且可精确控制）。</summary>
internal sealed class SliderControl : Control
{
    private int _min;
    private int _max = 100;
    private int _step = 1;
    private int _value;
    private bool _drag;

    public event EventHandler? ValueChanged;

    public int Minimum { get => _min; set { _min = value; if (_value < _min) _value = _min; Invalidate(); } }
    public int Maximum { get => _max; set { _max = value; if (_value > _max) _value = _max; Invalidate(); } }
    public int Step { get => _step; set => _step = Math.Max(1, value); }

    public int Value
    {
        get => _value;
        set
        {
            int v = Snap(value);
            if (_value == v) return;
            _value = v;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public SliderControl()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        Size = new Size(220, 26);
        Cursor = Cursors.Hand;
        TabStop = false;
    }

    private int Snap(int v)
    {
        int lo = _min;
        int hi = Math.Max(_min, _max);   // 初始化时 Maximum 可能还没跟上 Minimum
        v = Math.Clamp(v, lo, hi);
        if (_step > 1) v = lo + (int)Math.Round((v - lo) / (double)_step) * _step;
        return Math.Clamp(v, lo, hi);
    }

    private (float Left, float Right, float KnobR, float Cy) Metrics()
    {
        float s = Draw.Scale(this);
        float r = Math.Min(Height / 2f - s, 9 * s);
        return (r + s, Width - r - s, r, Height / 2f);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = Draw.Scale(this);
        var m = Metrics();
        float th = 4 * s;
        float span = Math.Max(1, m.Right - m.Left);

        Draw.FillRound(g, new RectangleF(m.Left, m.Cy - th / 2, span, th), th / 2, Theme.Track);

        double t = _max > _min ? (_value - _min) / (double)(_max - _min) : 0;
        float kx = m.Left + (float)(span * t);
        if (kx - m.Left > 1)
            Draw.FillRound(g, new RectangleF(m.Left, m.Cy - th / 2, kx - m.Left, th), th / 2, Theme.Accent);

        using var b = new SolidBrush(Theme.IsDark ? Theme.SurfaceAlt : Color.White);
        using var pen = new Pen(_drag ? Theme.Accent : Draw.Mix(Theme.Accent, Theme.IsDark ? Color.Black : Color.White, 0.25), 2 * s);
        g.FillEllipse(b, kx - m.KnobR, m.Cy - m.KnobR, m.KnobR * 2, m.KnobR * 2);
        g.DrawEllipse(pen, kx - m.KnobR, m.Cy - m.KnobR, m.KnobR * 2, m.KnobR * 2);
    }

    private void SetFromX(int x)
    {
        var m = Metrics();
        double t = (x - m.Left) / Math.Max(1f, m.Right - m.Left);
        Value = _min + (int)Math.Round(Math.Clamp(t, 0, 1) * (_max - _min));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _drag = true;
            Capture = true;
            SetFromX(e.X);
            Invalidate();
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_drag) SetFromX(e.X);
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _drag = false;
        Capture = false;
        Invalidate();
        base.OnMouseUp(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        int unit = _step > 1 ? _step : 1;
        Value = _value + unit * Math.Sign(e.Delta);
        base.OnMouseWheel(e);
    }
}

/// <summary>分段选择器（替代下拉框，避免控件主题不统一）。</summary>
internal sealed class Segmented : Control
{
    private string[] _options = Array.Empty<string>();
    private int _index;
    private int _hover = -1;

    public event EventHandler? SelectedIndexChanged;

    public string[] Options
    {
        get => _options;
        set
        {
            _options = value ?? Array.Empty<string>();
            if (_index >= _options.Length) _index = Math.Max(0, _options.Length - 1);
            Invalidate();
        }
    }

    public int SelectedIndex
    {
        get => _index;
        set
        {
            int v = Math.Clamp(value, 0, Math.Max(0, _options.Length - 1));
            if (_index == v) return;
            _index = v;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public Segmented()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        Size = new Size(272, 34);
        Cursor = Cursors.Hand;
        TabStop = false;
    }

    private RectangleF[] Cells()
    {
        float s = Draw.Scale(this);
        int n = Math.Max(1, _options.Length);
        float pad = 3 * s;
        float w = (Width - pad * 2) / n;
        var cells = new RectangleF[n];
        for (int i = 0; i < n; i++)
            cells[i] = new RectangleF(pad + i * w, pad, w, Height - pad * 2);
        return cells;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = Draw.Scale(this);
        float radius = (Height - 6 * s) / 2f;

        Draw.FillRound(g, new RectangleF(0, 0, Width, Height), radius, Theme.Track);
        var cells = Cells();
        for (int i = 0; i < cells.Length; i++)
        {
            var c = cells[i];
            if (i == _index) Draw.FillRound(g, c, radius, Theme.Accent);
            else if (i == _hover) Draw.FillRound(g, c, radius, Draw.Mix(Theme.Track, Theme.IsDark ? Color.White : Color.Black, 0.12));

            var f = i == _index ? Theme.BodyStrong : Theme.Body;
            var col = i == _index ? Color.White : Theme.Muted;
            TextRenderer.DrawText(g, _options[i], f, Rectangle.Round(c), col,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int idx = IndexAt(e.X);
        if (idx != _hover) { _hover = idx; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hover != -1) { _hover = -1; Invalidate(); }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) SelectedIndex = IndexAt(e.X);
        base.OnMouseDown(e);
    }

    private int IndexAt(int x)
    {
        var cells = Cells();
        for (int i = 0; i < cells.Length; i++)
            if (x >= cells[i].Left - 2 && x <= cells[i].Right + 2) return i;
        return -1;
    }
}

/// <summary>左侧分页导航。</summary>
internal sealed class NavRail : Control
{
    private int _index;
    private int _hover = -1;

    public List<string> Items { get; } = new();
    public string Header { get; set; } = "SeerHover";
    public string SubHeader { get; set; } = "";

    public event EventHandler? SelectedIndexChanged;

    public int SelectedIndex
    {
        get => _index;
        set
        {
            if (value < 0 || value >= Items.Count || _index == value)
            {
                Invalidate();
                return;
            }
            _index = value;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public NavRail()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bg;
        Width = 176;
        Cursor = Cursors.Hand;
    }

    private float S => Draw.Scale(this);
    private int ItemTop => (int)(88 * S);
    private int ItemH => (int)(38 * S);
    private int ItemGap => (int)(4 * S);

    public Rectangle ItemRect(int i) => new(0, ItemTop + i * (ItemH + ItemGap), Width - 1, ItemH);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = S;

        using var bg = new SolidBrush(Theme.Bg);
        g.FillRectangle(bg, ClientRectangle);

        TextRenderer.DrawText(g, Header, Theme.H1, new Point((int)(20 * s), (int)(22 * s)), Theme.Text, TextFormatFlags.NoPrefix);
        if (SubHeader.Length > 0)
            TextRenderer.DrawText(g, SubHeader, Theme.Small, new Point((int)(21 * s), (int)(48 * s)), Theme.Muted, TextFormatFlags.NoPrefix);

        for (int i = 0; i < Items.Count; i++)
        {
            var r = ItemRect(i);
            float inset = 8 * s;
            var pill = new RectangleF(inset, r.Y, r.Width - inset * 2, r.Height);
            if (i == _index)
            {
                Draw.FillRound(g, pill, 8 * s, Theme.Surface);
                Draw.FillRound(g, new RectangleF(inset + 8 * s, r.Y + r.Height / 2f - 8 * s, 3 * s, 16 * s), 1.5f * s, Theme.Accent);
            }
            else if (i == _hover)
            {
                Draw.FillRound(g, pill, 8 * s, Theme.Hover);
            }

            var col = i == _index ? Theme.Text : Theme.Muted;
            var f = i == _index ? Theme.BodyStrong : Theme.Body;
            var textRect = new Rectangle(r.X + (int)(22 * s), r.Y, r.Width - (int)(30 * s), r.Height);
            TextRenderer.DrawText(g, Items[i], f, textRect, col,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        using var pen = new Pen(Theme.Border);
        g.DrawLine(pen, Width - 1, 0, Width - 1, Height);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int idx = -1;
        for (int i = 0; i < Items.Count; i++) if (ItemRect(i).Contains(e.Location)) idx = i;
        if (idx != _hover) { _hover = idx; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hover != -1) { _hover = -1; Invalidate(); }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        for (int i = 0; i < Items.Count; i++)
        {
            if (ItemRect(i).Contains(e.Location)) { SelectedIndex = i; break; }
        }
        base.OnMouseDown(e);
    }
}

/// <summary>圆角按钮。</summary>
internal sealed class PillButton : Control
{
    private bool _hover;
    private bool _down;

    public bool Primary { get; set; } = true;
    public int Radius { get; set; } = 8;

    public PillButton(string text, bool primary = true)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor | ControlStyles.StandardClick, true);
        Text = text;
        Primary = primary;
        Size = new Size(96, 34);
        Cursor = Cursors.Hand;
        Font = Theme.Body;
        BackColor = Color.Transparent;
        TabStop = false;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = Draw.Scale(this);
        var r = new RectangleF(0, 0, Width, Height);

        Color fill, textColor, borderColor;
        if (Primary)
        {
            fill = !Enabled ? Draw.Mix(Theme.Accent, Theme.Surface, 0.55)
                 : _down ? Draw.Mix(Theme.Accent, Color.Black, 0.12)
                 : _hover ? Draw.Mix(Theme.Accent, Color.Black, 0.06)
                 : Theme.Accent;
            textColor = Color.White;
            borderColor = Color.Transparent;
        }
        else
        {
            fill = !Enabled ? Theme.SurfaceAlt
                 : _down ? Draw.Mix(Theme.Border, Theme.Surface, 0.2)
                 : _hover ? Theme.Hover
                 : Theme.Surface;
            textColor = Enabled ? Theme.Text : Theme.Muted;
            borderColor = Theme.Border;
        }

        Draw.FillRound(g, RectangleF.Inflate(r, -0.5f, -0.5f), Radius * s, fill);
        if (borderColor != Color.Transparent)
            Draw.StrokeRound(g, RectangleF.Inflate(r, -0.5f, -0.5f), Radius * s, borderColor);

        TextRenderer.DrawText(g, Text, Font, Rectangle.Round(r), textColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
}

/// <summary>圆角卡片容器（内部行按宽度重排）。</summary>
internal sealed class CardPanel : Panel
{
    public int Radius { get; set; } = 10;

    /// <summary>卡片内左右留白（设备像素）</summary>
    public int PadH { get; set; }

    /// <summary>卡片内上下留白（设备像素）</summary>
    public int PadV { get; set; }

    private readonly List<Control> _items = new();

    public CardPanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Surface;
    }

    public void AddRow(Control row)
    {
        if (_items.Count > 0)
        {
            var sep = new Panel { BackColor = Theme.Border, Height = 1 };
            _items.Add(sep);
            Controls.Add(sep);
        }
        _items.Add(row);
        Controls.Add(row);
    }

    /// <summary>按新的卡片宽度重排所有行（行自己会重算高度）。</summary>
    public void Reflow(int width)
    {
        if (width <= 0) return;
        SuspendLayout();
        Width = width;
        int inner = Math.Max(1, width - PadH * 2);
        int y = PadV;
        foreach (var c in _items)
        {
            if (c is SettingRow sr) sr.Arrange(inner);
            c.SetBounds(PadH, y, inner, c.Height);
            y += c.Height;
        }
        Height = y + PadV;
        ResumeLayout(true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = Draw.Scale(this);
        var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        Draw.FillRound(g, r, Radius * s, BackColor);
        Draw.StrokeRound(g, r, Radius * s, Theme.Border);
    }
}

/// <summary>
/// 可以直接敲数字的数值框（滑杆拖不到精确值时手输；↑↓ / 滚轮步进，回车或失焦生效）。
/// 右侧显示单位，聚焦时边框高亮。
/// </summary>
internal sealed class NumberField : Panel
{
    private readonly TextBox _box;
    private readonly string _suffix;
    private readonly int _min;
    private readonly int _max;
    private readonly int _step;
    private int _value;
    private bool _focused;

    public event EventHandler? ValueChanged;

    public NumberField(int value, int min, int max, int step, string suffix)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.SurfaceAlt;
        _min = min;
        _max = Math.Max(min, max);
        _step = Math.Max(1, step);
        _suffix = suffix ?? string.Empty;
        _value = Math.Clamp(value, _min, _max);
        Size = new Size(104, 28);
        Cursor = Cursors.IBeam;

        _box = new TextBox
        {
            BorderStyle = BorderStyle.None,
            TextAlign = HorizontalAlignment.Right,
            Font = Theme.BodyStrong,
            BackColor = Theme.SurfaceAlt,
            ForeColor = Theme.Text,
            Text = _value.ToString(),
            // 不参与 Tab / 初始焦点：否则一打开设置窗口就被全选，滑杆也会被这个旧值锁住
            TabStop = false
        };
        _box.GotFocus += (_, _) => { _focused = true; _box.SelectAll(); Invalidate(); };
        _box.LostFocus += (_, _) => { _focused = false; Commit(); Invalidate(); };
        _box.KeyDown += OnBoxKeyDown;
        Controls.Add(_box);
    }

    /// <summary>当前值（自动夹在 [min, max]）。</summary>
    public int Value
    {
        get => _value;
        set
        {
            int v = Math.Clamp(value, _min, _max);
            if (v == _value)
            {
                if (!_focused && _box.Text != v.ToString()) _box.Text = v.ToString();
                return;
            }
            _value = v;
            if (!_focused) _box.Text = v.ToString();
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public int Minimum => _min;
    public int Maximum => _max;

    /// <summary>
    /// 由滑杆等外部来源驱动时调用：数值和显示文本一起改，且不发 ValueChanged。
    /// 关键点：即使输入框正聚焦也要刷新文本 —— 否则框里留着旧数字，
    /// 一失焦 Commit 就把旧值写回去，滑杆看起来「弹回原位、被锁住」。
    /// </summary>
    public void SetValueFromCode(int value)
    {
        int v = Math.Clamp(value, _min, _max);
        _value = v;
        string text = v.ToString();
        if (_box.Text != text) _box.Text = text;
        Invalidate();
    }

    private int SuffixWidth
    {
        get
        {
            if (_suffix.Length == 0) return 0;
            return TextRenderer.MeasureText(_suffix, Theme.Small).Width + 2;
        }
    }

    private void LayoutBox()
    {
        if (_box is null) return;      // 构造函数里设 Size 会先触发一次 OnResize
        float s = Draw.Scale(this);
        int left = (int)(9 * s);
        int right = SuffixWidth + (int)(5 * s);
        int w = Math.Max(12, Width - left - right);
        int h = _box.PreferredHeight;
        _box.SetBounds(left, Math.Max(0, (Height - h) / 2), w, h);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutBox();
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        _box.Focus();
        _box.SelectAll();
        base.OnMouseDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = Draw.Scale(this);
        var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        Draw.FillRound(g, r, 6 * s, BackColor);
        Draw.StrokeRound(g, r, 6 * s, _focused ? Theme.Accent : Theme.Border);

        if (_suffix.Length > 0)
        {
            int sw = SuffixWidth;
            var sr = new Rectangle(Width - sw - (int)(1 * s), 0, sw, Height);
            TextRenderer.DrawText(g, _suffix, Theme.Small, sr, Theme.Muted,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    private void Commit()
    {
        if (int.TryParse(_box.Text.Trim(), out int v))
        {
            v = Math.Clamp(v, _min, _max);
            bool changed = v != _value;
            _value = v;
            if (changed) ValueChanged?.Invoke(this, EventArgs.Empty);
        }
        // 回显最终值：输入可能被 step 取整（如 285 -> 280），焦点内也要刷新
        _box.Text = _value.ToString();
        _box.SelectAll();
    }

    /// <summary>按一步增减（↑↓ / 滚轮）。</summary>
    public void StepBy(int dir)
    {
        if (dir == 0) return;
        Value = _value + Math.Sign(dir) * _step;
    }

    private void OnBoxKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Enter:
                Commit();
                _box.SelectAll();
                e.Handled = true;
                e.SuppressKeyPress = true;
                break;
            case Keys.Up:
                StepBy(+1);
                _box.SelectAll();
                e.Handled = true;
                e.SuppressKeyPress = true;
                break;
            case Keys.Down:
                StepBy(-1);
                _box.SelectAll();
                e.Handled = true;
                e.SuppressKeyPress = true;
                break;
            case Keys.Escape:
                _box.Text = _value.ToString();
                e.Handled = true;
                e.SuppressKeyPress = true;
                break;
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        StepBy(e.Delta);
        base.OnMouseWheel(e);
    }
}

/// <summary>
/// 圆角输入框容器（内含无边框多行 TextBox）。
/// 多行时系统滚动条在深色主题下永远是白色（无法调色），所以用窗口 Region 把原生滚动条裁掉，
/// 右侧改画主题色细滚动条：拖动 / 悬停 / 滚轮都走这里，滚动本身仍由 TextBox 原生处理。/// <summary>
/// 圆角输入框容器（内含无边框多行 TextBox）。
/// 多行时系统滚动条在深色主题下永远是白色（无法调色），所以用窗口 Region 把原生滚动条裁掉，
/// 右侧放一个自绘的 BarStrip 子控件负责滚动条的显示与拖动；滚动本身仍由 TextBox 原生处理
/// （滚轮 / 方向键 / 光标跟随不动），BarStrip 只是把「滚到第几行」翻译成 EM_LINESCROLL。
/// </summary>
internal sealed class TextArea : Panel
{
    private const int EM_GETFIRSTVISIBLELINE = 0x00CE;
    private const int EM_GETLINECOUNT = 0x00BA;
    private const int EM_LINESCROLL = 0x00B6;

    private readonly bool _multi;
    private readonly int _padL;
    private readonly int _padT;
    private readonly int _padR;
    private readonly int _padB;
    private readonly int _barW;
    private readonly BarStrip? _bar;

    public TextBox Box { get; }

    public TextArea(string text, int height, bool multiline = true)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.SurfaceAlt;
        Height = height;

        float s = Draw.Scale(this);
        _multi = multiline;
        _padL = (int)(10 * s);
        _padT = (int)(8 * s);
        _padB = (int)(8 * s);
        _barW = _multi ? (int)Math.Ceiling(6 * s) : 0;
        _padR = (int)(10 * s) + _barW;

        Box = new TextBox
        {
            Text = text,
            Multiline = multiline,
            BorderStyle = BorderStyle.None,
            BackColor = Theme.SurfaceAlt,
            ForeColor = Theme.Text,
            Font = Theme.Mono,
            ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None,
            AcceptsReturn = multiline,
            WordWrap = false
        };
        Controls.Add(Box);

        if (multiline)
        {
            _bar = new BarStrip(this);
            Controls.Add(_bar);
            _bar.BringToFront();
        }

        Box.TextChanged += (_, _) => _bar?.Invalidate();
        Box.KeyUp += (_, _) => _bar?.Invalidate();
        Box.MouseDown += (_, _) => _bar?.Invalidate();
        Box.MouseUp += (_, _) => _bar?.Invalidate();
        Box.MouseMove += (_, _) => { if (Control.MouseButtons == MouseButtons.Left) _bar?.Invalidate(); };
    }

    /// <summary>原生滚动条宽度（要被 Region 裁掉的那一条）。</summary>
    private static int NativeBarW => SystemInformation.VerticalScrollBarWidth;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        LayoutBox();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutBox();
        Invalidate();
    }

    /// <summary>
    /// Box 比可见区多出「原生滚动条宽度」，再用 Region 把这一条裁掉：
    /// 滚动条还在（滚轮 / 光标跟随 / EM_LINESCROLL 都能用），但看不见，画的是我们的主题色细条。
    /// </summary>
    private void LayoutBox()
    {
        if (Box is null) return;       // 构造函数里设 Height 会先触发一次 OnResize
        int extra = _multi ? NativeBarW : 0;
        int w = Math.Max(24, Width - _padL - _padR + extra);
        int h = Math.Max(20, Height - _padT - _padB);
        Box.SetBounds(_padL, _padT, w, h);
        _bar?.SetBounds(Math.Max(0, Width - _padR), 0, _padR, Height);

        if (_multi)
        {
            var old = Box.Region;
            Box.Region = new Region(new Rectangle(0, 0, Math.Max(10, w - NativeBarW), h));
            old?.Dispose();
        }
    }

    // ── 滚动状态（读原生 Edit 控件的行信息，不自己维护偏移）─────────
    private int FirstLine => _multi
        ? (int)Native.SendMessageW(Box.Handle, EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero)
        : 0;

    private int LineCount => _multi
        ? (int)Native.SendMessageW(Box.Handle, EM_GETLINECOUNT, IntPtr.Zero, IntPtr.Zero)
        : 0;

    private int VisibleLines
    {
        get
        {
            int lh = Math.Max(1, Box.Font.Height);
            return Math.Max(1, Box.ClientSize.Height / lh);
        }
    }

    private int MaxFirst => Math.Max(0, LineCount - VisibleLines);

    /// <summary>滚到指定首行（相对滚动，交给 Edit 控件自己算）。</summary>
    public void ScrollToLine(int first)
    {
        if (!_multi) return;
        int target = Math.Clamp(first, 0, MaxFirst);
        int delta = target - FirstLine;
        if (delta == 0) return;
        Native.SendMessageW(Box.Handle, EM_LINESCROLL, IntPtr.Zero, new IntPtr(delta));
        _bar?.Invalidate();
    }

    /// <summary>外部（滚轮转发）滚动后刷新滚动条。</summary>
    public void RefreshBar() => _bar?.Invalidate();

    private (float Top, float Height, float Span) Thumb()
    {
        float s = Draw.Scale(this);
        float trackTop = _padT;
        float trackH = Math.Max(1, Height - _padT - _padB);
        int total = Math.Max(1, LineCount);
        int vis = Math.Min(total, VisibleLines);
        float frac = vis / (float)total;
        float th = Math.Max(24 * s, trackH * frac);
        if (th > trackH) th = trackH;
        float span = Math.Max(0, trackH - th);
        int maxFirst = MaxFirst;
        float t = maxFirst <= 0 ? 0 : Math.Clamp(FirstLine / (float)maxFirst, 0, 1);
        return (trackTop + span * t, th, span);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = Draw.Scale(this);
        var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        Draw.FillRound(g, r, 8 * s, BackColor);
        Draw.StrokeRound(g, r, 8 * s, Theme.Border);
    }

    /// <summary>滚动条子控件：独占右侧一条，点击 / 拖拽 / 悬停都在这里，不受 Region 命中测试影响。</summary>
    private sealed class BarStrip : Control
    {
        private readonly TextArea _ta;
        private bool _hover;
        private int _dragY0 = -1;
        private int _dragGrab;
        private int _dragSpan;

        public BarStrip(TextArea ta)
        {
            _ta = ta;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            TabStop = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (_ta.MaxFirst <= 0) return;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = Draw.Scale(_ta);
            float w = 4 * s;
            float x = (Width - w) / 2f;
            var (top, h, _) = _ta.Thumb();
            var color = _dragY0 >= 0 ? Theme.Accent : _hover ? Theme.Muted : Theme.Track;
            Draw.FillRound(g, new RectangleF(x, top, w, h), w / 2f, color);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || _ta.MaxFirst <= 0) return;
            var (top, h, span) = _ta.Thumb();
            if (e.Y >= top && e.Y <= top + h)
            {
                _dragY0 = e.Y;
                _dragGrab = (int)(e.Y - top);
                _dragSpan = (int)span;
                Capture = true;
            }
            else
            {
                _ta.ScrollToLine(_ta.FirstLine + (e.Y < top ? -_ta.VisibleLines : _ta.VisibleLines));
            }
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_dragY0 >= 0)
            {
                if (_dragSpan > 0)
                {
                    float top = e.Y - _dragGrab - _ta._padT;
                    float t = Math.Clamp(top / _dragSpan, 0, 1);
                    _ta.ScrollToLine((int)Math.Round(t * _ta.MaxFirst));
                }
                return;
            }

            bool hover = true;
            if (hover != _hover) { _hover = hover; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_hover) { _hover = false; Invalidate(); }
            if (_dragY0 >= 0) { _dragY0 = -1; Capture = false; Invalidate(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_dragY0 >= 0)
            {
                _dragY0 = -1;
                Capture = false;
                Invalidate();
            }
        }
    }
}
