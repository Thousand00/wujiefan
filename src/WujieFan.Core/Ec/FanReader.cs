using WujieFan.Core.Hardware;
using WujieFan.Core.Native;

namespace WujieFan.Core.Ec;

/// <summary>
/// 读取风扇转速与 CPU 温度, 并可切换"自动 / 强冷"两档模式。
///
/// 生命周期: 构造 -> Open() -> 循环 Read() -> Dispose()。
/// 打开后一直复用同一个 PawnIO 句柄与端口, 不反复探测, 避免 I/O 抖动。
///
/// 写入只发生在显式调用 SetMode() 时, 且只写两个风扇的手动 PWM 值与模式标志,
/// 绝不碰温度阈值表、充电限制或任何其它寄存器。
/// </summary>
public sealed class FanReader : IDisposable
{
    private readonly byte[]? _lpcIoBlob;
    private readonly IClock _clock;
    private PawnIo? _io;
    private IEcTransport? _ec;
    private EcMutex? _mutex;
    private bool _disposed;

    /// <summary>
    /// 只读模式: 机型/固件未经验证、由用户选择"强行继续"时置位。
    /// 此时读可以继续 (哪怕数字可能不对), 但 SetMode 一律拒绝 ——
    /// 往未知机器的寄存器里写值不是"试试看"该有的权限。
    /// </summary>
    private bool _readOnly;

    /// <summary>当前是否处于"强行继续"的只读模式 (界面可据此提示用户为什么不能切强冷)。</summary>
    public bool IsReadOnly => _readOnly;

    /// <summary>
    /// 等待 EC 互斥量的超时。
    /// 这把锁是"君子协定": 拿到就规规矩矩地读; 拿不到也不放弃采样 ——
    /// 界面每停一秒不刷新, 用户都会以为程序挂了, 而只读采样本身有防撕裂保护兜底。
    /// </summary>
    private const int MutexTimeoutMs = 300;

    /// <summary>
    /// 写入时等锁的时间要比读取长一些。
    /// 读失败大不了这一秒不刷新; 写失败会让界面以为切换成功、EC 实际没变,
    /// 那种"界面显示强冷但风扇没动"的不一致比多等一会儿糟得多。
    /// </summary>
    private const int WriteMutexTimeoutMs = 1500;

    public bool IsOpen => _ec is not null;
    public string? PawnIoVersion { get; private set; }

    /// <summary>是否成功创建过互斥量。为 false 时仍可只读采样, 但可能与其它监控软件互相干扰。</summary>
    public bool HasMutex => _mutex is not null;

    /// <summary>打开前读取的机器身份 (只读 SMBIOS, 不碰硬件)。</summary>
    public HardwareIdentity? Identity { get; private set; }

    /// <summary>硬件校验结果。未通过时 Open() 会抛 HardwareNotSupportedException。</summary>
    public SupportVerdict? Verdict { get; private set; }

    /// <summary>端口基址。仅在真实硬件路径下有意义, 注入传输时恒为 0。</summary>
    public ushort PortBase => (_ec as EcAccess)?.PortBase ?? 0;

    /// <summary>I2EC 槽位。仅在真实硬件路径下有意义, 注入传输时恒为 -1。</summary>
    public int Slot => (_ec as EcAccess)?.Slot ?? -1;

    /// <param name="lpcIoBlob">LpcIO.bin 的字节内容 (LGPL 2.1, 随程序分发)。</param>
    public FanReader(byte[] lpcIoBlob) : this(lpcIoBlob, SystemClock.Instance) { }

    /// <summary>
    /// 走真硬件的构造。行为与抽取传输层之前完全一致。
    /// </summary>
    public FanReader(byte[] lpcIoBlob, IClock clock)
    {
        _lpcIoBlob = lpcIoBlob;
        _clock = clock;
    }

