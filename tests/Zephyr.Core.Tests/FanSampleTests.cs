using Zephyr.Core.Ec;
using Xunit;

namespace Zephyr.Core.Tests;

/// <summary>
/// 转速换算与显示层的纯函数。这些规则是"风扇到底转多快"的唯一依据,
/// 改错一个分支不会让程序崩溃, 只会安静地显示错误数字 —— 所以必须锁死。
/// </summary>
public class FanSampleTests
{
    [Theory]
    [InlineData(0)]      // 停转: 计数器 0 表示风扇确实不动
    [InlineData(-1)]     // 负数不该出现, 但也不该让换算炸掉
    [InlineData(-40000)]
    public void ConvertRpm_非正数_返回零且不报异常(int count)
    {
        var (rpm, note) = FanSample.ConvertRpm(count);

        Assert.Equal(0, rpm);
        Assert.Equal("", note);
    }

    [Fact]
    public void ConvertRpm_零_是停转而不是读取失败()
    {
        // 这条区分很重要: 界面 / RTSS 要把"风扇停了"显示成 0, 而不是留空。
        var reading = new FanReading { RawCount = 0 };
        Assert.True(reading.IsStopped);
        Assert.True(reading.IsValid);   // 停转是有效读数, 不该被当成坏数据丢掉
    }

    [Theory]
    [InlineData(0x4000)]
    [InlineData(0x4001)]
    [InlineData(0xFFFF)]
    public void ConvertRpm_达到上限_判定为无有效信号(int count)
    {
        var (rpm, note) = FanSample.ConvertRpm(count);

        Assert.Equal(0, rpm);
        Assert.Equal("计数值越界(无有效信号)", note);
    }

    [Theory]
    [InlineData(0x7F)]   // 127: 换算结果约 16978 RPM, 超过量程
    [InlineData(1)]
    public void ConvertRpm_低于0x80_判定为超量程(int count)
    {
        var (rpm, note) = FanSample.ConvertRpm(count);

        Assert.Equal(9999, rpm);

        // 期望值由常数算出, 不是抄一个数字: 最小的合法 count 是 0x80=128,
        // 对应 2156250/128=16845 RPM。这样一旦有人改了 TachConstant 而忘了同步文案,
        // 这条断言立刻会红。
        int maxRpm = EcRegisters.TachConstant / 0x80;
        Assert.Equal(16845, maxRpm);
        Assert.Equal($"超量程(>{maxRpm}RPM)", note);
    }

    [Theory]
    [InlineData(0x80, 16845)]    // 边界: 刚进入合法区间
    [InlineData(4000, 539)]
    [InlineData(1000, 2156)]
    [InlineData(8192, 263)]
    [InlineData(0x3FFF, 131)]    // 上限下方最后一个合法值
    public void ConvertRpm_合法区间_按常数整除(int count, int expectedRpm)
    {
        var (rpm, note) = FanSample.ConvertRpm(count);

        Assert.Equal(expectedRpm, rpm);
        Assert.Equal("", note);
    }

    [Fact]
    public void ConvertRpm_换算常数与实测一致()
    {
        // 2156250 是对着 EC 2.12 实测标定的; 改它等于改所有转速读数。
        Assert.Equal(2_156_250, EcRegisters.TachConstant);
        Assert.Equal(2156, FanSample.ConvertRpm(1000).Rpm);
    }

    [Theory]
    [InlineData(0, 0.0)]
    [InlineData(92, 50.0)]
    [InlineData(184, 100.0)]
    [InlineData(200, 100.0)]    // 硬件输出可能被别的软件配到越界值, 显示层要夹取
    [InlineData(-5, 0.0)]
    public void PwmPercent_夹取到0到100(int pwmOut, double expected)
    {
        var reading = new FanReading { PwmOut = pwmOut };
        Assert.Equal(expected, reading.PwmPercent, precision: 6);
    }

    [Fact]
    public void PwmPercent_184就是100个百分点()
    {
        // PwmMax=184 是这个机型 PWM 周期寄存器的满量程, 不是 255。
        Assert.Equal(184, EcRegisters.PwmMax);
        Assert.Equal(0, EcRegisters.PwmMin);
        Assert.Equal(100.0, new FanReading { PwmOut = EcRegisters.PwmMax }.PwmPercent);
    }

    [Fact]
    public void IsValid_有说明即为无效读数()
    {
        Assert.True(new FanReading { RawCount = 4000, Note = "" }.IsValid);
        Assert.False(new FanReading { RawCount = 4000, Note = "读取不稳定(已丢弃本次计数)" }.IsValid);
    }

    [Fact]
    public void Transient_默认是false_只有明确标注才是瞬时失败()
    {
        // 默认必须保守: 没标注的一律当"设备可能坏了", 走连续失败计数与重连。
        // 反过来(默认 true)会让真正的设备失效永远不触发重连。
        Assert.False(new FanSample().Transient);
        Assert.True(new FanSample { Error = "占着", Transient = true }.Transient);
    }

    [Fact]
    public void Transient_失败的样本没有数据_但仍带错误说明()
    {
        // 上层靠 HasData 区分"有读数"与"这一拍什么都没有"; 靠 Error 给用户看原因。
        // 瞬时失败两者都要满足, 否则会出现"提示了争用却还保留旧读数"的错觉。
        var s = new FanSample { Error = "EC 正被其它监控软件占用, 本次采样跳过。", Transient = true };

        Assert.False(s.HasData);
        Assert.NotNull(s.Error);
    }
}
