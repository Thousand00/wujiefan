using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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
///
/// === 安全边界（t11 加固，务必读完再改）===
///
/// 1) 目标程序是编译期固定的同目录 WujieFan.exe，命令行参数不参与"执行谁"的决定。
///    0.4.0 及更早的实现允许 `WujieFanLauncher.exe D:\evil.exe` 直接把任意 exe
///    经 UAC 提权拉起 —— 那是一个现成的提权原语，任何能写计划任务/快捷方式的
///    普通用户都能借它拿到管理员代码执行。该行为已彻底删除，参数只做白名单透传。
///
/// 2) 启动前做一次完整性比对，但它只能"发现"被替换，不能"阻止"被替换。
///    真正的威胁模型是：安装目录可被普通用户写入（"解压即用"），攻击者替换
///    WujieFan.exe 后等用户下次登录。同一个攻击者当然也能同时替换我们的比对清单
///    （它就在旁边），所以哈希清单挡不住这条路径 —— 它拦的是"只有主 exe 被换掉"
///    这一种更粗心的攻击，以及下载/复制过程中的损坏。
///    彻底修复只有两条路：给 exe 做 Authenticode 签名，或把程序装进
///    Program Files 这类普通用户不可写的目录。两者都不在本程序能力范围内。
///
/// 3) 校验失败时的行为：明确警告 + 拒绝自动进入后台驻留，但不硬拒运行。
///    误判（例如用户自己重新编译过、清单没更新）不该让正常用户完全用不了程序，
///    所以给出"仍然手动启动"的出口，只是不再以 --tray 静默常驻。
/// </summary>
internal static class Program
{
    /// <summary>唯一允许被提权拉起的可执行文件。写死在代码里，不接受任何外部输入覆盖。</summary>
    private const string DefaultTargetName = "WujieFan.exe";

    /// <summary>随程序发布的完整性清单文件名（与两个 exe 同目录）。</summary>
    private const string PinFileName = "WujieFan.integrity.json";

    /// <summary>
    /// 必须被清单覆盖的同伴文件。注意判定条件是「磁盘上存在就要求清单记录它」：
    /// 便携包是单文件发布，WujieFan.Core.dll 被并进了 WujieFan.exe，磁盘上并没有
    /// 这个文件；若在这里无条件要求，每个便携包用户开机都会收到一次误报。
    /// </summary>
    private static readonly string[] PinnedFiles = { "WujieFan.exe", "WujieFan.Core.dll" };

    /// <summary>
    /// 允许透传给主程序的参数白名单。主程序目前只认 --tray。
    /// 不做白名单就等于把未过滤的命令行送进一个管理员进程。
    /// </summary>
    private static readonly string[] KnownArgs = { "--tray" };

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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private const uint MB_OK = 0x00000000;
    private const uint MB_YESNO = 0x00000004;
    private const uint MB_ICONWARNING = 0x00000030;
    private const uint MB_ICONERROR = 0x00000010;
    private const uint MB_DEFBUTTON2 = 0x00000100;
    private const uint MB_TOPMOST = 0x00040000;
    private const int IDYES = 6;

    /// <summary>完整性比对的结论。</summary>
    private enum Integrity
    {
        /// <summary>清单存在且所有文件都匹配。</summary>
        Verified,

        /// <summary>清单存在，但有文件哈希/大小对不上，或有文件缺失。</summary>
        Mismatch,

        /// <summary>清单不存在 —— 无法判断，只能如实说"没校验"。</summary>
        PinsMissing,

        /// <summary>清单存在但读不懂（损坏 / 结构不认识）。</summary>
        PinsInvalid,

        /// <summary>校验过程本身出错（权限 / IO）。</summary>
        CheckError,
    }

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

        // 目标固定为同目录的 WujieFan.exe —— 参数不再参与决定"执行谁"。
        // 用 GetFullPath 归一化后再比对目录，防止 baseDir 里带 ".." 之类的意外形态。
        baseDir = Path.GetFullPath(baseDir);
        string target = Path.GetFullPath(Path.Combine(baseDir, DefaultTargetName));

