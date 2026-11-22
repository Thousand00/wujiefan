using Zephyr.Core.Config;
using Xunit;

namespace Zephyr.Core.Tests;

/// <summary>
/// 真实配置文件的隔离护栏。
///
/// 为什么不能"指向临时目录": <see cref="AppSettings"/> 的路径来自
/// <c>Environment.GetFolderPath(SpecialFolder.LocalApplicationData)</c>, 实测**不认**
/// LOCALAPPDATA / USERPROFILE 环境变量 (进程内改、子进程继承都试过, 返回的仍是真实路径),
/// 路径又没有任何注入点。所以在"不改 src"的约束下, 唯一可行的做法是:
///   测试前把真实 settings.json 挪走 → 跑测试 → 再挪回来。
///
/// 这是本工程唯一的"碰用户数据"的地方, 因此写得很保守: 备份文件放在临时目录而不是
/// 配置目录 (避免它自己被当成配置读), 还原失败会抛异常让测试红, 而不是安静地丢配置。
///
/// 改名到 Zephyr 之后这里多了一层: <see cref="AppSettings.Load"/> 会在"新目录没有配置、
/// 旧目录有"时把 <c>%LOCALAPPDATA%\WujieFan\settings.json</c> 复制过来。如果只隔离新目录,
/// 迁移逻辑就会把**用户真实的旧配置**搬进测试目录, 于是所有"文件不存在 = 安全默认"
/// 的用例都会读到用户的真实快捷键与过热阈值 —— 测试看起来通过, 断言却是错的。
/// 所以旧目录的 settings.json 也要一并挪走, 并由 <see cref="WriteLegacySettings"/>
/// 给迁移用例提供受控的输入。
/// </summary>
public sealed class RealSettingsFileFixture : IDisposable
{
    /// <summary>
    /// 备份路径**固定**不随机: 进程被中断 (Ctrl+C、调试器停止、蓝屏) 时 Dispose 不会跑,
    /// 随机名的备份就永远没人认得, 用户的配置等于被静默删除 —— 这在本工程真的发生过一次。
    /// 固定名让下一次运行能把上一次没来得及还的配置先还回去。
    /// </summary>
    private static readonly string BackupPath =
        Path.Combine(Path.GetTempPath(), "zephyr-tests.settings.bak");

    /// <summary>旧目录 (改名前的 WujieFan) 配置备份。固定名的理由同上。</summary>
    private static readonly string LegacyBackupPath =
        Path.Combine(Path.GetTempPath(), "zephyr-tests.legacy-settings.bak");

    private readonly bool _hadOriginal;
    private readonly bool _hadLegacyOriginal;

    public RealSettingsFileFixture()
    {
        Directory.CreateDirectory(ConfigDir);

        // 上一次运行可能中途被杀, 留下"备份还在、配置没了"的状态: 先原地还回去。
        if (!File.Exists(SettingsPath) && File.Exists(BackupPath))
            File.Copy(BackupPath, SettingsPath, overwrite: true);

        _hadOriginal = File.Exists(SettingsPath);
        if (_hadOriginal)
            File.Copy(SettingsPath, BackupPath, overwrite: true);

        // 旧目录同理: 先还上一次没还的, 再备份这次的, 最后挪走。
        if (Directory.Exists(LegacyConfigDir))
        {
            if (!File.Exists(LegacySettingsPath) && File.Exists(LegacyBackupPath))
                File.Copy(LegacyBackupPath, LegacySettingsPath, overwrite: true);

            _hadLegacyOriginal = File.Exists(LegacySettingsPath);
            if (_hadLegacyOriginal)
                File.Copy(LegacySettingsPath, LegacyBackupPath, overwrite: true);
        }

        // 从干净状态开始: 没有文件 = AppSettings.Load() 走 SafeDefaults 分支。
        DeleteSettings();
        DeleteLegacySettings();
    }

    public static string ConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zephyr");

    public static string SettingsPath => Path.Combine(ConfigDir, "settings.json");

    /// <summary>改名前的配置目录。迁移逻辑的输入源, 也是"必须隔离"的第二个位置。</summary>
    public static string LegacyConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WujieFan");

    public static string LegacySettingsPath => Path.Combine(LegacyConfigDir, "settings.json");

    public static void DeleteSettings()
    {
        if (File.Exists(SettingsPath)) File.Delete(SettingsPath);
    }

    public static void WriteSettings(string json)
    {
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(SettingsPath, json, new System.Text.UTF8Encoding(false));
    }

    /// <summary>写一份"改名前的旧配置", 用来驱动迁移逻辑。</summary>
    public static void WriteLegacySettings(string json)
    {
        Directory.CreateDirectory(LegacyConfigDir);
        File.WriteAllText(LegacySettingsPath, json, new System.Text.UTF8Encoding(false));
    }

    public static void DeleteLegacySettings()
    {
        if (File.Exists(LegacySettingsPath)) File.Delete(LegacySettingsPath);
    }

    public void Dispose()
    {
        // 清掉测试写下的东西, 包括 Save() 可能残留的临时文件。
        DeleteSettings();
        foreach (string tmp in Directory.GetFiles(ConfigDir, "settings.json.*.tmp"))
        {
            try { File.Delete(tmp); } catch { /* 残留无害, 不掩盖真正的失败 */ }
        }

        if (_hadOriginal)
            File.Copy(BackupPath, SettingsPath, overwrite: true);

        // 还原之后才删备份 —— 顺序反了就会丢配置。
        if (_hadOriginal)
        {
            try { if (File.Exists(BackupPath)) File.Delete(BackupPath); } catch { }
        }

        // 旧目录: 测试如果写过就删掉, 再还原用户原本的。
        DeleteLegacySettings();
        if (_hadLegacyOriginal)
            File.Copy(LegacyBackupPath, LegacySettingsPath, overwrite: true);

        if (_hadLegacyOriginal)
        {
            try { if (File.Exists(LegacyBackupPath)) File.Delete(LegacyBackupPath); } catch { }
        }
    }
}

[CollectionDefinition("RealSettingsFile")]
public sealed class RealSettingsFileCollection : ICollectionFixture<RealSettingsFileFixture> { }
