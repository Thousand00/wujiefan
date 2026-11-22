using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Zephyr.Ui;

/// <summary>
/// 数据卡: 深色圆角面板 + 左侧小圆点 + 标签 + 等宽大号数字 + 单位。
/// 对应设计稿 .data-cell。
/// </summary>
internal sealed class DataCard : Control
{
    private string _label = "";
    private string _value = "--";
    private string _unit = "";
    private Color _dotColor = Color.Transparent;
    private bool _big = true;

    public DataCard()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.WinBg;
    }

    public string Label { get => _label; set { _label = value; Invalidate(); } }
    public string Value { get => _value; set { if (_value == value) return; _value = value; Invalidate(); } }
    public string Unit { get => _unit; set { _unit = value; Invalidate(); } }

    /// <summary>标签前的小圆点颜色。Color.Transparent 表示不画点。</summary>
    public Color DotColor { get => _dotColor; set { _dotColor = value; Invalidate(); } }

    /// <summary>温度卡用小一号的数字 (设计稿 23px vs 27px)。</summary>
    public bool BigValue { get => _big; set { _big = value; Invalidate(); } }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var rect = Round.Inset(new RectangleF(0, 0, Width, Height), 0.5f);
        float radius = Dpi.PxF(this, 8);

        using (var path = Round.Path(rect, radius))
        using (var fill = new SolidBrush(Theme.Panel))
        using (var pen = new Pen(Theme.LineSoft))
        {
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }

        int padX = Dpi.Px(this, 14);
        int y = Dpi.Px(this, 14);

        // 标签行: 圆点 + 文案
        int dotSize = Dpi.Px(this, 7);
        int textX = padX;

        if (_dotColor != Color.Transparent)
        {
            int dotY = y + Dpi.Px(this, 8);
            using var dotBrush = new SolidBrush(_dotColor);
            g.FillEllipse(dotBrush, padX, dotY, dotSize, dotSize);
            textX = padX + dotSize + Dpi.Px(this, 6);
        }

        TextRenderer.DrawText(g, _label, Theme.CardLabel,
            new Rectangle(textX, y, Width - textX - padX, Dpi.Px(this, 18)),
            Theme.Text2, TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

        y += Dpi.Px(this, 24);

        // 数字 + 单位: 单位贴在数字右侧基线附近
        Font valueFont = _big ? Theme.CardValue : Theme.CardValueTemp;
        Size valueSize = TextRenderer.MeasureText(g, _value, valueFont, Size.Empty,
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

        TextRenderer.DrawText(g, _value, valueFont,
            new Rectangle(padX, y, Math.Min(valueSize.Width + Dpi.Px(this, 4), Width - padX * 2), Dpi.Px(this, 40)),
            Theme.Text1, TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

        if (_unit.Length > 0)
        {
            int unitX = padX + valueSize.Width + Dpi.Px(this, 5);
            int unitY = y + valueSize.Height - Dpi.Px(this, 15);
            if (unitX < Width - padX)
            {
                TextRenderer.DrawText(g, _unit, Theme.CardUnit,
                    new Rectangle(unitX, unitY, Width - unitX - Dpi.Px(this, 4), Dpi.Px(this, 16)),
                    Theme.Text3, TextFormatFlags.Left | TextFormatFlags.NoPadding);
            }
        }
    }
}

/// <summary>
/// 一行开关: 18px 方形勾选框 + 主文案 + 括注 + 右侧徽章。
/// 对应设计稿 .ctrl-row, 整行可点 (不只是点那个小方块)。
/// </summary>
internal sealed class ToggleRow : Control
{
    private bool _checked;
    private string _text = "";
    private string _hint = "";
    private string _badge = "";
    private Color _badgeFore = Theme.Text2;
    private Color _badgeBack = Theme.Panel2;
    private Color _badgeBorder = Theme.LineSoft;
    private bool _hover;

    public ToggleRow()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
               | ControlStyles.Selectable, true);
        BackColor = Theme.WinBg;
        TabStop = true;
    }

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

    /// <summary>只在界面层改显示, 不触发 CheckedChanged。用于 EC 回读校准。</summary>
    public void SetCheckedSilently(bool value)
    {
        if (_checked == value) return;
        _checked = value;
        Invalidate();
    }

    public event EventHandler? CheckedChanged;

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string Text { get => _text; set { _text = value ?? ""; Invalidate(); } }
    public string Hint { get => _hint; set { _hint = value; Invalidate(); } }

    /// <summary>右侧徽章文案。空串表示不显示。</summary>
    public string Badge { get => _badge; set { _badge = value ?? ""; Invalidate(); } }

    public void SetBadgeTone(string text, Color fore, Color back, Color border)
    {
        _badge = text;
        _badgeFore = fore;
        _badgeBack = back;
        _badgeBorder = border;
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnClick(EventArgs e)
    {
        if (Enabled) Checked = !Checked;
        base.OnClick(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter)
        {
            e.Handled = true;
            Checked = !Checked;
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        if (_hover && Enabled)
        {
            using var hoverPath = Round.Path(new RectangleF(0, 0, Width, Height), Dpi.PxF(this, 8));
            using var hoverBrush = new SolidBrush(Theme.Panel);
            g.FillPath(hoverBrush, hoverPath);
        }

        int padX = Dpi.Px(this, 8);
        int midY = Height / 2;

        // 勾选框
        int box = Dpi.Px(this, 18);
        int boxX = padX;
        int boxY = midY - box / 2;

        using (var boxPath = Round.Path(new RectangleF(boxX, boxY, box, box), Dpi.PxF(this, 5)))
        using (var boxFill = new SolidBrush(_checked ? (Enabled ? Theme.Accent : Theme.Text3) : Theme.Panel2))
        using (var boxPen = new Pen(_checked ? (Enabled ? Theme.Accent : Theme.Text3) : Theme.Text3, Dpi.PxF(this, 1.5f)))
        {
            g.FillPath(boxFill, boxPath);
            g.DrawPath(boxPen, boxPath);
        }

        if (_checked)
        {
            // 对勾: 用两条线段画, 不依赖字体里的 ✔ (Segoe MDL2 在部分机器上缺字)
            using var tick = new Pen(Theme.AppBg, Dpi.PxF(this, 1.8f))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };
            float x0 = boxX + box * 0.24f, y0 = boxY + box * 0.53f;
            float x1 = boxX + box * 0.43f, y1 = boxY + box * 0.71f;
            float x2 = boxX + box * 0.77f, y2 = boxY + box * 0.31f;
            g.DrawLine(tick, x0, y0, x1, y1);
            g.DrawLine(tick, x1, y1, x2, y2);
        }

        int textX = boxX + box + Dpi.Px(this, 11);
        int badgeWidth = 0;

        if (_badge.Length > 0)
        {
            Size bs = TextRenderer.MeasureText(g, _badge, Theme.Badge, Size.Empty,
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            badgeWidth = bs.Width + Dpi.Px(this, 20);
            badgeWidth = Math.Min(badgeWidth, Dpi.Px(this, 160));

            int bh = Dpi.Px(this, 24);
            var badgeRect = new RectangleF(Width - padX - badgeWidth, midY - bh / 2f, badgeWidth, bh);

            using var badgePath = Round.Path(badgeRect, bh / 2f);
            using var badgeFill = new SolidBrush(_badgeBack);
            using var badgePen = new Pen(_badgeBorder);
            g.FillPath(badgeFill, badgePath);
            g.DrawPath(badgePen, badgePath);

            TextRenderer.DrawText(g, _badge, Theme.Badge,
                Rectangle.Round(badgeRect), _badgeFore,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        Color mainColor = Enabled ? Theme.Text1 : Theme.Text3;
        int textRight = Width - padX - (badgeWidth > 0 ? badgeWidth + Dpi.Px(this, 11) : 0);

        Size mainSize = TextRenderer.MeasureText(g, _text, Theme.RowLabel, Size.Empty,
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

        int mainWidth = Math.Min(mainSize.Width, Math.Max(0, textRight - textX));

        TextRenderer.DrawText(g, _text, Theme.RowLabel,
            new Rectangle(textX, midY - mainSize.Height / 2, mainWidth, mainSize.Height),
            mainColor, TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

        if (_hint.Length > 0)
        {
            int hintX = textX + mainWidth + Dpi.Px(this, 5);
            if (hintX < textRight - Dpi.Px(this, 30))
            {
                TextRenderer.DrawText(g, _hint, Theme.RowHint,
                    new Rectangle(hintX, midY - mainSize.Height / 2, textRight - hintX, mainSize.Height),
                    Theme.Text3, TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            }
        }
    }
}

/// <summary>
/// 状态栏: 圆角面板 + 前置语义色圆点 + 可换行文案。
/// 对应设计稿 .status, 高度按内容自适应。
/// </summary>
internal sealed class StatusBar : Control
{
    private string _text = "";
    private Color _color = Theme.Text2;
    private Color _dotColor = Color.Transparent;

    public StatusBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.WinBg;
    }

    public void SetStatus(string text, Color color, Color dotColor)
    {
        _text = text ?? "";
        _color = color;
        _dotColor = dotColor;
        Invalidate();
    }

    /// <summary>按当前宽度算出的所需高度, 供外部布局使用。</summary>
    public int MeasureHeight(int width)
    {
        int padX = Dpi.Px(this, 13);
        int padY = Dpi.Px(this, 11);
        int dotZone = _dotColor == Color.Transparent ? 0 : Dpi.Px(this, 7) + Dpi.Px(this, 9);
        int textWidth = Math.Max(Dpi.Px(this, 40), width - padX * 2 - dotZone);

        Size s = TextRenderer.MeasureText(_text, Theme.Status,
            new Size(textWidth, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);

        return Math.Max(Dpi.Px(this, 42), s.Height + padY * 2);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var rect = Round.Inset(new RectangleF(0, 0, Width, Height), 0.5f);
        using (var path = Round.Path(rect, Dpi.PxF(this, 8)))
        using (var fill = new SolidBrush(Theme.Panel))
        using (var pen = new Pen(Theme.LineSoft))
        {
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }

        int padX = Dpi.Px(this, 13);
        int padY = Dpi.Px(this, 11);
        int textX = padX;

        if (_dotColor != Color.Transparent)
        {
            int dot = Dpi.Px(this, 7);
            int dotY = padY + Dpi.Px(this, 6);
            using var db = new SolidBrush(_dotColor);
            g.FillEllipse(db, padX, dotY, dot, dot);
            textX = padX + dot + Dpi.Px(this, 9);
        }

        TextRenderer.DrawText(g, _text, Theme.Status,
            new Rectangle(textX, padY, Math.Max(Dpi.Px(this, 40), Width - textX - padX), Height - padY * 2),
            _color, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }
}
