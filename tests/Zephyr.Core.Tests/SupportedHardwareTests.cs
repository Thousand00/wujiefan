using Zephyr.Core.Hardware;
using Xunit;

namespace Zephyr.Core.Tests;

/// <summary>
/// 机型白名单。寄存器地址与换算常数是对着特定机型 + 特定 EC 固件逆向出来的,
/// 所以"身份不符必须拒绝"是一条安全属性, 不是体验优化。
///
/// 这些用例全部构造 HardwareIdentity (不读注册表), 因此在任何机器上都能跑通;
/// 而 EcReadSmoke --checkhw 里的同类用例依赖本机真实 SMBIOS, 换台机器就全红。
/// </summary>
public class SupportedHardwareTests
{
    /// <summary>本机 (无界14+, EC 2.12) 的身份。</summary>
    private static HardwareIdentity Supported() => new()
    {
        WasRead = true,
        SystemManufacturer = "MECHREVO",
        SystemProductName = "WUJIE 14+",
        BaseBoardManufacturer = "MECHREVO",
        BaseBoardProduct = "WUJIE 14+",
        EcFirmwareMajor = 2,
        EcFirmwareMinor = 12,
    };

    [Fact]
    public void Check_支持机型且EC版本匹配_通过()
    {
        SupportVerdict v = SupportedHardware.Check(Supported());

        Assert.True(v.IsSupported);
        Assert.NotNull(v.Profile);
        Assert.Equal("MECHREVO", v.Profile!.Manufacturer);
        Assert.Equal(2, v.Profile.EcMajor);
        Assert.Equal(12, v.Profile.EcMinor);
    }

    [Fact]
    public void Check_EC次版本更高_拒绝()
    {
        // 2.20 意味着固件升级过, 寄存器布局可能整体偏移 —— 读出来的数字会"看着合理但是错的"。
        var v = SupportedHardware.Check(new HardwareIdentity
        {
            WasRead = true,
            SystemManufacturer = "MECHREVO",
            SystemProductName = "WUJIE 14+",
            BaseBoardManufacturer = "MECHREVO",
            BaseBoardProduct = "WUJIE 14+",
            EcFirmwareMajor = 2,
            EcFirmwareMinor = 20,
        });

        Assert.False(v.IsSupported);
        Assert.Equal("EC 固件版本不匹配", v.Title);
    }

    [Fact]
    public void Check_EC主版本不同_拒绝()
    {
        var v = SupportedHardware.Check(new HardwareIdentity
        {
            WasRead = true,
            SystemManufacturer = "MECHREVO",
            SystemProductName = "WUJIE 14+",
            EcFirmwareMajor = 3,
            EcFirmwareMinor = 12,
        });

        Assert.False(v.IsSupported);
        Assert.Equal("EC 固件版本不匹配", v.Title);
    }

    [Fact]
    public void Check_无EC版本字段_拒绝()
    {
        var v = SupportedHardware.Check(new HardwareIdentity
        {
            WasRead = true,
            SystemManufacturer = "MECHREVO",
            SystemProductName = "WUJIE 14+",
            EcFirmwareMajor = null,
            EcFirmwareMinor = null,
        });

        Assert.False(v.IsSupported);
        Assert.Equal("读不到 EC 固件版本", v.Title);
    }

    [Fact]
    public void Check_其他厂商_拒绝()
    {
        var v = SupportedHardware.Check(new HardwareIdentity
        {
            WasRead = true,
            SystemManufacturer = "LENOVO",
            SystemProductName = "WUJIE 14+",
            BaseBoardManufacturer = "LENOVO",
            EcFirmwareMajor = 2,
            EcFirmwareMinor = 12,
        });

        Assert.False(v.IsSupported);
        Assert.Equal("机型不在支持列表", v.Title);
    }

    [Fact]
    public void Check_同厂商但不同型号_拒绝()
    {
        // 关键反例: 同一个厂商的 16 寸机型, 机型号相近但 EC 布局不同。
        var v = SupportedHardware.Check(new HardwareIdentity
        {
            WasRead = true,
            SystemManufacturer = "MECHREVO",
            SystemProductName = "WUJIE 16",
            BaseBoardManufacturer = "MECHREVO",
            BaseBoardProduct = "WUJIE 16",
            EcFirmwareMajor = 2,
            EcFirmwareMinor = 12,
        });

        Assert.False(v.IsSupported);
        Assert.Equal("机型不在支持列表", v.Title);
    }

    [Fact]
    public void Check_读不到SMBIOS_拒绝()
    {
        var v = SupportedHardware.Check(new HardwareIdentity { WasRead = false });

        Assert.False(v.IsSupported);
        Assert.Equal("读不到 SMBIOS 信息", v.Title);
    }

    [Theory]
    [InlineData("WUJIE 14")]      // Profile 里登记的另一个 product name
    [InlineData("wujie 14+")]     // 大小写不同
    public void Check_机型号写法差异_仍识别(string productName)
    {
        var v = SupportedHardware.Check(new HardwareIdentity
        {
            WasRead = true,
            SystemManufacturer = "MECHREVO",
            SystemProductName = productName,
            EcFirmwareMajor = 2,
            EcFirmwareMinor = 12,
        });

        Assert.True(v.IsSupported);
    }

    [Fact]
    public void Check_主板型号可单独满足机型匹配()
    {
        var v = SupportedHardware.Check(new HardwareIdentity
        {
            WasRead = true,
            SystemManufacturer = "MECHREVO",
            SystemProductName = "Unknown",
            BaseBoardManufacturer = "MECHREVO",
            BaseBoardProduct = "ARN39P",
            EcFirmwareMajor = 2,
            EcFirmwareMinor = 12,
        });

        Assert.True(v.IsSupported);
    }

    [Theory]
    [InlineData(2, 12, "2.12")]
    [InlineData(2, 20, "2.20")]
    [InlineData(0, 0, "0.0")]
    public void EcVersionText_两段式(int maj, int min, string expected)
    {
        var id = new HardwareIdentity { EcFirmwareMajor = maj, EcFirmwareMinor = min };
        Assert.Equal(expected, id.EcVersionText);
    }

    [Fact]
    public void EcVersionText_缺失时为空串()
    {
        Assert.Equal("", new HardwareIdentity().EcVersionText);
        Assert.Equal("", new HardwareIdentity { EcFirmwareMajor = 2 }.EcVersionText);
    }

    [Fact]
    public void 拒绝时的说明必须提到强行继续的风险入口()
    {
        // 拒绝不是死胡同: 用户明知风险时可以跳过校验, 文案里要给出这条路径。
        var v = SupportedHardware.Check(new HardwareIdentity { WasRead = false });
        Assert.Contains("强行继续", v.Detail);
    }
}
