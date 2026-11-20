using System.Drawing;
using WujieFan.Core.Config;

namespace WujieFan.Ui;

/// <summary>
/// 托盘图标与"收进托盘"这件事。窗口生命周期不归它管, 只负责图标、菜单、气泡提示,
/// 以及把菜单点击转成上层给的回调 (显示窗口 / 交还自动 / 重新检测 / 退出)。
///
/// 单独成类的原因不是它大, 而是它有两处容易写错且后果难查的状态: 图标建失败时必须改
/// 设置并落盘 (否则下次启动还会按同一路径失败), 以及菜单图标是现画的位图、得有人释放。
/// </summary>
internal sealed class TrayHost : IDisposable
{
    private readonly Form _form;
    private readonly AppSettings _settings;
    private readonly Action<string, Color> _setStatus;
    private readonly Action<string> _saveSettingsQuietly;

    private readonly List<Image> _menuImages = new();

    /// <summary>气泡显示时长。热键切换的结果很短, 3 秒够看一眼又不会挡着别的东西。</summary>
    private const int BalloonMs = 3000;

    private NotifyIcon? _icon;
    private bool _hintShown;
    private bool _hiding;

    public TrayHost(
        Form form,
        AppSettings settings,
        Action<string, Color> setStatus,
        Action<string> saveSettingsQuietly)
    {
        _form = form;
        _settings = settings;
        _setStatus = setStatus;
        _saveSettingsQuietly = saveSettingsQuietly;
    }

    /// <summary>托盘图标是否真的建出来了。关闭窗口要不要退到托盘、能不能隐藏都看它。</summary>
    public bool Available => _icon is not null;

    /// <param name="onRestore">显示主窗口。</param>
    /// <param name="onRestoreAuto">立刻把风扇交还 EC 自动 (托盘里的逃生口)。</param>
    /// <param name="onRetryHardware">清掉跳过校验标志并重新检测机型。</param>
    /// <param name="onSettings">打开设置弹窗 (自启动 / RTSS 供数 / 过热保护)。</param>
    /// <param name="onExit">真正退出程序。</param>
    public void Create(Action onRestore, Action onRestoreAuto, Action onRetryHardware, Action onSettings, Action onExit)
    {
        try
        {
            _icon = new NotifyIcon
            {
                Icon = LoadAppIcon(32),
                Text = "WujieFan - 无界14+ 风扇转速",
                Visible = true,
                ContextMenuStrip = BuildMenu(onRestore, onRestoreAuto, onRetryHardware, onSettings, onExit),
            };

            _icon.DoubleClick += (_, _) => onRestore();
        }
        catch (Exception ex)
        {
            UiLog("CreateTrayIcon FAILED: " + ex);
            // 托盘建不出来时至少要把窗口留住, 否则关掉窗口就再也找不回来了。
            // 这里必须落盘: 只改内存的话下次启动又会按 MinimizeToTray=true 尝试缩托盘,
            // 而托盘建不出来是环境问题 (explorer 没起来、图标资源损坏), 大概率还会失败。
            _settings.MinimizeToTray = false;
            _saveSettingsQuietly("关闭托盘回退选项");
            _icon = null;
            _setStatus("托盘图标创建失败, 已改为关窗口即退出: " + ex.Message, Theme.Warn);
        }
    }

    /// <summary>把隐藏的窗口叫回来 (托盘双击、托盘菜单、以及第二次启动的唤醒事件都走这里)。</summary>
    public void Restore()
    {
        _form.ShowInTaskbar = true;
        if (!_form.Visible) _form.Show();
        _form.WindowState = FormWindowState.Normal;
        _form.Activate();
        _form.BringToFront();
    }

    /// <summary>收进托盘。没有托盘图标时什么都不做 —— 隐藏了就叫不回来。</summary>
    public void HideToTray()
    {
        if (_icon is null) return;
        if (_hiding) return;

        _hiding = true;
        try
        {
            _form.WindowState = FormWindowState.Minimized;
            _form.ShowInTaskbar = false;
            _form.Hide();

            // 只在第一次收进托盘时提示一次, 之后每天开机都弹就是骚扰了。
            if (!_hintShown)
            {
                _hintShown = true;
                _icon.BalloonTipTitle = "WujieFan 仍在后台运行";
                _icon.BalloonTipText = "风扇转速照常读数并送往 RTSS。双击托盘图标可以重新打开窗口, 右键可以退出。";
                _icon.ShowBalloonTip(4000);
            }
        }
        finally
        {
            _hiding = false;
        }
    }

