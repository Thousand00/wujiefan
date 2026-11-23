using System.Text;
using System.Text.Json;
using Zephyr.Core.Ec;

namespace Zephyr.Core.Config;

/// <summary>
/// 用户设置。存在 %LOCALAPPDATA%\Zephyr\settings.json。
/// 只保存程序自己的偏好, 不碰任何系统级配置。
///
/// 安全相关约定 (t11 加固):
///  * 写入必须是原子的 —— 先写同目录唯一临时文件并 flush 到磁盘, 再 File.Replace 覆盖。
///    否则进程中途被杀 / 断电会留下半截 JSON, 下次启动解析失败。
///  * 读不出来时回退到"不干预"形态, 绝不回退到会主动拉满风扇的状态 (见 SafeDefaults)。
///  * 版本未知 / 来自更新版本的文件, 按保守默认处理, 不信任其中的风险字段。
/// </summary>
public sealed class AppSettings
{
    /// <summary>当前配置结构版本。改字段语义时必须 +1, 并处理旧版本的迁移。</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>采样间隔 (毫秒)。风扇转速变化不快, 1 秒足够, 也不会让 EC 太忙。</summary>
    public int PollIntervalMs { get; set; } = 1000;

    /// <summary>关窗口时缩到托盘而不是退出。</summary>
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>是否向 RTSS 供数 (写 AIDA64_SensorValues 共享内存)。</summary>
    public bool PublishToRtss { get; set; } = true;

    /// <summary>跳过机型校验。只有用户明知风险并手动开启才为 true。</summary>
    public bool ForceUnsupported { get; set; }

    /// <summary>
    /// 用户上一次显式确认"跳过机型校验"的时刻。
    ///
    /// 为什么需要它：ForceUnsupported 一旦落盘就是永久生效，等于用户某次点了
    /// "继续读取"，此后每次开机都在无人知情的情况下跳过机型校验，静默向一台
    /// 未知机型的 EC 写 0x021E / 0x029E / 0xDC。审计 (t2-P1-1) 要求这条路径
    /// 至少不能是静默的。这里给它加一个有效期：超过 <see cref="ForceUnsupportedValidity"/>
    /// 就自动失效，用户必须重新确认。
    /// </summary>
    public DateTimeOffset? ForceUnsupportedConfirmedAt { get; set; }

    /// <summary>
    /// 一次确认的有效期。取 12 小时：覆盖一个工作日，但跨不过"第二天开机"，
    /// 所以每次新的使用日都会重新征得同意，又不至于频繁打断。
    /// </summary>
    public static readonly TimeSpan ForceUnsupportedValidity = TimeSpan.FromHours(12);

    /// <summary>
    /// 上次选择的扇热模式。启动时按它做归一化: 自动就确保标志是 0x00,
    /// 强冷就重新写一遍满转 —— 这样"关机时是什么状态"不再取决于上次进程死得干不干净。
    /// </summary>
    public FanMode FanMode { get; set; } = FanMode.Auto;

    /// <summary>
    /// 过热保护阈值 (摄氏度)。强冷模式下 CPU 温度达到这个值、且连续若干次采样都达到,
    /// 就把风扇交还给 EC 自动控制。
    ///
    /// 允许用户改的原因: 这个阈值本质上是"多吵 vs 多热"的取舍, 因人因场景而异 ——
    /// 编译/渲染的用户嫌 92℃ 太保守 (风扇满转本来就是为了压温度), 而安静优先的用户
    /// 希望更早收手。默认值取 <see cref="EcRegisters.EmergencyTemperatureC"/> (92),
    /// 与 0.4.x 的行为完全一致, 即"不改设置就等于没变"。
    /// </summary>
    public int EmergencyTemperatureC { get; set; } = EcRegisters.EmergencyTemperatureC;

