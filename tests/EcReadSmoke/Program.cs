using System.Diagnostics;
using System.Text;
using Zephyr.Core.Ec;
using Zephyr.Core.Hardware;
using Zephyr.Core.Config;
using Zephyr.Core.Providers;

namespace EcReadSmoke;

/// <summary>
/// 阶段 1+2 的验证程序 (只读 + 供数)。
///
/// 阶段 1: 证明 C# 通过 PawnIO 读到的风扇转速与已验证的 fanprobe.py 一致。
/// 阶段 2: 证明写进 AIDA64_SensorValues 的数据格式能被 RTSS 接受。
///
/// 用法:
///   EcReadSmoke.exe                      采 5 次并打印明细
///   EcReadSmoke.exe --seconds N          连续采样 N 秒
///   EcReadSmoke.exe --json               输出 JSON (便于与 fanprobe --json 对比)
///   EcReadSmoke.exe --aida               同时写入 AIDA64 共享内存
///   EcReadSmoke.exe --xml                只生成并打印 XML, 不访问硬件
///   EcReadSmoke.exe --autostart-test     用指定 exe 路径测试自启动计划任务的解析逻辑 (不创建任务)
///   EcReadSmoke.exe --autostart-live     真实创建/查询/删除计划任务 Zephyr (需管理员, 会还原)
///   EcReadSmoke.exe --autostart-cn       中文 exe 路径的自启动回归 (需管理员)
///   EcReadSmoke.exe --autostart-upgrade  旧版任务定义被重写修正的回归 (需管理员)
///   EcReadSmoke.exe --autostart-on       真正开启开机自启 (需管理员, 会留在系统里)
///   EcReadSmoke.exe --autostart-off      真正关闭开机自启 (需管理员)
///   EcReadSmoke.exe --fanmode-live       真实切换 自动->强冷->自动 并校验回读 (需管理员, 会还原)
/// </summary>
internal static class Program
{
    private const string NL0 = "\r\n";

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        TrySetUtf8Console();

