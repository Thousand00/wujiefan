namespace WujieFan.Core.Hardware;

/// <summary>
/// 已验证机型白名单与配套的 EC 固件版本。
///
/// 为什么必须有这一层:
/// 本程序读的风扇转速寄存器 (0x181E/0x1820 计数、0x1809/0x1804 PWM、0x0470 温度)
/// 和换算常数 (TachConstant = 2156250) 全部是**特定型号 + 特定 EC 固件**逆向出来的。
/// 换一台机器读同样的地址, 拿到的要么是别的含义的字段, 要么是空洞, 会显示出
/// 看起来合理但完全错误的风扇转速。所以身份不符时必须拒绝运行, 而不是"尽力而为"。
/// </summary>
public static class SupportedHardware
{
    /// <summary>已验证的机型。</summary>
    public sealed record Profile(
        string Manufacturer,
        string[] ProductNames,
        string[] BaseBoardProducts,
        int EcMajor,
        int EcMinor,
        string Note);

    public static readonly Profile[] Known =
    {
        new(
            Manufacturer: "MECHREVO",
            ProductNames: new[] { "WUJIE 14", "WUJIE 14+" },
            BaseBoardProducts: new[] { "WUJIE 14", "WUJIE 14+", "ARN39P" },
            EcMajor: 2,
            EcMinor: 12,
            Note: "机械革命 无界14+ (ARN39P, Ryzen 7 7840HS), BIOS 2.31 / EC 2.12"),
    };

    /// <summary>
    /// 判断这台机器是否在支持范围内。
    /// 判定顺序: 先看厂商与机型, 再看 EC 固件版本 —— 后者决定寄存器布局。
    /// </summary>
    public static SupportVerdict Check(HardwareIdentity id)
    {
        if (!id.WasRead)
        {
            return SupportVerdict.Refuse(
                "读不到 SMBIOS 信息",
                "无法从注册表读取机器型号, 为了安全不读取任何 EC 寄存器。\n" +
                "如果你确认这是受支持的机型, 可以在提示框里选\"是\"强行继续 (风险自负)。");
        }

        Profile? matched = null;
        foreach (Profile p in Known)
        {
            bool makerOk = Contains(id.SystemManufacturer, p.Manufacturer)
                        || Contains(id.BaseBoardManufacturer, p.Manufacturer);
            if (!makerOk) continue;

            // 机型用**精确相等** (空白归一化后) 而不是子串匹配。
            //
            // 子串匹配会把「WUJIE 14 Pro」「WUJIE 14 SE」这类同系列不同型号的机器
            // 一起放进来 —— 而它们的 EC 风扇寄存器很可能整体不同 (见文件头注释)。
            // 宁可让用户在"强行继续"里自己选, 也不要默默把一个未验证的型号当验证过的用。
            bool modelOk = AnyEquals(id.SystemProductName, p.ProductNames)
                        || AnyEquals(id.BaseBoardProduct, p.BaseBoardProducts)
                        || AnyEquals(id.SystemSku, p.ProductNames)
                        || AnyEquals(id.SystemFamily, p.ProductNames);
            if (!modelOk) continue;

            matched = p;
            break;
        }

        if (matched is null)
        {
            string what = $"这台机器是「{id.SystemManufacturer} {id.SystemProductName}」(主板 {id.BaseBoardProduct}), 不在已验证的机型列表里。";
            string known = string.Join("; ", Known.Select(p => p.Note));
            return SupportVerdict.Refuse(
                "机型不在支持列表",
                what + "\n\n已验证的机型:\n  " + known + "\n\n" +
                "不同机型的 EC 风扇寄存器地址与换算系数完全不同, 强行读取只会得到错误的转速。\n" +
                "如果你确认这是一台型号相同、只是 SMBIOS 写法不同的机器, 可以在提示框里选\"是\"强行继续 (风险自负)。");
        }

        // EC 固件版本决定寄存器布局, 必须精确匹配。
        if (id.EcFirmwareMajor is int maj && id.EcFirmwareMinor is int min)
        {
            if (maj != matched.EcMajor || min != matched.EcMinor)
            {
                return SupportVerdict.Refuse(
                    "EC 固件版本不匹配",
                    $"机型已识别为 {matched.Note}, 但当前 EC 固件是 {maj}.{min}, " +
                    $"而本程序只验证过 EC {matched.EcMajor}.{matched.EcMinor}。\n\n" +
                    "风扇寄存器的地址和换算系数是逐字节对着特定固件逆向出来的, " +
                    "固件不同就意味着地址可能整体偏移。为了不给你一个错误的风扇转速, 这里直接停下。\n" +
                    "如果你确认要冒险, 可以在提示框里选\"是\"强行继续 (风险自负)。");
            }
        }
        else
        {
            return SupportVerdict.Refuse(
                "读不到 EC 固件版本",
                $"机型已识别为 {matched.Note}, 但 SMBIOS 里没有 EC 固件版本字段, " +
                "无法确认寄存器布局一致。可以在提示框里选\"是\"强行继续 (风险自负)。");
        }

        return SupportVerdict.Accept(matched);
    }

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static bool AnyContains(string haystack, IEnumerable<string> needles) =>
        needles.Any(n => Contains(haystack, n));

    /// <summary>
    /// 归一化后精确相等: 折叠连续空白、去掉首尾空白、忽略大小写。
    ///
    /// SMBIOS 里的型号写法常有细微差别 (多余空格、"14 ＋" 全角加号之类),
    /// 所以不能直接用 ==; 但也不能退化成子串匹配 —— 那会把同系列其它型号放进来。
    /// </summary>
    private static bool AnyEquals(string haystack, IEnumerable<string> needles)
    {
        string h = Normalize(haystack);
        if (h.Length == 0) return false;
        return needles.Any(n => Normalize(n) == h);
    }

    private static string Normalize(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";

        var sb = new System.Text.StringBuilder(s.Length);
        bool lastWasSpace = false;

        foreach (char ch in s)
        {
            // 全角加号/空格统一成半角, 免得 "WUJIE 14＋" 与 "WUJIE 14+" 判成不同型号。
            char c = ch switch
            {
                '＋' => '+',
                '\u3000' => ' ',
                _ => ch,
            };

            if (char.IsWhiteSpace(c))
            {
                if (lastWasSpace) continue;
                lastWasSpace = true;
                sb.Append(' ');
            }
            else
            {
                lastWasSpace = false;
                sb.Append(char.ToUpperInvariant(c));
            }
        }

        return sb.ToString().Trim();
    }
}

/// <summary>硬件校验结果。</summary>
public sealed class SupportVerdict
{
    public bool IsSupported { get; init; }
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public SupportedHardware.Profile? Profile { get; init; }

    public static SupportVerdict Accept(SupportedHardware.Profile p) => new()
    {
        IsSupported = true,
        Title = "机型已验证",
        Detail = p.Note,
        Profile = p,
    };

    public static SupportVerdict Refuse(string title, string detail) => new()
    {
        IsSupported = false,
        Title = title,
        Detail = detail,
    };
}