    /// <summary>
    /// 过热保护需要的连续采样次数。默认 <see cref="EcRegisters.EmergencyHoldSamples"/> (3)。
    ///
    /// 单次尖峰 (编译瞬间、游戏加载) 不该打断用户的强冷, 所以要求"连续 N 次"。
    /// 调小 = 更早收手但更容易被瞬时尖峰误触发; 调大 = 更能容忍尖峰但反应更慢。
    /// </summary>
    public int EmergencyHoldSamples { get; set; } = EcRegisters.EmergencyHoldSamples;

    /// <summary>过热阈值的可设下界。低于 70℃ 会在正常负载下频繁交还, 不像是用户想要的行为。</summary>
    public const int EmergencyTemperatureMin = 70;

    /// <summary>
    /// 过热阈值的可设上界。取 100: EC 温度寄存器的有效量程是 1..100
    /// (见 <see cref="EcRegisters.CpuTemperature"/>), 设到 100 以上等于关掉这项保护。
    /// </summary>
    public const int EmergencyTemperatureMax = 100;

    /// <summary>连续采样次数的可设下界。1 表示"单次达标就收手"。</summary>
    public const int EmergencyHoldSamplesMin = 1;

    /// <summary>连续采样次数的可设上界。10000ms 轮询下 60 次已是 1 分钟, 再大就没有保护意义了。</summary>
    public const int EmergencyHoldSamplesMax = 60;

    /// <summary>
    /// 切换强冷模式的全局组合键, 写法见 <see cref="HotKeyCombo.ToSettingString"/> (例如 "Ctrl+Alt+F9")。
    ///
    /// 空串表示没设, 此时不注册任何全局热键 —— 这是默认值, 与 0.5.2 及更早的行为完全一致:
    /// "不设就什么都不变"。存字符串而不是结构体, 一是让手改配置文件的人看得懂,
    /// 二是老版本读到它只是多出一个不认识的字段, 而不是反序列化失败。
    /// </summary>
    public string StrongCoolHotKey { get; set; } = "";

    /// <summary>配置文件自身的结构版本。缺失视为 0 (0.4.0 及更早写的文件)。</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    private static string Dir => RuntimeIdentity.DataDirectory;

    private static string FilePath => Path.Combine(Dir, "settings.json");

    /// <summary>
    /// 改名前的配置位置 (<c>%LOCALAPPDATA%\WujieFan\settings.json</c>)。
    /// 只用于首次启动时把旧设置沿用过来: 不搬的话, 从 WujieFan 升到 Zephyr 的用户会发现
    /// 快捷键、过热阈值、RTSS 开关全部回到默认值 —— 表面上"能用", 实际是设置全丢了。
    /// </summary>
    private static string LegacyFilePath => Path.Combine(RuntimeIdentity.LegacyDataDirectory, "settings.json");

    /// <summary>
    /// 最近一次 Load/Save 的失败原因; 成功时清空。
    /// 界面可以在状态栏据此提示"设置没存上", 但调用方可以完全不理会它。
    /// </summary>
    public static string? LastError { get; private set; }

    /// <summary>
    /// 本次 Load 是否**降级**了 —— 即文件读不出来 / 损坏 / 为空 / 版本未知,
    /// 结果是一份 <see cref="SafeDefaults"/> (不接管风扇)。
    /// 界面只能在它为 true 时说"不接管风扇"; 只是字段被归一化时它是 false。
    /// </summary>
    public static bool LoadDegraded { get; private set; }

    /// <summary>
    /// 本次 Load 是否**归一化**过字段 (越界夹取、过期失效、旧格式补时间戳等)。
    /// 这与降级是两件事: 归一化之后程序照常工作, 风扇照常接管,
    /// 只是有个别字段不是文件里写的那个值。界面应说明"已修正"而不是"不接管风扇"。
    /// </summary>
    public static bool LoadNormalized { get; private set; }

    /// <summary>本次 Load 的提示信息 (降级原因与归一化说明都可能在里面); 无话可说时为 null。</summary>
    public static string? LoadWarning { get; private set; }

