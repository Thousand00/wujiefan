using System.Text;

namespace WujieFan.Core.Config;

/// <summary>
/// 组合键里的修饰键。取值与 Win32 RegisterHotKey 的 MOD_* 完全一致,
/// 界面层可以直接强转, 不必再维护第二张映射表 —— 两张表迟早会不一致。
/// </summary>
[Flags]
public enum HotKeyModifiers
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008,
}

/// <summary>
/// 一个全局组合键: 若干修饰键 + 一个真正的按键。
///
/// 放在 Core 而不是主程序, 是因为它同时被三处需要: 配置文件的读写与归一化、
/// 设置弹窗的录入控件、以及注册热键的那层。Core 不能引用 WinForms (枚举 Keys 在那里),
/// 所以这里只认 Win32 虚拟键码 (0x41 = 'A', 0x70 = F1), 由界面层自己换算。
///
/// 序列化用字符串 ("Ctrl+Alt+F9") 而不是结构体: 配置文件是明文、用户会手改,
/// 一个可读的字符串比 {"Modifiers":3,"VirtualKey":120} 更容易看懂, 也更容易改坏 ——
/// 所以 <see cref="TryParse"/> 必须把"改坏了"当成正常情况处理, 而不是抛异常。
/// </summary>
public sealed class HotKeyCombo : IEquatable<HotKeyCombo>
{
    /// <summary>修饰键组合, 至少要有一个。</summary>
    public HotKeyModifiers Modifiers { get; init; }

    /// <summary>按键的 Win32 虚拟键码。0 表示还没选键。</summary>
    public int VirtualKey { get; init; }

    /// <summary>
    /// 修饰键的规范写法, 顺序固定 (Ctrl、Alt、Shift、Win), 结尾带 '+'; 没有修饰键时是空串。
    /// 录制过程中只需要显示"已经按住了哪些修饰键", 所以单独拿出来给界面用。
    /// </summary>
    public static string ModifiersToString(HotKeyModifiers modifiers)
    {
        var sb = new StringBuilder();
        if ((modifiers & HotKeyModifiers.Control) != 0) sb.Append("Ctrl+");
        if ((modifiers & HotKeyModifiers.Alt) != 0) sb.Append("Alt+");
        if ((modifiers & HotKeyModifiers.Shift) != 0) sb.Append("Shift+");
        if ((modifiers & HotKeyModifiers.Win) != 0) sb.Append("Win+");
        return sb.ToString();
    }

    /// <summary>规范写法, 例如 "Ctrl+Alt+F9"。修饰键顺序固定, 便于比较与去重。</summary>
    public string ToSettingString()
    {
        if (VirtualKey == 0) return "";
        return ModifiersToString(Modifiers) + KeyName(VirtualKey);
    }

    public override string ToString() => ToSettingString();

    /// <summary>
    /// 解析配置里的字符串。
    ///
    /// 返回值表示"这段文本能不能被接受", 而不是"解析出了组合键":
    ///   null / 空白  -> true, combo = null (没配, 这是合法状态);
    ///   合法写法     -> true, combo = 解析结果;
    ///   改坏了       -> false, combo = null (调用方应当把它当成"没配"并记一次归一化)。
    ///
    /// 大小写不敏感, 所以 "ctrl+alt+f9" 与 "Ctrl+Alt+F9" 是同一个键 —— 归一化时
    /// 统一改写成本类的规范写法, 免得配置文件里两种写法各存一份。
    /// </summary>
    public static bool TryParse(string? text, out HotKeyCombo? combo)
    {
        combo = null;
        if (string.IsNullOrWhiteSpace(text)) return true;

        HotKeyModifiers mods = HotKeyModifiers.None;
        int vk = 0;
        bool sawKey = false;

        foreach (string raw in text.Split('+'))
        {
            string token = raw.Trim();
            if (token.Length == 0) return false;

            // 修饰键只能在按键之前出现 —— "F9+Ctrl" 这种写法是人写错了, 不猜。
            if (!sawKey && TryReadModifier(token, out HotKeyModifiers modifier))
            {
                if ((mods & modifier) != 0) return false;
                mods |= modifier;
                continue;
            }

            if (sawKey) return false;
            if (!TryReadKey(token, out vk)) return false;
            sawKey = true;
        }

        // 只按修饰键不算一个组合键; 没有修饰键则会抢走用户正常打字, 也不接受。
        if (!sawKey || mods == HotKeyModifiers.None) return false;

        combo = new HotKeyCombo { Modifiers = mods, VirtualKey = vk };
        return true;
    }

