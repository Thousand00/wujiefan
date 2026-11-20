using System.Runtime.InteropServices;
using System.Windows.Forms;
using WujieFan.Core.Config;

namespace WujieFan.Ui;

/// <summary>
/// 一个全局热键的宿主: 向系统注册 / 注销, 按下时回调。
///
/// 为什么单独搞一个隐藏窗口而不是把热键挂在主窗口上:
/// RegisterHotKey 把 WM_HOTKEY 投递到指定窗口的窗口过程, 而 WinForms 的一个
/// HWND 只对应一个窗口过程 —— 想截主窗口的消息就得接管它的 WndProc, 那会连带
/// 影响标题栏拖拽、圆角等一堆既有逻辑。这里用一个从不显示的窗口专门收热键,
/// 与主窗口彻底解耦, 主窗口隐藏到托盘、正在显示模态对话框都不影响注册状态。
///
/// 热键注册是"按线程"的: 必须在创建窗口的那个线程 (UI 线程) 上注册与注销,
/// 所以本类的方法都只能在 UI 线程调用。
/// </summary>
internal sealed class GlobalHotKey : IDisposable
{
    private const int WM_HOTKEY = 0x0312;

    /// <summary>本程序只用一个热键, 固定 id 即可; 不同程序之间 id 不冲突。</summary>
    private const int HotKeyId = 0x5746; // 'W' 'F'

    /// <summary>按住不放时系统只会投递一次, 避免长按把强冷反复开关。</summary>
    private const uint ModNoRepeat = 0x4000;

    private const int ErrorHotKeyAlreadyRegistered = 1409;

    private readonly HiddenMessageWindow _window;
    private HotKeyCombo? _current;
    private bool _disposed;

    public GlobalHotKey()
    {
        _window = new HiddenMessageWindow(OnHotKeyMessage);
    }

    /// <summary>当前真正注册成功的组合键; null 表示没有注册。</summary>
    public HotKeyCombo? Current => _current;

    /// <summary>热键被按下。在 UI 线程上触发。</summary>
    public event EventHandler? Pressed;

    /// <summary>
    /// 换成新的组合键。传 null / 空组合 = 只注销, 不注册 (不算失败)。
    /// 失败时 error 是给用户看的原因, 且保证旧热键已经被注销 —— 不会出现
    /// "界面上写着 A, 实际注册的还是 B"。
    /// </summary>
    public bool TryRegister(HotKeyCombo? combo, out string? error)
    {
        error = null;
        Unregister();

        if (combo is null || combo.VirtualKey == 0) return true;

        string? problem = HotKeyCombo.DescribeProblem(combo);
        if (problem is not null)
        {
            error = problem;
            return false;
        }

        if (!RegisterHotKey(_window.Handle, HotKeyId,
                (uint)combo.Modifiers | ModNoRepeat, (uint)combo.VirtualKey))
        {
            int code = Marshal.GetLastWin32Error();
            error = code == ErrorHotKeyAlreadyRegistered
                ? $"{combo.ToSettingString()} 已被其他程序占用, 换一个组合键试试。"
                : $"注册 {combo.ToSettingString()} 失败 (系统错误 {code})。";
            return false;
        }

        _current = combo;
        return true;
    }

    public void Unregister()
    {
        if (_current is null) return;
        try { UnregisterHotKey(_window.Handle, HotKeyId); }
        catch { /* 窗口已经没了就当注销成功, 进程退出时系统也会回收 */ }
        _current = null;
    }

    private void OnHotKeyMessage()
    {
        if (_disposed) return;
        Pressed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Unregister();
        _window.Destroy();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    /// <summary>
    /// 只用来收 WM_HOTKEY 的窗口: 从不 Show, 所以看不见、不进任务栏。
    ///
    /// 这里刻意**不**指定 ClassName。写成 "STATIC" 之类的系统预定义类会踩一个
    /// 隐蔽的坑: WinForms 的 NativeWindow 会去 RegisterClass 这个名字, 而系统类
    /// 早已注册过, 于是抛 Win32Exception(1410 类已存在) —— 且异常发生在主窗口
    /// 构造期间, 整个程序启动即崩。留空则 WinForms 自己生成一个唯一类名。
    /// </summary>
    private sealed class HiddenMessageWindow : NativeWindow
    {
        private readonly Action _onHotKey;

        public HiddenMessageWindow(Action onHotKey)
        {
            _onHotKey = onHotKey;
            CreateHandle(new CreateParams
            {
                Caption = "WujieFan.GlobalHotKey",
                Style = 0,
                X = 0,
                Y = 0,
                Width = 0,
                Height = 0,
            });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotKeyId)
            {
                _onHotKey();
                return;
            }

            base.WndProc(ref m);
        }

        public void Destroy()
        {
            if (Handle != IntPtr.Zero) DestroyHandle();
        }
    }
}
