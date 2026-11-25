using System.Drawing;
using Zephyr.Core.Config;
using Zephyr.Core.Ec;
using Zephyr.Core.Hardware;

namespace Zephyr.Ui;

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

    /// <summary>自动重连失败后的退避: 当前等待秒数 (0 表示没有在退避)。</summary>
    private int _openBackoffSeconds;

    /// <summary>下一次允许尝试自动重连的时刻。早于此刻的 Poll 直接返回。</summary>
    private DateTime _nextOpenAttemptUtc = DateTime.MinValue;

    /// <summary>首次重连失败后等 2 秒再试 —— 一次偶发失败不值得等太久。</summary>
    private const int MinOpenBackoffSeconds = 2;

    /// <summary>退避上限。持续失败时每 30 秒试一次, 足够把设备接回来而不至于刷屏。</summary>
    private const int MaxOpenBackoffSeconds = 30;

    /// <summary>一拍耗时的环形缓冲长度与上报间隔 (拍)。</summary>
    private const int DurationWindow = 128;
    private const int DurationReportEvery = 60;

    private readonly double[] _durations = new double[DurationWindow];
    private int _durationIndex;
    private int _durationCount;
    private int _pollsSinceReport;

    public FanPollingController(
        Form owner,
        AppSettings settings,
        DataCard fan1Card,
        DataCard fan2Card,
        RtssPublisher publisher,
        Func<FanModeController?> mode,
        Action<string, Color> setStatus)
    {
        _owner = owner;
        _settings = settings;
        _fan1Card = fan1Card;
        _fan2Card = fan2Card;
        _publisher = publisher;
        _mode = mode;
        _setStatus = setStatus;

        _timer.Interval = Math.Clamp(_settings.PollIntervalMs, 200, 10000);
        _timer.Tick += (_, _) => RunPollSafely();
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

            // 成功一次就清掉退避: 不管是自动重连还是用户手动重试接回来的,
            // 都不该继续被之前的等待时间挡着。
            _openBackoffSeconds = 0;
            _nextOpenAttemptUtc = DateTime.MinValue;

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

            // 用户选了"继续"时, ShowBlockingMessage 内部已经用 force 重开过一次,
            // 而且成功了(状态栏已写成"已连接 · 已跳过机型校验")。
            // 这里如果接着往下走, 会把那条成功提示覆盖成红字"读取失败" 并 return false,
            // 而设备其实正在正常轮询 —— 状态栏从此永远停在一个假的失败上。
            if (interactive && ShowBlockingMessage(ex.Reason, ex.Guidance) && _reader is not null)
                return true;

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

    /// <summary>
    /// 询问用户是否在机型不符的情况下继续。用户点"继续"就直接重新打开。
    /// </summary>
    /// <returns>用户选了继续并且重新打开成功时为 true。</returns>
    private bool ShowBlockingMessage(string reason, string guidance)
    {
        bool go = DarkDialog.Ask(_owner, "硬件不受支持",
            reason + "\n\n" + guidance + "\n\n是否仍然继续? (可能显示错误的风扇转速)",
            yes: "继续读取", no: "取消", tone: DarkDialog.Tone.Warning);

        if (!go) return false;

        _settings.ForceUnsupported = true;

        // 用户已经明确选了"继续", 所以不因为存盘失败就回退这次打开 ——
        // 但下次启动会再问一遍, 这个后果要说清楚, 否则用户以为"以后不再问了"。
        if (!_settings.Save())
            UiLog("保存 ForceUnsupported=true 失败: " + (AppSettings.LastError ?? "未知原因"));

        // 重新打开一次。这一步失败也不再弹第二个对话框。
        _giveUpOpening = false;
        return Open(interactive: false);
    }

    /// <summary>清掉"跳过机型校验"的标志并重新检测。托盘菜单用, 给用户一个反悔的入口。</summary>
    public void RetryHardwareCheck()
    {
        _settings.ForceUnsupported = false;

        // 存盘失败时内存里已是 false, 所以本次检测照常按"不跳过校验"进行;
        // 但下次启动会重新读回磁盘上的 true, 用户会觉得"点了没记住"。
        if (!_settings.Save())
            UiLog("保存 ForceUnsupported=false 失败: " + (AppSettings.LastError ?? "未知原因"));
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
    /// 定时器回调的兜底壳: 任何未预期异常都不能从 Tick 里冒出去。
    /// WinForms 的定时器回调抛异常会走 Application.ThreadException,
    /// 在默认配置下就是"程序崩溃"的对话框 —— 每 1 秒一次的机会, 不值得赌。
    /// 这里只记日志 + 状态栏提示, 下一拍照常继续 (设备可能只是这一拍出问题)。
    /// </summary>
    private void RunPollSafely()
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            Poll();
        }
        catch (Exception ex)
        {
            _setStatus("采样时出现异常, 已跳过这一拍: " + ex.Message, Theme.Err);
            UiLog("Poll 异常: " + ex);
        }
        finally
        {
            RecordPollDuration(System.Diagnostics.Stopwatch.GetElapsedTime(started));
        }
    }

    /// <summary>
    /// 一拍耗时的滚动统计, 每 <see cref="DurationReportEvery"/> 拍往 ui.log 记一行 p50/p99。
    ///
    /// 为什么要记这个: 轮询跑在 UI 线程的定时器回调里, 一拍卡住就等于界面卡住。
    /// 而耗时的大头不在本进程 —— 是等 EC 互斥量 (HWiNFO / LHM 占着时最坏 300 毫秒)
    /// 和十几条内核驱动往返。没有这条日志就只能靠"用户说卡"来推断。
    /// 只留最近 128 拍, 内存固定几 KB, 不影响稳态开销。
    /// </summary>
    private void RecordPollDuration(TimeSpan elapsed)
    {
        _durations[_durationIndex] = elapsed.TotalMilliseconds;
        _durationIndex = (_durationIndex + 1) % _durations.Length;
        if (_durationCount < _durations.Length) _durationCount++;

        if (++_pollsSinceReport < DurationReportEvery) return;
        _pollsSinceReport = 0;

        if (_durationCount < 8) return;   // 样本太少, 分位数没有意义

        double[] sorted = _durations.Take(_durationCount).OrderBy(v => v).ToArray();
        double p50 = sorted[(int)(sorted.Length * 0.50)];
        double p99 = sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * 0.99))];
        UiLog($"轮询耗时 p50 {p50:F1}ms / p99 {p99:F1}ms / max {sorted[^1]:F1}ms ({sorted.Length} 拍)");
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

            // 自动重连要退避。每次 Open 都会 new PawnIO、加载 LpcIO.bin、探测端口,
            // 失败时还要写一次状态栏 (会触发整个窗口 Relayout)。
            // 不节流的话, 设备拔掉/驱动没起来的那种持续失败会变成
            // 「每秒重建设备 + 每秒重绘整窗」的风暴, 而用户看到的只是一行错误。
            // 首次失败后从 2 秒起翻倍, 封顶 30 秒; 成功一次就清零。
            if (_nextOpenAttemptUtc > DateTime.UtcNow) return;

            // 设备可能因为休眠/驱动重启而失效, 定期尝试重连。
            // 这里绝不能弹对话框: 定时器每秒跑一次。
            if (!Open(interactive: false))
            {
                _openBackoffSeconds = Math.Min(
                    Math.Max(_openBackoffSeconds * 2, MinOpenBackoffSeconds), MaxOpenBackoffSeconds);
                _nextOpenAttemptUtc = DateTime.UtcNow.AddSeconds(_openBackoffSeconds);
                return;
            }

            _openBackoffSeconds = 0;
            _nextOpenAttemptUtc = DateTime.MinValue;
        }

        // 占空比不读: 界面和 RTSS 都不显示它, 每轮却要为它多跑 24 次驱动往返。
        FanSample sample = _reader!.Read(readPwmOut: false);

        if (sample.Error is not null)
        {
            // 锁被别人临时占着, 不是设备坏了。这种失败不该累加到"连续失败"里:
            // 累加到 5 次就会触发下面的重连, 而重连本身也要抢同一把锁,
            // 于是变成「争用 → 重连 → 清零 → 再争用」的自转, 反而更久拿不到数据。
            // 只提示一下, 下一拍继续试即可 —— EC 互斥量本来就是"君子协定",
            // 别人偶尔占一下是正常状态。
            if (sample.Transient)
            {
                // 但看门狗照走: 交还自动本身也要抢同一把锁, 抢不到只是白跑一趟,
                // 而少走这一步就等于把"读不到就交还"这条安全路径关掉了。
                _setStatus("EC 正被其它监控软件占用, 稍后自动重试。", Theme.Warn);
                _mode()?.CheckReadError();
                return;
            }

            _consecutiveErrors++;

            // 读失败也必须过看门狗。原实现这里直接 return, 后面的看门狗
            // 永远不会执行 —— 而"EC 读不到"正是既确认不了转速、也确认不了模式的时候,
            // 是最需要兜底的那一种状态, 而不是可以跳过监控的状态。
            _mode()?.CheckReadError();

            // 界面与覆盖层都清成"无数据"。原来这里只 return, 卡片和 RTSS 覆盖层
            // 会一直停在最后一次成功读到的值 —— 看起来一切正常, 实际上什么都没读到。
            // 这种"静默陈旧"比显示空白危险: 用户会照着假转速判断散热是否正常。
            _fan1Card.Value = "--";
            _fan2Card.Value = "--";

            // 刻度条一起清空。留着上一次的比例比留数字更误导: 数字至少看得出是旧的,
            // 一条还在 60% 的条看起来就像"现在正转着"。
            _fan1Card.SetMeterRaw(-1);
            _fan2Card.SetMeterRaw(-1);
            _publisher.PublishUnavailable();

            if (_consecutiveErrors >= 5)
            {
                // 连续失败多半是句柄失效, 重连一次。
                // 先记住原因: Open 成功会把状态改成"已连接", 失败会写它自己的错误,
                // 两种情况都不该把"为什么重连"这条线索丢掉, 所以重连完再补写一次。
                //
                // 但重连也要受退避闸门约束: 原来这里是无条件 Open, 于是设备真的坏了
                // 的时候, 每 5 拍就 new 一次 PawnIo + 重载 LpcIO.bin + 探测端口,
                // 而 Open 成功又会把 _openBackoffSeconds 清零 —— 那两处互相抵消,
                // 退避等于不存在, 退化成"每 5 秒重建设备"的稳定循环。
                string why = "读取异常: " + sample.Error;
                if (_nextOpenAttemptUtc <= DateTime.UtcNow)
                {
                    Open(interactive: false);

                    // 重连没成也要像上面那条路径一样把退避推远, 否则下一次失败
                    // 立刻又落进这个分支。
                    if (_reader is null || !_reader.IsOpen)
                    {
                        _openBackoffSeconds = Math.Min(
                            Math.Max(_openBackoffSeconds * 2, MinOpenBackoffSeconds), MaxOpenBackoffSeconds);
                        _nextOpenAttemptUtc = DateTime.UtcNow.AddSeconds(_openBackoffSeconds);
                    }
                }

                if (_reader is null || !_reader.IsOpen)
                    _setStatus(why, Theme.Err);
                _consecutiveErrors = 0;
            }
            return;
        }

        _consecutiveErrors = 0;

        // 按实际返回的条数逐张刷新, 不要写成 ">= 2 才刷新两张":
        // 只回来一个风扇时, 另一张会保留上一帧的数字, 看起来像还在转。
        ApplyFanCard(_fan1Card, sample.Fans, 0);
        ApplyFanCard(_fan2Card, sample.Fans, 1);

        // 回读校准 + 看门狗。顺序与拆分前一致 (先校准再判), 不要调换。
        _mode()?.Observe(sample);

        _publisher.Publish(sample);
    }

    /// <summary>
    /// 把某一号风扇的读数刷到卡片上。这一号没回来时清成"--"并清空刻度条,
    /// 而不是保留上一帧 —— 静默陈旧比空白危险。
    /// </summary>
    private static void ApplyFanCard(DataCard card, IReadOnlyList<FanReading> fans, int index)
    {
        if (index >= fans.Count)
        {
            card.Value = "--";
            card.DotColor = Theme.Text3;
            card.SetMeterRaw(-1);
            return;
        }

        FanReading f = fans[index];
        card.Value = FormatRpm(f);

        // 圆点语义: 有可信读数=绿; 停转=灰 (正常但值得注意); 不可信=橙。
        card.DotColor = !f.IsValid ? Theme.Warn : (f.IsStopped ? Theme.Text3 : Theme.Ok);

        // 刻度条只认可信读数。越界/读不稳定的值如果画成条, 会显示成"几乎满转",
        // 而实际情况是根本没读到 —— 清空才诚实。
        card.SetMeter(f.IsValid ? f.Rpm : null, DataCard.FanScaleRpm);
    }

    private static string FormatRpm(FanReading f)
    {
        // 读不稳定/越界优先报 "--"; 只有在读数可信时,"0" 才真的代表风扇停了。
        if (!f.IsValid) return "--";
        if (f.IsStopped) return "0";

        // 不截断成 ">9999": RTSS 那边写的是原始数字 (例如 10029),
        // 界面和 overlay 显示成两个不同的值会让人以为哪边坏了。
        // 数字太长时由卡片自己按宽度省略, 而不是在这里改写数值本身。
        return f.Rpm.ToString();
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

    private static void UiLog(string message) => global::Zephyr.Program.UiLog(message);
}
