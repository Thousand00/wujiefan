using System.Drawing;
using WujieFan.Core.Config;
using WujieFan.Core.Ec;
using WujieFan.Core.Hardware;

namespace WujieFan.Ui;

/// <summary>
/// 轮询节拍与设备生命周期: 定时器、打开/重连 EC、把一次采样分发到界面、看门狗、供数。
///
/// 这个类拥有 FanReader —— 唯一打开硬件的地方。别处要用 reader (模式切换、退出交还)
/// 只能通过 <see cref="Reader"/> 拿, 不要自己再 new 一个: 同一进程开两次 PawnIO 句柄
/// 会让 EC 互斥量的语义变得难以推理。
/// </summary>
internal sealed class FanPollingController
{
    private readonly AppSettings _settings;
    private readonly DataCard _fan1Card;
    private readonly DataCard _fan2Card;
    private readonly DataCard _tempCard;
    private readonly Action<string, Color> _setStatus;
    private readonly RtssPublisher _publisher;
    private readonly Func<FanModeController?> _mode;
    private readonly Form _owner;

    private readonly System.Windows.Forms.Timer _timer = new();

    private FanReader? _reader;

    private int _consecutiveErrors;

    /// <summary>
    /// 已经明确放弃自动重试 (机型不受支持且用户选择不再继续)。
    /// 没有这个标志时, 定时器每秒都会重开一次设备, 每次都会再弹一个"硬件不受支持"对话框,
    /// 在用户看来就是弹窗刷屏。
    /// </summary>
    private bool _giveUpOpening;

    public FanPollingController(
        Form owner,
        AppSettings settings,
        DataCard fan1Card,
        DataCard fan2Card,
        DataCard tempCard,
        RtssPublisher publisher,
        Func<FanModeController?> mode,
        Action<string, Color> setStatus)
    {
        _owner = owner;
        _settings = settings;
        _fan1Card = fan1Card;
        _fan2Card = fan2Card;
        _tempCard = tempCard;
        _publisher = publisher;
        _mode = mode;
        _setStatus = setStatus;

        _timer.Interval = Math.Clamp(_settings.PollIntervalMs, 200, 10000);
        _timer.Tick += (_, _) => Poll();
    }

    /// <summary>当前 EC 读取器。未打开或打开失败时为 null, 调用方必须自行判空。</summary>
    public FanReader? Reader => _reader;

    public void Start() => _timer.Start();

    public void Stop() => _timer.Stop();

    public void DisposeTimer() => _timer.Dispose();

    /// <summary>
    /// 打开 (或重开) EC 设备。已打开的先关掉 —— 重连语义就是"从头来一次"。
    /// </summary>
    /// <param name="interactive">
    /// 是否允许弹对话框。只有用户主动触发 (启动、托盘菜单重试) 时才为 true,
    /// 定时器里的自动重连必须为 false —— 否则会每秒弹一个对话框。
    /// </param>
    public bool Open(bool interactive = true)
    {
        Close();

        try
        {
            _reader = new FanReader(LoadLpcIoBlob());
            _reader.Open(force: _settings.ForceUnsupported);
            _giveUpOpening = false;
            _consecutiveErrors = 0;

            _setStatus(
                _settings.ForceUnsupported
                    ? $"已连接 · PawnIO {_reader.PawnIoVersion} · 已跳过机型校验"
                    : $"已连接 · PawnIO {_reader.PawnIoVersion}",
                _settings.ForceUnsupported ? Theme.Warn : Theme.Ok);

            // 重连成功后要补建共享内存: 如果启动时设备没打开, 之前那次 Apply
            // 根本不会执行, 映射一直是 null, 之后即使读到数据也永远不会往 RTSS 送。
            // 放在 SetStatus 之后, 这样"共享内存打不开"的提示不会被"已连接"覆盖掉。
            // Apply 自身幂等, 重复调用没有副作用。
            _publisher.Apply();
            return true;
        }
        catch (HardwareNotSupportedException ex)
        {
            _reader?.Dispose();
            _reader = null;

            // 只有用户主动拒绝(interactive 时选"否")才算放弃。
            // 定时器里的自动重连失败不能设这个标志: 读 SMBIOS 有可能一次偶然失败,
            // 那会让程序从此再也不采样, 而界面上只留一句"机型不受支持"。
            _giveUpOpening = interactive;

            if (interactive) ShowBlockingMessage(ex.Reason, ex.Guidance);
            _setStatus(_settings.ForceUnsupported
                ? "机型不受支持 (已选择强行读取), 读取失败"
                : "机型不受支持, 已停止读取", Theme.Err);
            return false;
        }
        catch (Exception ex)
        {
            _reader?.Dispose();
            _reader = null;
            _setStatus("打不开硬件: " + ex.Message, Theme.Err);
            return false;
        }
    }

