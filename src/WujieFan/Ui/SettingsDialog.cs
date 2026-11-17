using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using WujieFan.Core.Config;

namespace WujieFan.Ui;

/// <summary>
/// 设置弹窗。把「开机自动启动」「向 RTSS 提供数据」从主面板挪进来, 并加上过热保护的两个参数。
///
/// 挪走这两个开关不是因为它们不重要, 而是因为主窗口是一个"看一眼就走"的面板:
/// 三列数据卡 + 状态栏已经把纵向空间占满, 每加一个开关就往下挤一格,
/// 迟早把转速数字挤到需要滚动的位置。设置项会用一次就不再碰, 应该收起来。
///
/// 这里只负责"用户想要什么", 不负责"怎么生效": 值写回调用方传进来的对象,
/// 落盘、改计划任务、通知 RTSS 都留给 MainForm —— 那边才能根据失败原因决定
/// 是回滚勾选还是提示用户。
/// </summary>
internal static class SettingsDialog
{
    /// <summary>
    /// 打开设置。返回 true 表示用户点了保存, 此时 <paramref name="settings"/> 与
    /// <paramref name="autoStart"/> 里的值已经是用户选定的; 但这些值**还没落盘**,
    /// 也**还没真的生效** —— 调用方必须自己 Save() 并按新值执行。
    /// </summary>
    public static bool Show(IWin32Window? owner, AppSettings settings, AutoStartState autoStart)
    {
        using var dlg = new SettingsDialogForm(settings, autoStart);
        return dlg.ShowDialog(owner) == DialogResult.OK;
    }
}

/// <summary>
/// 开机自启动在界面上的状态。它不在 AppSettings 里 —— 真相是计划任务本机的存在与否,
/// 每次都要问 AutoStartManager, 所以单独传, 不塞进配置文件 (否则两边迟早不一致)。
/// </summary>
internal sealed class AutoStartState
{
    public bool Enabled { get; set; }

    /// <summary>同名的外部任务已经存在, 这一行只展示不让改 —— 抢别人的任务比不启动更糟。</summary>
    public bool Locked { get; set; }

    public string Hint { get; set; } = "(需要管理员权限)";
}

/// <summary>
/// 一行"点了就做点什么"的入口 (不是开关): 左侧主文案 + 括注, 右侧一个指向右的尖角。
///
/// 放在本文件而不是 Controls.cs, 是因为它和设置入口是同批出现的,
/// 而 Controls.cs 这一轮有别人在改 —— 少一处共同编辑就少一次冲突。
/// </summary>
internal sealed class ActionRow : Control
{
    private string _text = "";
    private string _hint = "";
    private bool _hover;

