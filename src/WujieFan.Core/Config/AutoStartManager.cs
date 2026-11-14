using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace WujieFan.Core.Config;

/// <summary>
/// 管理"开机自启动"开关。
///
/// 实现方式是用计划任务 (WujieFan) 而不是注册表 Run 键, 原因:
///   1. 本程序需要管理员权限, 注册表 Run 键在 UAC 下会被系统拦截不提升,
///      结果是开机后弹不出界面、也读不到风扇。
///   2. 计划任务对用户可见 (任务计划程序里能看到), 容易检查和删除, 比注册表透明。
///
/// 关键: 计划任务<b>不能</b>直接以最高权限运行 WujieFan.exe。
/// 任务计划程序拉起的进程会被放进一个带 UI 限制的 job 对象, 其中的
/// Shell_NotifyIcon(NIM_ADD) 直接返回 ERROR_ACCESS_DENIED(5), 托盘图标永远不出现,
/// 而且这个限制会被所有子进程继承 —— 无论任务本身是 Highest 还是 Limited 都一样。
///
/// 所以任务改为: 普通权限运行同目录的 WujieFanLauncher.exe(纯 asInvoker),
/// 由它再调 ShellExecuteEx("runas") 真正经 AppInfo 提权拉起 WujieFan.exe。
/// 只有 AppInfo 新建的进程才不会继承那个受限 job。详见 WujieFan.Launcher 的注释。
///
/// 安全约定: 只操作名为 WujieFan 的计划任务; 若同名任务已存在但指向别的程序,
/// 一律拒绝覆盖, 并且不删除它。
/// </summary>
public sealed class AutoStartManager
{
    public const string TaskName = "WujieFan";

    /// <summary>启动器的文件名。它必须和 WujieFan.exe 放在同一目录。</summary>
    public const string LauncherFileName = "WujieFanLauncher.exe";

    private readonly string _exePath;
    private readonly string _taskName;

    public AutoStartManager(string? exePath = null, string? taskName = null)
    {
        _exePath = exePath ?? Environment.ProcessPath ?? "";
        _taskName = string.IsNullOrWhiteSpace(taskName) ? TaskName : taskName!;
    }

    /// <summary>
    /// 计划任务里真正要执行的程序: 同目录的启动器, 而不是 WujieFan.exe 本身。
    /// 找不到 WujieFan.exe 所在目录时返回空串。
    /// </summary>
    public string LauncherPath
    {
        get
        {
            if (string.IsNullOrEmpty(_exePath)) return "";
            string? dir = Path.GetDirectoryName(_exePath);
            return string.IsNullOrEmpty(dir) ? "" : Path.Combine(dir, LauncherFileName);
        }
    }

    /// <summary>本实例操作的计划任务名。默认是 WujieFan, 测试里可以换成别的名字。</summary>
    public string Name => _taskName;

    public sealed record Result(bool Ok, string Message);

    /// <summary>查询当前状态。任务不存在返回 false。</summary>
    public bool IsEnabled()
    {
        var (exists, command) = QueryTask();
        return exists && IsOurCommand(command);
    }

    /// <summary>同名任务存在但不属于本程序。此时任何自动操作都应当停下。</summary>
    public bool HasForeignTask()
    {
        var (exists, command) = QueryTask();
        return exists && !IsOurCommand(command);
    }

