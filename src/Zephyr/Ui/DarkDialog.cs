using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Zephyr.Ui;

/// <summary>
/// 深色弹窗。系统 MessageBox 是白底灰框, 和设计稿的深色窗口摆在一起非常突兀,
/// 所以这几处提示 (机型不受支持、切换失败、开机自启失败、过热保护) 都换成自绘。
///
/// 刻意保持无边框 + 手工绘制, 并且只做三种形态: 提示、警告、二选一。
/// 不做通用 MessageBox 的完整克隆 —— 用的地方就这五处。
/// </summary>
internal static class DarkDialog
{
    public enum Tone { Info, Warning, Error }

    /// <summary>单按钮提示。返回 true 表示用户点了确定。</summary>
    public static void Show(IWin32Window? owner, string title, string body, Tone tone = Tone.Warning)
        => ShowCore(owner, title, body, tone, yesNo: false, yes: "确定", no: null);

    /// <summary>二选一。返回 true 表示选了"是"。</summary>
    public static bool Ask(IWin32Window? owner, string title, string body,
        string yes = "继续", string no = "取消", Tone tone = Tone.Warning)
        => ShowCore(owner, title, body, tone, yesNo: true, yes, no);

    private static bool ShowCore(IWin32Window? owner, string title, string body, Tone tone,
        bool yesNo, string yes, string? no)
    {
        using var dlg = new DarkDialogForm(title, body, tone, yesNo, yes, no);
        dlg.ShowDialog(owner);
        return dlg.Accepted;
    }

    private static Color ToneColor(Tone tone) => tone switch
    {
        Tone.Error => Theme.Err,
        Tone.Warning => Theme.Warn,
        _ => Theme.Accent,
    };
}

internal sealed class DarkDialogForm : Form
{
    private const int DesignWidth = 420;

    public bool Accepted { get; private set; }

    private readonly string _title;
    private readonly string _body;
    private readonly Color _tone;
    private readonly bool _yesNo;
    private readonly string _yesText;
    private readonly string? _noText;

    private Rectangle _yesRect;
    private Rectangle _noRect;
    private Rectangle _bodyRect;
    private Rectangle _titleRect;
    private bool _hoverYes;
    private bool _hoverNo;

    public DarkDialogForm(string title, string body, DarkDialog.Tone tone,
        bool yesNo, string yesText, string? noText)
    {
        _title = title;
        _body = body;
        _tone = tone switch
        {
            DarkDialog.Tone.Error => Theme.Err,
            DarkDialog.Tone.Warning => Theme.Warn,
            _ => Theme.Accent,
        };
        _yesNo = yesNo;
        _yesText = yesText;
        _noText = noText;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        BackColor = Theme.WinBg;
        DoubleBuffered = true;
        KeyPreview = true;

        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        LayoutDialog();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);