    /// <summary>
    /// 注入现成传输层的构造。**用于测试**: 传进来的假 EC 已经"打开", 因此
    /// <see cref="Open"/> 变成空操作, 也不会有 PawnIo / 互斥量 / 机型校验参与。
    ///
    /// 这样 <see cref="SetMode"/> 的写序列 (先 PWM 后标志、失败回滚的顺序) 可以在
    /// 没有 EC 硬件的机器上被逐条断言 —— 而这些顺序恰恰是上机肉眼验不出来的。
    /// </summary>
    public FanReader(IEcTransport transport, IClock? clock = null)
    {
        _ec = transport;
        _clock = clock ?? SystemClock.Instance;
    }

    /// <summary>
    /// 打开设备并探测端口。失败会抛出带可读说明的异常。
    ///
    /// 打开顺序是刻意安排的: 先做机型/固件校验, 通过了才去碰 PawnIO 和 EC。
    /// 寄存器地址是特定固件逆向出来的, 机型不符时读出来的数字看着合理但完全错误,
    /// 所以宁可直接拒绝, 也不"尽力而为"。
    /// </summary>
    /// <param name="force">跳过机型校验。仅供用户明确知情后使用。</param>
    public void Open(bool force = false)
    {
        if (_ec is not null) return;

        if (!force)
        {
            Identity = HardwareIdentity.Read();
            Verdict = SupportedHardware.Check(Identity);
            if (!Verdict.IsSupported)
                throw new HardwareNotSupportedException(Verdict.Title, Verdict.Detail);
        }
        else
        {
            // 强行继续 (机型/固件未验证) 时只允许读, 不允许写。
            //
            // 理由: 寄存器地址是逐字节对着特定固件逆向出来的。读错了顶多显示一个
            // 错误的风扇转速, 用户看得见、能判断"这数不对"; 而**写**错了是把
            // 未知机器的电源/风扇/充电寄存器改成某个具体值 —— 后果不可见、不可逆,
            // 也不该由"我想试试"这个选项承担。所以 force 只解锁观察, 不解锁控制。
            _readOnly = true;
        }

        _io = new PawnIo();
        PawnIoVersion = _io.Version;

        _mutex = new EcMutex();

        try
        {
            var ec = new EcAccess(_io);
            if (_lpcIoBlob is null)
                throw new InvalidOperationException("LpcIO.bin 未提供, 无法加载 EC 访问模块。");
            ec.LoadModule(_lpcIoBlob);

            // 端口探测要反复读寄存器, 这段必须持锁。
            // 探测本身是"在候选端口里试哪一个对", 每次都写端口选择寄存器;
            // 拿不到锁就是别的软件正在做同样的时序, 这时候硬插进去会把两边的
            // 时序都打乱 —— 与其猜一个端口, 不如直接说清楚现在不能探测。
            if (!_mutex.Acquire(MutexTimeoutMs))
            {
                throw new EcException(
                    "EC 正被其它监控软件占用, 无法探测端口。请关掉 HWiNFO / 风扇控制类软件后重试, 或稍后再试。");
            }
            try { ec.DetectPort(); }
            finally { _mutex.Release(); }

            // 探测成功后才挂上, 避免半初始化的 EcAccess 被 Read() 用到。
            _ec = ec;
        }
        catch
        {
            _ec = null;
            _io.Dispose();
            _io = null;
            _mutex?.Dispose();
            _mutex = null;
            throw;
        }
    }

