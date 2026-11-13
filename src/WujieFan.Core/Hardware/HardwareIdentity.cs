using Microsoft.Win32;

namespace WujieFan.Core.Hardware;

/// <summary>
/// 从 SMBIOS 读取的机器身份信息。
///
/// 全部来自 HKLM\HARDWARE\DESCRIPTION\System\BIOS, 不需要管理员权限,
/// 也不碰任何硬件端口 —— 这个类只读注册表。
///
/// 之所以用 SMBIOS 而不是 EC 内部寄存器判断版本:
/// 实测 EC XRAM 的 0x0000 是变化的计数值 (两次分别读到 27 和 45),
/// 不是固件版本; 而 SMBIOS 的 ECFirmwareMajorRelease / MinorRelease
/// 由 BIOS 在启动时写入, 稳定且精确, 实测为 2 / 12。
/// </summary>
public sealed class HardwareIdentity
{
    private const string BiosKey = @"HARDWARE\DESCRIPTION\System\BIOS";

    public string SystemManufacturer { get; init; } = "";
    public string SystemProductName { get; init; } = "";
    public string SystemFamily { get; init; } = "";
    public string SystemSku { get; init; } = "";
    public string BaseBoardManufacturer { get; init; } = "";
    public string BaseBoardProduct { get; init; } = "";
    public string BiosVersion { get; init; } = "";

    /// <summary>SMBIOS 记录的 EC 固件主版本号。读不到时为 null。</summary>
    public int? EcFirmwareMajor { get; init; }

    /// <summary>SMBIOS 记录的 EC 固件次版本号。读不到时为 null。</summary>
    public int? EcFirmwareMinor { get; init; }

    /// <summary>是否成功读到了注册表项。</summary>
    public bool WasRead { get; init; }

    /// <summary>形如 "2.12" 的 EC 版本串; 缺失时为空串。</summary>
    public string EcVersionText =>
        EcFirmwareMajor is int maj && EcFirmwareMinor is int min ? $"{maj}.{min}" : "";

    public static HardwareIdentity Read()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(BiosKey);
            if (key is null) return new HardwareIdentity { WasRead = false };

            return new HardwareIdentity
            {
                WasRead = true,
                SystemManufacturer = Str(key, "SystemManufacturer"),
                SystemProductName = Str(key, "SystemProductName"),
                SystemFamily = Str(key, "SystemFamily"),
                SystemSku = Str(key, "SystemSKU"),
                BaseBoardManufacturer = Str(key, "BaseBoardManufacturer"),
                BaseBoardProduct = Str(key, "BaseBoardProduct"),
                BiosVersion = Str(key, "BIOSVersion"),
                EcFirmwareMajor = Int(key, "ECFirmwareMajorRelease"),
                EcFirmwareMinor = Int(key, "ECFirmwareMinorRelease"),
            };
        }
        catch
        {
            return new HardwareIdentity { WasRead = false };
        }
    }

    private static string Str(RegistryKey k, string name) => k.GetValue(name) as string ?? "";
    private static int? Int(RegistryKey k, string name) => k.GetValue(name) is int v ? v : null;

    public override string ToString() =>
        $"{SystemManufacturer} {SystemProductName} / 主板 {BaseBoardProduct} / BIOS {BiosVersion} / EC {EcVersionText}";
}
