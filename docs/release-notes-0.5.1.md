# v0.5.1 — 发布包瘦身

这个版本没有任何功能变化，只改发布包的组织方式。

## 包里从 18 个条目降到 6 个

0.5.0 的框架依赖版解压出 18 个条目，其中 10 个是程序文件：两个 exe 各自还跟着同名的 `.dll`、`.deps.json`、`.runtimeconfig.json`，再加一个 `WujieFan.Core.dll` 和完整性清单。另有 5 个 `tools/` 下的脚本。

现在两个包内容完全一致，各 6 个条目：

| 条目 | 说明 |
| --- | --- |
| `WujieFan.exe` | 主程序，单文件 |
| `WujieFanLauncher.exe` | 启动器，单文件 |
| `WujieFan.integrity.json` | 完整性清单 |
| `README.md` / `THIRDPARTY.md` / `LICENSE` | 文档 |

`.dll`、`.deps.json`、`.runtimeconfig.json` 全部折进了 exe。框架依赖版用的还是本机已装的 .NET 8 桌面运行时，不是把运行时塞进包里——所以框架包反而更小了（270,158 B → 247,611 B），省下的是重复存储的元数据。

## 为什么不合并成一个 exe

做不到，这是硬约束，不是偷懒：

1. **主程序必须要求管理员权限**。它访问的 PawnIO 驱动设备 SDDL 是 `D:P(A;;GA;;;SY)(A;;GA;;;BA)`，只允许 SYSTEM 与 Administrators 打开。
2. **但计划任务拉起的进程拿不到托盘图标**。任务计划程序会把进程放进一个带 UI 限制的 job 里，此时 `Shell_NotifyIcon` 返回 `ERROR_ACCESS_DENIED`，图标永不出现，而且这个限制会被子进程继承——用「最高权限」还是「仅当用户登录」都一样。

所以开机自启必须走这样一条路：计划任务以**普通权限**拉起 `WujieFanLauncher.exe`，由它调用 `ShellExecuteEx("runas")` 经 AppInfo 提权拉起主程序。合并成一个 exe 就得放弃开机自启时的托盘图标。

## tools 不再随包分发

`verify.ps1`、`make-release.ps1` 是开发工具；`read-aida.py`、`peek-overlay.py`、`restart-rtss.ps1` 需要 Python 才能用。它们都留在仓库里：

    https://github.com/Thousand00/wujiefan/tree/master/tools

README 里的排障步骤指向仓库的 `tools/` 目录。

## 顺带修掉的一个隐患

打包脚本原先只清 `dist\app`，不清 `dist\portable`。单文件化之后 `Core.dll` 不再落盘，但 `dist\portable` 里若残留上一轮多文件发布的 `WujieFan.Core.dll`，完整性清单生成逻辑会按「磁盘上存在就收录」把它写进去，而包里没有这个文件——启动器会**每次开机都误报完整性不符**。

现在三个暂存目录在 publish 前都会清空。另外框架包的打包逻辑从「目录里有什么就打什么」改成显式白名单：缺少预期文件直接报错，出现白名单外的文件也直接报错，不会静默打出散碎的包。

## 验证

- 构建 0 警告 0 错误。
- 单测 178/178 通过。
- `tools/verify.ps1 -SkipBuild -FailOnSkip` 退出码 0（版本三项、单测、autostart 6/6、XML 契约）。
- 从打好的框架包解压出单文件版，以 `--tray` 启动，`read-aida.py` 正常读到 `Fan1 2156 / Fan2 1868 / 55°C` —— Core、嵌入式 `LpcIO.bin`、PawnIO 访问、AIDA 供数在单文件下都正常。
- 包内 `WujieFan.integrity.json` 的 sha256 与 length 与包内 exe 逐字节相符。
- 两种包内程序 `FileVersion` 均为 `0.5.1`，`ProductVersion` 为 `0.5.1+c8ef71c`（该提交之后只有本文件与文档改动，不影响二进制）。

## 下载

| 文件 | 大小 | sha256 |
| --- | --- | --- |
| `WujieFan-0.5.1-win-x64.zip` | 247,611 B | `da60e12a77fe15beda9cc530f2250eb1f020c3cb074cc5f866644f5f270ed59f` |
| `WujieFan-0.5.1-win-x64-portable.zip` | 95,223,729 B | `78711ce4dce09cff2a32add775afa939568bf649be22a9d83166085fe1e4ee7f` |

框架版需要本机已装 .NET 8 桌面运行时，便携版免运行时（自带运行时，解压即用，体积大）。

## 已知的取巧

框架版单文件在**首次启动**时会把 5 个 WPF 原生库解压到 `%LOCALAPPDATA%\Temp\.net\WujieFan\<hash>\`，共约 8.2 MB。这是 .NET 单文件打包的固定行为，之后复用。便携版同理但走自己的解压路径。如果介意这一点，可以用 0.5.0 的多文件版——但那正是这个版本要解决的问题。

框架版比 0.5.0 小、条目更少，功能完全相同，建议直接升级。
