using WujieFan.Core.Config;
using Xunit;

namespace WujieFan.Core.Tests;

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
/// </summary>
public sealed class RealSettingsFileFixture : IDisposable
{
    /// <summary>
    /// 备份路径**固定**不随机: 进程被中断 (Ctrl+C、调试器停止、蓝屏) 时 Dispose 不会跑,
    /// 随机名的备份就永远没人认得, 用户的配置等于被静默删除 —— 这在本工程真的发生过一次。
    /// 固定名让下一次运行能把上一次没来得及还的配置先还回去。
    /// </summary>
    private static readonly string BackupPath =
        Path.Combine(Path.GetTempPath(), "wujiefan-tests.settings.bak");

    private readonly bool _hadOriginal;

    public RealSettingsFileFixture()
    {
        Directory.CreateDirectory(ConfigDir);

        // 上一次运行可能中途被杀, 留下"备份还在、配置没了"的状态: 先原地还回去。
        if (!File.Exists(SettingsPath) && File.Exists(BackupPath))
            File.Copy(BackupPath, SettingsPath, overwrite: true);

        _hadOriginal = File.Exists(SettingsPath);
        if (_hadOriginal)
            File.Copy(SettingsPath, BackupPath, overwrite: true);

        // 从干净状态开始: 没有文件 = AppSettings.Load() 走 SafeDefaults 分支。
        DeleteSettings();
    }

    public static string ConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WujieFan");

    public static string SettingsPath => Path.Combine(ConfigDir, "settings.json");

    public static void DeleteSettings()
    {
        if (File.Exists(SettingsPath)) File.Delete(SettingsPath);
    }

    public static void WriteSettings(string json)
    {
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(SettingsPath, json, new System.Text.UTF8Encoding(false));
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
    }
}

[CollectionDefinition("RealSettingsFile")]
public sealed class RealSettingsFileCollection : ICollectionFixture<RealSettingsFileFixture> { }