    /// <summary>
    /// 这个组合键能不能真的注册成全局热键。返回 null 表示可以, 否则是给用户看的原因。
    ///
    /// 这里只列系统自己占用、注册必然失败或者会顶掉系统功能的几个键。其余冲突
    /// (被别的程序占了) 只能等 RegisterHotKey 真的返回失败才知道, 那时再报。
    /// </summary>
    public static string? DescribeProblem(HotKeyCombo combo)
    {
        if (combo.VirtualKey == 0) return "还没按到具体的键。";
        if (combo.Modifiers == HotKeyModifiers.None)
            return "至少要按住 Ctrl / Alt / Shift / Win 中的一个, 否则会抢走正常打字。";

        bool alt = (combo.Modifiers & HotKeyModifiers.Alt) != 0;
        bool ctrl = (combo.Modifiers & HotKeyModifiers.Control) != 0;
        bool win = (combo.Modifiers & HotKeyModifiers.Win) != 0;
        int vk = combo.VirtualKey;

        if (alt && vk == 0x73) return "Alt+F4 是关闭窗口的系统键, 不能占用。";
        if (alt && vk == 0x09) return "Alt+Tab 是切换窗口的系统键, 不能占用。";
        if (alt && vk == 0x1B) return "Alt+Esc 是切换窗口的系统键, 不能占用。";
        if (alt && vk == 0x0D) return "Alt+Enter 会打开属性窗口, 不能占用。";
        if (ctrl && vk == 0x1B) return "Ctrl+Esc 是打开开始菜单的系统键, 不能占用。";
        if (win && vk == 0x4C) return "Win+L 是锁屏键, 系统不允许任何程序占用。";
        if (win && vk == 0x09) return "Win+Tab 是任务视图的系统键, 不能占用。";

        return null;
    }

    /// <summary>界面层需要把虚拟键码显示出来时用; 表里没有的键退回十六进制写法。</summary>
    public static string KeyName(int virtualKey) =>
        s_vkToName.TryGetValue(virtualKey, out string? name) ? name : "0x" + virtualKey.ToString("X2");

    /// <summary>
    /// 这个键能不能被序列化成可解析的名字。
    ///
    /// 白名单外的键 (PrintScreen / CapsLock / Pause / 小键盘 +/- ...) <see cref="KeyName"/>
    /// 只能退回 "0x2C" 这种写法, 而 <see cref="TryParse"/> 原本不认它 —— 于是会出现
    /// "录制成功 → 当次不生效 → 下次启动被当成手改坏了清空" 的静默数据丢失。
    /// 现在解析侧已经能认回 "0xXX", 两边等价; 这个方法留给界面做"先说清楚"的入口。
    /// </summary>
    public static bool IsRepresentable(int virtualKey) =>
        virtualKey != 0 && (s_vkToName.ContainsKey(virtualKey) || (virtualKey > 0 && virtualKey <= 0xFF));

    /// <summary>
    /// 按键名或十六进制写法解析出虚拟键码。
    ///
    /// 认 "0x2C" 是为了让 <see cref="ToSettingString"/> 与 <see cref="TryParse"/> 往返恒等:
    /// 白名单外的键只能写成十六进制, 解析器若不认, 存进去的字符串下次就会被判成坏值。
    /// </summary>
    private static bool TryReadKey(string token, out int virtualKey)
    {
        virtualKey = 0;
        if (s_nameToVk.TryGetValue(token, out virtualKey)) return true;

        if (token.Length is < 3 or > 4) return false;
        if (token[0] != '0' || (token[1] != 'x' && token[1] != 'X')) return false;

        if (!int.TryParse(token.AsSpan(2), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out int vk))
            return false;

        if (vk <= 0 || vk > 0xFF) return false;
        virtualKey = vk;
        return true;
    }