        if (!string.Equals(Path.GetDirectoryName(target), baseDir.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            Log("target escaped base dir: " + target);
            return 2;
        }

        // 参数只按白名单透传。未知参数丢弃并留痕：主程序不认它们，
        // 但把它们原样交给一个管理员进程就是在放大攻击面。
        string[] forwarded = FilterArgs(args, out string[] dropped);
        if (dropped.Length > 0)
        {
            Log("dropped unknown args: [" + string.Join(" ", dropped) + "]");
        }

        if (!File.Exists(target))
        {
            Log("target missing: " + target);
            return 2;
        }

        Integrity status = VerifyIntegrity(baseDir, out string detail);
        bool wantBackground = forwarded.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));

        if (status != Integrity.Verified)
        {
            Log("integrity=" + status + " " + detail);
        }

        // 拦截分两档, 区别在于"要不要打扰用户":
        //
        //   确认不一致 (Mismatch / PinsInvalid) → 拦下来并弹窗问用户;
        //   没能力判断 (PinsMissing / CheckError) → 也拦下来, 但不弹窗, 只记日志。
        //
        // 第二档以前是"什么都不做"。那样一来, 只要把清单文件删掉 (或让它读不出来),
        // 程序就能照常以 --tray 静默常驻后台 —— 完整性校验等于提供了一个
        // "删一个文件即可完全绕过"的开关。默认不弹窗是为了不打扰开发构建,
        // 但"不打扰"不等于"放行": 无法核实就退回前台可见启动, 不能悄悄常驻。
        bool suspicious = status is Integrity.Mismatch or Integrity.PinsInvalid;

        if (status != Integrity.Verified && wantBackground)
        {
            forwarded = forwarded.Where(a => !a.Equals("--tray", StringComparison.OrdinalIgnoreCase)).ToArray();
            Log("background start refused: integrity=" + status + " " + detail);
        }

        if (suspicious)
        {
            string message =
                "WujieFan 的程序文件与随包发布的完整性清单不一致。\r\n\r\n" +
                detail + "\r\n\r\n" +
                "这可能意味着文件被替换或损坏。启动器已拒绝让它在后台静默常驻。\r\n\r\n" +
                "是否仍然手动启动？(选择\"是\"表示你了解风险并自行承担)";

            int answer = MessageBoxW(IntPtr.Zero, message, "WujieFan 启动检查",
                MB_YESNO | MB_ICONWARNING | MB_DEFBUTTON2 | MB_TOPMOST);

            if (answer != IDYES)
            {
                Log("user declined manual start after integrity warning");
                return 4;
            }
        }

        bool elevated = IsElevated();
        Log("pid=" + Environment.ProcessId + " elevated=" + elevated + " target=" + target
            + " args=[" + string.Join(" ", forwarded) + "]");

        // 正常情况下本进程是普通权限 (任务以 Limited 运行), runas 会真正经过 AppInfo,
        // 弹一次 UAC 后拉起管理员进程, 新进程不继承受限 job。
        string? error = LaunchElevated(target, forwarded, Path.GetDirectoryName(target) ?? baseDir, out bool launched);

        if (launched)
        {
            Log("launched via AppInfo");
            return 0;
        }

        Log("runas failed: " + error);
        return 3;
    }

    /// <summary>
    /// 按白名单过滤参数。比较不区分大小写，保留原有顺序。
    /// 任何不在白名单里的参数都被丢弃 —— 包括看起来像 exe 路径的第一个参数。
    /// </summary>
    private static string[] FilterArgs(string[] args, out string[] dropped)
    {
        if (args.Length == 0)
        {
            dropped = Array.Empty<string>();
            return Array.Empty<string>();
        }

        var kept = new List<string>(args.Length);
        var lost = new List<string>();
        foreach (string a in args)
        {
            if (KnownArgs.Any(k => k.Equals(a, StringComparison.OrdinalIgnoreCase)))
            {
                kept.Add(a);
            }
            else
            {
                lost.Add(a);
            }
        }

        dropped = lost.ToArray();
        return kept.ToArray();
    }

    /// <summary>
    /// 把当前目录里的关键文件与 <see cref="PinFileName"/> 里记录的 SHA-256 + 长度比对。
    ///
    /// 注意（不要被这段代码误导）：清单和 exe 放在同一个可写目录里，能被替换 exe 的人
    /// 一起替换或直接删除。本函数的定位是"检测"而不是"防御"，它抬高了攻击成本，
    /// 但没有消除 t2-P0-1 那条路径。真正的修复是 Authenticode 签名或受保护安装目录。
    /// </summary>
    private static Integrity VerifyIntegrity(string baseDir, out string detail)
    {
        string pinPath = Path.Combine(baseDir, PinFileName);

        if (!File.Exists(pinPath))
        {
            detail = "未找到完整性清单 " + PinFileName + "，本次启动未做校验。";
            return Integrity.PinsMissing;
        }

        Dictionary<string, (string Hash, long Length)> expected;
        try
        {
            expected = ReadPins(pinPath);
        }
        catch (Exception ex)
        {
            detail = "完整性清单无法解析: " + ex.Message;
            return Integrity.PinsInvalid;
        }

        if (expected.Count == 0)
        {
            detail = "完整性清单为空。";
            return Integrity.PinsInvalid;
        }

        var problems = new List<string>();
        int compared = 0;

        foreach (string name in PinnedFiles)
        {
            string path = Path.Combine(baseDir, name);
            bool onDisk = File.Exists(path);
            bool inPins = expected.TryGetValue(name, out var want);

            if (!onDisk && !inPins)
            {
                // 单文件发布 (便携包) 的正常形态: 磁盘上没有这个组件, 因为它被并进
                // 主 exe 了, 清单里自然也没有。跳过, 不算问题。
                continue;
            }

            if (!onDisk)
            {
                // 清单记录了这个文件, 磁盘上没有 —— 被删了, 或者包不完整。
                problems.Add(name + ": 文件不存在");
                continue;
            }

            if (!inPins)
            {
                // 磁盘上有, 清单里没有 —— 清单与本包不配套, 按不可信处理。
                problems.Add(name + ": 清单中缺少该文件的记录");
                continue;
            }

            try
            {
                var info = new FileInfo(path);
                if (info.Length != want.Length)
                {
                    problems.Add($"{name}: 大小不符 (实际 {info.Length} / 期望 {want.Length})");
                    continue;
                }

                string actual = Sha256Hex(path);
                if (!string.Equals(actual, want.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add(name + ": 内容哈希不符");
                }

                compared++;
            }
            catch (Exception ex)
            {
                detail = name + ": 无法读取 (" + ex.Message + ")";
                return Integrity.CheckError;
            }
        }

        if (compared == 0)
        {
            // 一个文件都没有真正比对过 (清单与磁盘没有交集), 不能报"已校验",
            // 也不该报成"内容不符"——那是另一回事, 会误导用户去查篡改。
            detail = "清单与磁盘上的文件没有交集, 无法校验。";
            return Integrity.PinsInvalid;
        }

        if (problems.Count > 0)
        {
            detail = string.Join("\r\n", problems);
            return Integrity.Mismatch;
        }

        detail = compared + " 个文件已校验";
        return Integrity.Verified;
    }

    private static Dictionary<string, (string Hash, long Length)> ReadPins(string path)
    {
        var result = new Dictionary<string, (string, long)>(StringComparer.OrdinalIgnoreCase);

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("files", out JsonElement files)
            || files.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("缺少 files 节点");
        }

        foreach (JsonProperty prop in files.EnumerateObject())
        {
            string hash = prop.Value.TryGetProperty("sha256", out JsonElement h) ? h.GetString() ?? "" : "";
            long length = prop.Value.TryGetProperty("length", out JsonElement l) && l.TryGetInt64(out long v) ? v : -1;

            if (hash.Length != 64 || length < 0)
            {
                throw new InvalidDataException("条目格式不正确: " + prop.Name);
            }

            result[prop.Name] = (hash, length);
        }

        return result;
    }

    private static string Sha256Hex(string path)
    {
        // FileShare.Read: 不挡住正在运行的实例, 也不影响我们拿到一致的快照
        // (文件被替换时句柄仍指向旧内容, 哈希不会读到半新半旧的数据)。
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
        using var sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(fs);
        return Convert.ToHexString(hash);
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
