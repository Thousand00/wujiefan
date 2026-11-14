using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Windows.Forms;
using WujieFan.Core.Config;
using WujieFan.Core.Ec;
using WujieFan.Core.Hardware;
using WujieFan.Core.Providers;

namespace WujieFan.Ui;

/// <summary>
/// 主窗口: 显示两个风扇转速与 CPU 温度, 提供开机自启动与 RTSS 供数开关。
///
/// 刻意做得朴素: 这个程序的价值是把正确的数字送到 RTSS,
/// 界面只要让人一眼看清状态、能开关自启动就够了。
/// </summary>
public sealed class MainForm : Form
{
    private readonly bool _startMinimized;

    private readonly Label _fan1Value = new();
    private readonly Label _fan2Value = new();
    private readonly Label _tempValue = new();
    private readonly Label _statusLabel = new();
    private readonly CheckBox _autoStartCheck = new();

    private readonly System.Windows.Forms.Timer _timer = new();

    private FanReader? _reader;
    private AidaSharedMemory? _aida;
    private AppSettings _settings = AppSettings.Load();
    private AutoStartManager? _autoStart;

    private int _consecutiveErrors;
    private bool _trayHintShown;

    private NotifyIcon? _trayIcon;
    private bool _exiting;
    private bool _hidingToTray;

    public MainForm(bool startMinimized)
    {
        _startMinimized = startMinimized;
        BuildUi();

        Load += OnFormLoad;
        FormClosing += OnFormClosing;
        Resize += OnFormResize;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    private void BuildUi()
    {
        Text = "WujieFan - 无界14+ 风扇转速";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        ClientSize = new Size(420, 300);
        Font = new Font("Microsoft YaHei UI", 9F);

        var title = new Label
        {
            Text = "风扇转速",
            Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold),
            Location = new Point(20, 16),
            AutoSize = true,
        };

        var model = new Label
        {
            Name = "modelLabel",
            Text = "机器信息读取中...",
            ForeColor = Color.Gray,
            Location = new Point(20, 46),
            Size = new Size(380, 32),
        };

        _fan1Value.Text = "--";
        _fan1Value.Font = new Font("Consolas", 26F, FontStyle.Bold);
        _fan1Value.Location = new Point(20, 86);
        _fan1Value.AutoSize = true;

        var fan1Caption = new Label
        {
            Text = "Fan1 (CPU)",
            ForeColor = Color.Gray,
            Location = new Point(20, 148),
            AutoSize = true,
        };

        _fan2Value.Text = "--";
        _fan2Value.Font = new Font("Consolas", 26F, FontStyle.Bold);
        _fan2Value.Location = new Point(200, 86);
        _fan2Value.AutoSize = true;

        var fan2Caption = new Label
        {
            Text = "Fan2 (GPU)",
            ForeColor = Color.Gray,
            Location = new Point(200, 148),
            AutoSize = true,
        };

        _tempValue.Text = "--";
        _tempValue.Font = new Font("Consolas", 16F);
        _tempValue.Location = new Point(330, 96);
        _tempValue.AutoSize = true;

        var tempCaption = new Label
        {
            Text = "CPU °C",
            ForeColor = Color.Gray,
            Location = new Point(330, 130),
            AutoSize = true,
        };

        _autoStartCheck.Text = "开机自动启动 (需要管理员权限)";
        _autoStartCheck.Location = new Point(20, 182);
        _autoStartCheck.AutoSize = true;
        _autoStartCheck.CheckedChanged += OnAutoStartChanged;

        var rtssCheck = new CheckBox
        {
            Text = "向 RTSS 提供数据 (AIDA64 通道)",
            Location = new Point(20, 208),
            AutoSize = true,
            Checked = _settings.PublishToRtss,
        };
        rtssCheck.CheckedChanged += (_, _) =>
        {
            _settings.PublishToRtss = rtssCheck.Checked;
            _settings.Save();
            ApplyPublishMode();
        };

        _statusLabel.Text = "正在初始化...";
        _statusLabel.ForeColor = Color.Gray;
        _statusLabel.Location = new Point(20, 244);
        _statusLabel.Size = new Size(380, 48);

        Controls.AddRange(new Control[]
        {
            title, model, _fan1Value, fan1Caption, _fan2Value, fan2Caption,
            _tempValue, tempCaption, _autoStartCheck, rtssCheck, _statusLabel,
        });
    }

    private void CreateTrayIcon()
    {
        _trayIcon = new NotifyIcon
        {
            Icon = LoadAppIcon(32),
            Text = "WujieFan - 无界14+ 风扇转速",
            Visible = true,
            ContextMenuStrip = BuildTrayMenu(),
        };

        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
    }

