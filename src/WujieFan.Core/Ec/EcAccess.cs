using WujieFan.Core.Native;

namespace WujieFan.Core.Ec;

/// <summary>
/// ITE EC 的 I2EC 间接访问通道。
///
/// 时序照搬已验证的实现 (fanprobe.py 的 EcI2EC):
///   选槽位:     exec("ioctl_select_slot", [slot], 0)     —— 无输出参数
///   选择寄存器: outb(base, 0x2E) 然后 outb(base+1, reg)   —— 无输出参数
///   提交数据:   outb(base, 0x2F) 然后 outb(base+1, value) —— 无输出参数
///   读地址:     置 0x10=addr低, 0x11=addr高, 然后 outb(base,0x2F) 再 inb(base+1)
///
/// 无输出的函数必须按 out_size=0 调用。传 out_size=1 时驱动不写缓冲区,
/// 读到的会是残留内存 —— 曾把 "ioctl_se" 字符串解释成返回值 7310291539566227305。
///
/// 这里只做读取。写 EC 寄存器会改变风扇/电源行为, 超出本程序职责。
/// </summary>
public sealed class EcAccess : IDisposable
{
    private readonly PawnIo _io;
    private bool _moduleLoaded;

    public ushort PortBase { get; private set; } = 0x4E;
    public int Slot { get; private set; }

    public EcAccess(PawnIo io) => _io = io;

    public void LoadModule(byte[] lpcIoBlob)
    {
        if (_moduleLoaded) return;
        _io.LoadModule(lpcIoBlob, "LpcIO.bin");
        _moduleLoaded = true;
    }

    /// <summary>
    /// 探测 I2EC 端口基址: 读 CPU 温度, 落在 1..100 才算命中。
    /// 每次尝试都重新 select_slot —— 槽位与基址一一对应。
    /// </summary>
    public void DetectPort()
    {
        var failures = new List<string>();

        foreach (var (basePort, slot) in EcRegisters.PortBases)
        {
            PortBase = basePort;
            Slot = slot;
            try
            {
                _io.ExecuteVoid("ioctl_select_slot", (ulong)slot);
                int t = Read(EcRegisters.CpuTemperature);
                if (t is >= 1 and <= 100)
                    return;
                failures.Add($"端口 0x{basePort:X2} 读到的温度是 {t} (应在 1..100)");
            }
            catch (Exception ex)
            {
                failures.Add($"端口 0x{basePort:X2} 读取异常: {ex.Message}");
            }
        }

        throw new EcException(
            "无法探测 I2EC 端口基址 (试过 0x4E / 0x2E)。" + Environment.NewLine +
            string.Join(Environment.NewLine, failures) + Environment.NewLine +
            "可能原因: 机型不同 / 端口被其它软件占用 / EC 忙。");
    }

    // ---------------------------------------------------------------- I/O

    private void WritePort(ushort port, byte value)
        => _io.ExecuteVoid("ioctl_pio_outb", port, value);

    private byte ReadPort(ushort port)
        => (byte)(_io.Execute1("ioctl_pio_inb", port) & 0xFF);

    private void Select(byte register)
    {
        WritePort(PortBase, 0x2E);
        WritePort((ushort)(PortBase + 1), register);
    }

    private void Commit(byte value)
    {
        WritePort(PortBase, 0x2F);
        WritePort((ushort)(PortBase + 1), value);
    }

    // ---------------------------------------------------------------- 对外

    /// <summary>
    /// 写 EC 地址空间的一个字节。
    ///
    /// 这是**危险操作**: 写错地址可能改变风扇/电源/充电行为, 甚至让机器过热。
    /// 生产程序 (WujieFan) 不调用本方法; 只有经过显式确认的实验工具才会用到。
    /// 调用方必须保证: 写完能恢复原值, 且有独立的温控看门狗兜底。
    /// </summary>
    public void Write(ushort address, byte value)
    {
        Select(0x10); Commit((byte)(address & 0xFF));
        Select(0x11); Commit((byte)(address >> 8));
        Select(0x12); Commit(value);
    }

    /// <summary>读 EC 地址空间的一个字节。</summary>
    public int Read(ushort address)
    {
        Select(0x10);
        Commit((byte)(address & 0xFF));
        Select(0x11);
        Commit((byte)(address >> 8));
        Select(0x12);
        WritePort(PortBase, 0x2F);
        return ReadPort((ushort)(PortBase + 1));
    }

    /// <summary>
    /// 读 16 位转速计数器。按 高-低-高 顺序读, 两次高字节一致才采信,
    /// 避免计数器在读取过程中进位导致的错值。6 次都不稳定时返回最后一次拼出的值,
    /// 但把 stable 置为 false —— 调用方必须据此判定这次读数不可信,
    /// 因为撕裂值可能恰好落在看起来正常的区间里, 只靠范围检查发现不了。
    /// </summary>
    public int Read16Stable(ushort loAddress, ushort hiAddress, out bool stable)
    {
        int last = 0;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            int hi1 = Read(hiAddress);
            int lo  = Read(loAddress);
            int hi2 = Read(hiAddress);
            last = (hi2 << 8) | lo;
            if (hi1 == hi2)
            {
                stable = true;
                return last;
            }
        }

        stable = false;
        return last;
    }

    public void Dispose() { }
}

public sealed class EcException : Exception
{
    public EcException(string message) : base(message) { }
    public EcException(string message, Exception inner) : base(message, inner) { }
}
