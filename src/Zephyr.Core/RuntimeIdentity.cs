// 刻意放在 Zephyr 根命名空间而不是 Zephyr.Core: 主程序(Zephyr)、Core 的子命名空间
// (Zephyr.Core.Config / Zephyr.Core.Providers)与启动器(Zephyr.Launcher)在 C# 的
// 命名空间解析中都会向外层查找, 所以三处都不需要额外 using 就能直接写 RuntimeIdentity。
namespace Zephyr;

/// <summary>
/// 与"这个程序叫什么、住在哪里"有关的运行期字符串, 全部集中在这里。
///
/// 为什么需要这个类: 改名(2026-11-22, WujieFan → Zephyr)时同一串名字散落在
/// 4 个文件的 14 处 —— 单实例互斥量、唤醒事件、后台线程名、计划任务名、启动器
/// 文件名、配置目录、日志目录、传感器 ID 前缀。漏掉任何一处都不会编译失败,
/// 只会让新旧版本悄悄不互斥、或者日志写到另一个目录里, 而这类问题只有用户
/// 真的踩到才会被发现。收敛之后, 下一次改名只改这一个文件。
///
/// 分三组, 因为它们的可见范围不同:
///   · 进程内可见(互斥量/事件/线程名/日志目录) —— 主程序与启动器都要用
///   · 跨进程约定(计划任务名/启动器文件名) —— 启动器与主程序必须完全一致
///   · 对外契约(传感器 ID 前缀) —— RTSS 图层按这个前缀引用, 改了要同步改图层
///
/// 这个文件会被主程序、Core 与启动器三处**链接编译**(见两个 csproj 的
/// &lt;Compile Include&gt;), 因为启动器为了缩小攻击面刻意不引用 Core。
/// 因此这里不能用任何需要引用 Core 其他类型的写法。
/// </summary>
public static class RuntimeIdentity
{
    /// <summary>产品名。出现在窗口标题、托盘提示、计划任务描述与错误弹窗里。</summary>
    public const string ProductName = "Zephyr";

    /// <summary>单实例互斥量。程序会在开机自启动与手动启动之间重复拉起, 必须只留一个。</summary>
    public const string SingleInstanceMutexName = @"Global\Zephyr_SingleInstance";

    /// <summary>唤醒事件名。二次启动的进程 Set 一下就走, 主实例的后台线程负责把窗口拽回前台。</summary>
    public const string ShowWindowEventName = @"Global\Zephyr_ShowWindow";

    /// <summary>后台等待唤醒事件的线程名。只出现在调试器里, 但便于区分它是谁。</summary>
    public const string WakeThreadName = "ZephyrWake";

    /// <summary>配置与日志所在的目录名, 位于 %LOCALAPPDATA% 之下。</summary>
    public const string DataDirectoryName = "Zephyr";

    /// <summary>改名之前用的目录名。首次启动时若新目录还是空的, 会把这里的配置搬过来。</summary>
    public const string LegacyDataDirectoryName = "WujieFan";

    /// <summary>计划任务名。安全约定是只操作这个名字的任务, 同名但指向别处一律拒绝。</summary>
    public const string TaskName = "Zephyr";

    /// <summary>改名之前用的计划任务名。只读它来判断"是否需要提示用户手动删除"。</summary>
    public const string LegacyTaskName = "WujieFan";

    /// <summary>启动器文件名。它必须和 Zephyr.exe 放在同一目录。</summary>
    public const string LauncherFileName = "ZephyrLauncher.exe";

    /// <summary>完整性清单文件名, 与两个 exe 同目录。</summary>
    public const string IntegrityFileName = "Zephyr.integrity.json";

    /// <summary>主程序文件名。启动器只允许拉起这一个可执行文件。</summary>
    public const string MainExecutableName = "Zephyr.exe";

    /// <summary>Core 程序集文件名。便携包把它并进 exe, 磁盘上不一定存在。</summary>
    public const string CoreAssemblyName = "Zephyr.Core.dll";

    /// <summary>RTSS 图层里引用的数据源 ID 前缀。改这里必须同步改 tools/add-zephyr-layers.py。</summary>
    public const string SensorIdPrefix = "ZEPHYR_";

    /// <summary>%LOCALAPPDATA%\Zephyr 的完整路径。</summary>
    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DataDirectoryName);

    /// <summary>改名之前的配置目录 %LOCALAPPDATA%\WujieFan 的完整路径。</summary>
    public static string LegacyDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), LegacyDataDirectoryName);
}