    /// <summary>
    /// 现有任务是否已经符合当前版本的要求。用来避免每次启动都跑一遍 schtasks
    /// 重写 —— 用户没改过配置就不该动系统。读不到任务时返回 false, 让调用方走重写路径兜底。
    ///
    /// 要同时满足四条, 少一条就得重写:
    ///   1. 执行时限无限 (ExecutionTimeLimit=PT0S) —— 否则跑满 72 小时会被悄悄结束
    ///   2. 电池上也要启动 (DisallowStartIfOnBatteries=false)
    ///   3. 切到电池时不要停 (StopIfGoingOnBatteries=false)
    ///   4. 以普通权限运行启动器 (RunLevel=LeastPrivilege 且 Command 指向启动器)
    /// 第 4 条是 0.2.0 新增的: 0.1.x 建的任务是 HighestAvailable + 直指 WujieFan.exe,
    /// 那份定义会让托盘图标永远不出现, 必须被重写升级。
    /// </summary>
    public bool IsUpToDate()
    {
        var (exists, command) = QueryTask();
        if (!exists) return false;

        string xml = QueryTaskXml();
        if (xml.Length == 0) return false;

        // ExecutionTimeLimit 等于默认值 PT72H 时 /Query /XML 会把整个节点省掉,
        // 所以"没有这个节点"同样判为"不符合要求"。
        bool noLimit = XmlValue(xml, "ExecutionTimeLimit") is { Length: > 0 } limit
                       && limit.StartsWith("PT0", StringComparison.OrdinalIgnoreCase);

        bool runsLauncher = SamePath(ExtractExecutable(command), LauncherPath);

        // 同理: RunLevel 等于默认值 LeastPrivilege 时 /Query /XML 会把整个节点省掉
        // (实测 Limited 任务没有 <RunLevel>, Highest 任务才有 <RunLevel>HighestAvailable</RunLevel>)。
        // 所以"没有这个节点"必须算通过, 否则 IsUpToDate 永远为 false, 每次启动都重写一遍任务。
        string runLevel = XmlValue(xml, "RunLevel");
        bool leastPrivilege = runLevel.Length == 0
                              || string.Equals(runLevel, "LeastPrivilege", StringComparison.OrdinalIgnoreCase);

        return noLimit
               && IsXmlFalse(xml, "DisallowStartIfOnBatteries")
               && IsXmlFalse(xml, "StopIfGoingOnBatteries")
               && runsLauncher
               && leastPrivilege;
    }

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

