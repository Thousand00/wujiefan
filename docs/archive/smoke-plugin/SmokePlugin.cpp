// SmokePlugin.cpp - RTSS 插件宿主机制冒烟测试
//
// 目的: 用最小 x86 DLL 验证 RTSS.exe 是否真的会
//   1) 扫描 PlugIns\\Client\\*.dll 并 LoadLibrary
//   2) 调用导出函数 Setup(NULL) / Start() / Stop()
//
// 不读硬件、不碰任何配置，只把调用事实写进日志文件。
// 验证完即可删除。

#include <windows.h>
#include <stdio.h>

static char g_logPath[MAX_PATH] = {0};
static HMODULE g_hSelf = NULL;

// 依次尝试三个可写位置，取第一个成功的
static void ResolveLogPath()
{
    char dir[MAX_PATH];

    // 1) %ProgramData%\\WujieEc
    if (GetEnvironmentVariableA("ProgramData", dir, MAX_PATH))
    {
        char full[MAX_PATH];
        sprintf_s(full, sizeof(full), "%s\\WujieEc", dir);
        CreateDirectoryA(full, NULL);
        sprintf_s(g_logPath, sizeof(g_logPath), "%s\\smoke.log", full);

        HANDLE h = CreateFileA(g_logPath, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
                               NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
        if (h != INVALID_HANDLE_VALUE) { CloseHandle(h); return; }
    }

    // 2) DLL 同目录
    if (g_hSelf && GetModuleFileNameA(g_hSelf, dir, MAX_PATH))
    {
        char* p = strrchr(dir, '\\');
        if (p) *p = 0;
        sprintf_s(g_logPath, sizeof(g_logPath), "%s\\_wujieec_smoke.log", dir);

        HANDLE h = CreateFileA(g_logPath, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
                               NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
        if (h != INVALID_HANDLE_VALUE) { CloseHandle(h); return; }
    }

    // 3) %TEMP%
    if (GetEnvironmentVariableA("TEMP", dir, MAX_PATH))
    {
        sprintf_s(g_logPath, sizeof(g_logPath), "%s\\wujieec_smoke.log", dir);

        HANDLE h = CreateFileA(g_logPath, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
                               NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
        if (h != INVALID_HANDLE_VALUE) { CloseHandle(h); return; }
    }

    g_logPath[0] = 0;
}

static void Log(const char* fmt, ...)
{
    if (!g_logPath[0]) return;

    char msg[1024];
    va_list ap;
    va_start(ap, fmt);
    vsprintf_s(msg, sizeof(msg), fmt, ap);
    va_end(ap);

    SYSTEMTIME st;
    GetLocalTime(&st);

    char line[1400];
    sprintf_s(line, sizeof(line), "[%04d-%02d-%02d %02d:%02d:%02d.%03d] [pid=%lu tid=%lu] %s\r\n",
              st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond, st.wMilliseconds,
              GetCurrentProcessId(), GetCurrentThreadId(), msg);

    HANDLE h = CreateFileA(g_logPath, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
                           NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE) return;

    DWORD written = 0;
    SetFilePointer(h, 0, NULL, FILE_END);
    WriteFile(h, line, (DWORD)strlen(line), &written, NULL);
    CloseHandle(h);
}

extern "C" BOOL WINAPI DllMain(HINSTANCE hInstance, DWORD dwReason, LPVOID lpReserved)
{
    UNREFERENCED_PARAMETER(lpReserved);

    if (dwReason == DLL_PROCESS_ATTACH)
    {
        g_hSelf = hInstance;
        DisableThreadLibraryCalls(hInstance);
        ResolveLogPath();
        Log("=== DllMain DLL_PROCESS_ATTACH  log=%s ===", g_logPath);
    }
    else if (dwReason == DLL_PROCESS_DETACH)
    {
        Log("=== DllMain DLL_PROCESS_DETACH ===");
    }

    return TRUE;
}

extern "C" __declspec(dllexport) BOOL WINAPI Setup(HWND hWnd)
{
    Log("Setup(hWnd=%p) called -> return TRUE", (void*)hWnd);
    return TRUE;
}

extern "C" __declspec(dllexport) BOOL WINAPI Start()
{
    Log("Start() called -> return TRUE");
    return TRUE;
}

extern "C" __declspec(dllexport) void WINAPI Stop()
{
    Log("Stop() called");
}