    /// <summary>采一次样。读失败时返回带 Error 的对象, 不抛异常 (供界面持续刷新)。</summary>
    public FanSample Read()
    {
        if (_ec is null)
            return new FanSample { Timestamp = _clock.Now, Error = "设备未打开" };

        try
        {
            // 一把锁只覆盖一次完整采样: 拿不到就等下一轮。
            // 长期持锁会让 HWiNFO / LHM 这类同样守约定的软件彻底读不到 EC。
            //
            // 拿不到锁**不能**硬读: 一次采样由十几条端口写组成, 别人的 I2EC 时序
            // 正插在中间时读出来的是错值 —— 温度尤其危险, 它会被送进过热看门狗。
            // 如实报本轮失败, 让上层按"读失败"处理 (连续 N 次才收手)。
            if (_mutex is not null && !_mutex.Acquire(MutexTimeoutMs))
            {
                return new FanSample
                {
                    Timestamp = _clock.Now,
                    Error = "EC 正被其它监控软件占用, 本次采样跳过。",
                };
            }

            int? temp;
            var fans = new List<FanReading>(2);
            FanModeState mode;
            try
            {
                temp = _ec.Read(EcRegisters.CpuTemperature);
                if (temp is < 1 or > 100) temp = null;

                fans.Add(ReadFan(1, "Fan1 (CPU)", EcRegisters.Tach1, EcRegisters.PwmOut1));
                fans.Add(ReadFan(2, "Fan2 (GPU)", EcRegisters.Tach2, EcRegisters.PwmOut2));

                mode = ReadModeStateLocked();
            }
            finally
            {
                _mutex?.Release();
            }

            return new FanSample
            {
                Timestamp = _clock.Now,
                Temperature = temp,
                Fans = fans,
                Mode = mode,
            };
        }
        catch (Exception ex)
        {
            return new FanSample { Timestamp = _clock.Now, Error = ex.Message };
        }
    }

    /// <summary>
    /// 只读当前模式 (单独调用时自己拿锁)。
    ///
    /// 拿不到锁返回 Unknown, 而不是在锁外硬读: 这个方法的结果会被拿去决定
    /// "退出时要不要交还自动", 一个在交错时序下读错的 Mixed/Auto 会让程序
    /// 误判成"不用收手", 把一个风扇永久留在手动满转上。
    /// 读不到就说读不到, 上层据此走保守分支。
    /// </summary>
    public FanModeState ReadModeState()
    {
        if (_ec is null) return FanModeState.Unknown;

        try
        {
            if (_mutex is not null && !_mutex.Acquire(MutexTimeoutMs))
                return FanModeState.Unknown;

            try { return ReadModeStateLocked(); }
            finally { _mutex?.Release(); }
        }
        catch
        {
            return FanModeState.Unknown;
        }
    }

    /// <summary>调用方必须已持有互斥量。</summary>
    private FanModeState ReadModeStateLocked()
    {
        if (_ec is null) return FanModeState.Unknown;

        int f1 = _ec.Read(EcRegisters.ModeFlag1);
        int f2 = _ec.Read(EcRegisters.ModeFlag2);

        if (f1 == EcRegisters.ModeAuto && f2 == EcRegisters.ModeAuto) return FanModeState.Auto;
        if (f1 == EcRegisters.ModeManualPwm && f2 == EcRegisters.ModeManualPwm) return FanModeState.StrongCool;

        // 0xA5 / 0xCC 以及两个风扇不一致的情形都归到 Mixed:
        // 这些状态下 EC 的行为不由本程序决定, 界面只能如实报告, 不能假装是自动。
        return FanModeState.Mixed;
    }