        // 这类对话框只在有 DPI 变化的会话里短暂出现, 但一旦漏了重排,
        // 正文会按旧比例撑出圆角框, 看起来像渲染坏了。
        LayoutDialog();
        Invalidate(true);
    }

    private void LayoutDialog()
    {
        int padX = Dpi.Px(this, 22);
        int padTop = Dpi.Px(this, 20);
        int bodyWidth = Dpi.Px(this, DesignWidth) - padX * 2;

        // 自上而下排一遍: 标题 -> 间距 -> 正文 -> 间距 -> 按钮 -> 底部留白。
        // 之前标题和正文各自用一套坐标, 正文顶到了标题上, 多行文字还会被裁掉。
        _titleRect = new Rectangle(padX + Dpi.Px(this, 12), padTop,
            bodyWidth - Dpi.Px(this, 12), Dpi.Px(this, 24));

        int bodyTop = _titleRect.Bottom + Dpi.Px(this, 14);

        Size bodySize = TextRenderer.MeasureText(_body, Theme.DlgBody,
            new Size(bodyWidth, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);

        _bodyRect = new Rectangle(padX, bodyTop, bodyWidth, bodySize.Height);

        int btnH = Dpi.Px(this, 34);
        int btnY = _bodyRect.Bottom + Dpi.Px(this, 20);
        int totalH = btnY + btnH + Dpi.Px(this, 20);

        ClientSize = new Size(Dpi.Px(this, DesignWidth), totalH);

        int btnW = Dpi.Px(this, 88);

        if (_yesNo)
        {
            int gap = Dpi.Px(this, 10);
            _noRect = new Rectangle(ClientSize.Width - padX - btnW, btnY, btnW, btnH);
            _yesRect = new Rectangle(_noRect.Left - gap - btnW, btnY, btnW, btnH);
        }
        else
        {
            _yesRect = new Rectangle(ClientSize.Width - padX - btnW, btnY, btnW, btnH);
            _noRect = Rectangle.Empty;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var full = new RectangleF(0, 0, Width, Height);
        using (var path = Round.Path(Round.Inset(full, 0.5f), Dpi.PxF(this, 12)))
        using (var fill = new SolidBrush(Theme.WinBg))
        using (var pen = new Pen(Theme.Line))
        {
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }

        int padX = Dpi.Px(this, 22);
        int padTop = Dpi.Px(this, 20);

        // 左侧语义色竖条, 一眼看出严重程度
        using (var bar = new SolidBrush(_tone))
        {
            g.FillRectangle(bar, padX, padTop + Dpi.Px(this, 3), Dpi.Px(this, 3), Dpi.Px(this, 16));
        }

        TextRenderer.DrawText(g, _title, Theme.DlgTitle, _titleRect,
            Theme.Text1, TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

        TextRenderer.DrawText(g, _body, Theme.DlgBody, _bodyRect,
            Theme.Text2, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

        DrawButton(g, _yesRect, _yesText, primary: true, _hoverYes);

        if (_yesNo && _noText is not null)
            DrawButton(g, _noRect, _noText, primary: false, _hoverNo);
    }

    private void DrawButton(Graphics g, Rectangle rect, string text, bool primary, bool hover)
    {
        if (rect.IsEmpty) return;

        var rf = new RectangleF(rect.X, rect.Y, rect.Width, rect.Height);
        Color fill = primary
            ? (hover ? Theme.Accent : Theme.Panel2)
            : (hover ? Theme.Panel : Theme.LineSoft);
        Color fore = primary && hover ? Theme.AppBg : Theme.Text1;

        using (var path = Round.Path(rf, Dpi.PxF(this, 8)))
        using (var brush = new SolidBrush(fill))
        using (var pen = new Pen(primary && hover ? Theme.Accent : Theme.Line))
        {
            g.FillPath(brush, path);
            g.DrawPath(pen, path);
        }

        TextRenderer.DrawText(g, text, Theme.DlgButton, rect, fore,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        bool hy = _yesRect.Contains(e.Location);
        bool hn = _yesNo && _noRect.Contains(e.Location);

        if (hy != _hoverYes || hn != _hoverNo)
        {
            _hoverYes = hy;
            _hoverNo = hn;
            Invalidate();
        }

        Cursor = (hy || hn) ? Cursors.Hand : Cursors.Default;
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hoverYes || _hoverNo)
        {
            _hoverYes = false;
            _hoverNo = false;
            Invalidate();
        }
        Cursor = Cursors.Default;
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

        if (_yesRect.Contains(e.Location))
        {
            Accepted = true;
            Close();
            return;
        }

        if (_yesNo && _noRect.Contains(e.Location))
        {
            Accepted = false;
            Close();
            return;
        }

        base.OnMouseDown(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            Accepted = false;
            Close();
            return;
        }

        if (e.KeyCode == Keys.Enter)
        {
            Accepted = true;
            Close();
            return;
        }

        base.OnKeyDown(e);
    }

    /// <summary>无边框窗口默认没有阴影和圆角, 借 DWM 补上。</summary>
    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
            return cp;
        }
    }
}
