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

    [Fact]
    public void 迁移失败必须留下可观测的痕迹()
    {
        // 旧配置在、新位置写不进去 —— 用户看到的是"快捷键和阈值全回默认了"。
        // 这个失败以前只写 LastError, 而 LastError 会被之后任何一次 Save 覆盖掉,
        // 于是界面上什么都看不到, 用户只能以为新版本把设置弄丢了。
        // 所以这里钉住: 迁移失败要有一个不会被 Save 冲掉的独立信号。
        //
        // 制造失败的手法: 在新配置的位置放一个**同名目录**。File.Exists 对目录返回 false
        // (于是走到复制那一步), 而 File.Copy 往目录路径写会抛异常。
        RealSettingsFileFixture.DeleteSettings();
        RealSettingsFileFixture.WriteLegacySettings(
            "{\n" +
            "  \"EmergencyTemperatureC\": 96\n" +
            "}");
        Directory.CreateDirectory(RealSettingsFileFixture.SettingsPath);

        try
        {
            AppSettings s = AppSettings.Load();

            Assert.NotNull(AppSettings.LoadMigrateError);
            Assert.False(AppSettings.LoadMigrated);

            // 拿不到旧配置就等于"没有配置文件": 回到安全默认。
            // 注意这里**不是降级** —— 降级的含义是"文件在那儿但读不出来, 所以不敢接管风扇";
            // 而"压根没有配置"本来就是全新安装的正常状态, 安全默认(自动模式)本身已经是安全态。
            // 用户可见性由 LoadMigrateError 单独承担。
            Assert.Equal(EcRegisters.EmergencyTemperatureC, s.EmergencyTemperatureC);
            Assert.Equal(EcRegisters.EmergencyHoldSamples, s.EmergencyHoldSamples);
            Assert.False(AppSettings.LoadDegraded);
        }
        finally
        {
            if (Directory.Exists(RealSettingsFileFixture.SettingsPath))
                Directory.Delete(RealSettingsFileFixture.SettingsPath, recursive: true);
        }
    }

    [Fact]
    public void 迁移失败的信号不会被之后一次Save冲掉()
    {
        // 这个信号唯一的作用就是"撑到用户看见"。而 Load() 之后程序自己会 Save 好几次
        // (切风扇模式、开机自启改动、托盘操作), 只要其中任何一次把它清掉,
        // 用户就永远看不到"旧设置没搬过来"。
        //
        // 现在它是 static 字段, JsonSerializer 默认不序列化 static 成员, 所以 Save 碰不到它。
        // 这条断言钉住的就是这个"靠不序列化才成立"的巧合 —— 哪天有人给它加上
        // [JsonProperty] 让它一起落盘, Save 的读写循环就会把它覆盖成 null。
        RealSettingsFileFixture.DeleteSettings();
        RealSettingsFileFixture.WriteLegacySettings(
            "{\n" +
            "  \"EmergencyTemperatureC\": 96\n" +
            "}");
        Directory.CreateDirectory(RealSettingsFileFixture.SettingsPath);

        try
        {
            AppSettings s = AppSettings.Load();
            Assert.NotNull(AppSettings.LoadMigrateError);

            string reason = AppSettings.LoadMigrateError!;

            // 拆掉制造失败的那个同名目录, 让后面的 Save 能真正写下去。
            // 现实里也是这个顺序: 迁移那一刻因为某种原因没写成, 之后程序照常跑、照常保存。
            Directory.Delete(RealSettingsFileFixture.SettingsPath, recursive: true);

            // 成功的一次 Save: 这条路以前会清掉 LastError, 现在要确认它没顺手清掉这个。
            Assert.True(s.Save(), AppSettings.LastError ?? "");

            Assert.Equal(reason, AppSettings.LoadMigrateError);

            // 落盘内容里也不能出现它 —— 一旦写进配置文件, 下次 Load 读到的就是
            // 上一次的旧错误, 用户会以为这次也失败了。
            string json = File.ReadAllText(RealSettingsFileFixture.SettingsPath);
            Assert.DoesNotContain("LoadMigrateError", json);
        }
        finally
        {
            if (Directory.Exists(RealSettingsFileFixture.SettingsPath))
                Directory.Delete(RealSettingsFileFixture.SettingsPath, recursive: true);
        }
    }

    [Fact]
    public void 迁移成功之后不该留下失败痕迹()
    {
        // 反向确认: 上一条的断言不是因为 LoadMigrateError 永远非空才通过的。
        RealSettingsFileFixture.DeleteSettings();
        RealSettingsFileFixture.WriteLegacySettings(
            "{\n" +
            "  \"EmergencyTemperatureC\": 95\n" +
            "}");

        AppSettings.Load();

        Assert.True(AppSettings.LoadMigrated);
        Assert.Null(AppSettings.LoadMigrateError);
    }
}
