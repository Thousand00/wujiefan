using System.Runtime.InteropServices;

namespace WujieFan.Core.Native;

/// <summary>
/// PawnIOLib.dll 的最小 P/Invoke 封装。
///
/// 用哪一组导出函数很关键。官方头文件 C:\Program Files\PawnIO\PawnIOLib.h 定义了三套:
///   PAWNIOAPI    = EXTERN_C __declspec(dllexport) HRESULT STDAPICALLTYPE   -> pawnio_open 等, 返回 HRESULT
///   PAWNIOWINAPI = EXTERN_C __declspec(dllexport) BOOL    WINAPI          -> pawnio_open_win32 等, 返回 BOOL
///   PAWNIONTAPI  = EXTERN_C __declspec(dllexport) NTSTATUS NTAPI          -> pawnio_open_nt 等
///
/// 本类统一使用**不带后缀**的那一套 (HRESULT, 0 表示成功), 与已验证的 Python 实现一致。
/// 曾经误用 _win32 变体并按 HRESULT 判定, 导致成功(返回 TRUE=1)被当成失败。
///
/// 设备 SDDL 为 D:P(A;;GA;;;SY)(A;;GA;;;BA) —— 只允许 SYSTEM 与 Administrators,
/// 因此 pawnio_open 在非提权进程里必然返回 E_ACCESSDENIED(0x80070005)。
/// </summary>
public sealed class PawnIo : IDisposable
{
    public const uint E_ACCESSDENIED = 0x80070005;

    private IntPtr _handle;
    private IntPtr _hModule;

    public string Version { get; } = "?";

    public PawnIo(string? dllPath = null)
    {
        string path = dllPath ?? DefaultDllPath();
        if (!File.Exists(path))
            throw new PawnIoException(
                "找不到 PawnIOLib.dll：" + path + Environment.NewLine +
                "PawnIO 驱动没有安装。请先安装 https://pawnio.eu/ 后再运行本程序。");

        _hModule = NativeLibrary.Load(path);

        try
        {
            _version = Bind<VersionFn>(_hModule, "pawnio_version");
            _open    = Bind<OpenFn>   (_hModule, "pawnio_open");
            _load    = Bind<LoadFn>   (_hModule, "pawnio_load");
            _execute = Bind<ExecuteFn>(_hModule, "pawnio_execute");
            _close   = Bind<CloseFn>  (_hModule, "pawnio_close");
        }
        catch (Exception ex)
        {
            NativeLibrary.Free(_hModule);
            _hModule = IntPtr.Zero;
            throw new PawnIoException("PawnIOLib.dll 的导出函数不完整，版本可能太旧：" + ex.Message, ex);
        }

        uint api = 0;
        int hrVer = _version(ref api);
        if (hrVer < 0)
            throw new PawnIoException($"pawnio_version 失败 hr=0x{unchecked((uint)hrVer):X8}", unchecked((uint)hrVer));

        // 该返回值是编译期常量, 不代表实际安装版本; 可信的是文件版本号。
        string fileVer = ReadFileVersion(path) ?? "?";
        Version = $"{fileVer} (API {api >> 16}.{(api >> 8) & 0xFF}.{api & 0xFF})";

        IntPtr handle = IntPtr.Zero;
        int hr = _open(ref handle);
        if (hr < 0)
        {
            uint code = unchecked((uint)hr);
            if (code == E_ACCESSDENIED)
                throw new PawnIoException(
                    "PawnIO 设备访问被拒绝 (E_ACCESSDENIED)。" + Environment.NewLine +
                    "该驱动只允许管理员打开，请以管理员身份运行本程序。", code);
            throw new PawnIoException($"pawnio_open 失败 hr=0x{code:X8}", code);
        }

        if (handle == IntPtr.Zero)
            throw new PawnIoException("pawnio_open 报告成功但返回了空句柄。");

        _handle = handle;
    }