    private static bool TryReadModifier(string token, out HotKeyModifiers modifier)
    {
        switch (token.ToUpperInvariant())
        {
            case "CTRL":
            case "CONTROL":
                modifier = HotKeyModifiers.Control;
                return true;
            case "ALT":
                modifier = HotKeyModifiers.Alt;
                return true;
            case "SHIFT":
                modifier = HotKeyModifiers.Shift;
                return true;
            case "WIN":
            case "WINDOWS":
            case "META":
                modifier = HotKeyModifiers.Win;
                return true;
            default:
                modifier = HotKeyModifiers.None;
                return false;
        }
    }

    private static readonly Dictionary<string, int> s_nameToVk = BuildNameToVk();

    private static readonly Dictionary<int, string> s_vkToName =
        s_nameToVk.ToDictionary(pair => pair.Value, pair => pair.Key);

    /// <summary>
    /// 可录入的按键白名单。刻意不含 Esc (它是录制的取消手势), 也不含鼠标键
    /// (RegisterHotKey 本来就只认键盘)。Num+ / Num- 用 "NumPlus" / "NumMinus"
    /// 这两个名字, 不写成 "+" / "-": 后两者与解析器的分隔符撞车。
    ///
    /// 白名单之外的键并不是"不能用", 而是只能存成 "0x2C" 这种十六进制写法。
    /// 解析器认这种写法, 所以往返仍然恒等 —— 只是界面上的名字不好看。
    /// 常见键 (PrintScreen / CapsLock / Pause / 小键盘加减等) 都已补进表里。
    /// </summary>
    private static Dictionary<string, int> BuildNameToVk()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < 24; i++) map["F" + (i + 1)] = 0x70 + i;
        for (char c = 'A'; c <= 'Z'; c++) map[c.ToString()] = c;
        for (char c = '0'; c <= '9'; c++) map[c.ToString()] = c;
        for (int i = 0; i <= 9; i++) map["Num" + i] = 0x60 + i;

        map["Space"] = 0x20;
        map["Tab"] = 0x09;
        map["Enter"] = 0x0D;
        map["Backspace"] = 0x08;
        map["Insert"] = 0x2D;
        map["Delete"] = 0x2E;
        map["Home"] = 0x24;
        map["End"] = 0x23;
        map["PageUp"] = 0x21;
        map["PageDown"] = 0x22;
        map["Left"] = 0x25;
        map["Up"] = 0x26;
        map["Right"] = 0x27;
        map["Down"] = 0x28;

        map["`"] = 0xC0;
        map["-"] = 0xBD;
        map["="] = 0xBB;
        map["["] = 0xDB;
        map["]"] = 0xDD;
        map["\\"] = 0xDC;
        map[";"] = 0xBA;
        map["'"] = 0xDE;
        map[","] = 0xBC;
        map["."] = 0xBE;
        map["/"] = 0xBF;

        map["Num*"] = 0x6A;
        map["Num/"] = 0x6F;
        map["Num."] = 0x6E;

        // 这几个键原本不在表里, 只能退回 "0x2C" 这种十六进制写法 —— 界面显示成
        // "Ctrl+0x2C" 既看不懂也不可信。它们都是正常的可组合键, 补上名字即可。
        // NumPlus / NumMinus 用全名而不是 "+" / "-": 后两者与分隔符撞车。
        map["PrintScreen"] = 0x2C;
        map["CapsLock"] = 0x14;
        map["NumLock"] = 0x90;
        map["ScrollLock"] = 0x91;
        map["Pause"] = 0x13;
        map["Apps"] = 0x5D;
        map["NumPlus"] = 0x6B;
        map["NumMinus"] = 0x6D;

        return map;
    }

    public bool Equals(HotKeyCombo? other) =>
        other is not null && other.Modifiers == Modifiers && other.VirtualKey == VirtualKey;

    public override bool Equals(object? obj) => Equals(obj as HotKeyCombo);

    public override int GetHashCode() => HashCode.Combine((int)Modifiers, VirtualKey);
}
