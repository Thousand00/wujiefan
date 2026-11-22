using Zephyr.Core.Config;
using Zephyr.Core.Ec;
using Xunit;

namespace Zephyr.Core.Tests;

/// <summary>
/// 改名 (WujieFan → Zephyr) 时旧配置目录的自动沿用。
///
/// 为什么这几条值得单独成文件: 迁移是**唯一会读用户真实文件并写入新位置**的逻辑,
/// 它的失败方式都很安静 —— 覆盖掉用户已经在新版里改好的设置、或者把旧配置搬到一半
/// 又当成"文件损坏"降级, 用户看到的是"我的快捷键/阈值没了", 日志里什么都没有。
/// 所以这里逐条钉住四条边界: 该迁的时候迁、不该迁的时候一次都不碰、
/// 迁移后不降级、迁移完旧文件还在 (允许回退)。
/// </summary>
[Collection("RealSettingsFile")]
public class SettingsMigrationTests
{
    [Fact]
    public void 新目录没有配置而旧目录有_自动沿用旧设置()
    {
        RealSettingsFileFixture.DeleteSettings();
        RealSettingsFileFixture.WriteLegacySettings(
            "{\n" +
            "  \"PollIntervalMs\": 1000,\n" +
            "  \"MinimizeToTray\": true,\n" +
            "  \"PublishToRtss\": false,\n" +
            "  \"EmergencyTemperatureC\": 97,\n" +
            "  \"EmergencyHoldSamples\": 4,\n" +
            "  \"StrongCoolHotKey\": \"Ctrl+Shift+End\"\n" +
            "}");

        AppSettings s = AppSettings.Load();

        // 用户最在意的三样: 快捷键、过热阈值、RTSS 开关。
        Assert.Equal("Ctrl+Shift+End", s.StrongCoolHotKey);
        Assert.Equal(97, s.EmergencyTemperatureC);
        Assert.Equal(4, s.EmergencyHoldSamples);
        Assert.False(s.PublishToRtss);

        // 迁移不是"出错", 不该按降级处理 —— 降级会让程序不接管风扇。
        Assert.True(AppSettings.LoadMigrated);
        Assert.False(AppSettings.LoadDegraded);
        Assert.Null(AppSettings.LastError);

        // 新位置真写出来了, 否则下次启动又要迁一次。
        Assert.True(File.Exists(RealSettingsFileFixture.SettingsPath));

        // 旧文件必须留着: 用户想回退到旧版本时配置还在。
        Assert.True(File.Exists(RealSettingsFileFixture.LegacySettingsPath));
    }

    [Fact]
    public void 新目录已经有配置_不拿旧目录覆盖()
    {
        // 场景: 用户先在新版里改过设置, 然后旧目录还在 (回退过、或从没删过)。
        // 这时候覆盖等于把用户刚改的东西悄悄回滚, 是这套逻辑最危险的失败方式。
        RealSettingsFileFixture.WriteSettings(
            "{\n" +
            "  \"EmergencyTemperatureC\": 99,\n" +
            "  \"EmergencyHoldSamples\": 1,\n" +
            "  \"StrongCoolHotKey\": \"Ctrl+Alt+F10\"\n" +
            "}");
        RealSettingsFileFixture.WriteLegacySettings(
            "{\n" +
            "  \"EmergencyTemperatureC\": 80,\n" +
            "  \"EmergencyHoldSamples\": 9,\n" +
            "  \"StrongCoolHotKey\": \"Ctrl+Shift+End\"\n" +
            "}");

        AppSettings s = AppSettings.Load();

        Assert.Equal(99, s.EmergencyTemperatureC);
        Assert.Equal(1, s.EmergencyHoldSamples);
        Assert.Equal("Ctrl+Alt+F10", s.StrongCoolHotKey);
        Assert.False(AppSettings.LoadMigrated);
    }

    [Fact]
    public void 旧目录也没有配置_就是全新安装()
    {
        RealSettingsFileFixture.DeleteSettings();
        RealSettingsFileFixture.DeleteLegacySettings();

        AppSettings s = AppSettings.Load();

        // 全新安装的判据: 拿到安全默认值, 且没有任何"迁移/降级"的痕迹。
        Assert.Equal(EcRegisters.EmergencyTemperatureC, s.EmergencyTemperatureC);
        Assert.Equal(EcRegisters.EmergencyHoldSamples, s.EmergencyHoldSamples);
        Assert.Equal("", s.StrongCoolHotKey);
        Assert.False(AppSettings.LoadMigrated);
        Assert.False(AppSettings.LoadDegraded);
    }

    [Fact]
    public void 迁移来的配置越界_照样归一回默认值()
    {
        // 迁移路径不能绕过归一化: 旧文件是手改过的, 里面的值不能直接信。
        RealSettingsFileFixture.DeleteSettings();
        RealSettingsFileFixture.WriteLegacySettings(
            "{\n" +
            "  \"EmergencyTemperatureC\": 200,\n" +
            "  \"EmergencyHoldSamples\": 0\n" +
            "}");

        AppSettings s = AppSettings.Load();

        Assert.Equal(EcRegisters.EmergencyTemperatureC, s.EmergencyTemperatureC);
        Assert.Equal(EcRegisters.EmergencyHoldSamples, s.EmergencyHoldSamples);

        // 迁移成功 + 字段被归一化, 两件事各自都要有记录。
        Assert.True(AppSettings.LoadMigrated);
        Assert.True(AppSettings.LoadNormalized);

        // 归一化不是降级: 仍然接管风扇。
        Assert.False(AppSettings.LoadDegraded);
    }

    [Fact]
    public void 迁移只发生一次_第二次启动不再标记()
    {
        RealSettingsFileFixture.DeleteSettings();
        RealSettingsFileFixture.WriteLegacySettings(
            "{\n" +
            "  \"EmergencyTemperatureC\": 96\n" +
            "}");

        AppSettings first = AppSettings.Load();
        Assert.True(AppSettings.LoadMigrated);
        Assert.Equal(96, first.EmergencyTemperatureC);

        // 第二次启动: 新配置已经在, 旧目录也还在, 但不该再迁一次、也不该再提示。
        AppSettings second = AppSettings.Load();
        Assert.False(AppSettings.LoadMigrated);
        Assert.Equal(96, second.EmergencyTemperatureC);
    }
}