    /// <summary>
    /// 切到指定模式。这是本程序唯一会写 EC 的地方。
    ///
    /// 顺序是硬要求:
    ///   进强冷 -> 先写手动 PWM 值, 再写模式标志。反过来的话, 标志一旦变成 0xDC,
    ///             EC 会立刻采信**上一次残留**的手动 PWM 值, 中间那一瞬转速是错的。
    ///   回自动 -> 只写模式标志 0x00, 不碰手动 PWM 值。EC 在自动模式下根本不读它,
    ///            留着旧值无害, 而多写一次就多一次出错机会。
    /// </summary>
    public FanModeResult SetMode(FanMode mode)
    {
        if (_ec is null)
            return FanModeResult.Failure("设备未打开, 无法切换风扇模式。");

        // 强行继续模式下只读。这条路必须在这里拦住, 而不是靠界面不显示按钮:
        // 逻辑上"能不能写"是这一层的属性, 上面任何一个调用点漏判都会变成
        // 往未验证的机器上写寄存器。
        if (_readOnly)
            return FanModeResult.Failure(
                "这台机器的机型/EC 固件没有通过校验, 当前处于只读模式, 不能切换风扇模式。" +
                "只读模式只保证不会写错寄存器, 显示的转速也可能不准。");

        bool locked = false;
        try
        {
            if (_mutex is not null) locked = _mutex.Acquire(WriteMutexTimeoutMs);

            // 拿不到锁就不写。只读采样拿不到锁可以照旧进行, 写入不行:
            // 别的软件的 I2EC 时序正插在中间时写进去, 可能落到错误地址。
            if (_mutex is not null && !locked)
                return FanModeResult.Failure("EC 正被其它监控软件占用, 请稍后重试。");

            // 写入前快照。中途失败时靠它把 EC 还原, 并在提示里说清还原到了什么状态。
            var snapshot = ModeSnapshot.Capture(_ec);

            try
            {
                if (mode == FanMode.StrongCool)
                {
                    // 先写 PWM 再写标志: 反过来的话标志一变 0xDC, EC 会立刻采信上一次的残留值。
                    _ec.WritePwm(EcRegisters.ManualPwm1, EcRegisters.PwmMax);
                    _ec.WritePwm(EcRegisters.ManualPwm2, EcRegisters.PwmMax);
                    _ec.Write(EcRegisters.ModeFlag1, EcRegisters.ModeManualPwm);
                    _ec.Write(EcRegisters.ModeFlag2, EcRegisters.ModeManualPwm);
                }
                else
                {
                    _ec.Write(EcRegisters.ModeFlag1, EcRegisters.ModeAuto);
                    _ec.Write(EcRegisters.ModeFlag2, EcRegisters.ModeAuto);
                }
            }
            catch (Exception writeEx)
            {
                // 四个寄存器不是原子写入的: 中断在这里就可能留下
                // Fan1=0xDC / Fan2=0x00 的分裂状态 (一个风扇手动满转, 另一个交还 EC)。
                // 必须主动回滚, 否则这个残留不受本程序任何后续逻辑管理。
                return RecoverToAuto(writeEx, snapshot);
            }

            // 回读确认。写完不看一眼就等于赌 EC 收下了 ——
            // 之前踩过"界面说切换成功、EC 里其实还是旧值"的坑, 这里必须核对。
            //
            // 回读单独 try: 走到这里说明四个写入全部成功, 只有"看一眼"这一下失败了。
            // 这时有两种错误做法, 都必须避免:
            //   报成普通失败 —— 用户以为没生效, 实际风扇已经在按新模式转;
            //   回滚 —— 把用户刚要的强冷又撤掉, 等于违背指令。
            // 真实的语义是"写完了, 但无法确认现在是什么模式", 确定性恰恰是未知的。
            FanModeState actual;
            int pwm1;
            int pwm2;
            try
            {
                actual = ReadModeStateLocked();

                // 强冷时还要核对"EC 是否真的收下了 184"。
                //
                // 只读模式标志是不够的: 标志写成功只能说明 EC 切到了手动模式,
                // 而手动模式下它采信的是 ManualPwm1/2 里的值 —— 那个值可能是
                // **上一次残留的** (比如 0 或几十), 于是标志对了、风扇却低速甚至停转,
                // 程序却报"两个风扇满转"。EC 收下时序但丢弃数值这种静默失败,
                // 只有回读能发现。
                pwm1 = mode == FanMode.StrongCool ? _ec.Read(EcRegisters.ManualPwm1) : 0;
                pwm2 = mode == FanMode.StrongCool ? _ec.Read(EcRegisters.ManualPwm2) : 0;
            }
            catch (Exception readEx)
            {
                string what = mode == FanMode.StrongCool ? "强冷" : "自动";
                return FanModeResult.Failure(
                    $"已向 EC 写入切换到{what}模式的指令, 但回读确认失败: {readEx.Message} " +
                    "写入可能已经生效, 风扇也许已在按新模式转动 —— 请查看当前转速与温度确认实际状态。",
                    FanModeState.Unknown, indeterminate: true);
            }

            // 回读到 Mixed 说明两个风扇的标志不一致, 正是"分裂残留"的样子,
            // 属于不确定状态, 要区别于普通的"EC 没接受"。
            bool split = actual == FanModeState.Mixed;

            // 标志对了但 PWM 没进去 —— 风扇可能按上一次残留的占空比在转。
            // 这是"看着成功、实际没满转"的静默失败, 必须报成不确定: 写入确实生效了一部分
            // (模式已切手动), 不能简单说"失败"让用户以为风扇还是自动。
            if (mode == FanMode.StrongCool && actual == FanModeState.StrongCool
                && (pwm1 != EcRegisters.PwmMax || pwm2 != EcRegisters.PwmMax))
            {
                return FanModeResult.Failure(
                    $"已切到强冷模式, 但手动 PWM 值没有生效 (回读到 Fan1={pwm1} / Fan2={pwm2}, " +
                    $"应为 {EcRegisters.PwmMax})。" +
                    "风扇可能按上一次残留的占空比转动, 转速未必是满转 —— 请查看当前转速确认。",
                    actual, indeterminate: true);
            }

            return mode switch
            {
                FanMode.StrongCool when actual == FanModeState.StrongCool =>
                    FanModeResult.Success("已切到强冷模式 (两个风扇满转)。", actual),

                FanMode.StrongCool =>
                    FanModeResult.Failure(
                        $"写入后回读的模式是 {Describe(actual)}, EC 没有接受强冷。" +
                        (split ? "两个风扇的标志不一致, 可能有一个仍随手动值转动, 请确认温度。" : ""),
                        actual, indeterminate: split),

                _ when actual == FanModeState.Auto =>
                    FanModeResult.Success("已交还自动模式, 由 EC 按温度曲线调速。", actual),

                _ =>
                    FanModeResult.Failure(
                        $"写入后回读的模式是 {Describe(actual)}, EC 没有回到自动。" +
                        (split ? "两个风扇的标志不一致, 风扇状态不明确, 请确认温度。" : ""),
                        actual, indeterminate: split),
            };
        }
        catch (Exception ex)
        {
            return FanModeResult.Failure("切换风扇模式失败: " + ex.Message);
        }
        finally
        {
            // 无条件释放: Acquire 部分成功时也可能已经持有了句柄,
            // 按返回值决定是否 Release 会把那把锁永久留在本进程手里。
            _mutex?.Release();
        }
    }

