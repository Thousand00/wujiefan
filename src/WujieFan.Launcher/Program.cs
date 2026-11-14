using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WujieFan.Launcher;

/// <summary>
/// 开机自启动的"中间人"。
///
/// 存在理由（一句话）：任务计划程序拉起的进程会被放进带 UI 限制的 job 对象，
/// 其中的 Shell_NotifyIcon(NIM_ADD) 直接返回 ERROR_ACCESS_DENIED(5)，托盘图标
/// 因此永远不出现；而这条限制会被子进程继承。
///
/// 于是启动链拆成两跳：
///   计划任务(普通权限) -> WujieFanLauncher.exe(普通权限)
///   -> ShellExecuteEx("runas") -> 经 AppInfo 提权 -> WujieFan.exe(管理员)
/// 只有 AppInfo 新建的进程才不会继承那个受限 job，托盘才正常。
///
/// 反面教材（实测）：让计划任务直接以最高权限运行 WujieFan.exe，或者让一个
/// 已提权的进程再调 runas —— 后者会被 UAC 判成同级别、退化成普通 CreateProcess，
/// 子进程照样继承 job，托盘依旧不出现。
///
/// 本程序不读 EC、不碰任何硬件寄存器，只做一次进程创建。
/// </summary>
internal static class Program
{
    private const string DefaultTargetName = "WujieFan.exe";
    private const string LogFileName = "launcher.log";
    private const long LogMaxBytes = 64 * 1024;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public string? lpVerb;
        public string? lpFile;
        public string? lpParameters;
        public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIconOrMonitor;
        public IntPtr hProcess;
    }

    private const uint SEE_MASK_NOCLOSEPROCESS = 0x00000040;
    private const uint SEE_MASK_NOASYNC = 0x00000100;
    private const int SW_SHOWNORMAL = 1;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO info);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            Log("FATAL " + ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        string baseDir = AppContext.BaseDirectory;

        // 第一个参数若是现存的可执行文件, 就当作目标; 否则目标固定为同目录的 WujieFan.exe。
        // 这样测试时可以把任意 exe 交给本启动器, 产品运行时则完全不需要传目标。
        string target;
        string[] targetArgs;

        if (args.Length > 0 && args[0].EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && File.Exists(ResolvePath(baseDir, args[0])))
        {
            target = ResolvePath(baseDir, args[0]);
            targetArgs = args.Skip(1).ToArray();
        }
        else
        {
            target = Path.Combine(baseDir, DefaultTargetName);
            targetArgs = args;
        }

        if (!File.Exists(target))
        {
            Log("target missing: " + target);
            return 2;
        }

        bool elevated = IsElevated();
        Log("pid=" + Environment.ProcessId + " elevated=" + elevated + " target=" + target
            + " args=[" + string.Join(" ", targetArgs) + "]");

        // 正常情况下本进程是普通权限 (任务以 Limited 运行), runas 会真正经过 AppInfo,
        // 弹一次 UAC 后拉起管理员进程, 新进程不继承受限 job。
        string? error = LaunchElevated(target, targetArgs, Path.GetDirectoryName(target) ?? baseDir, out bool launched);

        if (launched)
        {
            Log("launched via AppInfo");
            return 0;
        }

        Log("runas failed: " + error);
        return 3;
    }

    /// <summary>用 runas 拉起目标程序。返回 null 表示成功; 否则返回错误描述。</summary>
    private static string? LaunchElevated(string file, string[] args, string workDir, out bool launched)
    {
        var sei = new SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
            // SEE_MASK_NOASYNC: 调用返回后立刻释放本进程, 不让 shell 在后台等我们。
            fMask = SEE_MASK_NOCLOSEPROCESS | SEE_MASK_NOASYNC,
            lpVerb = "runas",
            lpFile = file,
            lpParameters = args.Length == 0 ? null : string.Join(" ", args.Select(QuoteIfNeeded)),
            lpDirectory = workDir,
            nShow = SW_SHOWNORMAL,
        };

        bool ok = ShellExecuteEx(ref sei);
        int err = Marshal.GetLastWin32Error();

        if (sei.hProcess != IntPtr.Zero)
        {
            CloseHandle(sei.hProcess);
        }

        launched = ok;
        if (ok) return null;

        // 1223 = ERROR_CANCELLED (用户在 UAC 对话框点了"否")
        return err == 1223 ? "cancelled by user (UAC)" : "Win32 error " + err;
    }

    private static string QuoteIfNeeded(string s) =>
        s.Length > 0 && s.IndexOf(' ') >= 0 ? "\"" + s + "\"" : s;

    private static string ResolvePath(string baseDir, string path) =>
        Path.IsPathRooted(path) ? path : Path.Combine(baseDir, path);

    private static bool IsElevated()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 写诊断日志。这个启动器失败时用户只会看到"没反应", 没有日志根本没法排查。
    /// 日志超过 64KB 就整体丢弃重来, 避免长年累月堆积。
    /// </summary>
    private static void Log(string message)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WujieFan");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, LogFileName);

            var info = new FileInfo(path);
            if (info.Exists && info.Length > LogMaxBytes)
            {
                info.Delete();
            }

            File.AppendAllText(path,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + message + Environment.NewLine,
                new UTF8Encoding(false));
        }
        catch
        {
            // 日志写不出来不能影响启动。
        }
    }
}
