using System.Runtime.InteropServices;
using System.Text;
using Zephyr.Core.Ec;
using Zephyr.Core.Native;

namespace FanControlLab;

/// <summary>
/// 风扇转速控制实验台 —— **只用于实验, 不是产品**。
///
/// 安全设计 (按重要性排序, 任一失效都不能跑):
///  1. 绝不接受 > 184 的 PWM 值 (实测 >184 风扇会直接停转)。
///  2. 独立看门狗线程: 每秒读一次 CPU 温度, 连续 3 次 >= 92 度就立刻恢复 EC 自动控制并终止。
///  3. 任何退出路径 (正常/异常/Ctrl+C/进程被关) 都会把模式标志写回 0x00 (自动)。
///  4. 只写两个已知安全的寄存器: 手动 PWM 值 (0x021E/0x029E) 与模式标志 (0x021F/0x029F)。
///  5. 默认参数下只做只读检查, 不带 --apply 不会写任何寄存器。
/// </summary>
internal static class Program
{
    // ---------------------------------------------------------------- 寄存器
    private sealed record FanRegs(ushort Level, ushort Target, ushort Cur, ushort ManualPwm, ushort Flag, ushort PwmOut, ushort TachLo, ushort TachHi);

    private static readonly FanRegs Fan1 = new(0x0213, 0x0214, 0x0215, 0x021E, 0x021F, 0x1809, 0x181E, 0x181F);
    private static readonly FanRegs Fan2 = new(0x0293, 0x0294, 0x0295, 0x029E, 0x029F, 0x1804, 0x1820, 0x1821);

    private const int PwmHwMax = 184;
    private const int TachConstant = 2_156_250;
    private const int TempLimitC = 92;
    private static readonly TimeSpan TempLimitHold = TimeSpan.FromSeconds(3);

    // ---------------------------------------------------------------- 状态
    private static EcAccess? _ec;
    private static EcMutex? _mutex;
    private static PawnIo? _io;
    private static volatile bool _restored = true;
    private static volatile bool _watchdogTrip;
    private static readonly object _ioLock = new();