    /// <summary>
    /// 本次 Load 尝试沿用改名前的旧配置但失败了 (权限 / 磁盘 / 文件被独占)。
    /// 与 <see cref="LastError"/> 分开是必要的: LastError 会被后续任何一次 Save 覆盖,
    /// 而这条要说的是"你的旧设置没搬过来", 属于启动期一次性事实, 界面要单独说一句。
    /// 为 null 表示没有迁移失败 (含"根本没有旧配置可迁")。
    /// </summary>
    public static string? LoadMigrateError { get; private set; }

    /// <summary>
    /// 本次 Load 是否从改名前的旧目录沿用了配置。界面据此提示一次
    /// "设置已从旧版本沿用", 让用户知道快捷键与阈值不是被重置了。
    /// </summary>
    public static bool LoadMigrated { get; private set; }

    /// <summary>
    /// "不干预"的安全默认: 交还 EC 自动控速, 不做机型校验绕过。
    /// 任何解析失败都必须回到这里 —— 绝不能回退成 StrongCool, 否则一次读配置失败
    /// 就会把风扇钉死在满转 (5700 RPM, 噪声与功耗都会失控)。
    /// </summary>
    public static AppSettings SafeDefaults() => new()
    {
        PollIntervalMs = 1000,
        MinimizeToTray = true,
        PublishToRtss = true,
        ForceUnsupported = false,
        FanMode = FanMode.Auto,
        EmergencyTemperatureC = EcRegisters.EmergencyTemperatureC,
        EmergencyHoldSamples = EcRegisters.EmergencyHoldSamples,
        StrongCoolHotKey = "",
        SchemaVersion = CurrentSchemaVersion,
    };

    public static AppSettings Load()
    {
        LoadDegraded = false;
        LoadNormalized = false;
        LoadWarning = null;
        LoadMigrated = false;
        LoadMigrateError = null;
        LastError = null;

        // 改名后第一次启动: 新目录还没有配置文件, 但旧目录有, 就沿用过来。
        TryMigrateFromLegacyDirectory();

        string json;
        try
        {
            if (!File.Exists(FilePath)) return SafeDefaults();
            json = File.ReadAllText(FilePath);
        }
        catch (Exception ex)
        {
            // 读不到 (权限 / 被独占 / IO 错) 不该让程序起不来, 但要留下原因。
            LastError = ex.Message;
            return Degrade("读取设置失败, 已按安全默认启动 (不接管风扇): " + ex.Message);
        }

        AppSettings? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<AppSettings>(json, s_jsonOptions);
        }
        catch (JsonException ex)
        {
            // 半截 JSON / 手改坏了 / 被截断 —— 原子写能挡住我们自己的写入流程,
            // 但挡不住外部写入与掉电, 所以这里仍然要能兜住。
            LastError = ex.Message;
            return Degrade("设置文件损坏, 已按安全默认启动 (不接管风扇): " + ex.Message);
        }

        if (parsed is null) return Degrade("设置文件为空, 已按安全默认启动 (不接管风扇)。");

        // 版本处理: 缺失(=0) 是 0.4.0 及更早的旧文件, 结构相同, 只做字段归一化;
        // 高于当前版本的文件来自更新的程序, 我们不知道字段含义, 只保留无风险项。
        if (parsed.SchemaVersion > CurrentSchemaVersion)
        {
            var safe = SafeDefaults();
            // 这三个字段本身无害, 但仍要归一化: 这个分支在下面的 Math.Clamp 之前
            // 就返回了, 不在这里夹一次, 一个越界的间隔会被原样带进运行期。
            safe.PollIntervalMs = Math.Clamp(parsed.PollIntervalMs, 250, 10000);
            safe.MinimizeToTray = parsed.MinimizeToTray;
            safe.PublishToRtss = parsed.PublishToRtss;
            safe.SchemaVersion = CurrentSchemaVersion;
            // 注意: 必须返回 safe 本身, 不能返回 Degrade(...) 的返回值 ——
            // Degrade 内部会新建一个 SafeDefaults(), 那样上面保留的三个无害字段就丢了。
            return Degrade(
                safe,
                $"设置文件版本 {parsed.SchemaVersion} 高于本程序支持的 {CurrentSchemaVersion}, " +
                "已忽略其中的机型校验与风扇模式设定, 按安全默认启动。");
        }