    private static T Bind<T>(IntPtr hModule, string name) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(hModule, name));

    public static string DefaultDllPath()
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return Path.Combine(programFiles, "PawnIO", "PawnIOLib.dll");
    }

    /// <summary>加载 PawnIO blob (例如 LpcIO.bin)。</summary>
    public void LoadModule(byte[] blob, string displayName)
    {
        EnsureOpen();
        int hr = _load!(_handle, blob, (nuint)blob.Length);
        if (hr < 0)
            throw new PawnIoException($"加载模块失败 {displayName} hr=0x{unchecked((uint)hr):X8}", unchecked((uint)hr));
    }

    /// <summary>执行 blob 导出的函数, 返回写回输出缓冲区的值 (数量由 return_size 决定)。</summary>
    public ulong[] Execute(string function, ulong[]? input, int outCount)
    {
        EnsureOpen();

        ulong[] inArr  = input is { Length: > 0 } ? input : new ulong[1];
        ulong[] outArr = new ulong[Math.Max(outCount, 1)];
        nuint written = 0;

        int hr = _execute!(_handle, function, inArr, (nuint)(input?.Length ?? 0),
                           outArr, (nuint)outCount, ref written);
        if (hr < 0)
            throw new PawnIoException($"{function} 失败 hr=0x{unchecked((uint)hr):X8}", unchecked((uint)hr));

        int n = (int)written;
        if (n <= 0) return Array.Empty<ulong>();
        if (n > outArr.Length) n = outArr.Length;

        ulong[] result = new ulong[n];
        Array.Copy(outArr, result, n);
        return result;
    }

    /// <summary>执行并取第一个返回值 (无输出时返回 0)。</summary>
    public ulong Execute1(string function, params ulong[] input)
    {
        ulong[] r = Execute(function, input.Length == 0 ? null : input, 1);
        return r.Length > 0 ? r[0] : 0;
    }

    /// <summary>
    /// 执行一个没有输出参数的函数。
    /// 必须传 out_size=0 —— 传 1 时驱动不写缓冲区, 读到的会是残留内存
    /// (曾把 "ioctl_se" 这段字符串当成返回值)。
    /// </summary>
    public void ExecuteVoid(string function, params ulong[] input)
    {
        EnsureOpen();

        ulong[] inArr  = input is { Length: > 0 } ? input : new ulong[1];
        ulong[] outArr = new ulong[1];
        nuint written = 0;

        int hr = _execute!(_handle, function, inArr, (nuint)input.Length,
                           outArr, 0, ref written);
        if (hr < 0)
            throw new PawnIoException($"{function} 失败 hr=0x{unchecked((uint)hr):X8}", unchecked((uint)hr));
    }

    private void EnsureOpen()
    {
        if (_handle == IntPtr.Zero)
            throw new PawnIoException("PawnIO 句柄已关闭。");
    }

    private static string? ReadFileVersion(string path)
    {
        try { return System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileVersion; }
        catch { return null; }
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            try { _close?.Invoke(_handle); } catch { }
            _handle = IntPtr.Zero;
        }
        if (_hModule != IntPtr.Zero)
        {
            try { NativeLibrary.Free(_hModule); } catch { }
            _hModule = IntPtr.Zero;
        }
    }

    // ---------------------------------------------------------------- 委托
    // 全部 HRESULT (__stdcall 返回 int), 0 = 成功。

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int VersionFn(ref uint version);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int OpenFn(ref IntPtr handle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int LoadFn(IntPtr handle, byte[] blob, nuint size);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ExecuteFn(
        IntPtr handle,
        [MarshalAs(UnmanagedType.LPStr)] string name,
        ulong[] input, nuint inSize,
        ulong[] output, nuint outSize,
        ref nuint returnSize);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CloseFn(IntPtr handle);

    private readonly VersionFn? _version;
    private readonly OpenFn?    _open;
    private readonly LoadFn?    _load;
    private readonly ExecuteFn? _execute;
    private readonly CloseFn?   _close;
}

public sealed class PawnIoException : Exception
{
    public uint NativeCode { get; }

    public PawnIoException(string message, uint code = 0) : base(message) => NativeCode = code;
    public PawnIoException(string message, Exception inner) : base(message, inner) { }
}
