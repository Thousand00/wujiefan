using WujieFan.Ui;

namespace WujieFan;

internal static class Program
{
    /// <summary>单实例互斥量。程序会在开机自启动与手动启动之间重复拉起, 必须只留一个。</summary>
    private const string SingleInstanceMutexName = @"Global\WujieFan_SingleInstance";

    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(true, SingleInstanceMutexName, out bool isFirst);

        if (!isFirst)
        {
            // 已经有实例在跑: 唤醒它并把窗口带到前台, 自己退出。
            NativeMethods.PostMessage(
                (IntPtr)NativeMethods.HWND_BROADCAST,
                SingleInstanceMessage,
                IntPtr.Zero, IntPtr.Zero);
            return;
        }

        ApplicationConfiguration.Initialize();

        bool minimize = args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));

        Application.Run(new MainForm(minimize));
    }

    /// <summary>自定义窗体消息, 用于把已有实例的窗口拽到前面。</summary>
    public static readonly int SingleInstanceMessage =
        NativeMethods.RegisterWindowMessage("WujieFan_ShowWindow");

    private static class NativeMethods
    {
        public const int HWND_BROADCAST = 0xFFFF;

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        public static extern int RegisterWindowMessage(string lpString);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    }
}