    private ContextMenuStrip BuildTrayMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("显示主窗口", null, (_, _) => RestoreFromTray());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出 WujieFan", null, (_, _) => ExitApplication());
        return menu;
    }

    /// <summary>从嵌入资源取出多尺寸 .ico, 按需要的尺寸建图标 (托盘与任务栏尺寸不同)。</summary>
    private static Icon LoadAppIcon(int size)
    {
        var asm = typeof(MainForm).Assembly;
        foreach (string name in asm.GetManifestResourceNames())
        {
            if (!name.EndsWith("wujiefan.ico", StringComparison.OrdinalIgnoreCase)) continue;

            using Stream s = asm.GetManifestResourceStream(name)!;
            using var buffer = new MemoryStream();
            s.CopyTo(buffer);
            buffer.Position = 0;
            return new Icon(buffer, size, size);
        }

        // 兜底: 没有嵌入资源就用 exe 自己的图标。
        return Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
    }

    /// <summary>把隐藏的窗口叫回来 (托盘双击、托盘菜单、以及第二次启动的唤醒事件都走这里)。</summary>
    public void RestoreFromTray()
    {
        ShowInTaskbar = true;
        if (!Visible) Show();
        WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    private void ExitApplication()
    {
        _exiting = true;
        Close();
    }

    private void OnFormLoad(object? sender, EventArgs e)
    {
        CreateTrayIcon();

        _autoStart = new AutoStartManager();

        if (_autoStart.HasForeignTask())
        {
            _autoStartCheck.Enabled = false;
            _autoStartCheck.Text = "开机自动启动 (同名任务已存在, 已锁定)";
        }
        else
        {
            _autoStartCheck.Checked = _autoStart.IsEnabled();
        }

        (Controls["modelLabel"] as Label)!.Text = DescribeMachine();

        _timer.Interval = Math.Clamp(_settings.PollIntervalMs, 200, 10000);
        _timer.Tick += OnTick;

        bool opened = OpenReader();

        if (opened && _settings.PublishToRtss) ApplyPublishMode();

        _timer.Start();
        OnTick(null, EventArgs.Empty);

        if (_startMinimized || _settings.MinimizeToTray)
        {
            // 开机自启动时不弹窗打扰, 直接进托盘。
            if (_startMinimized) HideToTray();
        }
    }

    private static string DescribeMachine()
    {
        HardwareIdentity id = HardwareIdentity.Read();
        if (!id.WasRead) return "机器信息不可读";
        return $"{id.SystemManufacturer} {id.SystemProductName} · BIOS {id.BiosVersion} · EC {id.EcVersionText}";
    }

    private bool OpenReader()
    {
        CloseReader();

        try
        {
            _reader = new FanReader(LoadLpcIoBlob());
            _reader.Open(force: _settings.ForceUnsupported);
            SetStatus($"已连接 · PawnIO {_reader.PawnIoVersion}", Color.Green);
            return true;
        }
        catch (HardwareNotSupportedException ex)
        {
            _reader?.Dispose();
            _reader = null;
            ShowBlockingMessage(ex.Reason, ex.Guidance);
            SetStatus("机型不受支持, 已停止读取", Color.Firebrick);
            return false;
        }
        catch (Exception ex)
        {
            _reader?.Dispose();
            _reader = null;
            SetStatus("打不开硬件: " + ex.Message, Color.Firebrick);
            return false;
        }
    }

    private void ShowBlockingMessage(string reason, string guidance)
    {
        DialogResult r = MessageBox.Show(this,
            reason + "\n\n" + guidance + "\n\n是否仍然继续? (可能显示错误的风扇转速)",
            "WujieFan - 硬件不受支持",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);

        if (r == DialogResult.Yes)
        {
            _settings.ForceUnsupported = true;
            _settings.Save();
            OpenReader();
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_reader is null || !_reader.IsOpen)
        {
            // 设备可能因为休眠/驱动重启而失效, 定期尝试重连。
            if (!OpenReader()) return;
        }

        FanSample sample = _reader!.Read();

        if (sample.Error is not null)
        {
            _consecutiveErrors++;
            if (_consecutiveErrors >= 5)
            {
                // 连续失败多半是句柄失效, 重连一次。
                SetStatus("读取异常, 正在重连: " + sample.Error, Color.DarkOrange);
                OpenReader();
                _consecutiveErrors = 0;
            }
            return;
        }

        _consecutiveErrors = 0;

        if (sample.Fans.Count >= 2)
        {
            _fan1Value.Text = FormatRpm(sample.Fans[0]);
            _fan2Value.Text = FormatRpm(sample.Fans[1]);
        }

        _tempValue.Text = sample.Temperature?.ToString() ?? "--";

        if (_aida is not null && _settings.PublishToRtss)
        {
            try { _aida.Publish(ToSnapshot(sample)); }
            catch (Exception ex) { SetStatus("向 RTSS 供数失败: " + ex.Message, Color.DarkOrange); }
        }
    }

    private static string FormatRpm(FanReading f)
    {
        if (!f.IsValid) return "--";
        return f.Rpm >= 9999 ? ">9999" : f.Rpm.ToString();
    }

    private static FanSnapshot ToSnapshot(FanSample s) => new()
    {
        Fans = s.Fans.Select(f => new FanEntry
        {
            Id = $"FAN{f.Index}",
            Label = f.Label,
            ValueText = f.IsValid ? f.Rpm.ToString() : "",
        }).ToArray(),
        Temperature = s.Temperature,
    };

    private void ApplyPublishMode()
    {
        if (_settings.PublishToRtss)
        {
            try
            {
                _aida ??= new AidaSharedMemory();
                _aida.Open();
            }
            catch (Exception ex)
            {
                SetStatus("打不开 AIDA64 共享内存: " + ex.Message, Color.DarkOrange);
            }
        }
        else
        {
            _aida?.Dispose();
            _aida = null;
        }
    }

    private void OnAutoStartChanged(object? sender, EventArgs e)
    {
        if (_autoStart is null) return;

        AutoStartManager.Result r = _autoStartCheck.Checked ? _autoStart.Enable() : _autoStart.Disable();

        if (!r.Ok)
        {
            MessageBox.Show(this, r.Message, "WujieFan", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            // 回滚勾选状态, 避免界面与实际不符。
            _autoStartCheck.CheckedChanged -= OnAutoStartChanged;
            _autoStartCheck.Checked = _autoStart.IsEnabled();
            _autoStartCheck.CheckedChanged += OnAutoStartChanged;
            return;
        }

        SetStatus(r.Message.Split('\n')[0], Color.DimGray);
    }

    private void SetStatus(string text, Color color)
    {
        _statusLabel.Text = text;
        _statusLabel.ForeColor = color;
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {

        // 点右上角关闭 = 收进托盘继续在后台供数; 只有托盘菜单的"退出"才真的结束进程。
        if (_exiting || e.CloseReason != CloseReason.UserClosing) return;

        e.Cancel = true;

        // 关键: 不能在 FormClosing 里直接改 ShowInTaskbar —— 那会重建窗口句柄,
        // 让这次的"取消关闭"失效, 进程会被真的关掉。必须等关闭流程走完再隐藏。
        BeginInvoke(new Action(HideToTray));
    }

    private void HideToTray()
    {

        // WindowState 变化会再次触发 Resize, 用标志位挡住重入。
        if (_hidingToTray) return;
        _hidingToTray = true;

        try
        {
            // 先隐藏再改 ShowInTaskbar: 句柄重建发生在不可见状态下, 不会闪一下。
            WindowState = FormWindowState.Minimized;
            Hide();
            ShowInTaskbar = false;

            if (!_trayHintShown && _trayIcon is not null)
            {
                _trayHintShown = true;
                _trayIcon.BalloonTipTitle = "WujieFan 仍在后台运行";
                _trayIcon.BalloonTipText = "风扇转速照常读数并送往 RTSS。双击托盘图标可以重新打开窗口, 右键可以退出。";
                _trayIcon.BalloonTipIcon = ToolTipIcon.Info;
                _trayIcon.ShowBalloonTip(4000);
            }
        }
        finally
        {
            _hidingToTray = false;
        }
    }

    private void OnFormResize(object? sender, EventArgs e)
    {
        if (WindowState == FormWindowState.Minimized && _settings.MinimizeToTray && !_exiting)
            BeginInvoke(new Action(HideToTray));
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        // 从休眠/睡眠恢复后 EC 句柄可能失效, 主动重连一次。
        if (e.Mode == PowerModes.Resume)
        {
            _timer.Stop();
            OpenReader();
            ApplyPublishMode();
            _timer.Start();
        }
    }

    private void CloseReader()
    {
        _reader?.Dispose();
        _reader = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            _timer.Stop();
            _timer.Dispose();
            CloseReader();
            _aida?.Dispose();
            _aida = null;

            if (_trayIcon is not null)
            {
                _trayIcon.Visible = false;
                _trayIcon.ContextMenuStrip?.Dispose();
                _trayIcon.Dispose();
                _trayIcon = null;
            }
        }
        base.Dispose(disposing);
    }

    private static byte[] LoadLpcIoBlob()
    {
        var asm = typeof(FanReader).Assembly;
        foreach (string name in asm.GetManifestResourceNames())
        {
            if (name.EndsWith("LpcIO.bin", StringComparison.OrdinalIgnoreCase))
            {
                using Stream s = asm.GetManifestResourceStream(name)!;
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }
        throw new FileNotFoundException("找不到 LpcIO.bin 嵌入资源。");
    }
}