    /// <summary>
    /// 写入中途失败后的回滚: 尽力把两个风扇都交还 EC 自动模式。
    ///
    /// 顺序上先撤标志再还原 PWM —— EC 在自动模式下不采信手动 PWM 值,
    /// 所以让标志离开 0xDC 是止损的第一步; 之后再把手动 PWM 还原成我们进来时的值,
    /// 免得残留的 184 将来被别的软件改成 0xDC 时立刻满转。
    ///
    /// 回滚本身也可能失败 (EC 忙 / 句柄失效)。那种情况下风扇状态是不确定的,
    /// 必须通过 <see cref="FanModeResult.Indeterminate"/> 让上层显著提示用户,
    /// 不能只写一行日志就当作"切换失败"了事。
    /// </summary>
    private FanModeResult RecoverToAuto(Exception cause, ModeSnapshot snapshot)
    {
        var problems = new List<string>();

        TryWrite(EcRegisters.ModeFlag1, EcRegisters.ModeAuto, problems);
        TryWrite(EcRegisters.ModeFlag2, EcRegisters.ModeAuto, problems);
        TryWritePwm(EcRegisters.ManualPwm1, snapshot.Pwm1, problems);
        TryWritePwm(EcRegisters.ManualPwm2, snapshot.Pwm2, problems);

        FanModeState actual;
        try
        {
            actual = ReadModeStateLocked();
        }
        catch (Exception ex)
        {
            problems.Add("回读模式失败: " + ex.Message);
            actual = FanModeState.Unknown;
        }

        string causeText = cause.Message;
        string extra = problems.Count == 0 ? "" : " 回滚过程中的问题: " + string.Join("; ", problems) + "。";

        if (actual == FanModeState.Auto)
        {
            return FanModeResult.Failure(
                $"切换风扇模式失败: {causeText} 已回滚到自动模式 (EC 现为{Describe(actual)}), 由固件接管调速。{extra}",
                actual, indeterminate: false);
        }

        return FanModeResult.Failure(
            $"切换风扇模式失败: {causeText} **且回滚未能完成**, EC 现在回读到{Describe(actual)}。" +
            "风扇可能处于不确定状态 (一个手动满转、另一个交给 EC, 或标志未生效)。" +
            "请立即确认 CPU 温度, 必要时重启本程序或手动切回自动。" + extra,
            actual, indeterminate: true);
    }

