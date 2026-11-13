using WujieFan.Core.Hardware;
using WujieFan.Core.Native;

namespace WujieFan.Core.Ec;

/// <summary>
/// 读取风扇转速与 CPU 温度。整个过程只读 EC 寄存器。
///
/// 生命周期: 构造 -> Open() -> 循环 Read() -> Dispose()。
/// 打开后一直复用同一个 PawnIO 句柄与端口, 不反复探测, 避免 I/O 抖动。
/// </summary>
public sealed class FanReader : IDisposable
{
    private readonly byte[] _lpcIoBlob;
    private PawnIo? _io;
    private EcAccess? _ec;
    private EcMutex? _mutex;
    private bool _disposed;

    public bool IsOpen => _ec is not null;
    public string? PawnIoVersion { get; private set; }
    public bool HasMutex { get; private set; }

    /// <summary>打开前读取的机器身份 (只读 SMBIOS, 不碰硬件)。</summary>
    public HardwareIdentity? Identity { get; private set; }

    /// <summary>硬件校验结果。未通过时 Open() 会抛 HardwareNotSupportedException。</summary>
    public SupportVerdict? Verdict { get; private set; }
    public ushort PortBase => _ec?.PortBase ?? 0;
    public int Slot => _ec?.Slot ?? -1;

    /// <param name="lpcIoBlob">LpcIO.bin 的字节内容 (LGPL 2.1, 随程序分发)。</param>
    public FanReader(byte[] lpcIoBlob) => _lpcIoBlob = lpcIoBlob;

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

        _io = new PawnIo();
        PawnIoVersion = _io.Version;

        _mutex = new EcMutex();
        HasMutex = _mutex.Acquire(2000);

        try
        {
            _ec = new EcAccess(_io);
            _ec.LoadModule(_lpcIoBlob);
            _ec.DetectPort();
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
            return new FanSample { Timestamp = DateTime.Now, Error = "设备未打开" };

        try
        {
            int? temp = _ec.Read(EcRegisters.CpuTemperature);
            if (temp is < 1 or > 100) temp = null;

            var fans = new List<FanReading>(2)
            {
                ReadFan(1, "Fan1 (CPU)", EcRegisters.Tach1, EcRegisters.PwmOut1),
                ReadFan(2, "Fan2 (GPU)", EcRegisters.Tach2, EcRegisters.PwmOut2),
            };

            return new FanSample
            {
                Timestamp = DateTime.Now,
                Temperature = temp,
                Fans = fans,
            };
        }
        catch (Exception ex)
        {
            return new FanSample { Timestamp = DateTime.Now, Error = ex.Message };
        }
    }

    private FanReading ReadFan(int index, string label, (ushort Lo, ushort Hi) tach, ushort pwmOutAddr)
    {
        int count = _ec!.Read16Stable(tach.Lo, tach.Hi);
        var (rpm, note) = FanSample.ConvertRpm(count);
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
        _ec?.Dispose();
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