        int seconds = 5;
        bool json = false, aida = false, xmlOnly = false, peek = false, checkHw = false, autoStartTest = false, autoStartLive = false, autoStartCn = false, autoStartUpgrade = false, fanModeLive = false;
        string? autoStartOn = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--json": json = true; break;
                case "--aida": aida = true; break;
                case "--xml": xmlOnly = true; break;
                case "--peek": peek = true; break;
                case "--checkhw": checkHw = true; break;
                case "--autostart-test": autoStartTest = true; break;
                case "--autostart-live": autoStartLive = true; break;
                case "--autostart-cn": autoStartCn = true; break;
                case "--autostart-upgrade": autoStartUpgrade = true; break;
                case "--fanmode-live": fanModeLive = true; break;
                case "--autostart-on":
                    autoStartOn = i + 1 < args.Length ? args[++i] : @"E:\WORKS\RTSS-0\dist\app\Zephyr.exe";
                    break;
                case "--autostart-off":
                    autoStartOn = "";
                    break;
                case "--seconds" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out int s)) seconds = s;
                    break;
                case "-h" or "--help":
                    Console.WriteLine("用法: EcReadSmoke.exe [--seconds N] [--json] [--aida] [--xml]");
                    return 0;
                default:
                    if (int.TryParse(args[i], out int n)) seconds = n;
                    break;
            }
        }

        if (autoStartTest) return RunAutoStartTest();
        if (autoStartLive) return RunAutoStartLive();
        if (autoStartCn) return RunAutoStartChinese();
        if (autoStartUpgrade) return RunAutoStartUpgrade();
        if (fanModeLive) return RunFanModeLive();

        if (autoStartOn is not null) return RunAutoStartSet(autoStartOn);

        if (checkHw) return RunCheckHw();

        if (peek) return RunPeek();

        if (xmlOnly) return RunXmlOnly();

        byte[] blob = LoadLpcIoBlob();
        Console.Error.WriteLine($"LpcIO.bin: {blob.Length} 字节");

        using var reader = new FanReader(blob);

        try { reader.Open(); }
        catch (Exception ex)
        {
            Console.Error.WriteLine("打开设备失败: " + ex.Message);
            return 2;
        }

        Console.Error.WriteLine($"PawnIO: {reader.PawnIoVersion}");
        Console.Error.WriteLine($"端口基址: 0x{reader.PortBase:X2} (槽位 {reader.Slot})");
        // 互斥量现在是"每次采样前后成对获取/释放", 打开时不再一直持锁,
        // 所以这里只能说"是否用上了互斥量", 不能说"是否已获取"。
        Console.Error.WriteLine($"硬件互斥量: {(reader.HasMutex ? "已启用(每次采样成对获取)" : "未启用(仍可只读采样)")}");

        AidaSharedMemory? aidaSm = null;
        if (aida)
        {
            try
            {
                aidaSm = new AidaSharedMemory();
                aidaSm.Open();
                Console.Error.WriteLine($"AIDA64 共享内存: 已打开 ({AidaSharedMemory.MappingName})");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("打开 AIDA64 共享内存失败: " + ex.Message);
                return 4;
            }
        }

        var samples = new List<FanSample>();
        var sw = Stopwatch.StartNew();

        if (!json)
            Console.WriteLine($"{"时间",-8} {"温度",4}  {"Fan1 RPM",8} {"PWM1",5}  {"Fan2 RPM",8} {"PWM2",5}");

        while (sw.Elapsed.TotalSeconds < seconds)
        {
            FanSample s = reader.Read();

            if (s.Error is not null)
            {
                if (!json) Console.WriteLine($"{s.Timestamp:HH:mm:ss}  读取失败: {s.Error}");
            }
            else
            {
                samples.Add(s);
                if (!json)
                {
                    FanReading f1 = s.Fans[0], f2 = s.Fans[1];
                    Console.WriteLine($"{s.Timestamp:HH:mm:ss} {FormatRpm(f1),8} {f1.PwmPercent,4:F1}%  {FormatRpm(f2),8} {f2.PwmPercent,4:F1}%");
                }

                aidaSm?.Publish(ToSnapshot(s));
            }

            Thread.Sleep(1000);
        }

        if (aidaSm is not null)
        {
            Console.Error.WriteLine("最后写入的 XML:");
            Console.Error.WriteLine(aidaSm.LastXml);
        }

        if (json) PrintJson(samples);
        else PrintStats(samples);

        aidaSm?.Dispose();
        return samples.Count > 0 ? 0 : 3;
    }

    /// <summary>只读本机身份并判定是否受支持; 同时用构造的身份验证拒绝路径。</summary>
    /// <summary>
    /// 验证 AutoStartManager 对计划任务命令行的解析与比对。
    /// 只做纯字符串判断, 不创建/删除任何计划任务。
    /// </summary>
    private static int RunAutoStartTest()
    {
        // (命令行, 本程序路径, 期望是否识别为自己的)
        var cases = new (string Command, string Exe, bool Expected)[]
        {
            ("\"E:\\WORKS\\RTSS-0\\dist\\app\\Zephyr.exe\" --tray",
             @"E:\WORKS\RTSS-0\dist\app\Zephyr.exe", true),

            (@"E:\WORKS\RTSS-0\dist\app\Zephyr.exe",
             @"E:\WORKS\RTSS-0\dist\app\Zephyr.exe", true),

            ("\"E:\\WORKS\\RTSS-0\\dist\\app\\Zephyr.exe\"",
             @"E:\WORKS\RTSS-0\dist\app\Zephyr.exe", true),

            ("\"e:\\works\\rtss-0\\dist\\app\\zephyr.exe\" --tray",
             @"E:\WORKS\RTSS-0\dist\app\Zephyr.exe", true),

            ("\"C:\\Other\\Thing.exe\" --tray",
             @"E:\WORKS\RTSS-0\dist\app\Zephyr.exe", false),

            ("", @"E:\WORKS\RTSS-0\dist\app\Zephyr.exe", false),
        };

        int pass = 0, fail = 0;

        Console.WriteLine("=== AutoStartManager 命令行解析 ===");
        foreach (var c in cases)
        {
            string extracted = AutoStartManager.ExtractExecutable(c.Command);
            bool same;
            try
            {
                same = extracted.Length > 0 && string.Equals(
                    Path.GetFullPath(extracted), Path.GetFullPath(c.Exe),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { same = false; }

            bool ok = same == c.Expected;
            if (ok) pass++; else fail++;

            Console.WriteLine($"  [{(ok ? "OK" : "FAIL")}] 期望 {c.Expected,-5} 实际 {same,-5}  input={Show(c.Command)}");
            Console.WriteLine($"         取到 = {Show(extracted)}");
        }

        Console.WriteLine();
        Console.WriteLine($"通过 {pass} / 失败 {fail}");
        return fail == 0 ? 0 : 1;
    }

    /// <summary>
    /// 真实往返测试: Enable -> IsEnabled -> Disable -> IsEnabled。
    /// 会创建名为 Zephyr 的计划任务并在结束时删除。需管理员权限。
    /// </summary>
    private static int RunAutoStartLive()
    {
        string exe = @"E:\WORKS\RTSS-0\dist\app\Zephyr.exe";
        var mgr = new AutoStartManager(exe);

        Console.WriteLine("=== 自启动真实往返测试 ===");
        Console.WriteLine($"目标 exe = {exe}");

        bool before = mgr.IsEnabled();
        Console.WriteLine($"1. 初始 IsEnabled  = {before}");

        if (mgr.HasForeignTask())
        {
            Console.WriteLine("同名任务指向别的程序, 中止测试。");
            return 1;
        }

        if (before)
        {
            Console.WriteLine("已处于启用状态, 先关闭再测。");
            var rd = mgr.Disable();
            Console.WriteLine($"   Disable: {rd.Ok} {rd.Message}");
        }

        var en = mgr.Enable();
        Console.WriteLine($"2. Enable  -> ok={en.Ok} msg={en.Message}");
        if (!en.Ok) return 1;

        bool afterEnable = mgr.IsEnabled();
        Console.WriteLine($"3. 启用后 IsEnabled = {afterEnable}  {(afterEnable ? "OK" : "FAIL 界面会回弹")}");

        string cmd = "";
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("schtasks.exe",
                "/Query /TN \"Zephyr\" /XML")
            { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            using var pr = System.Diagnostics.Process.Start(psi)!;
            cmd = pr.StandardOutput.ReadToEnd();
            pr.WaitForExit(10000);
        }
        catch { }
        int ci = cmd.IndexOf("<Command>", StringComparison.Ordinal);
        if (ci >= 0)
        {
            int ce = cmd.IndexOf("</Command>", ci, StringComparison.Ordinal);
            Console.WriteLine($"   任务 Command = {cmd.Substring(ci, ce - ci + 10).Trim()}");
        }

        // 这几项是本轮重写任务 XML 的目的所在, 一并断言, 免得以后又退回模板默认值:
        //   笔记本不接电源也要启动、不能被执行时限中途杀掉。
        //
        // 另外两条是 0.2.0 加的, 关系到托盘图标能不能出现:
        //   任务必须以 LeastPrivilege 运行启动器, 而不是以最高权限直接跑主程序。
        //   以最高权限跑主程序时, 任务计划那个受限 job 会让 NIM_ADD 返回拒绝访问,
        //   托盘图标永远不出现, 而且子进程会继承该限制。
        bool batteryStart = HasXmlValue(cmd, "DisallowStartIfOnBatteries", "false");
        bool batteryStop = HasXmlValue(cmd, "StopIfGoingOnBatteries", "false");
        bool unlimited = HasXmlValue(cmd, "ExecutionTimeLimit", "PT0S");
        bool leastPrivilege = HasXmlValueOrAbsent(cmd, "RunLevel", "LeastPrivilege");
        bool runsLauncher = cmd.IndexOf("ZephyrLauncher.exe", StringComparison.OrdinalIgnoreCase) >= 0;

        Console.WriteLine($"   DisallowStartIfOnBatteries=false : {(batteryStart ? "OK" : "FAIL 拔电源就不启动")}");
        Console.WriteLine($"   StopIfGoingOnBatteries=false     : {(batteryStop ? "OK" : "FAIL 拔电源就被停")}");
        Console.WriteLine($"   ExecutionTimeLimit=PT0S (无限)   : {(unlimited ? "OK" : "FAIL 会被定时杀掉")}");
        Console.WriteLine($"   RunLevel=LeastPrivilege          : {(leastPrivilege ? "OK" : "FAIL 会以最高权限直启, 托盘图标出不来")}");
        Console.WriteLine($"   执行 ZephyrLauncher.exe        : {(runsLauncher ? "OK" : "FAIL 任务没指向启动器")}");

        var dis = mgr.Disable();
        Console.WriteLine($"4. Disable -> ok={dis.Ok} msg={dis.Message}");

        bool afterDisable = mgr.IsEnabled();
        Console.WriteLine($"5. 关闭后 IsEnabled = {afterDisable}  {(afterDisable ? "FAIL" : "OK")}");

        bool pass = afterEnable && !afterDisable && batteryStart && batteryStop && unlimited
                    && leastPrivilege && runsLauncher;
        Console.WriteLine();
        Console.WriteLine(pass ? "结果: 通过" : "结果: 失败");
        return pass ? 0 : 1;
    }

    /// <summary>
    /// 中文路径往返测试: 用带中文目录的 exe 路径建一个临时计划任务,
    /// 验证 IsEnabled() 能认出来。
    ///
    /// 这是 schtasks 输出编码的回归测试: 它的控制台输出是 UTF-8, 若按系统 ANSI
    /// 代码页 (简体中文 = GBK) 解码, 中文路径会变成乱码, IsOurCommand 匹配失败,
    /// 自启动勾选就会永远回弹成"未启用"。
    /// 用一个独立的任务名, 不会动到真正的 Zephyr 任务。
    /// </summary>
    private static int RunAutoStartChinese()
    {
        const string TaskName = "ZephyrCnTest";

        // 自启动管理只做字符串比对, 不会去启动 exe; 但 0.2.0 起 Enable() 会先校验
        // 启动器和主程序同目录, 所以这两个文件必须真的存在。用完在 finally 里清掉。
        string cnDir = "E:" + System.IO.Path.DirectorySeparatorChar + "\u6D4B\u8BD5";
        string exe = System.IO.Path.Combine(cnDir, "Zephyr.exe");
        string launcher = System.IO.Path.Combine(cnDir, AutoStartManager.LauncherFileName);

        var mgr = new AutoStartManager(exe, TaskName);

        Console.WriteLine("=== 中文路径自启动测试 ===");
        Console.WriteLine($"目标 exe = {exe}");

        bool createdDir = false;
        try
        {
            createdDir = !System.IO.Directory.Exists(cnDir);
            System.IO.Directory.CreateDirectory(cnDir);
            System.IO.File.WriteAllBytes(exe, Array.Empty<byte>());
            System.IO.File.WriteAllBytes(launcher, Array.Empty<byte>());
        }
        catch (Exception ex)
        {
            Console.WriteLine("无法准备中文目录: " + ex.Message);
            return 1;
        }

        var existing = new AutoStartManager(exe, TaskName);
        existing.Disable();

        try
        {
            var en = mgr.Enable();
            Console.WriteLine($"1. Enable -> ok={en.Ok}");
            if (!en.Ok)
            {
                Console.WriteLine("   " + en.Message.Replace("\n", "\n   "));
                return 1;
            }

            bool enabled = mgr.IsEnabled();
            Console.WriteLine($"2. IsEnabled (中文路径) = {enabled}  {(enabled ? "OK" : "FAIL 自启动勾选会回弹")}");

            string cmd = ReadTaskCommand(TaskName);
            Console.WriteLine($"3. 任务里的 Command = {Show(cmd)}");
            // 0.2.0 起任务指向启动器而不是主程序 (任务本身跑普通权限, 由启动器经 UAC 提权)。
            bool same = string.Equals(cmd, launcher, StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"4. 与启动器路径一致 = {same}  {(same ? "OK" : "FAIL 编码被破坏")}");

            var dis = mgr.Disable();
            bool afterDisable = mgr.IsEnabled();
            Console.WriteLine($"5. Disable -> ok={dis.Ok}, 之后 IsEnabled = {afterDisable}  {(afterDisable ? "FAIL" : "OK")}");

            bool pass = enabled && same && !afterDisable;
            Console.WriteLine();
            Console.WriteLine(pass ? "结果: 通过" : "结果: 失败");
            return pass ? 0 : 1;
        }
        finally
        {
            // 无论成败都不要把这个测试任务留在系统里。
            try { mgr.Disable(); } catch { }
            try
            {
                if (System.IO.File.Exists(exe)) System.IO.File.Delete(exe);
                if (System.IO.File.Exists(launcher)) System.IO.File.Delete(launcher);
                if (createdDir && System.IO.Directory.Exists(cnDir)
                    && System.IO.Directory.GetFileSystemEntries(cnDir).Length == 0)
                {
                    System.IO.Directory.Delete(cnDir);
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// 旧版任务定义被升级修正的回归测试。
    ///
    /// 0.1.0 用 schtasks /SC ONLOGON 的模板建任务, 那份定义里
    /// DisallowStartIfOnBatteries=true (笔记本拔电源就不启动) 且 ExecutionTimeLimit=PT72H
    /// (连续跑满 72 小时被结束)。用户升级后不会再去点一次"开机自启动", 所以 Enable()
    /// 必须把已存在的任务也重写一遍。这个测试先故意造一份旧定义, 再验证它被改正。
    /// </summary>
    private static int RunAutoStartUpgrade()
    {
        const string TaskName = "ZephyrUpgradeTest";
        string exe = @"E:\WORKS\RTSS-0\dist\app\Zephyr.exe";
        var mgr = new AutoStartManager(exe, TaskName);

        Console.WriteLine("=== 自启动升级路径测试 ===");
        Console.WriteLine($"目标 exe = {exe}");

        try { mgr.Disable(); } catch { }

        try
        {
            // 1. 造一份"旧版定义": 用 XML 直接导入, 精确复刻 0.1.0 那份模板的缺陷
            //    (DisallowStartIfOnBatteries=true + ExecutionTimeLimit=PT72H)。
            //    刻意不通过 /TR 命令行拼引号, 那样测得是 schtasks 的引号规则而不是升级路径。
            string? oldXmlPath = null;
            try
            {
                oldXmlPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                    $"zephyr-oldtask-{Guid.NewGuid():N}.xml");
                System.IO.File.WriteAllText(oldXmlPath, BuildLegacyTaskXml(exe), System.Text.Encoding.Unicode);

                var (exit, output) = RunSchtasks(
                    "/Create /TN \"" + TaskName + "\" /XML \"" + oldXmlPath + "\" /F");
                if (exit != 0)
                {
                    Console.WriteLine($"0. 造旧任务失败 (退出码 {exit}): {output}");
                    return 1;
                }
            }
            finally
            {
                if (oldXmlPath is not null) { try { System.IO.File.Delete(oldXmlPath); } catch { } }
            }

            string oldXml = ReadTaskXml(TaskName);
            string oldBatteryRaw = XmlValue(oldXml, "DisallowStartIfOnBatteries");
            string oldLimitRaw = XmlValue(oldXml, "ExecutionTimeLimit");
            bool oldBattery = string.Equals(oldBatteryRaw, "true", StringComparison.OrdinalIgnoreCase);

            // PT72H 是任务计划程序 v1.2 的默认执行时限, 而 /Query /XML 会把"等于默认值"的
            // 节点整个省掉 (系统自带的 ScheduledDefrag 也查不到这个节点)。
            // 所以这里"读不到"恰恰表示旧定义用的就是默认的 72 小时, 不是缺失。
            bool oldLimitDefault = oldLimitRaw.Length == 0
                                   || oldLimitRaw.StartsWith("PT72", StringComparison.OrdinalIgnoreCase);

            Console.WriteLine($"1. 旧定义 DisallowStartIfOnBatteries = {Show(oldBatteryRaw)}  {(oldBattery ? "OK 旧缺陷已复现" : "注意 本机模板没带这一项")}");
            Console.WriteLine($"   旧定义 ExecutionTimeLimit = {(oldLimitRaw.Length == 0 ? "(节点被省略 = 默认 PT72H)" : oldLimitRaw)}  {(oldLimitDefault ? "OK 旧缺陷已复现" : "注意")}");

            // 旧定义必须被判定为"需要升级", 否则界面启动时那次 IsUpToDate 检查不会触发重写。
            bool oldStale = !mgr.IsUpToDate();
            Console.WriteLine($"   旧定义 IsUpToDate = false : {(oldStale ? "OK 会触发自动重写" : "FAIL 启动时不会修")}");

            // 2. 升级路径: Enable() 不该短路返回, 而要重写定义。
            var en = mgr.Enable();
            Console.WriteLine($"2. Enable -> ok={en.Ok}");
            if (!en.Ok)
            {
                Console.WriteLine("   " + en.Message.Replace("\n", "\n   "));
                return 1;
            }

            bool enabled = mgr.IsEnabled();
            Console.WriteLine($"3. IsEnabled = {enabled}  {(enabled ? "OK" : "FAIL")}");

            string oneLine = en.Message.Replace("\n", " / ");
            string newXml = ReadTaskXml(TaskName);
            bool newBatteryStart = HasXmlValue(newXml, "DisallowStartIfOnBatteries", "false");
            bool newBatteryStop = HasXmlValue(newXml, "StopIfGoingOnBatteries", "false");
            bool newUnlimited = HasXmlValue(newXml, "ExecutionTimeLimit", "PT0S");
            Console.WriteLine($"4. 重写后 DisallowStartIfOnBatteries=false : {(newBatteryStart ? "OK" : "FAIL 升级后仍不能拔电源启动")}");
            Console.WriteLine($"   StopIfGoingOnBatteries=false           : {(newBatteryStop ? "OK" : "FAIL 升级后拔电源会被停")}");
            Console.WriteLine($"   ExecutionTimeLimit=PT0S (无限)         : {(newUnlimited ? "OK" : "FAIL 升级后仍会被强制结束")}");
            Console.WriteLine($"   提示文字 = {oneLine}");

            // 托盘图标那一条: 旧定义是 HighestAvailable + 直指 Zephyr.exe, 必须被改成
            // LeastPrivilege + 启动器, 否则升级上来的用户会一直"没有托盘图标"。
            bool newLeastPrivilege = HasXmlValueOrAbsent(newXml, "RunLevel", "LeastPrivilege");
            bool newRunsLauncher = newXml.IndexOf("ZephyrLauncher.exe", StringComparison.OrdinalIgnoreCase) >= 0;
            Console.WriteLine($"   RunLevel=LeastPrivilege                 : {(newLeastPrivilege ? "OK" : "FAIL 升级后仍以最高权限直启")}");
            Console.WriteLine($"   执行 ZephyrLauncher.exe              : {(newRunsLauncher ? "OK" : "FAIL 升级后仍直指主程序")}");

            bool nowCurrent = mgr.IsUpToDate();
            Console.WriteLine($"5. 重写后 IsUpToDate = {nowCurrent}  {(nowCurrent ? "OK 以后启动不再重复重写" : "FAIL 每次启动都会再跑一遍 schtasks")}");

            bool pass = enabled && oldStale && newBatteryStart && newBatteryStop && newUnlimited
                        && newLeastPrivilege && newRunsLauncher && nowCurrent;
            Console.WriteLine();
            Console.WriteLine(pass ? "结果: 通过" : "结果: 失败");
            return pass ? 0 : 1;
        }
        finally
        {
            // 无论成败都不要把测试任务留在系统里。
            try { mgr.Disable(); } catch { }
        }
    }

    /// <summary>
    /// 复刻 0.1.0 用 /SC ONLOGON 模板建出来的任务定义, 只保留与本次回归相关的两项缺陷设置。
    /// </summary>
    private static string BuildLegacyTaskXml(string exe)
    {
        string e = System.Security.SecurityElement.Escape(exe) ?? "";
        string user = System.Security.SecurityElement.Escape(
            System.Security.Principal.WindowsIdentity.GetCurrent().Name) ?? "";
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-16\"?>").Append(NL0);
        sb.Append("<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">").Append(NL0);
        sb.Append("  <RegistrationInfo><Description>legacy</Description></RegistrationInfo>").Append(NL0);
        sb.Append("  <Triggers><LogonTrigger><Enabled>true</Enabled></LogonTrigger></Triggers>").Append(NL0);
        sb.Append("  <Principals><Principal id=\"Author\"><UserId>").Append(user)
          .Append("</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>").Append(NL0);
        sb.Append("  <Settings>").Append(NL0);
        sb.Append("    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>").Append(NL0);
        sb.Append("    <DisallowStartIfOnBatteries>true</DisallowStartIfOnBatteries>").Append(NL0);
        sb.Append("    <StopIfGoingOnBatteries>true</StopIfGoingOnBatteries>").Append(NL0);
        sb.Append("    <AllowHardTerminate>true</AllowHardTerminate>").Append(NL0);
        sb.Append("    <StartWhenAvailable>false</StartWhenAvailable>").Append(NL0);
        sb.Append("    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>").Append(NL0);
        sb.Append("    <IdleSettings><StopOnIdleEnd>true</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>").Append(NL0);
        sb.Append("    <AllowStartOnDemand>true</AllowStartOnDemand>").Append(NL0);
        sb.Append("    <Enabled>true</Enabled>").Append(NL0);
        sb.Append("    <Hidden>false</Hidden>").Append(NL0);
        sb.Append("    <RunOnlyIfIdle>false</RunOnlyIfIdle>").Append(NL0);
        sb.Append("    <WakeToRun>false</WakeToRun>").Append(NL0);
        sb.Append("    <ExecutionTimeLimit>PT72H</ExecutionTimeLimit>").Append(NL0);
        sb.Append("    <Priority>7</Priority>").Append(NL0);
        sb.Append("  </Settings>").Append(NL0);
        sb.Append("  <Actions Context=\"Author\"><Exec><Command>").Append(e)
          .Append("</Command><Arguments>--tray</Arguments></Exec></Actions>").Append(NL0);
        sb.Append("</Task>").Append(NL0);
        return sb.ToString();
    }

    /// <summary>直接跑 schtasks.exe, 返回退出码与合并输出 (两流并发读, 避免 stderr 写满死锁)。</summary>
    private static (int Exit, string Output) RunSchtasks(string args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("schtasks.exe", args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var pr = System.Diagnostics.Process.Start(psi)!;
            Task<string> errTask = pr.StandardError.ReadToEndAsync();
            string outText = pr.StandardOutput.ReadToEnd();
            string errText = errTask.GetAwaiter().GetResult();
            if (!pr.WaitForExit(15000))
            {
                try { pr.Kill(entireProcessTree: true); } catch { }
                return (-1, "schtasks 超时未返回, 已终止。");
            }
            return (pr.ExitCode, (outText + "\n" + errText).Trim());
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    /// <summary>
    /// 读指定计划任务的 &lt;Command&gt; 值。
    ///
    /// 按原始字节读出来再判断编码: schtasks 控制台输出的编码跟着代码页走,
    /// 实测同一台机器上时而是 UTF-8 时而是 GBK, 固定按一种解会得到乱码。
    /// </summary>
    private static string ReadTaskCommand(string taskName)
    {
        string xml = ReadTaskXml(taskName);
        int i = xml.IndexOf("<Command>", StringComparison.Ordinal);
        if (i < 0) return "";
        int j = xml.IndexOf("</Command>", i, StringComparison.Ordinal);
        return j < 0 ? "" : xml.Substring(i + 9, j - i - 9).Trim();
    }

    /// <summary>
    /// 读指定计划任务的完整 XML。读不到返回空串。
    /// 编码判断与 ReadTaskCommand 原先的逻辑一致, 抽出来给需要断言多项设置的测试用。
    /// </summary>
    private static string ReadTaskXml(string taskName)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("schtasks.exe",
                $"/Query /TN \"{taskName}\" /XML")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var pr = System.Diagnostics.Process.Start(psi)!;

            using var ms = new System.IO.MemoryStream();
            pr.StandardOutput.BaseStream.CopyTo(ms);
            byte[] bytes = ms.ToArray();
            pr.StandardError.BaseStream.CopyTo(System.IO.Stream.Null);
            pr.WaitForExit(10000);

            try
            {
                var strict = new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true);
                return strict.GetString(bytes);
            }
            catch (System.Text.DecoderFallbackException)
            {
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                return System.Text.Encoding.GetEncoding(0).GetString(bytes);
            }
        }
        catch { return ""; }
    }
    /// <summary>从 schtasks /XML 的输出里取某个节点的原始值, 取不到返回空串。</summary>
    private static string XmlValue(string xml, string tag)
    {
        string open = "<" + tag + ">";
        string close = "</" + tag + ">";
        int i = xml.IndexOf(open, StringComparison.Ordinal);
        if (i < 0) return "";
        int j = xml.IndexOf(close, i + open.Length, StringComparison.Ordinal);
        if (j < 0) return "";
        return xml.Substring(i + open.Length, j - i - open.Length).Trim();
    }

    /// <summary>从 schtasks /XML 的输出里取某个节点的值并比对 (忽略大小写)。</summary>
    private static bool HasXmlValue(string xml, string tag, string expected)
    {
        string open = "<" + tag + ">";
        string close = "</" + tag + ">";
        int i = xml.IndexOf(open, StringComparison.Ordinal);
        if (i < 0) return false;
        int j = xml.IndexOf(close, i + open.Length, StringComparison.Ordinal);
        if (j < 0) return false;
        string v = xml.Substring(i + open.Length, j - i - open.Length).Trim();
        return string.Equals(v, expected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 节点值等于该设置在 XML schema 里的默认值时, /Query /XML 会把整个节点省掉
    /// (实测: LeastPrivilege 与 PT72H 都不输出节点)。所以"节点缺失"同样算通过。
    /// </summary>
    private static bool HasXmlValueOrAbsent(string xml, string tag, string expected)
    {
        string open = "<" + tag + ">";
        int i = xml.IndexOf(open, StringComparison.Ordinal);
        if (i < 0) return true;   // 省略 = 默认值
        return HasXmlValue(xml, tag, expected);
    }

    /// <summary>直接把真实的自启动任务开/关掉 (exePath 为空表示关闭), 关掉时不动其它设置。</summary>
    /// <summary>
    /// 真实切换一次 自动 -> 强冷 -> 自动, 校验每一步 EC 回读是否与请求一致。
    ///
    /// 这是产品代码路径 (FanReader.SetMode) 的端到端回归,
    /// 与 tests/FanControlLab 那个实验台不是同一份实现。
    /// 结束时无论成败都把风扇交还自动模式。
    /// </summary>
    private static int RunFanModeLive()
    {
        byte[] blob = LoadLpcIoBlob();
        using var reader = new FanReader(blob);

        try { reader.Open(); }
        catch (Exception ex)
        {
            Console.Error.WriteLine("打开设备失败: " + ex.Message);
            return 2;
        }

        int pass = 0, fail = 0;

        void Check(string what, bool ok, string detail)
        {
            Console.WriteLine((ok ? "[PASS] " : "[FAIL] ") + what + ": " + detail);
            if (ok) pass++; else fail++;
        }

        FanModeState original = reader.ReadModeState();
        Console.WriteLine("=== 风扇两档控制 ===");
        Console.WriteLine("切换前实际模式: " + FanReader.Describe(original));

        try
        {
            // --- 1. 切到强冷 ---
            FanModeResult r1 = reader.SetMode(FanMode.StrongCool);
            Check("SetMode(StrongCool)", r1.Ok, r1.Message + " / " + FanReader.Describe(r1.State));

            FanSample hot = reader.Read();
            Check("强冷回读标志", hot.Mode == FanModeState.StrongCool, FanReader.Describe(hot.Mode));

            int rpm1Hot = hot.Fans.Count > 0 ? hot.Fans[0].Rpm : 0;
            int rpm2Hot = hot.Fans.Count > 1 ? hot.Fans[1].Rpm : 0;
            Console.WriteLine($"  强冷采样: Fan1 {rpm1Hot} RPM  Fan2 {rpm2Hot} RPM");

            // 满转需要一点时间, 再等 3 秒取一次。
            Thread.Sleep(3000);
            FanSample hot2 = reader.Read();
            int rpm1b = hot2.Fans.Count > 0 ? hot2.Fans[0].Rpm : 0;
            int rpm2b = hot2.Fans.Count > 1 ? hot2.Fans[1].Rpm : 0;
            Console.WriteLine($"  3 秒后: Fan1 {rpm1b} RPM  Fan2 {rpm2b} RPM");

            // 184/184 理想值是 5650 左右; 留足余量, 只要明显高于怠速即可。
            Check("强冷转速确实拉高", rpm1b > 4500 && rpm2b > 4500,
                $"Fan1 {rpm1b} / Fan2 {rpm2b} (期望都 > 4500)");

            // --- 2. 交还自动 ---
            FanModeResult r2 = reader.SetMode(FanMode.Auto);
            Check("SetMode(Auto)", r2.Ok, r2.Message + " / " + FanReader.Describe(r2.State));

            FanSample idle = reader.Read();
            Check("恢复自动后的标志", idle.Mode == FanModeState.Auto, FanReader.Describe(idle.Mode));

            Thread.Sleep(4000);
            FanSample idle2 = reader.Read();
            int rpm1c = idle2.Fans.Count > 0 ? idle2.Fans[0].Rpm : 0;
            int rpm2c = idle2.Fans.Count > 1 ? idle2.Fans[1].Rpm : 0;
            Console.WriteLine($"  恢复 4 秒后: Fan1 {rpm1c} RPM  Fan2 {rpm2c} RPM");

            // 60C 上下时自动模式大约 3000-3600 RPM, 低于强冷的 5600。
            Check("自动模式转速已回落", rpm1c < 5200 && rpm2c < 5200,
                $"Fan1 {rpm1c} / Fan2 {rpm2c} (期望都 < 5200)");
        }
        finally
        {
            // 无论前面哪一步炸了, 这里都要把风扇交还自动。
            FanModeResult restore = reader.SetMode(FanMode.Auto);
            Console.WriteLine("[恢复] " + restore.Message + " / " + FanReader.Describe(restore.State));
        }

        Console.WriteLine($"结果: 通过 {pass} / 失败 {fail}");
        return fail == 0 ? 0 : 1;
    }

    private static int RunAutoStartSet(string exePath)
    {
        bool enable = exePath.Length > 0;
        string exe = enable ? exePath : @"E:\WORKS\RTSS-0\dist\app\Zephyr.exe";

        var mgr = new AutoStartManager(exe);

        Console.WriteLine(enable ? "=== 开启开机自启 ===" : "=== 关闭开机自启 ===");
        Console.WriteLine($"目标 exe = {exe}");

        if (enable)
        {
            var en = mgr.Enable();
            Console.WriteLine($"1. Enable -> ok={en.Ok}");
            if (!en.Ok)
            {
                Console.WriteLine("   " + en.Message.Replace("\n", "\n   "));
                return 1;
            }
            Console.WriteLine($"   提示文字 = {en.Message.Replace("\n", " / ")}");

            bool on = mgr.IsEnabled();
            bool up = mgr.IsUpToDate();
            Console.WriteLine($"2. IsEnabled   = {on}  {(on ? "OK" : "FAIL")}");
            Console.WriteLine($"3. IsUpToDate  = {up}  {(up ? "OK" : "FAIL")}");

            string cmd = ReadTaskCommand(AutoStartManager.TaskName);
            Console.WriteLine($"4. 任务 Command = {Show(cmd)}");

            return on && up ? 0 : 1;
        }

        var dis = mgr.Disable();
        bool after = mgr.IsEnabled();
        Console.WriteLine($"1. Disable -> ok={dis.Ok}");
        Console.WriteLine($"2. 之后 IsEnabled = {after}  {(after ? "FAIL" : "OK")}");
        return after ? 1 : 0;
    }

    private static string Show(string s) =>
        string.IsNullOrEmpty(s) ? "(空)" : s;

    private static int RunCheckHw()
    {
        HardwareIdentity real = HardwareIdentity.Read();
        Console.WriteLine("== 本机 SMBIOS ==");
        Console.WriteLine($"  SystemManufacturer = {real.SystemManufacturer}");
        Console.WriteLine($"  SystemProductName  = {real.SystemProductName}");
        Console.WriteLine($"  SystemFamily       = {real.SystemFamily}");
        Console.WriteLine($"  SystemSKU          = {real.SystemSku}");
        Console.WriteLine($"  BaseBoardProduct   = {real.BaseBoardProduct}");
        Console.WriteLine($"  BIOSVersion        = {real.BiosVersion}");
        Console.WriteLine($"  EC Firmware        = {real.EcVersionText}  (raw {real.EcFirmwareMajor}/{real.EcFirmwareMinor})");
        Console.WriteLine();

        SupportVerdict v = SupportedHardware.Check(real);
        Console.WriteLine($"== 判定: {(v.IsSupported ? "支持" : "拒绝")} ==");
        Console.WriteLine($"  {v.Title}");
        foreach (string line in v.Detail.Split('\n')) Console.WriteLine($"  {line}");
        Console.WriteLine();

        Console.WriteLine("== 构造身份的判定结果 ==");
        var cases = new (string Label, HardwareIdentity Id, bool Expect)[]
        {
            ("同型号 + EC 2.12", New("MECHREVO", "WUJIE 14", "WUJIE 14", 2, 12), true),
            ("同型号 + EC 2.20", New("MECHREVO", "WUJIE 14", "WUJIE 14", 2, 20), false),
            ("同型号 + 无 EC 版本", New("MECHREVO", "WUJIE 14", "WUJIE 14", null, null), false),
            ("别家厂商", New("LENOVO", "ThinkPad X1", "X1 Carbon", 2, 12), false),
            ("同厂商别的型号", New("MECHREVO", "WUJIE 16", "WUJIE 16", 2, 12), false),
            ("读不到 SMBIOS", new HardwareIdentity { WasRead = false }, false),
        };

        bool allOk = true;
        foreach (var (label, id, expect) in cases)
        {
            SupportVerdict r = SupportedHardware.Check(id);
            bool ok = r.IsSupported == expect;
            if (!ok) allOk = false;
            Console.WriteLine($"  [{(ok ? "通过" : "失败")}] {label,-16} -> {(r.IsSupported ? "接受" : "拒绝")}  ({r.Title})");
        }

        Console.WriteLine();
        Console.WriteLine(allOk ? "全部符合预期。" : "有不符合预期的用例。");
        return allOk && v.IsSupported ? 0 : 1;

        static HardwareIdentity New(string maker, string product, string board, int? maj, int? min) => new()
        {
            WasRead = true,
            SystemManufacturer = maker,
            SystemProductName = product,
            BaseBoardProduct = board,
            EcFirmwareMajor = maj,
            EcFirmwareMinor = min,
        };
    }

    /// <summary>只读转储若干 EC 寄存器, 用于确认版本字节编码。仅开发期使用。</summary>
    private static int RunPeek()
    {
        byte[] blob = LoadLpcIoBlob();
        using var reader = new FanReader(blob);
        try { reader.Open(); }
        catch (Exception ex) { Console.Error.WriteLine("打开失败: " + ex.Message); return 2; }

        Console.Error.WriteLine($"PawnIO: {reader.PawnIoVersion}  端口: 0x{reader.PortBase:X2}  槽位 {reader.Slot}");

        var addrs = new List<ushort>();
        for (ushort a = 0x0000; a <= 0x0010; a++) addrs.Add(a);
        for (ushort a = 0x00A8; a <= 0x00C0; a++) addrs.Add(a);
        for (ushort a = 0x04B0; a <= 0x04C0; a++) addrs.Add(a);
        addrs.Add(0x0470);

        var ec = reader as object;
        var field = reader.GetType().GetField("_ec", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var access = field?.GetValue(reader);
        if (access is null) { Console.Error.WriteLine("无法访问 EcAccess (内部字段)。"); return 3; }

        var readMethod = access.GetType().GetMethod("Read");
        if (readMethod is null) { Console.Error.WriteLine("找不到 Read 方法。"); return 3; }

        foreach (ushort a in addrs)
        {
            int v = (int)readMethod.Invoke(access, new object[] { a })!;
            Console.WriteLine($"0x{a:X4} = {v,3} (0x{v:X2})");
        }
        return 0;
    }

    private static int RunXmlOnly()
    {
        // 刻意用一眼可辨的合成值, 而不是真实读数量级: 本命令不访问硬件, 但输出会进 CI 日志,
        // 若写成 3433/3032/65 这种真实量级, 读日志的人会误以为它验证了硬件读数。
        var snap = new FanSnapshot
        {
            Fans = new[]
            {
                new FanEntry { Id = "FAN1", Label = "Fan1 (CPU)", ValueText = "1111" },
                new FanEntry { Id = "FAN2", Label = "Fan2 (GPU)", ValueText = "2222" },
            },
        };
        string xml = AidaSharedMemory.BuildXml(snap);
        Console.WriteLine(xml);
        Console.WriteLine();
        Console.WriteLine($"长度 {Encoding.UTF8.GetByteCount(xml)} 字节, 无 BOM, 无 XML 声明, 无根节点。");
        return 0;
    }

    private static FanSnapshot ToSnapshot(FanSample s) => new()
    {
        Fans = s.Fans.Select(f => new FanEntry
        {
            Id = $"FAN{f.Index}",
            Label = f.Label,
            // 无有效读数时留空, 而不是写 0 —— 0 会被显示成"风扇停转"。
            ValueText = f.IsValid ? f.Rpm.ToString() : "",
        }).ToArray(),
    };

    private static string FormatRpm(FanReading f) => f.Note.Length > 0 ? $"{f.Rpm}!" : f.Rpm.ToString();

    private static void PrintStats(List<FanSample> samples)
    {
        if (samples.Count == 0) { Console.WriteLine("\r\n没有任何有效样本。"); return; }

        Console.WriteLine();
        Console.WriteLine($"样本数 {samples.Count}");

        foreach (int idx in new[] { 0, 1 })
        {
            var rpms = samples.Where(s => s.Fans.Count > idx)
                              .Select(s => s.Fans[idx].Rpm)
                              .Where(r => r is > 0 and < 9999).ToList();
            if (rpms.Count == 0) { Console.WriteLine($"  {samples[0].Fans[idx].Label}: 无有效读数"); continue; }
            double avg = rpms.Average();
            int min = rpms.Min(), max = rpms.Max();
            double jitter = avg > 0 ? (max - min) * 100.0 / avg : 0;
            Console.WriteLine($"  {samples[0].Fans[idx].Label}: 平均 {avg:F0} RPM, 范围 {min}..{max}, 抖动 {jitter:F1}%");
        }

    }

    private static void PrintJson(List<FanSample> samples)
    {
        var sb = new StringBuilder();
        sb.Append("{\"samples\":[");
        for (int i = 0; i < samples.Count; i++)
        {
            FanSample s = samples[i];
            if (i > 0) sb.Append(',');
            sb.Append('{');
            sb.Append($"\"t\":\"{s.Timestamp:HH:mm:ss}\",");
            sb.Append("\"fans\":[");
            for (int j = 0; j < s.Fans.Count; j++)
            {
                FanReading f = s.Fans[j];
                if (j > 0) sb.Append(',');
                sb.Append($"{{\"rpm\":{f.Rpm},\"count\":{f.RawCount},\"pwm\":{f.PwmOut},\"note\":\"{f.Note}\"}}");
            }
            sb.Append("]}");
        }
        sb.Append("]}");
        Console.WriteLine(sb.ToString());
    }

    private static byte[] LoadLpcIoBlob()
    {
        var asm = typeof(FanReader).Assembly;
        foreach (string name in asm.GetManifestResourceNames())
        {
            if (name.EndsWith("LpcIO.bin", StringComparison.OrdinalIgnoreCase))
            {
                using Stream s = asm.GetManifestResourceStream(name)!;
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }

        string local = Path.Combine(AppContext.BaseDirectory, "Resources", "LpcIO.bin");
        if (File.Exists(local)) return File.ReadAllBytes(local);

        throw new FileNotFoundException("找不到 LpcIO.bin (既没有嵌入资源, 也不在 Resources 目录)。");
    }

    private static void TrySetUtf8Console()
    {
        try { SetConsoleOutputCP(65001); } catch { }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool SetConsoleOutputCP(uint codePage);
}