    /// <summary>询问用户是否在机型不符的情况下继续。用户点"继续"就直接重新打开。</summary>
    private void ShowBlockingMessage(string reason, string guidance)
    {
        bool go = DarkDialog.Ask(_owner, "硬件不受支持",
            reason + "\n\n" + guidance + "\n\n是否仍然继续? (可能显示错误的风扇转速)",
            yes: "继续读取", no: "取消", tone: DarkDialog.Tone.Warning);

        if (!go) return;

        _settings.ForceUnsupported = true;
        _settings.Save();

        // 重新打开一次。这一步失败也不再弹第二个对话框。
        _giveUpOpening = false;
        Open(interactive: false);
    }

    /// <summary>清掉"跳过机型校验"的标志并重新检测。托盘菜单用, 给用户一个反悔的入口。</summary>
    public void RetryHardwareCheck()
    {
        _settings.ForceUnsupported = false;
        _settings.Save();
        _giveUpOpening = false;
        Close();
        Open(interactive: true);
        Poll();
    }

    /// <summary>关掉读取器。退出与重连都走这里, 不对外暴露 Dispose 细节。</summary>
    public void Close()
    {
        _reader?.Dispose();
        _reader = null;
    }

    /// <summary>
    /// 一次轮询。定时器、启动、休眠恢复、托盘重试都会调它 —— 四种入口共用同一段逻辑,
    /// 免得"启动时做了什么、恢复时少做了什么"这类差异慢慢长出来。
    /// </summary>
    public void Poll()
    {
        if (_reader is null || !_reader.IsOpen)
        {
            // 已经明确放弃 (机型不受支持且用户选了不再继续) 就什么也不做,
            // 否则会每秒重开一次设备、每秒再弹一个对话框。
            if (_giveUpOpening) return;

            // 设备可能因为休眠/驱动重启而失效, 定期尝试重连。
            // 这里绝不能弹对话框: 定时器每秒跑一次。
            if (!Open(interactive: false)) return;
        }

        FanSample sample = _reader!.Read();

        if (sample.Error is not null)
        {
            _consecutiveErrors++;

            // 读失败也必须过看门狗。原实现这里直接 return, 后面的温度看门狗
            // 永远不会执行 —— 而"EC 读不到"正是既确认不了温度、也确认不了模式的时刻,
            // 是最需要兜底的那一种状态, 而不是可以跳过监控的状态。
            _mode()?.CheckReadError();

            if (_consecutiveErrors >= 5)
            {
                // 连续失败多半是句柄失效, 重连一次。
                // 先记住原因: Open 成功会把状态改成"已连接", 失败会写它自己的错误,
                // 两种情况都不该把"为什么重连"这条线索丢掉, 所以重连完再补写一次。
                string why = "读取异常: " + sample.Error;
                Open(interactive: false);
                if (_reader is null || !_reader.IsOpen)
                    _setStatus(why, Theme.Err);
                _consecutiveErrors = 0;
            }
            return;
        }

        _consecutiveErrors = 0;

        if (sample.Fans.Count >= 2)
        {
            ApplyFanCard(_fan1Card, sample.Fans[0]);
            ApplyFanCard(_fan2Card, sample.Fans[1]);
        }

        _tempCard.Value = sample.Temperature?.ToString() ?? "--";

        // 回读校准 + 温度看门狗。顺序与拆分前一致 (先校准再判), 不要调换。
        _mode()?.Observe(sample);

        _publisher.Publish(sample);
    }

    /// <summary>把一次读数刷到卡片上, 同时按可信度决定小圆点颜色。</summary>
    private static void ApplyFanCard(DataCard card, FanReading f)
    {
        card.Value = FormatRpm(f);

        // 圆点语义: 有可信读数=绿; 停转=灰 (正常但值得注意); 不可信=橙。
        card.DotColor = !f.IsValid ? Theme.Warn : (f.IsStopped ? Theme.Text3 : Theme.Ok);
    }

    private static string FormatRpm(FanReading f)
    {
        // 读不稳定/越界优先报 "--"; 只有在读数可信时,"0" 才真的代表风扇停了。
        if (!f.IsValid) return "--";
        if (f.IsStopped) return "0";
        return f.Rpm >= 9999 ? ">9999" : f.Rpm.ToString();
    }

    private static byte[] LoadLpcIoBlob()
    {
        var asm = typeof(FanReader).Assembly;
        foreach (string name in asm.GetManifestResourceNames())
        {
            if (!name.EndsWith("LpcIO.bin", StringComparison.OrdinalIgnoreCase)) continue;

            using Stream? s = asm.GetManifestResourceStream(name);
            if (s is null) continue;

            using var buffer = new MemoryStream();
            s.CopyTo(buffer);
            return buffer.ToArray();
        }

        throw new FileNotFoundException("找不到 LpcIO.bin 嵌入资源。");
    }
}