    private static bool IsXmlFalse(string xml, string tag) =>
        string.Equals(XmlValue(xml, tag), "false", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 创建计划任务: 登录时以普通权限运行启动器, 由启动器经 UAC 提权拉起主程序。
    /// 直接以最高权限运行主程序会让托盘图标永远不出现, 见类注释。
    /// </summary>
    public Result Enable()
    {
        if (string.IsNullOrEmpty(_exePath))
            return new Result(false, "无法确定本程序路径, 不能创建自启动项。");

        string launcher = LauncherPath;
        if (launcher.Length == 0)
            return new Result(false, "无法确定启动器路径, 不能创建自启动项。");

        if (!File.Exists(launcher))
            return new Result(false,
                "缺少启动器文件:\n" + launcher + "\n" +
                "它必须和 WujieFan.exe 放在同一个目录。请重新解压完整的发布包。");

        var (exists, command) = QueryTask();

        if (exists && !IsOurCommand(command))
            return new Result(false,
                $"计划任务「{_taskName}」已存在, 但指向别的程序:\n{command}\n" +
                "为避免覆盖别人的设置, 这里不做任何修改。请先在任务计划程序里手动处理该任务。");

        bool updating = exists && IsOurCommand(command);

        // 任务已经是我们的也照样重写一遍, 不能直接返回"已启用"就完事:
        // 0.1.0 用 schtasks /SC ONLOGON 的模板建过同一个任务, 那份定义里带着
        // DisallowStartIfOnBatteries=true 和 ExecutionTimeLimit=PT72H —— 前者让笔记本
        // 拔掉电源就不启动, 后者让它在连续运行 72 小时后被执行程序悄悄杀掉。
        // 而用户不会为了修这个再勾一次"开机自启动", 所以升级路径必须走重写。
        // /F 覆盖是幂等的, 代价只是用户在任务计划程序里对这个任务做过的手工调整会被覆盖,
        // 这本来就是"打开开机自启动"应有的语义。
        //
        // 这里刻意不用 /TR 那条最简单的命令, 而是写一份完整的任务 XML。
        // 用 /SC ONLOGON 时 schtasks 会用模板默认值填 Settings, 其中有两项对本程序是错的:
        //   DisallowStartIfOnBatteries=true  -> 笔记本不接电源时干脆不启动
        //   ExecutionTimeLimit=PT72H        -> 连续跑满 72 小时会被任务计划程序结束掉
        // 后者尤其隐蔽: 风扇监控是要长期常驻的, 三天后突然没了读数, 很难联想到计划任务。
        string xml = BuildTaskXml(launcher);
        string? tmp = null;

        try
        {
            tmp = Path.Combine(Path.GetTempPath(), $"wujiefan-task-{Guid.NewGuid():N}.xml");

            // 任务计划程序要求 XML 带 BOM 的 UTF-16; 写 UTF-8 会让中文路径/说明乱码甚至导入失败。
            File.WriteAllText(tmp, xml, Encoding.Unicode);

            var r = Run("schtasks.exe", $"/Create /TN \"{_taskName}\" /XML \"{tmp}\" /F");

            if (r.Exit != 0)
                return new Result(false, $"创建计划任务失败 (退出码 {r.Exit}):\n{r.Output}");

            string verb = updating ? "已更新" : "已启用";
            return new Result(true, $"{verb}: 登录 Windows 时自动启动 (会弹一次 UAC 确认)。\n计划任务名称: {_taskName}");
        }
        catch (Exception ex)
        {
            return new Result(false, "创建计划任务失败: " + ex.Message);
        }
        finally
        {
            // 临时文件里有本机路径, 用完就删, 不要留在 %TEMP%。
            if (tmp is not null)
            {
                try { File.Delete(tmp); } catch { }
            }
        }
    }

    /// <summary>删除计划任务。仅当它属于本程序时才删。</summary>
    public Result Disable()
    {
        var (exists, command) = QueryTask();

        if (!exists)
            return new Result(true, "已关闭 (本来就没有自启动项)。");

        if (!IsOurCommand(command))
            return new Result(false,
                $"计划任务「{_taskName}」指向别的程序:\n{command}\n" +
                "为避免误删别人的设置, 这里不做任何修改。");

        var r = Run("schtasks.exe", $"/Delete /TN \"{_taskName}\" /F");

        if (r.Exit != 0)
            return new Result(false, $"删除计划任务失败 (退出码 {r.Exit}):\n{r.Output}");

        return new Result(true, "已关闭开机自启动。");
    }

    /// <summary>
    /// 生成任务计划程序的任务定义。
    ///
    /// 除下面注释里点名的那几项, 其余都取 schtasks 的 ONLOGON 模板默认值, 以便
    /// 界面表现与"新建任务向导"一致, 用户去任务计划程序里看不会感到陌生。
    /// </summary>
    /// <param name="launcherPath">要执行的启动器完整路径 (不是 WujieFan.exe)。</param>
    private static string BuildTaskXml(string launcherPath)
    {
        // 必须以当前用户身份运行在交互式会话里: 本程序要弹窗口, 用 SYSTEM 或
        // 服务账户启动会看不见界面, 托盘图标也不会出现。
        string user = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
        string escapedExe = System.Security.SecurityElement.Escape(launcherPath) ?? launcherPath;

        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>WujieFan - 无界14+ 风扇转速监控。开机时以普通权限运行启动器, 由启动器经 UAC 提权拉起主程序并驻留托盘。</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{System.Security.SecurityElement.Escape(user)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <!--
                    必须是 LeastPrivilege。任务计划程序拉起的进程在带 UI 限制的 job 里,
                    以最高权限直接跑主程序会让托盘图标注册失败, 且子进程继承该限制。
                    普通权限跑启动器, 再由它 ShellExecuteEx("runas") 经 AppInfo 提权,
                    新进程才不受影响。
                  -->
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{escapedExe}</Command>
                  <Arguments>--tray</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    /// <summary>把子进程的原始字节全部读出来 (不改动流上的编码设置)。</summary>
    private static byte[] ReadAllBytes(Stream s)
    {
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream s)
    {
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms).ConfigureAwait(false);
        return ms.ToArray();
    }

    /// <summary>
    /// 猜测控制台输出的编码并解码。
    ///
    /// schtasks.exe 把 XML 往控制台打时用的编码取决于当前控制台代码页:
    /// 在中文系统上实测同一个命令, 有时是 UTF-8 字节, 有时是 GBK 字节。
    /// 于是先试严格 UTF-8 —— 合法的 UTF-8 几乎不可能是 GBK 中文的巧合;
    /// 解不开再退回系统 ANSI 代码页。
    /// </summary>
    private static string DecodeText(byte[] bytes)
    {
        if (bytes.Length == 0) return "";

        try
        {
            // throwOnInvalidBytes: GBK 中文那串字节按 UTF-8 解通常是非法的,
            // 会在这里抛异常而不是静默变成 "娴嬭瘯"。
            var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return strict.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return GetAnsiEncoding().GetString(bytes);
        }
    }

    /// <summary>系统 ANSI 代码页 (简体中文 = GBK 936)。取不到就退回 UTF-8。</summary>
    private static Encoding GetAnsiEncoding()
    {
        try
        {
            // .NET Core 默认只带 UTF-8/UTF-16/ASCII, 代码页编码要显式注册。
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(0);   // 0 = 系统当前 ANSI 代码页
        }
        catch
        {
            return Encoding.UTF8;
        }
    }
    /// <summary>读计划任务的完整 XML。任务不存在时返回空串。</summary>
    private string QueryTaskXml()
    {
        var r = Run("schtasks.exe", $"/Query /TN \"{_taskName}\" /XML");
        return r.Exit == 0 ? r.Output : "";
    }

    /// <summary>读计划任务的命令行。任务不存在时 exists 为 false。</summary>
    private (bool Exists, string Command) QueryTask()
    {
        string xml = QueryTaskXml();
        if (xml.Length == 0) return (false, "");

        Match m = Regex.Match(xml, @"<Command>(.*?)</Command>", RegexOptions.Singleline);
        string command = m.Success ? m.Groups[1].Value.Trim() : "";
        return (true, command);
    }

    /// <summary>
    /// 判断计划任务里的命令是否属于本程序。
    ///
    /// 注意比对的是启动器 (WujieFanLauncher.exe) 而不是 WujieFan.exe:
    /// 0.2.0 起任务执行的是启动器, 由它再提权拉起主程序。
    /// 若这里仍只认 WujieFan.exe, 我们自己建的任务会被判成"别人的任务",
    /// 于是关不掉、也升级不了。
    ///
    /// 为兼容 0.1.x 遗留任务 (直接指向 WujieFan.exe), 两种都算"我们的"。
    /// </summary>
    private bool IsOurCommand(string command)
    {
        string exe = ExtractExecutable(command);
        if (exe.Length == 0) return false;

        return SamePath(exe, LauncherPath) || SamePath(exe, _exePath);
    }

    private static bool SamePath(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return false;

        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// 从计划任务的命令行里取出可执行文件路径。
    ///
    /// 任务的 &lt;Command&gt; 字段形如:  "E:\...\WujieFan.exe" --tray
    /// 也可能不带引号:                 E:\...\WujieFan.exe
    /// 直接 Trim('"') 只会去掉开头引号, 剩下的尾部引号和参数会被当成路径的一部分,
    /// 所以这里按引号或第一个空格切开。
    /// </summary>
    public static string ExtractExecutable(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return "";

        string s = command.Trim();

        if (s.StartsWith('"'))
        {
            int end = s.IndexOf('"', 1);
            return end > 1 ? s.Substring(1, end - 1) : s.Trim('"');
        }

        // 未加引号: 没有空格时整串就是路径; 有空格时取到 ".exe" 结尾为止
        int exeEnd = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeEnd >= 0) return s.Substring(0, exeEnd + 4);

        int sp = s.IndexOf(' ');
        return sp > 0 ? s.Substring(0, sp) : s;
    }

    private static (int Exit, string Output) Run(string file, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using Process p = Process.Start(psi)!;

            // 两个流必须并发读。先读空 stdout 再读 stderr 会卡死:
            // 子进程把 stderr 缓冲区写满后就不再往下跑, 而我们在等它的 stdout 关闭。
            //
            // 这里按字节读而不是让 .NET 自动解码: schtasks 的输出编码跟着控制台代码页走,
            // 实测同一台机器上时而是 UTF-8 时而是 GBK, 固定按任何一种解都会出错。
            // 拿到原始字节后再自己判断编码 (见 DecodeText)。
            Task<byte[]> errTask = ReadAllBytesAsync(p.StandardError.BaseStream);
            byte[] outBytes = ReadAllBytes(p.StandardOutput.BaseStream);
            byte[] errBytes = errTask.GetAwaiter().GetResult();

            if (!p.WaitForExit(15000))
            {
                // 超时的 schtasks 不能就这么放着, 它会一直占着一个进程句柄。
                try { p.Kill(entireProcessTree: true); } catch { }
                return (-1, "schtasks 超时未返回, 已终止。");
            }

            string all = (DecodeText(outBytes) + "\n" + DecodeText(errBytes)).Trim();
            return (p.ExitCode, all);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
