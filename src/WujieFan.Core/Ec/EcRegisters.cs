namespace WujieFan.Core.Ec;

/// <summary>
/// MECHREVO 无界14+ (ARN39P-TF, Ryzen 7 7840HS, ITE EC) 的寄存器地址。
/// 全部来自对 32MB SPI 固件 (EC 固件位于 ROM 0x000000-0x05FFFF) 的逆向与实测。
/// 本软件只读这些地址, 绝不写入。
/// </summary>
public static class EcRegisters
{
    /// <summary>CPU 温度 (摄氏度, 1..100)。EC XRAM 0x9600 越界不可达, 这是主机侧唯一可用温度。</summary>
    public const ushort CpuTemperature = 0x0470;

    /// <summary>EC 版本三字节 (0x0000/0x0001/0x00B0)。2.12 = 27 / 240 / 1。</summary>
    public static readonly ushort[] VersionBytes = { 0x0000, 0x0001, 0x00B0 };

    /// <summary>风扇 1 (CPU) 转速计数器, 低字节在前, 高字节在后。</summary>
    public static readonly (ushort Lo, ushort Hi) Tach1 = (0x181E, 0x181F);

    /// <summary>风扇 2 (GPU) 转速计数器。</summary>
    public static readonly (ushort Lo, ushort Hi) Tach2 = (0x1820, 0x1821);

    /// <summary>风扇 1 PWM 硬件输出占空比。</summary>
    public const ushort PwmOut1 = 0x1809;

    /// <summary>风扇 2 PWM 硬件输出占空比。</summary>
    public const ushort PwmOut2 = 0x1804;

    /// <summary>PWM 周期寄存器对应的最大值: 有效占空比 0..184, 超过 184 风扇会直接停转。</summary>
    public const int PwmMax = 184;

    /// <summary>转速换算常数: RPM = TachConstant / 计数器值。</summary>
    public const int TachConstant = 2_156_250;

    /// <summary>I2EC 端口基址候选。0x4E 的 EC 槽位为 1, 0x2E 为 0。</summary>
    public static readonly (ushort Base, int Slot)[] PortBases = { (0x4E, 1), (0x2E, 0) };
}