        // 字段归一化。这里刻意不看"信任文件内容": 值越界一律拉回安全侧。
        bool changed = false;

        // 机型校验绕过必须"最近确认过"才生效。过期就强制回到校验状态,
        // 用户下次想跳过时界面会再问一次 —— 这就是 t2-P1-1 要求的"不静默"。
        if (parsed.ForceUnsupported)
        {
            if (parsed.ForceUnsupportedConfirmedAt is null)
            {
                // 0.4.0 及更早的文件没有这个字段。保留用户意图, 但只给一个有效期,
                // 之后必须重新确认。
                parsed.ForceUnsupportedConfirmedAt = DateTimeOffset.Now;
                changed = true;
                AddWarning("旧版设置里记录了「跳过机型校验」, 已为其设置 12 小时有效期, 到期需重新确认。");
            }
            else if (DateTimeOffset.Now - parsed.ForceUnsupportedConfirmedAt.Value > ForceUnsupportedValidity)
            {
                parsed.ForceUnsupported = false;
                parsed.ForceUnsupportedConfirmedAt = null;
                changed = true;
                AddWarning(
                    $"「跳过机型校验」的确认已超过 {ForceUnsupportedValidity.TotalHours:0} 小时, 本次启动已自动恢复机型校验。");
            }
        }
        else
        {
            // 没开就清掉时间戳, 免得旧时间戳让下一次开启"看起来"已经确认过。
            if (parsed.ForceUnsupportedConfirmedAt is not null)
            {
                parsed.ForceUnsupportedConfirmedAt = null;
                changed = true;
            }
        }

        if (!Enum.IsDefined(typeof(FanMode), parsed.FanMode))
        {
            parsed.FanMode = FanMode.Auto;
            changed = true;
        }

        // 间隔太小会把 EC 打爆, 太大则界面像死了。夹到 [250, 10000]。
        int clamped = Math.Clamp(parsed.PollIntervalMs, 250, 10000);
        if (clamped != parsed.PollIntervalMs)
        {
            parsed.PollIntervalMs = clamped;
            changed = true;
        }

        // 过热保护的两个参数。它们决定"什么时候把风扇交还 EC", 属于安全侧设置,
        // 所以越界一律**回到默认值**, 而不是夹到边界:
        //   - 温度若夹到上界 100, 等于悄悄把保护关掉 (EC 量程就是 1..100), 那是反向的;
        //     回到 92 至少还是一项真的保护。手改成 200 通常是手误或别的东西写坏了。
        //   - 次数同理: 夹到上界 60 在 1 秒轮询下是一分钟, 也已经没有保护意义。
        if (parsed.EmergencyTemperatureC < EmergencyTemperatureMin
            || parsed.EmergencyTemperatureC > EmergencyTemperatureMax)
        {
            parsed.EmergencyTemperatureC = EcRegisters.EmergencyTemperatureC;
            changed = true;
        }

        if (parsed.EmergencyHoldSamples < EmergencyHoldSamplesMin
            || parsed.EmergencyHoldSamples > EmergencyHoldSamplesMax)
        {
            parsed.EmergencyHoldSamples = EcRegisters.EmergencyHoldSamples;
            changed = true;
        }

        // 全局热键。三种情况:
        //   - 空 / 没这个字段: 合法, 表示用户没设, 什么都不做;
        //   - 解析得出来: 合法, 但统一改写成规范写法, 免得 "ctrl+alt+f9" 与
        //     "Ctrl+Alt+F9" 在文件里各存一份, 比较与去重都要多一层;
        //   - 解析不出来: 手改坏了。**当成没设**并把字段清空 —— 留着一个注册不了的
        //     字符串, 界面就会显示一个"看起来设了、其实不生效"的快捷键。
        //     这里不清成默认值而是清成空串, 因为热键的"默认"就是没有。
        if (!HotKeyCombo.TryParse(parsed.StrongCoolHotKey, out HotKeyCombo? hotKey))
        {
            parsed.StrongCoolHotKey = "";
            changed = true;
        }
        else if (hotKey is not null)
        {
            string canonical = hotKey.ToSettingString();
            if (!string.Equals(canonical, parsed.StrongCoolHotKey, StringComparison.Ordinal))
            {
                parsed.StrongCoolHotKey = canonical;
                changed = true;
            }
        }