    /// <summary>
    /// 弹一条托盘气泡。用于用户没在看主窗口时给他一个反馈 (比如按了全局热键)。
    /// 没有托盘图标时静默返回 —— 这种情况下调用方通常已经写了状态栏, 不必再吵。
    /// </summary>
    public void ShowBalloon(string text, Color tone)
    {
        if (_icon is null || string.IsNullOrWhiteSpace(text)) return;

        string title = BalloonTitleFor(tone);
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = text;
        _icon.ShowBalloonTip(BalloonMs);
    }

    private static string BalloonTitleFor(Color tone)
    {
        if (tone == Theme.Ok) return "强冷模式已切换";
        if (tone == Theme.Warn || tone == Theme.Err) return "操作未完成";
        return "WujieFan";
    }

    private ContextMenuStrip BuildMenu(
        Action onRestore,
        Action onRestoreAuto,
        Action onRetryHardware,
        Action onSettings,
        Action onExit)
    {
        var menu = new ContextMenuStrip
        {
            BackColor = Theme.Panel,
            ForeColor = Theme.Text1,
            Font = Theme.RowLabel,
            Renderer = new TrayMenuRenderer(),
            ShowImageMargin = true,
        };

        menu.Items.Add(new ToolStripMenuItem("显示主窗口", Track(TrayIcons.Show(16)), (_, _) => onRestore()));

        // 逃生口: 不论程序内部状态如何, 点一下就把 EC 交还自动。
        // 面板上那个勾选框万一因为界面卡住点不动, 这里是最后一条路。
        menu.Items.Add(new ToolStripMenuItem("恢复自动风扇", Track(TrayIcons.Restore(16)), (_, _) => onRestoreAuto()));
        menu.Items.Add(new ToolStripMenuItem("重新检测机型", Track(TrayIcons.Rescan(16)), (_, _) => onRetryHardware()));
        menu.Items.Add(new ToolStripMenuItem("设置...", Track(TrayIcons.Settings(16)), (_, _) => onSettings()));
        var sep = new ToolStripSeparator { Margin = new Padding(8, 4, 8, 4) };
        menu.Items.Add(sep);

        var exitItem = new ToolStripMenuItem("退出 WujieFan", Track(TrayIcons.Exit(16)), (_, _) => onExit());
        exitItem.ForeColor = Theme.Err;
        menu.Items.Add(exitItem);

        // 注意: 这一轮只给普通项上色, 不要把 exitItem 的红色覆盖回去。
        foreach (ToolStripItem item in menu.Items)
        {
            if (item is ToolStripSeparator || ReferenceEquals(item, exitItem)) continue;
            item.ForeColor = Theme.Text1;
        }

        return menu;
    }

    /// <summary>
    /// 记住菜单图标, 退出时统一释放。TrayIcons 每次调用都现画一张 Bitmap,
    /// ToolStripMenuItem 不会替你接管它的生命周期, 而托盘菜单要到进程结束才销毁。
    /// 数量固定且很小 (4 张 16px), 但既然建了就明确释放, 免得留下"谁负责"的模糊地带。
    /// </summary>
    private Image Track(Image image)
    {
        _menuImages.Add(image);
        return image;
    }

    /// <summary>从嵌入资源取出多尺寸 .ico, 按需要的尺寸建图标 (托盘与任务栏尺寸不同)。</summary>
    private static Icon LoadAppIcon(int size)
    {
        var asm = typeof(TrayHost).Assembly;
        foreach (string name in asm.GetManifestResourceNames())
        {
            if (!name.EndsWith("wujiefan.ico", StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                using Stream? s = asm.GetManifestResourceStream(name);
                if (s is null) continue;

                using var buffer = new MemoryStream();
                s.CopyTo(buffer);
                buffer.Position = 0;
                return new Icon(buffer, size, size);
            }
            catch
            {
                // 这一档尺寸在 .ico 里不存在时会抛异常; 不能因此让整个窗口起不来。
                continue;
            }
        }

        // 兜底: 没有嵌入资源就用 exe 自己的图标; 连这个都拿不到就用系统图标。
        try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application; }
        catch { return SystemIcons.Application; }
    }

    public void Dispose()
    {
        if (_icon is not null)
        {
            _icon.Visible = false;
            _icon.ContextMenuStrip?.Dispose();
            _icon.Dispose();
            _icon = null;
        }

        // 菜单项随 ContextMenuStrip 一起没了, 但它们引用的图标是本类现画并持有的。
        foreach (Image img in _menuImages) img.Dispose();
        _menuImages.Clear();
    }

    private static void UiLog(string message) => global::WujieFan.Program.UiLog(message);
}
