using System.Diagnostics;
using System.Text.RegularExpressions;

namespace WujieFan.Core.Config;

/// <summary>
/// 管理"开机自启动"开关。
///
/// 实现方式是用计划任务 (WujieFan) 而不是注册表 Run 键, 原因:
///   1. 本程序需要管理员权限, 注册表 Run 键在 UAC 下会被系统拦截不提升,
///      结果是开机后弹不出界面、也读不到风扇。计划任务可以指定"最高权限运行"。
///   2. 计划任务对用户可见 (任务计划程序里能看到), 容易检查和删除, 比注册表透明。
///
/// 安全约定: 只操作名为 WujieFan 的计划任务; 若同名任务已存在但指向别的程序,
/// 一律拒绝覆盖, 并且不删除它。
/// </summary>
public sealed class AutoStartManager
{
    public const string TaskName = "WujieFan";

    private readonly string _exePath;

    public AutoStartManager(string? exePath = null)
    {
        _exePath = exePath ?? Environment.ProcessPath ?? "";
    }

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

    /// <summary>创建计划任务, 登录时以最高权限启动本程序。</summary>
    public Result Enable()
    {
        if (string.IsNullOrEmpty(_exePath))
            return new Result(false, "无法确定本程序路径, 不能创建自启动项。");

        var (exists, command) = QueryTask();

        if (exists && !IsOurCommand(command))
            return new Result(false,
                $"计划任务「{TaskName}」已存在, 但指向别的程序:\n{command}\n" +
                "为避免覆盖别人的设置, 这里不做任何修改。请先在任务计划程序里手动处理该任务。");

        if (exists && IsOurCommand(command))
            return new Result(true, $"已启用 (计划任务「{TaskName}」已存在)。");

        var r = Run("schtasks.exe",
            $"/Create /TN \"{TaskName}\" /TR \"\"{_exePath}\" --tray\" /SC ONLOGON /RL HIGHEST /F");

        if (r.Exit != 0)
            return new Result(false, $"创建计划任务失败 (退出码 {r.Exit}):\n{r.Output}");

        return new Result(true, $"已启用: 登录 Windows 时自动以管理员身份启动。\n计划任务名称: {TaskName}");
    }

    /// <summary>删除计划任务。仅当它属于本程序时才删。</summary>
    public Result Disable()
    {
        var (exists, command) = QueryTask();

        if (!exists)
            return new Result(true, "已关闭 (本来就没有自启动项)。");

        if (!IsOurCommand(command))
            return new Result(false,
                $"计划任务「{TaskName}」指向别的程序:\n{command}\n" +
                "为避免误删别人的设置, 这里不做任何修改。");

        var r = Run("schtasks.exe", $"/Delete /TN \"{TaskName}\" /F");

        if (r.Exit != 0)
            return new Result(false, $"删除计划任务失败 (退出码 {r.Exit}):\n{r.Output}");

        return new Result(true, "已关闭开机自启动。");
    }

    /// <summary>读计划任务的命令行。任务不存在时 exists 为 false。</summary>
    private (bool Exists, string Command) QueryTask()
    {
        var r = Run("schtasks.exe", $"/Query /TN \"{TaskName}\" /XML");

        if (r.Exit != 0) return (false, "");

        string xml = r.Output;
        Match m = Regex.Match(xml, @"<Command>(.*?)</Command>", RegexOptions.Singleline);
        string command = m.Success ? m.Groups[1].Value.Trim() : "";
        return (true, command);
    }

    /// <summary>
    /// 判断计划任务里的命令是否就是本程序。
    /// 只比较文件名与路径, 容忍引号和大小写差异; 任务里带了 --tray 参数也算。
    /// </summary>
    private bool IsOurCommand(string command)
    {
        string exe = ExtractExecutable(command);
        if (exe.Length == 0) return false;

        try
        {
            return string.Equals(
                Path.GetFullPath(exe),
                Path.GetFullPath(_exePath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(exe, _exePath, StringComparison.OrdinalIgnoreCase);
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
            string outText = p.StandardOutput.ReadToEnd();
            string errText = p.StandardError.ReadToEnd();
            p.WaitForExit(15000);

            string all = (outText + "\n" + errText).Trim();
            return (p.HasExited ? p.ExitCode : -1, all);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
