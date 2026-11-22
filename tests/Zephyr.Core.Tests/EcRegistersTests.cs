using Zephyr.Core.Ec;
using Xunit;

namespace Zephyr.Core.Tests;

/// <summary>
/// 寄存器常量与机型档案的"契约测试"。
///
/// 这些地址是对着 EC 2.12 逆向出来的, 写错一位就会读到别的字段 —— 而症状是
/// "转速看起来正常但是错的", 比崩溃危险得多。所以把它们逐个钉住, 任何人改常量
/// 都会立刻让测试变红, 从而强制他去看一眼 docs 里的实测记录。
/// </summary>
public class EcRegistersTests
{
    [Fact]
    public void 寄存器地址与实测一致()
    {
        Assert.Equal(0x0470, EcRegisters.CpuTemperature);
        Assert.Equal(0x181E, EcRegisters.Tach1.Lo);
        Assert.Equal(0x181F, EcRegisters.Tach1.Hi);
        Assert.Equal(0x1820, EcRegisters.Tach2.Lo);
        Assert.Equal(0x1821, EcRegisters.Tach2.Hi);
        Assert.Equal(0x1809, EcRegisters.PwmOut1);
        Assert.Equal(0x1804, EcRegisters.PwmOut2);
        Assert.Equal(0x021E, EcRegisters.ManualPwm1);
        Assert.Equal(0x029E, EcRegisters.ManualPwm2);
    }

    [Fact]
    public void 模式标志取值与实测一致()
    {
        Assert.Equal(0x021F, EcRegisters.ModeFlag1);
        Assert.Equal(0x029F, EcRegisters.ModeFlag2);
        Assert.Equal(0x00, EcRegisters.ModeAuto);
        Assert.Equal(0xDC, EcRegisters.ModeManualPwm);
        // 这两个是别的软件 (厂商工具) 会写的模式, 本程序只识别不写。
        Assert.Equal(0xA5, EcRegisters.ModeCustomCurve);
        Assert.Equal(0xCC, EcRegisters.ModeCustomTemp);
    }

    [Fact]
    public void PWM量程是0到184()
    {
        Assert.Equal(0, EcRegisters.PwmMin);
        Assert.Equal(184, EcRegisters.PwmMax);
        // 232 是 8 位的 91%, 但本机型的周期寄存器满量程就是 184。
        Assert.NotEqual(255, EcRegisters.PwmMax);
    }

    [Fact]
    public void 两个风扇的寄存器互不重叠()
    {
        // 地址撞车会让"只写风扇1"变成同时改两个风扇。
        var addresses = new[]
        {
            EcRegisters.CpuTemperature,
            EcRegisters.Tach1.Lo, EcRegisters.Tach1.Hi,
            EcRegisters.Tach2.Lo, EcRegisters.Tach2.Hi,
            EcRegisters.PwmOut1, EcRegisters.PwmOut2,
            EcRegisters.ManualPwm1, EcRegisters.ManualPwm2,
            EcRegisters.ModeFlag1, EcRegisters.ModeFlag2,
        };

        Assert.Equal(addresses.Length, addresses.Distinct().Count());
    }

    [Fact]
    public void 每个风扇的Tach两个字节相邻且低字节在前()
    {
        // Read16Stable(lo, hi) 按 lo 再 hi 读; 顺序反了会得到 256 倍偏差的转速。
        Assert.Equal(EcRegisters.Tach1.Lo + 1, EcRegisters.Tach1.Hi);
        Assert.Equal(EcRegisters.Tach2.Lo + 1, EcRegisters.Tach2.Hi);
        Assert.True(EcRegisters.Tach1.Lo < EcRegisters.Tach1.Hi);
        Assert.True(EcRegisters.Tach2.Lo < EcRegisters.Tach2.Hi);
    }

    [Fact]
    public void 端口基址两个候选_探测器可回退()
    {
        Assert.Equal(2, EcRegisters.PortBases.Length);
        Assert.Equal(0x4E, EcRegisters.PortBases[0].Base);
        Assert.Equal(0x2E, EcRegisters.PortBases[1].Base);
        Assert.Equal(1, EcRegisters.PortBases[0].Slot);
        Assert.Equal(0, EcRegisters.PortBases[1].Slot);
    }

    [Fact]
    public void 过热阈值与连续次数()
    {
        // 92C 是"再不管就要降频"的位置; 连续 3 次是为了滤掉瞬时尖峰。
        Assert.Equal(92, EcRegisters.EmergencyTemperatureC);
        Assert.Equal(3, EcRegisters.EmergencyHoldSamples);
    }

    [Fact]
    public void 机型档案只有一台且与文档一致()
    {
        Assert.Single(SupportedHardwareKnown());

        var p = SupportedHardwareKnown()[0];
        Assert.Equal("MECHREVO", p.Manufacturer);
        Assert.Equal(2, p.EcMajor);
        Assert.Equal(12, p.EcMinor);
        Assert.Contains("ARN39P", p.Note);
    }

    [Fact]
    public void 模式枚举取值固定()
    {
        // 落盘的 settings.json 里存的是数字, 改枚举顺序会让老配置含义漂移。
        Assert.Equal(0, (int)FanMode.Auto);
        Assert.Equal(1, (int)FanMode.StrongCool);
    }

    [Fact]
    public void 模式状态枚举包含全部四种()
    {
        Assert.True(System.Enum.IsDefined(typeof(FanModeState), FanModeState.Auto));
        Assert.True(System.Enum.IsDefined(typeof(FanModeState), FanModeState.StrongCool));
        Assert.True(System.Enum.IsDefined(typeof(FanModeState), FanModeState.Mixed));
        Assert.True(System.Enum.IsDefined(typeof(FanModeState), FanModeState.Unknown));
    }

    [Fact]
    public void Describe_每种状态都有中文说明()
    {
        // 界面上写"Mixed"没人看得懂。
        Assert.Equal("自动", FanReader.Describe(FanModeState.Auto));
        Assert.Equal("强冷", FanReader.Describe(FanModeState.StrongCool));
        Assert.Contains("混合", FanReader.Describe(FanModeState.Mixed));
        Assert.False(string.IsNullOrWhiteSpace(FanReader.Describe(FanModeState.Unknown)));
    }

    private static Zephyr.Core.Hardware.SupportedHardware.Profile[] SupportedHardwareKnown()
        => Zephyr.Core.Hardware.SupportedHardware.Known;
}
