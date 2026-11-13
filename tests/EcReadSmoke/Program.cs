using System.Diagnostics;
using System.Text;
using WujieFan.Core.Ec;
using WujieFan.Core.Hardware;
using WujieFan.Core.Config;
using WujieFan.Core.Providers;

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
///   EcReadSmoke.exe --autostart-live     真实创建/查询/删除计划任务 WujieFan (需管理员, 会还原)
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        TrySetUtf8Console();

        int seconds = 5;
        bool json = false, aida = false, xmlOnly = false, peek = false, checkHw = false, autoStartTest = false, autoStartLive = false;

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
        Console.Error.WriteLine($"硬件互斥量: {(reader.HasMutex ? "已获取" : "未获取(仍可只读采样)")}");

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
                    string t = s.Temperature?.ToString() ?? "--";
                    Console.WriteLine($"{s.Timestamp:HH:mm:ss} {t,4}  {FormatRpm(f1),8} {f1.PwmPercent,4:F1}%  {FormatRpm(f2),8} {f2.PwmPercent,4:F1}%");
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
            ("\"E:\\WORKS\\RTSS-0\\dist\\app\\WujieFan.exe\" --tray",
             @"E:\WORKS\RTSS-0\dist\app\WujieFan.exe", true),

            (@"E:\WORKS\RTSS-0\dist\app\WujieFan.exe",
             @"E:\WORKS\RTSS-0\dist\app\WujieFan.exe", true),

            ("\"E:\\WORKS\\RTSS-0\\dist\\app\\WujieFan.exe\"",
             @"E:\WORKS\RTSS-0\dist\app\WujieFan.exe", true),

            ("\"e:\\works\\rtss-0\\dist\\app\\wujiefan.exe\" --tray",
             @"E:\WORKS\RTSS-0\dist\app\WujieFan.exe", true),

            ("\"C:\\Other\\Thing.exe\" --tray",
             @"E:\WORKS\RTSS-0\dist\app\WujieFan.exe", false),

            ("", @"E:\WORKS\RTSS-0\dist\app\WujieFan.exe", false),
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
    /// 会创建名为 WujieFan 的计划任务并在结束时删除。需管理员权限。
    /// </summary>
    private static int RunAutoStartLive()
    {
        string exe = @"E:\WORKS\RTSS-0\dist\app\WujieFan.exe";
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
                "/Query /TN \"WujieFan\" /XML")
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

        var dis = mgr.Disable();
        Console.WriteLine($"4. Disable -> ok={dis.Ok} msg={dis.Message}");

        bool afterDisable = mgr.IsEnabled();
        Console.WriteLine($"5. 关闭后 IsEnabled = {afterDisable}  {(afterDisable ? "FAIL" : "OK")}");

        bool pass = afterEnable && !afterDisable;
        Console.WriteLine();
        Console.WriteLine(pass ? "结果: 通过" : "结果: 失败");
        return pass ? 0 : 1;
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
        var snap = new FanSnapshot
        {
            Fans = new[]
            {
                new FanEntry { Id = "FAN1", Label = "Fan1 (CPU)", ValueText = "3433" },
                new FanEntry { Id = "FAN2", Label = "Fan2 (GPU)", ValueText = "3032" },
            },
            Temperature = 65,
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
        Temperature = s.Temperature,
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

        var temps = samples.Where(s => s.Temperature.HasValue).Select(s => s.Temperature!.Value).ToList();
        if (temps.Count > 0)
            Console.WriteLine($"  温度: 平均 {temps.Average():F0}°C, 范围 {temps.Min()}..{temps.Max()}");
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
            sb.Append($"\"temp\":{(s.Temperature?.ToString() ?? "null")},");
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