    private void TryWrite(ushort address, byte value, List<string> problems)
    {
        try { _ec?.Write(address, value); }
        catch (Exception ex) { problems.Add($"写 0x{address:X4} 失败: {ex.Message}"); }
    }

    private void TryWritePwm(ushort address, int value, List<string> problems)
    {
        // 还原快照用的是**原始写入**, 不是 WritePwm。
        // 快照里的值是"我们进来时 EC 里本来就有的", 可能是别的软件留下的
        // 越界值 (例如 200, 那时风扇本来就是停的)。用 WritePwm 还原会因为它
        // 越界而抛异常, 结果反而是我们写进去的 184 留在了寄存器里 ——
        // 还原一个既存状态, 不该受"命令值必须合法"的约束。
        // 反正标志已经先被撤成自动, EC 不会采信这个值。
        try { _ec?.Write(address, (byte)value); }
        catch (Exception ex) { problems.Add($"还原 0x{address:X4} 失败: {ex.Message}"); }
    }

    /// <summary>写入前四个寄存器 (两个手动 PWM、两个模式标志) 的取值。</summary>
    private readonly record struct ModeSnapshot(int Pwm1, int Pwm2, int Flag1, int Flag2)
    {
        public static ModeSnapshot Capture(IEcTransport ec) => new(
            ec.Read(EcRegisters.ManualPwm1),
            ec.Read(EcRegisters.ManualPwm2),
            ec.Read(EcRegisters.ModeFlag1),
            ec.Read(EcRegisters.ModeFlag2));
    }

    /// <summary>模式的中文说明, 界面和日志共用。</summary>
    public static string Describe(FanModeState state) => state switch
    {
        FanModeState.Auto => "自动",
        FanModeState.StrongCool => "强冷",
        FanModeState.Mixed => "混合/未知标志",
        _ => "未知",
    };

    private FanReading ReadFan(int index, string label, (ushort Lo, ushort Hi) tach, ushort pwmOutAddr)
    {
        int count = _ec!.Read16Stable(tach.Lo, tach.Hi, out bool stable);
        var (rpm, note) = FanSample.ConvertRpm(count);

        // 防撕裂读失败: 这次数值可能是高低字节来自不同时刻拼出来的, 丢弃。
        if (!stable) note = "读取不稳定(已丢弃本次计数)";

        return new FanReading
        {
            Index = index,
            Label = label,
            RawCount = count,
            Rpm = rpm,
            Note = note,
            PwmOut = _ec.Read(pwmOutAddr),
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // IEcTransport 不继承 IDisposable: 接口只留 FanReader 真正用到的三个操作,
        // 免得每个假实现都要写一个空 Dispose。真实现 (EcAccess) 仍是 IDisposable,
        // 这里按需转型释放; 注入的测试传输没有可释放资源时转型为 null 也无害。
        (_ec as IDisposable)?.Dispose();
        _ec = null;
        _io?.Dispose();
        _io = null;
        _mutex?.Dispose();
        _mutex = null;
    }
}

/// <summary>
/// 机器型号或 EC 固件版本与本程序验证过的不一致。
/// 捕获这个异常后应当向用户完整展示 Title 与 Detail, 而不是简单报"打开失败"。
/// </summary>
public sealed class HardwareNotSupportedException : Exception
{
    public string Reason { get; }
    public string Guidance { get; }

    public HardwareNotSupportedException(string reason, string guidance)
        : base($"{reason}\n{guidance}")
    {
        Reason = reason;
        Guidance = guidance;
    }
}
