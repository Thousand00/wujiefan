using System.Text.Json;

namespace WujieFan.Core.Config;

/// <summary>
/// 用户设置。存在 %LOCALAPPDATA%\WujieFan\settings.json。
/// 只保存程序自己的偏好, 不碰任何系统级配置。
/// </summary>
public sealed class AppSettings
{
    /// <summary>采样间隔 (毫秒)。风扇转速变化不快, 1 秒足够, 也不会让 EC 太忙。</summary>
    public int PollIntervalMs { get; set; } = 1000;

    /// <summary>关窗口时缩到托盘而不是退出。</summary>
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>是否向 RTSS 供数 (写 AIDA64_SensorValues 共享内存)。</summary>
    public bool PublishToRtss { get; set; } = true;

    /// <summary>跳过机型校验。只有用户明知风险并手动开启才为 true。</summary>
    public bool ForceUnsupported { get; set; }

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WujieFan");

    private static string FilePath => Path.Combine(Dir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            string json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            // 配置坏了就用默认值, 绝不因为读配置失败而让程序起不来。
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // 保存失败不该影响正在跑的采样。
        }
    }

    public static string LogDirectory => Dir;
}