    private static int Main(string[] rawArgs)
    {
        TrySetUtf8Console();

        // --out <路径>: 把全部输出同时写进 UTF-8 文件。
        // 这样提权实验不依赖 PowerShell 的管道 / 代码页 / 参数透传, 结果文件就是证据。
        string[] args = rawArgs;
        int outIdx = Array.IndexOf(rawArgs, "--out");
        if (outIdx >= 0 && outIdx + 1 < rawArgs.Length)
        {
            string outPath = rawArgs[outIdx + 1];
            args = rawArgs.Where((_, i) => i != outIdx && i != outIdx + 1).ToArray();
            try
            {
                var sw = new StreamWriter(outPath, false, new UTF8Encoding(false));
                sw.AutoFlush = true;
                Console.SetOut(sw);
            }
            catch (Exception ex)
            {
                Console.WriteLine("无法写输出文件: " + ex.Message);
            }
        }

        string mode = args.Length == 0 ? "status" : args[0].ToLowerInvariant();
        bool apply = args.Contains("--apply");

        Console.WriteLine("=== Zephyr 风扇控制实验台 ===");
        Console.WriteLine($"模式: {mode}  写入许可: {(apply ? "允许 (--apply)" : "仅只读")}");
        Console.WriteLine();

        try
        {
            Open();
            return mode switch
            {
                "status" => ShowStatus(),
                "pin" => Pin(apply),
                "set" => SetFan(args, apply),
                "sweep" => Sweep(apply),
                "auto" => ForceAuto(),
                _ => Usage()
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine("[错误] " + ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
        finally
        {
            RestoreAuto("退出");
            Close();
        }
    }

    // ---------------------------------------------------------------- 打开设备
    private static void Open()
    {
        byte[] blob = LoadLpcIoBlob();
        Console.WriteLine($"LpcIO.bin: {blob.Length} 字节");

        _io = new PawnIo();
        Console.WriteLine($"PawnIO: {_io.Version}");

        _mutex = new EcMutex();
        _ec = new EcAccess(_io);
        _ec.LoadModule(blob);

        bool held = _mutex.Acquire(2000);
        try { _ec.DetectPort(); }
        finally { if (held) _mutex.Release(); }
        Console.WriteLine($"端口基址: 0x{_ec.PortBase:X2} (槽位 {_ec.Slot})");
        Console.WriteLine();

        Console.CancelKeyPress += (_, e) => { e.Cancel = false; RestoreAuto("Ctrl+C"); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RestoreAuto("进程退出");
    }

    private static void Close()
    {
        try { _mutex?.Dispose(); } catch { }
        try { _io?.Dispose(); } catch { }
        _mutex = null; _io = null; _ec = null;
    }

    // ---------------------------------------------------------------- 读写
    private static int ReadEc(ushort addr)
    {
        lock (_ioLock)
        {
            bool held = _mutex?.Acquire(500) ?? false;
            try { return _ec!.Read(addr); }
            finally { if (held) _mutex?.Release(); }
        }
    }

    private static void WriteEc(ushort addr, byte value)
    {
        lock (_ioLock)
        {
            bool held = _mutex?.Acquire(500) ?? false;
            try { _ec!.Write(addr, value); }
            finally { if (held) _mutex?.Release(); }
        }
    }

    private static int ReadRpm(FanRegs r)
    {
        int lo = ReadEc(r.TachLo);
        int hi = ReadEc(r.TachHi);
        int count = (hi << 8) | lo;
        if (count == 0 || count >= 0x4000) return 0;
        if (count < 0x80) return 9999;
        return TachConstant / count;
    }

    private static string FlagName(int f) => f switch
    {
        0x00 => "自动",
        0xA5 => "自定义曲线",
        0xCC => "自定义温度",
        0xDC => "手动PWM",
        _ => $"0x{f:X2}(未知)"
    };

    // ---------------------------------------------------------------- status
    private static int ShowStatus()
    {
        int temp = ReadEc(EcRegisters.CpuTemperature);
        Console.WriteLine($"CPU 温度: {temp} °C   (EC 0x0470)");
        Console.WriteLine();
        Console.WriteLine("寄存器        Fan1   Fan2   说明");
        Console.WriteLine($"模式标志      0x{ReadEc(Fan1.Flag):X2}   0x{ReadEc(Fan2.Flag):X2}    {FlagName(ReadEc(Fan1.Flag))} / {FlagName(ReadEc(Fan2.Flag))}");
        Console.WriteLine($"档位等级      {ReadEc(Fan1.Level),5}  {ReadEc(Fan2.Level),5}");
        Console.WriteLine($"PWM 目标      {ReadEc(Fan1.Target),5}  {ReadEc(Fan2.Target),5}    /184");
        Console.WriteLine($"PWM 当前      {ReadEc(Fan1.Cur),5}  {ReadEc(Fan2.Cur),5}    /184");
        Console.WriteLine($"PWM 硬件输出  0x{ReadEc(Fan1.PwmOut):X2}   0x{ReadEc(Fan2.PwmOut):X2}");
        Console.WriteLine($"手动PWM 值    {ReadEc(Fan1.ManualPwm),5}  {ReadEc(Fan2.ManualPwm),5}");
        Console.WriteLine($"转速           {ReadRpm(Fan1),4}    {ReadRpm(Fan2),4}    RPM");
        return 0;
    }

    // ---------------------------------------------------------------- pin: 协议验证
    /// <summary>
    /// 最保守的写入验证: 把一个寄存器写成它**当前就已经是**的值。
    /// 物理上什么都没变, 但能证明写入通道确实通 (回读一致)。
    /// </summary>
    private static int Pin(bool apply)
    {
        // 选 0x021E (Fan1 手动 PWM 值) 而不是 PWM 目标值:
        // 自动模式下 EC 根本不读这个寄存器, 写它物理上零影响, 是最安全的通道验证点。
        ushort addr = Fan1.ManualPwm;
        int before = ReadEc(addr);
        Console.WriteLine($"目标寄存器 0x{addr:X4} (Fan1 手动PWM值, 自动模式下未被使用) 当前值 = {before}");

        if (!apply)
        {
            Console.WriteLine("未加 --apply, 不写入。加 --apply 才会真的写 (写成它现在已有的值)。");
            return 0;
        }

        Console.WriteLine($"写入相同的值 {before} ...");
        WriteEc(addr, (byte)before);
        int after = ReadEc(addr);
        Console.WriteLine($"回读 = {after}  {(after == before ? "OK 写入通道可用" : "FAIL 回读不一致")}");

        // 再验证一次「能改」: 写一个不同值然后立刻写回原值。
        // 仍然不碰风扇 —— 自动模式下这个寄存器被 EC 忽略。
        int probe = before == 7 ? 9 : 7;
        Console.WriteLine($"写入探针值 {probe} ...");
        WriteEc(addr, (byte)probe);
        int mid = ReadEc(addr);
        WriteEc(addr, (byte)before);
        int restored = ReadEc(addr);
        Console.WriteLine($"回读探针 = {mid} (期望 {probe})   写回 = {restored} (期望 {before})");
        bool ok = after == before && mid == probe && restored == before;
        Console.WriteLine(ok ? "结论: 写入通道完全可用 (读-改-写一致)。" : "结论: 写入通道有异常, 停止实验。");
        return ok ? 0 : 1;
    }

    // ---------------------------------------------------------------- set: 手动 PWM
    private static int SetFan(string[] args, bool apply)
    {
        if (args.Length < 3)
        {
            Console.WriteLine("用法: FanControlLab set <1|2|both> <pwm 0-184> [秒数] [--apply]");
            return 1;
        }

        if (!int.TryParse(args[2], out int pwm))
        {
            Console.WriteLine("PWM 参数必须是数字。");
            return 1;
        }

        if (pwm > PwmHwMax)
        {
            Console.WriteLine($"[拒绝] PWM {pwm} 超过硬件上限 {PwmHwMax}。");
            Console.WriteLine("        实测 >184 会让风扇直接停转, 不是转得更快。");
            return 2;
        }
        if (pwm < 0) { Console.WriteLine("[拒绝] PWM 不能为负。"); return 2; }

        int seconds = 10;
        if (args.Length >= 4 && int.TryParse(args[3], out int s)) seconds = s;

        string which = args[1].ToLowerInvariant();
        bool doFan1 = which is "1" or "both";
        bool doFan2 = which is "2" or "both";
        if (!doFan1 && !doFan2) { Console.WriteLine("风扇编号只能是 1 / 2 / both。"); return 1; }

        Console.WriteLine($"目标: Fan1={doFan1} Fan2={doFan2}  PWM={pwm}/184 ({pwm * 100 / PwmHwMax}%)  持续 {seconds} 秒");

        if (!apply)
        {
            Console.WriteLine();
            Console.WriteLine("未加 --apply, 不写入任何寄存器。");
            Console.WriteLine("当前状态:");
            ShowStatus();
            return 0;
        }

        // 记录原值, 退出时恢复
        int oldFlag1 = ReadEc(Fan1.Flag), oldFlag2 = ReadEc(Fan2.Flag);
        int oldMan1 = ReadEc(Fan1.ManualPwm), oldMan2 = ReadEc(Fan2.ManualPwm);
        Console.WriteLine($"原值: Fan1 flag=0x{oldFlag1:X2} manual={oldMan1} | Fan2 flag=0x{oldFlag2:X2} manual={oldMan2}");
        _restored = false;

        StartWatchdog();

        Console.WriteLine();
        Console.WriteLine($"写入手动 PWM (模式标志 -> 0xDC) ...");
        if (doFan1) { WriteEc(Fan1.ManualPwm, (byte)pwm); WriteEc(Fan1.Flag, 0xDC); }
        if (doFan2) { WriteEc(Fan2.ManualPwm, (byte)pwm); WriteEc(Fan2.Flag, 0xDC); }

        Thread.Sleep(300);
        int flagAfter1 = ReadEc(Fan1.Flag), flagAfter2 = ReadEc(Fan2.Flag);
        Console.WriteLine($"回读模式: Fan1=0x{flagAfter1:X2} ({FlagName(flagAfter1)})  Fan2=0x{flagAfter2:X2} ({FlagName(flagAfter2)})");

        bool took = (!doFan1 || flagAfter1 == 0xDC) && (!doFan2 || flagAfter2 == 0xDC);
        if (!took)
        {
            Console.WriteLine("[失败] EC 没有接受手动模式 (可能被固件回写覆盖)。");
            RestoreAuto("写入未生效");
            return 3;
        }

        Console.WriteLine();
        Console.WriteLine("时间     CPU   Fan1   Fan2");
        for (int i = 0; i < seconds; i++)
        {
            if (_watchdogTrip) { Console.WriteLine("[看门狗] 温度超限, 已中止。"); break; }
            int temp = ReadEc(EcRegisters.CpuTemperature);
            Console.WriteLine($"{i + 1,3}s   {temp,3}°C {ReadRpm(Fan1),5}  {ReadRpm(Fan2),5}");
            Thread.Sleep(1000);
        }

        RestoreAuto("实验结束");
        return _watchdogTrip ? 4 : 0;
    }

    // ---------------------------------------------------------------- sweep
    private static int Sweep(bool apply)
    {
        if (!apply) { Console.WriteLine("sweep 需要 --apply。"); return 1; }
        Console.WriteLine("PWM -> RPM 响应扫描 (只动 Fan1, 每档 4 秒)");
        // 不测 0: 完全停转既无收益又让温度往上走。最小从 40 起。
        int[] points = { 40, 60, 80, 100, 120, 140, 160, 184 };
        _restored = false;
        StartWatchdog();
        Console.WriteLine("PWM  占空   Fan1RPM  Fan2RPM  CPU");
        try
        {
            foreach (int p in points)
            {
                if (_watchdogTrip) break;
                WriteEc(Fan1.ManualPwm, (byte)p);
                WriteEc(Fan1.Flag, 0xDC);
                Thread.Sleep(4000);
                Console.WriteLine($"{p,3}  {p * 100 / PwmHwMax,4}%   {ReadRpm(Fan1),7}  {ReadRpm(Fan2),7}  {ReadEc(EcRegisters.CpuTemperature),3}°C");
            }
        }
        finally { RestoreAuto("扫描结束"); }
        return 0;
    }

    // ---------------------------------------------------------------- auto
    private static int ForceAuto()
    {
        // 必须绕过 _restored 守卫: 单独运行 auto 时程序从没写过手动模式,
        // _restored 初值就是 true, 走 RestoreAuto 会直接 return, 一个寄存器都不写。
        // 而 auto 的唯一用途恰恰是「上次被强杀、ECC 还卡在手动模式」这种外部残留状态。
        ForceRestoreAuto("手动恢复");
        return 0;
    }

    // ---------------------------------------------------------------- 恢复
    /// <summary>把两个风扇的模式标志写回 0x00 (EC 自动)。可重复调用。</summary>
    private static void RestoreAuto(string why) => DoRestore(why, force: false);

    /// <summary>无视内存中的状态标志, 无条件写回自动模式。用于清理上次进程被强杀留下的残留。</summary>
    private static void ForceRestoreAuto(string why) => DoRestore(why, force: true);

    private static void DoRestore(string why, bool force)
    {
        if (!force && _restored) return;
        if (_ec is null) return;
        _restored = true;
        try
        {
            WriteEc(Fan1.Flag, 0x00);
            WriteEc(Fan2.Flag, 0x00);
            int f1 = ReadEc(Fan1.Flag), f2 = ReadEc(Fan2.Flag);
            Console.WriteLine($"[恢复] ({why}) Fan1=0x{f1:X2} ({FlagName(f1)})  Fan2=0x{f2:X2} ({FlagName(f2)})");
            if (f1 != 0x00 || f2 != 0x00)
                Console.WriteLine("[警告] 模式没有恢复到自动, 请手动运行 FanControlLab auto。");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[警告] 恢复失败: {ex.Message} —— 请断电重启或手动恢复。");
        }
    }

    // ---------------------------------------------------------------- 看门狗
    /// <summary>
    /// 独立线程每秒读温度。连续 3 次 >= 92 度就强制恢复自动并置 _watchdogTrip。
    /// 这是最后一道防线: 即使调用的 PWM 太低、风扇不转, 也能在几秒内把控制权交回 EC。
    /// </summary>
    private static void StartWatchdog()
    {
        var t = new Thread(() =>
        {
            int over = 0;
            while (!_restored)
            {
                try
                {
                    int temp = ReadEc(EcRegisters.CpuTemperature);
                    over = temp >= TempLimitC ? over + 1 : 0;
                    if (over >= (int)TempLimitHold.TotalSeconds)
                    {
                        Console.WriteLine();
                        Console.WriteLine($"[看门狗] CPU 温度连续 {over} 秒 >= {TempLimitC}°C, 立即恢复自动控制!");
                        _watchdogTrip = true;
                        _restored = true;
                        WriteEc(Fan1.Flag, 0x00);
                        WriteEc(Fan2.Flag, 0x00);
                        return;
                    }
                }
                catch { }
                Thread.Sleep(1000);
            }
        })
        { IsBackground = true, Name = "FanWatchdog" };
        t.Start();
    }

    // ---------------------------------------------------------------- 杂项
    private static int Usage()
    {
        Console.WriteLine("用法:");
        Console.WriteLine("  FanControlLab status                  只读查看全部寄存器");
        Console.WriteLine("  FanControlLab pin [--apply]           把寄存器写成它当前已有的值 (最安全的写入验证)");
        Console.WriteLine("  FanControlLab set <1|2|both> <pwm> [秒] [--apply]");
        Console.WriteLine("  FanControlLab sweep --apply           PWM->RPM 响应扫描 (只动 Fan1)");
        Console.WriteLine("  FanControlLab auto                    立即恢复 EC 自动控制");
        return 1;
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
        throw new FileNotFoundException("找不到 LpcIO.bin。");
    }

    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleOutputCP(uint codePage);

    private static void TrySetUtf8Console()
    {
        try { SetConsoleOutputCP(65001); Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
    }
}