    public ActionRow()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
               | ControlStyles.Selectable, true);
        BackColor = Theme.WinBg;
        TabStop = true;
        Cursor = Cursors.Hand;
    }

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string Text { get => _text; set { _text = value ?? ""; Invalidate(); } }

    public string Hint { get => _hint; set { _hint = value ?? ""; Invalidate(); } }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }

    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // 整行可点, 也整行可敲 —— 只用鼠标能做的事, 键盘也该能做。
        if (e.KeyCode is Keys.Space or Keys.Enter)
        {
            e.Handled = true;
            OnClick(EventArgs.Empty);
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        if (_hover)
        {
            using var hoverPath = Round.Path(new RectangleF(0, 0, Width, Height), Dpi.PxF(this, 8));
            using var hoverBrush = new SolidBrush(Theme.Panel);
            g.FillPath(hoverBrush, hoverPath);
        }

        int padX = Dpi.Px(this, 8);
        int midY = Height / 2;
        int chevron = Dpi.Px(this, 16);
        int textRight = Width - padX - chevron;

        Size main = TextRenderer.MeasureText(g, _text, Theme.RowLabel, Size.Empty,
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

        int mainWidth = Math.Min(main.Width, Math.Max(0, textRight - padX - Dpi.Px(this, 8)));

        TextRenderer.DrawText(g, _text, Theme.RowLabel,
            new Rectangle(padX, midY - main.Height / 2, mainWidth, main.Height),
            Enabled ? Theme.Text1 : Theme.Text3,
            TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

        if (_hint.Length > 0)
        {
            int hintX = padX + mainWidth + Dpi.Px(this, 5);
            if (hintX < textRight - Dpi.Px(this, 30))
            {
                TextRenderer.DrawText(g, _hint, Theme.RowHint,
                    new Rectangle(hintX, midY - main.Height / 2, textRight - hintX, main.Height),
                    Theme.Text3, TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            }
        }

        // 尖角用两条线段画, 不依赖字体里的 › —— 那个字符在部分机器上会变成方块
        using var pen = new Pen(Theme.Text3, Dpi.PxF(this, 1.5f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        float cx = Width - padX - chevron * 0.5f;
        float cy = midY;
        float s = Dpi.PxF(this, 4f);
        g.DrawLine(pen, cx - s, cy - s, cx + s * 0.6f, cy);
        g.DrawLine(pen, cx + s * 0.6f, cy, cx - s, cy + s);
    }
}

/// <summary>
/// 设置弹窗本体。绘制手法与 DarkDialogForm 一致 (无边框 + Round.Path 圆角 + TextRenderer),
/// 只是内容从一段文字变成可编辑控件。
/// </summary>
internal sealed class SettingsDialogForm : Form
{
    private const int DesignWidth = 420;
    private const int DesignRowHeight = 34;
    private const int DesignBoxHeight = 24;

    private readonly AppSettings _settings;
    private readonly AutoStartState _autoStart;

    private readonly ToggleRow _autoStartRow = new();
    private readonly ToggleRow _rtssRow = new();
    private readonly TextBox _tempBox = new();
    private readonly TextBox _holdBox = new();

    /// <summary>打开时的值。输入框被清空(或塞进非数字)时用它兜底, 而不是兜到边界。</summary>
    private readonly int _initialTemp;
    private readonly int _initialHold;

    private Rectangle _titleRect;
    private Rectangle _sectionRect;
    private Rectangle _tempCaptionRect;
    private Rectangle _tempHintRect;
    private Rectangle _holdCaptionRect;
    private Rectangle _holdHintRect;
    private Rectangle _noteRect;
    private Rectangle _saveRect;
    private Rectangle _cancelRect;
    private Rectangle _tempPillRect;
    private Rectangle _holdPillRect;

    private bool _hoverSave;
    private bool _hoverCancel;

    public SettingsDialogForm(AppSettings settings, AutoStartState autoStart)
    {
        _settings = settings;
        _autoStart = autoStart;
        _initialTemp = settings.EmergencyTemperatureC;
        _initialHold = settings.EmergencyHoldSamples;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        BackColor = Theme.WinBg;
        DoubleBuffered = true;
        KeyPreview = true;
        Font = Theme.RowLabel;

        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        BuildControls();
        LayoutDialog();
    }

    private void BuildControls()
    {
        _autoStartRow.Text = "开机自动启动";
        _autoStartRow.Hint = _autoStart.Hint;
        _autoStartRow.SetCheckedSilently(_autoStart.Enabled);

        // 锁定态只展示不让改: 同名任务已经存在时, 我们既不敢删也不敢覆盖。
        _autoStartRow.Enabled = !_autoStart.Locked;

        _rtssRow.Text = "向 RTSS 提供数据";
        _rtssRow.Hint = "(AIDA64 通道)";
        _rtssRow.SetCheckedSilently(_settings.PublishToRtss);

        StyleNumberBox(_tempBox, _initialTemp, maxLength: 3);
        StyleNumberBox(_holdBox, _initialHold, maxLength: 2);

        // 输入过程中只放数字进来, 但框里仍可能是 "" 或 5 这种越界值,
        // 所以离开时归一化一次, 保存前再归一化一次。可见地"跳"一下,
        // 比偷偷换成别的值诚实 —— 范围就写在这一行的括号里。
        _tempBox.Leave += (_, _) => ClampBox(_tempBox,
            AppSettings.EmergencyTemperatureMin, AppSettings.EmergencyTemperatureMax, _initialTemp);
        _holdBox.Leave += (_, _) => ClampBox(_holdBox,
            AppSettings.EmergencyHoldSamplesMin, AppSettings.EmergencyHoldSamplesMax, _initialHold);

        _tempBox.KeyDown += (_, e) => Nudge(e, _tempBox,
            AppSettings.EmergencyTemperatureMin, AppSettings.EmergencyTemperatureMax);
        _holdBox.KeyDown += (_, e) => Nudge(e, _holdBox,
            AppSettings.EmergencyHoldSamplesMin, AppSettings.EmergencyHoldSamplesMax);

        Controls.AddRange(new Control[] { _autoStartRow, _rtssRow, _tempBox, _holdBox });
    }

    /// <summary>
    /// 数值输入用带深色样式的 TextBox 而不是 NumericUpDown: 后者的上下箭头由系统绘制,
    /// 深色主题下会留下两个浅灰方块, 而 BorderStyle=None 也去不掉它们。
    ///
    /// Multiline 打开只是为了能用 Bounds 指定高度 (单行 TextBox 的高度由字体决定, 撑不起来),
    /// 配合 WordWrap=false + 无滚动条, 行为上仍是单个数字输入。
    /// </summary>
    private static void StyleNumberBox(TextBox box, int value, int maxLength)
    {
        box.Text = value.ToString();
        box.MaxLength = maxLength;
        box.Multiline = true;
        box.WordWrap = false;
        box.ScrollBars = ScrollBars.None;
        box.BorderStyle = BorderStyle.None;
        box.BackColor = Theme.Panel2;
        box.ForeColor = Theme.Text1;
        box.Font = Theme.RowLabel;
        box.TextAlign = HorizontalAlignment.Center;

        box.KeyPress += (_, e) =>
        {
            if (char.IsControl(e.KeyChar)) return;
            if (!char.IsDigit(e.KeyChar)) e.Handled = true;
        };
    }

    private static void ClampBox(TextBox box, int min, int max, int fallback)
    {
        int v = int.TryParse(box.Text, out int parsed) ? parsed : fallback;
        v = Math.Clamp(v, min, max);
        box.Text = v.ToString();
    }

    /// <summary>上/下方向键微调。没有上下箭头, 键盘就是唯一能"拨"一格的方式。</summary>
    private static void Nudge(KeyEventArgs e, TextBox box, int min, int max)
    {
        int step = e.KeyCode == Keys.Up ? 1 : e.KeyCode == Keys.Down ? -1 : 0;
        if (step == 0) return;

        e.Handled = true;
        e.SuppressKeyPress = true;

        int v = int.TryParse(box.Text, out int parsed) ? parsed : min;
        v = Math.Clamp(v + step, min, max);
        box.Text = v.ToString();
        box.SelectionStart = box.Text.Length;
    }

    private void LayoutDialog()
    {
        int padX = Dpi.Px(this, 22);
        int designW = Dpi.Px(this, DesignWidth);
        int inner = designW - padX * 2;
        int y = Dpi.Px(this, 20);

        _titleRect = new Rectangle(padX + Dpi.Px(this, 12), y,
            inner - Dpi.Px(this, 12), Dpi.Px(this, 24));
        y = _titleRect.Bottom + Dpi.Px(this, 16);

        int rowH = Dpi.Px(this, DesignRowHeight);

        // 行本身就带 8px 内边距 (与 ToggleRow 的绘制一致), 所以这里向外扩同样的量。
        int rowX = padX - Dpi.Px(this, 8);
        int rowW = inner + Dpi.Px(this, 16);

        _autoStartRow.Bounds = new Rectangle(rowX, y, rowW, rowH);
        y += rowH + Dpi.Px(this, 2);

        _rtssRow.Bounds = new Rectangle(rowX, y, rowW, rowH);
        y += rowH + Dpi.Px(this, 18);

        _sectionRect = new Rectangle(padX, y, inner, Dpi.Px(this, 20));
        y = _sectionRect.Bottom + Dpi.Px(this, 6);

        int boxH = Dpi.Px(this, DesignBoxHeight);
        int boxW = Dpi.Px(this, 56);
        int captionW = Dpi.Px(this, 88);
        int lineH = Dpi.Px(this, 18);

        _tempCaptionRect = new Rectangle(padX, y + (boxH - lineH) / 2, captionW, lineH);
        _tempPillRect = new Rectangle(padX + captionW - Dpi.Px(this, 4), y, boxW, boxH);
        _tempBox.Bounds = new Rectangle(_tempPillRect.X + Dpi.Px(this, 4), _tempPillRect.Y + Dpi.Px(this, 3),
            _tempPillRect.Width - Dpi.Px(this, 8), _tempPillRect.Height - Dpi.Px(this, 6));
        _tempHintRect = new Rectangle(_tempPillRect.Right + Dpi.Px(this, 10),
            y + (boxH - lineH) / 2, inner - captionW - boxW - Dpi.Px(this, 10 - 4), lineH);
        y += boxH + Dpi.Px(this, 4);

        _holdCaptionRect = new Rectangle(padX, y + (boxH - lineH) / 2, captionW, lineH);
        _holdPillRect = new Rectangle(padX + captionW - Dpi.Px(this, 4), y, boxW, boxH);
        _holdBox.Bounds = new Rectangle(_holdPillRect.X + Dpi.Px(this, 4), _holdPillRect.Y + Dpi.Px(this, 3),
            _holdPillRect.Width - Dpi.Px(this, 8), _holdPillRect.Height - Dpi.Px(this, 6));
        _holdHintRect = new Rectangle(_holdPillRect.Right + Dpi.Px(this, 10),
            y + (boxH - lineH) / 2, inner - captionW - boxW - Dpi.Px(this, 6), lineH);
        y += boxH + Dpi.Px(this, 10);

        _noteRect = new Rectangle(padX, y, inner, Dpi.Px(this, 32));
        y += Dpi.Px(this, 32) + Dpi.Px(this, 14);

        int btnH = Dpi.Px(this, 34);
        int btnW = Dpi.Px(this, 88);
        int gap = Dpi.Px(this, 10);

        _cancelRect = new Rectangle(designW - padX - btnW, y, btnW, btnH);
        _saveRect = new Rectangle(_cancelRect.Left - gap - btnW, y, btnW, btnH);
        y += btnH + Dpi.Px(this, 20);

        ClientSize = new Size(designW, y);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        // 外框与卡片底色, 与 DarkDialog 同一套画法
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

        // 左侧语义色竖条, 和 DarkDialog 一致, 让几个弹窗看起来是一家
        using (var bar = new SolidBrush(Theme.Accent))
            g.FillRectangle(bar, padX, padTop + Dpi.Px(this, 3), Dpi.Px(this, 3), Dpi.Px(this, 16));

        TextRenderer.DrawText(g, "设置", Theme.DlgTitle, _titleRect,
            Theme.Text1, TextFormatFlags.Left | TextFormatFlags.NoPadding);

        TextRenderer.DrawText(g, "过热保护", Theme.RowLabel, _sectionRect,
            Theme.Text1, TextFormatFlags.Left | TextFormatFlags.NoPadding);

        TextRenderer.DrawText(g, "温度阈值", Theme.RowLabel, _tempCaptionRect,
            Theme.Text2, TextFormatFlags.Left | TextFormatFlags.NoPadding);

        TextRenderer.DrawText(g, "连续采样", Theme.RowLabel, _holdCaptionRect,
            Theme.Text2, TextFormatFlags.Left | TextFormatFlags.NoPadding);

        // 范围写进括号, 与输入框同一行 —— 手输越界时值会跳到边界上, 用户据此能自己解释
        TextRenderer.DrawText(g,
            $"( {AppSettings.EmergencyTemperatureMin}–{AppSettings.EmergencyTemperatureMax}℃ )",
            Theme.RowHint, _tempHintRect, Theme.Text3,
            TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

        TextRenderer.DrawText(g,
            $"( {AppSettings.EmergencyHoldSamplesMin}–{AppSettings.EmergencyHoldSamplesMax} 次 )",
            Theme.RowHint, _holdHintRect, Theme.Text3,
            TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

        // 单独一行说明, 不塞进上面两行的括注里 —— 那句话比"范围"长得多, 挤在同一行会被裁掉
        TextRenderer.DrawText(g,
            "CPU 温度达到该值、且连续 N 次采样都达到, 就把风扇交还 EC 自动控制。",
            Theme.RowHint, _noteRect, Theme.Text3,
            TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.WordBreak);

        DrawPill(g, _tempPillRect);
        DrawPill(g, _holdPillRect);

        DrawButton(g, _saveRect, "保存", primary: true, _hoverSave);
        DrawButton(g, _cancelRect, "取消", primary: false, _hoverCancel);
    }

    /// <summary>输入框底衬。TextBox 自己填的是直角矩形, 这里在它四周留 4px 画圆角, 角才看得见。</summary>
    private void DrawPill(Graphics g, Rectangle rect)
    {
        if (rect.IsEmpty) return;

        var rf = new RectangleF(rect.X, rect.Y, rect.Width, rect.Height);
        using var path = Round.Path(rf, Dpi.PxF(this, 6));
        using var fill = new SolidBrush(Theme.Panel2);
        using var pen = new Pen(Theme.LineSoft);
        g.FillPath(fill, path);
        g.DrawPath(pen, path);
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
        bool hs = _saveRect.Contains(e.Location);
        bool hc = _cancelRect.Contains(e.Location);

        if (hs != _hoverSave || hc != _hoverCancel)
        {
            _hoverSave = hs;
            _hoverCancel = hc;
            Invalidate();
        }

        Cursor = (hs || hc) ? Cursors.Hand : Cursors.Default;
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hoverSave || _hoverCancel)
        {
            _hoverSave = false;
            _hoverCancel = false;
            Invalidate();
        }

        Cursor = Cursors.Default;
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

        if (_saveRect.Contains(e.Location)) { Commit(); return; }
        if (_cancelRect.Contains(e.Location)) { Cancel(); return; }

        base.OnMouseDown(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // KeyPreview 让这里先拿到按键: 输入框是多行的, 不拦的话 Enter 会被它吃掉变成换行。
        if (e.KeyCode == Keys.Escape)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            Cancel();
            return;
        }

        if (e.KeyCode == Keys.Enter)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            Commit();
            return;
        }

        base.OnKeyDown(e);
    }

    private void Commit()
    {
        // 保存前再归一化一次: 用户可能输了值就直接按 Enter, 没经过 Leave。
        ClampBox(_tempBox, AppSettings.EmergencyTemperatureMin,
            AppSettings.EmergencyTemperatureMax, _initialTemp);
        ClampBox(_holdBox, AppSettings.EmergencyHoldSamplesMin,
            AppSettings.EmergencyHoldSamplesMax, _initialHold);

        _settings.PublishToRtss = _rtssRow.Checked;
        _settings.EmergencyTemperatureC = int.Parse(_tempBox.Text);
        _settings.EmergencyHoldSamples = int.Parse(_holdBox.Text);

        // 锁定态下这一行是 Enabled=false, 用户改不了, 回写的就是原值, 等于没动。
        _autoStart.Enabled = _autoStartRow.Checked;

        // 只把值交出去, 不落盘: Save() 失败与否要由调用方决定怎么告诉用户。
        DialogResult = DialogResult.OK;
    }

    private void Cancel() => DialogResult = DialogResult.Cancel;

    /// <summary>无边框窗口默认没有阴影, 借 DWM 补上, 与 DarkDialog 一致。</summary>
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