        if (parsed.SchemaVersion < CurrentSchemaVersion)
        {
            parsed.SchemaVersion = CurrentSchemaVersion;
            changed = true;
        }

        if (changed) AddWarning("设置中有越界或过期的字段, 已归一化。");

        return parsed;
    }

    /// <summary>
    /// 原子保存。返回 false 表示没写成功, 原因在 <see cref="LastError"/>。
    /// 调用方可以像以前一样忽略返回值, 但界面应该用它提示用户。
    /// </summary>
    public bool Save()
    {
        string tmp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";

        // 写盘前会顺手修正两个字段 (SchemaVersion / ForceUnsupportedConfirmedAt)。
        // 先记下原值: 写盘失败时必须还原。
        //
        // 不还原会有一个很隐蔽的后果: 对象还是被改过的那个, 调用方看到 false 只提示一句
        // "保存失败", 但内存里已经是"想要的值"。之后任何一次 Save (改风扇模式、改机型绕过标志、
        // 托盘操作) 都会把它悄悄落盘 —— 用户以为自己没保存成功, 实际生效了,
        // 而且 LastError 已被那次成功的 Save 清空, 事后查不到任何线索。
        int originalSchemaVersion = SchemaVersion;
        DateTimeOffset? originalConfirmedAt = ForceUnsupportedConfirmedAt;

        try
        {
            Directory.CreateDirectory(Dir);

            SchemaVersion = CurrentSchemaVersion;

            // 闭环: 界面在用户点"继续读取"时把 ForceUnsupported 置 true 并调用 Save(),
            // 这里就给这次显式确认记时。此后 12 小时内直接生效, 过期后 Load() 会把它
            // 翻回 false, 用户下次开机必须重新点一次 —— 这就是"不静默跳过机型校验"。
            if (ForceUnsupported)
            {
                ForceUnsupportedConfirmedAt ??= DateTimeOffset.Now;
            }
            else
            {
                ForceUnsupportedConfirmedAt = null;
            }

            string json = JsonSerializer.Serialize(this, s_jsonOptions);

            // 1) 写临时文件并 flush 到磁盘: 保证崩溃后临时文件要么完整要么不存在。
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(json);
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }

            // 2) 同目录原子替换。File.Replace 走 NTFS 的 ReplaceFile, 目标要么是旧文件
            //    要么是新文件, 不会出现"文件存在但内容是空的"这种中间态。
            if (File.Exists(FilePath))
            {
                try
                {
                    File.Replace(tmp, FilePath, destinationBackupFileName: null);
                }
                catch (Exception ex) when (ex is IOException or PlatformNotSupportedException
                                              or UnauthorizedAccessException)
                {
                    // 极少数文件系统 (网络重定向的 LOCALAPPDATA、部分 FAT/exFAT 卷) 不支持
                    // ReplaceFile。退回覆盖式 Move: 仍然只有一次可见的替换动作,
                    // 只是不再保证"断电时保留旧文件的完整副本"。
                    File.Move(tmp, FilePath, overwrite: true);
                }
            }
            else
            {
                File.Move(tmp, FilePath);
            }

            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            // 还原写盘前的两个字段: 让内存里的对象和磁盘上的文件保持一致。
            // 否则调用方看到 false 却拿着一个"已经改过"的对象,
            // 下一次任意 Save 都会把它落盘, 而 LastError 已被清空。
            SchemaVersion = originalSchemaVersion;
            ForceUnsupportedConfirmedAt = originalConfirmedAt;

            LastError = ex.Message;
            TryDelete(tmp);
            return false;
        }
    }

    /// <summary>
    /// 尽力删除残留的临时文件。删不掉也无所谓: 文件名带 GUID, 不会互相踩,
    /// 也不会被下一次 Save 当成正式配置读到。
    /// </summary>
    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 残留一个临时文件不是故障, 不值得让上层知道。
        }
    }

    /// <summary>
    /// 改名 (WujieFan → Zephyr) 后第一次启动时, 把旧目录里的配置与日志沿用过来。
    ///
    /// 三条边界, 每一条都对应一种"弄丢用户东西"的方式:
    ///   ① 新目录已经有配置就**一次都不碰** —— 用户可能已经在新版里改过设置,
    ///      再拿旧文件覆盖等于把他刚改的东西悄悄回滚;
    ///   ② 旧目录没有配置 = 全新安装, 什么都不做 (别去创建空目录);
    ///   ③ 用复制而不是移动 —— 用户想退回旧版本时配置还在原来的地方。
    ///
    /// 失败时只记 <see cref="LastError"/>, **不降级**: 拿不到旧配置的结果是"设置回默认值",
    /// 这和"配置文件损坏所以不敢接管风扇"是两件性质不同的事。
    /// </summary>
    private static void TryMigrateFromLegacyDirectory()
    {
        try
        {
            if (File.Exists(FilePath)) return;
            if (!File.Exists(LegacyFilePath)) return;

            Directory.CreateDirectory(Dir);
            File.Copy(LegacyFilePath, FilePath, overwrite: false);

            // 日志一起带过来, 否则升级后用户查不到改名前的历史。
            TryCopyLegacyLog("ui.log");
            TryCopyLegacyLog("launcher.log");
            TryCopyLegacyLog("diag.log");

            LoadMigrated = true;
        }
        catch (Exception ex)
        {
            // 两个实例同时启动时会撞上同一次迁移: 双方都通过上面的 File.Exists 检查,
            // 只有先到的那个 Copy 成功, 后到的抛 "文件已存在"。这不是失败 ——
            // 配置文件现在就在位 (先到者写的), 后续读取照常。所以只在文件仍不存在时记录,
            // 否则每次开机自启 + 手动点快捷方式都会在日志里留一条假故障。
            if (File.Exists(FilePath))
                return;

            // 迁移失败不该影响启动: 下一步读不到 FilePath 时会走安全默认。
            LastError = ex.Message;
            LoadMigrateError = ex.Message;
        }
    }

    /// <summary>把旧目录里的某个日志文件复制过来; 新位置已有同名文件时保留新的。</summary>
    private static void TryCopyLegacyLog(string fileName)
    {
        try
        {
            string from = Path.Combine(RuntimeIdentity.LegacyDataDirectory, fileName);
            string to = Path.Combine(Dir, fileName);
            if (File.Exists(from) && !File.Exists(to))
                File.Copy(from, to, overwrite: false);
        }
        catch
        {
            // 日志是诊断用的附属品, 复制失败不该让迁移整体失败。
        }
    }

    private static AppSettings Degrade(string reason) => Degrade(SafeDefaults(), reason);

    /// <summary>
    /// 用一个已经构造好的降级对象并记录原因。
    /// 「未来版本」分支需要保留几个无害字段, 所以不能在这里再新建 SafeDefaults()。
    /// </summary>
    private static AppSettings Degrade(AppSettings degraded, string reason)
    {
        LoadDegraded = true;
        LoadWarning = reason;
        return degraded;
    }

    /// <summary>
    /// 追加一条**归一化**提示(不是降级)。用追加而不是覆盖: 一次 Load 里可能同时命中
    /// "旧版绕过标志"和"字段越界", 后一条不该把前一条抹掉。
    /// </summary>
    private static void AddWarning(string reason)
    {
        LoadNormalized = true;
        LoadWarning = LoadWarning is null ? reason : LoadWarning + " " + reason;
    }

    public static string LogDirectory => Dir;
}
