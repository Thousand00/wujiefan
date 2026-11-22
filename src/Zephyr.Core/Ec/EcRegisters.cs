namespace Zephyr.Core.Ec;

/// <summary>
/// MECHREVO 无界14+ (ARN39P-TF, Ryzen 7 7840HS, ITE EC) 的寄存器地址。
/// 全部来自对 32MB SPI 固件 (EC 固件位于 ROM 0x000000-0x05FFFF) 的逆向与实测。
///
/// 其中只读地址用于采样; 模式标志与手动 PWM 值用于"强冷"两档控制,
/// 只有显式开启强冷时才会写, 且务必先写 PWM 值再写模式标志 (顺序见 FanModeControl)。
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

    /// <summary>
    /// 有效占空比的下界。0 是**合法**值 (EC 允许风扇完全停), 所以不拒绝写入,
    /// 但负数同样非法 —— 夹取层用它判断下界。
    /// </summary>
    public const int PwmMin = 0;

    /// <summary>转速换算常数: RPM = TachConstant / 计数器值。</summary>
    public const int TachConstant = 2_156_250;

    /// <summary>I2EC 端口基址候选。0x4E 的 EC 槽位为 1, 0x2E 为 0。</summary>
    public static readonly (ushort Base, int Slot)[] PortBases = { (0x4E, 1), (0x2E, 0) };

    /// <summary>风扇 1 手动 PWM 值。只在模式标志为 0xDC 时被 EC 采信。</summary>
    public const ushort ManualPwm1 = 0x021E;

    /// <summary>风扇 2 手动 PWM 值。</summary>
    public const ushort ManualPwm2 = 0x029E;

    /// <summary>风扇 1 模式标志: 0x00 自动 / 0xA5 自定义曲线 / 0xCC 自定义温度 / 0xDC 手动 PWM。</summary>
    public const ushort ModeFlag1 = 0x021F;

    /// <summary>风扇 2 模式标志。</summary>
    public const ushort ModeFlag2 = 0x029F;

    /// <summary>模式标志的取值: 自动, EC 按自己的温度曲线调速。本程序"交还自动"就是写它。</summary>
    public const byte ModeAuto = 0x00;

    /// <summary>
    /// 模式标志的取值: 手动 PWM, EC 直接采信 ManualPwm1/2 的数值。
    /// 这是唯一实测稳定的自定义模式; 0xA5 / 0xCC 会被 EC 在约 1.5 秒内回写覆盖。
    /// </summary>
    public const byte ModeManualPwm = 0xDC;

    /// <summary>模式标志的取值: 自定义曲线。本程序不写, 仅用于识别"别人的软件接管了"。</summary>
    public const byte ModeCustomCurve = 0xA5;

    /// <summary>模式标志的取值: 自定义温度。本程序不写, 仅用于识别。</summary>
    public const byte ModeCustomTemp = 0xCC;

    /// <summary>
    /// 温度看门狗的**默认**阈值。达到这个温度且连续若干次, 强冷模式立即交还 EC。
    ///
    /// 这里只是默认值 —— 实际生效的阈值来自 <c>AppSettings.EmergencyTemperatureC</c>,
    /// 用户可以在设置界面里改。之所以还留一组常量, 是因为 Core 层的测试与文档需要
    /// 一个"出厂值"的锚点, 且默认值本身属于机型经验, 不该散落在 UI 里。
    /// </summary>
    public const int EmergencyTemperatureC = 92;

    /// <summary>过热保护默认需要的连续采样次数。单次尖峰 (例如编译瞬间) 不该触发。</summary>
    public const int EmergencyHoldSamples = 3;
}
