# WujieFan

读取 MECHREVO 无界14+ 的 EC 风扇转速，显示在独立界面里，并作为可拖动图层送进 RTSS 叠加层。

适用机型：MECHREVO 无界14+ / ARN39P-TF / Ryzen 7 7840HS / ITE EC 2.12 / BIOS 2.31。

## 它解决什么问题

RTSS 自带的数据提供方（AIDA / HwInfo / MSI Afterburner / LHM / PresentMon 等）没有一个提供笔记本平台风扇转速。本项目从 EC 直接读取，经 AIDA 共享内存交给 RTSS，因此不需要改 RTSS 本身，也不需要往它的进程里塞插件。

     EC 寄存器 0x181E / 0x1820
       → WujieFan（管理员权限，PawnIO + LpcIO.bin）
       → 共享内存 AIDA64_SensorValues
       → RTSS OverlayEditor 的 AIDA 数据源
       → %WUJIE Fan1 RPM% 宏 → 图层文字

## 下载

打包好的版本见 [Releases](https://github.com/Thousand00/wujiefan/releases/latest)。

| 包 | 大小 | 前提 |
|---|---|---|
| `WujieFan-<版本>-win-x64.zip` | 约 245 KiB | 需 .NET 8 桌面运行时 |
| `WujieFan-<版本>-win-x64-portable.zip` | 约 94 MiB | 免运行时，解压即用 |

两个包解压后都是 6 个文件：

    WujieFan.exe              主程序（需要管理员）
    WujieFanLauncher.exe      自启动中转（普通权限）
    WujieFan.integrity.json   启动器比对的 sha256 清单
    README.md
    THIRDPARTY.md
    LICENSE

两个 exe 都是单文件发布，`WujieFan.Core.dll`、嵌入的 `LpcIO.bin`、PawnIO 互操作代码全部并进 exe，目录里不会有散落的 DLL 或 `.deps.json` / `.runtimeconfig.json`。精确字节数与 sha256 见 Release 页面。

### 为什么有两个 exe

任务计划程序拉起的进程会被放进带 UI 限制的 job 对象，`Shell_NotifyIcon(NIM_ADD)` 直接返回 `ERROR_ACCESS_DENIED(5)`，托盘图标不会出现，且该限制被子进程继承。所以自启动必须两跳：

    计划任务（普通权限）→ WujieFanLauncher.exe（asInvoker）
      → ShellExecuteEx("runas") → 经 AppInfo 提权 → WujieFan.exe（管理员）

只有 AppInfo 新建的进程才不继承那个受限 job。反过来让已提权的进程再调 `runas` 无效：UAC 判定同级，退化成普通 `CreateProcess`。启动器只做一次进程创建，不读 EC、不碰硬件寄存器。

## 使用

**运行**：解压后双击 `WujieFan.exe`，UAC 点「是」。管理员权限是必须的，PawnIO 设备对象的 SDDL 只放行 SYSTEM 与 Administrators。

**开机自启**：在「设置」里勾选。每次登录会弹一次 UAC 确认，这个点击省不掉 —— 既要管理员权限又要托盘图标，就必须经过 AppInfo 提权。

**进入 RTSS**：确认程序在跑、设置里勾选了「向 RTSS 提供数据」，然后

    python tools\add-wujie-layers.py "...\default.ovl"
    powershell -File tools\restart-rtss.ps1

脚本幂等，重复运行只原地更新；`--revert` 撤销，`--dry-run` 预览，改之前自动备份。RTSS 没有热重载，必须重启才生效。

**关窗不退出**：点 × 只是收进托盘，读数与供数继续。双击托盘图标、托盘菜单「显示主窗口」、或再运行一次 exe 都能唤回（单实例）。真退出用托盘菜单「退出 WujieFan」。

## 设置

主面板只放数据卡和「强冷模式」，其余在「设置」对话框里（主面板一行或托盘菜单进入）：

| 项 | 说明 |
|---|---|
| 开机自动启动 | 计划任务两跳拉起 |
| 向 RTSS 提供数据 | 写入 `AIDA64_SensorValues` 共享内存 |
| 过热保护 · 温度阈值 | 70–100°C，默认 92 |
| 过热保护 · 连续采样 | 1–60 次，默认 3 |

默认值等于旧版本行为。越界值在加载时**回到默认**而不是夹到边界 —— 温度夹到 100 等于悄悄关掉保护。

## 风扇控制

只有「自动 / 强冷」两档。

| 档位 | EC 状态 | 风扇 |
|---|---|---|
| 自动（默认） | 模式标志 `0x00` | 由固件按温度决定，日常 3000~4200 RPM |
| 强冷 | 手动 PWM `184` + 标志 `0xDC` | 两个风扇满转，约 5700 RPM |

**为什么不提供降速**：EC 进入手动模式后，进程被 `taskkill /F` 也不会自己回退，标志和 PWM 值原样留着。允许任意调低，就等于允许把机器留在比固件自己认为安全还低的转速上。只往上调，最坏结果是吵，不会过热。

**过热保护**：强冷状态下连续测温，连续 N 次采样都达到阈值温度（默认 92°C / 3 次）就自动退回自动模式。正常满转时温度只会往下掉，这条几乎不会触发。

**残留风险**：主程序被强杀时 EC 会停在手动满转（吵，但不超温）。三条恢复路径：下次启动时的归一化、托盘右键「恢复自动风扇」、关掉程序再重开。

## 界面

深色单窗口，432 宽（按 DPI 等比缩放），无系统边框，标题栏与按钮均为自绘。

| 区域 | 内容 |
|---|---|
| 标题栏 | 图标 + WujieFan / 无界14+ 风扇转速 + 最小化 / 关闭 |
| 机型行 | 厂商 型号 · BIOS 版本 · EC 版本（读不到时显示「机器信息不可读」） |
| 数据卡 ×3 | Fan1 (CPU) / Fan2 (GPU) / CPU 温度；左侧圆点：绿=正常、灰=停转、橙=读数不可信 |
| 强冷行 | 勾选框，右侧「实际: 自动 / 强冷 / 异常标志」徽章 |
| 设置行 | 「设置」入口 |
| 状态栏 | 一行文字 + 语义色圆点（绿=正常、橙=警告、红=错误、无点=中性） |

徽章显示的是从 EC 回读的真实状态，不是点击结果 —— 切换失败会立刻回滚并把原因写进状态栏。

设计稿在 `UI/WujieFan-UI-设计稿.html`（单页 HTML 原型，含主界面 / 状态图鉴 / 设计规范）。

## 排查

以下脚本在仓库的 `tools\` 目录，不随发布包分发：

    python tools\read-aida.py -n 3          # 程序到底写没写、写的数对不对
    python tools\peek-overlay.py --full     # RTSS 实际渲染出来的图层文本
    python tools\peek-overlay.py --slots    # 列出全部 8 个 OSD 槽的占用者

顺序很重要：先确认供数（read-aida），再看渲染（peek-overlay）。

若数值都对、`peek-overlay` 里也有图层文本，但屏幕上就是看不见，几乎一定是坐标跑出了渲染区：`.ovl` 的 `PositionX` 不是绝对像素，负值向右，数值过大会让图层静默消失（`-120` 完全看不见，`-16` 正常）。详见 [docs/01](docs/01-现状与技术总结.md) §9A.10。

## 目录

    assets\                    程序图标源文件
    UI\                        界面设计稿与图标源文件
    src\WujieFan\              主程序（WinForms .NET 8 x64）
    src\WujieFan\Ui\           界面与控制器
    src\WujieFan.Launcher\     自启动提权中转（asInvoker）
    src\WujieFan.Core\         核心库：读 EC / 机型校验 / AIDA 供数 / 设置 / 自启
    tests\EcReadSmoke\         命令行验证工具（含自启动往返测试）
    tests\FanControlLab\       风扇转速实验台（不是产品，见下）
    tests\WujieFan.Core.Tests\ xUnit 单测（无硬件依赖）
    tools\                     开发与诊断脚本（仅仓库，不随包分发）
    docs\                      技术文档
    .github\workflows\         CI
    dist\app\                  发布产物（不入库，可重建）
    backup\                    改动前的 RTSS 原始配置，用于还原（不入库）

`tests\FanControlLab` 是独立的实验程序，能写任意 PWM（必须显式 `--apply` 并带看门狗），不被主程序调用，也不随发布包分发。

## 构建

先退出正在运行的实例。

    dotnet publish src\WujieFan\WujieFan.csproj -c Release -r win-x64 --self-contained false `
      -p:PublishSingleFile=true -p:DebugType=none -o dist\app
    dotnet publish src\WujieFan.Launcher\WujieFan.Launcher.csproj -c Release -r win-x64 --self-contained false `
      -p:PublishSingleFile=true -p:DebugType=none -o dist\app

两个 exe 必须发布到同一目录，否则自启动会报「缺少启动器文件」。`PublishSingleFile=true` 不能省，不加会退回多文件输出，打包脚本会拒绝出包。`--self-contained false` 表示依赖系统已装的 .NET 8 桌面运行时；要做免运行时的便携包，两处都换成 `--self-contained true -p:IncludeNativeLibrariesForSelfExtract=true`。

一次产出两个 zip：

    powershell -File tools\make-release.ps1 -Version <版本号>

版本号只在仓库根的 `Directory.Build.props` 定义，脚本会校验 `-Version` 与它一致。

回归检查（免管理员）：

    pwsh -File tools\verify.ps1

运行时不进仓库：`dist/`、`bin/`、`obj/` 均在 `.gitignore` 里。

## 前提与风险

- **必须管理员权限**。PawnIO 设备对象的 SDDL 只放行 SYSTEM 与 Administrators。
- **只支持无界14+ / ARN39P-TF + EC 2.12**。寄存器地址是从这个固件逆向出来的，机型或固件版本不符时读出来的数字看着合理但完全错误，所以程序直接拒绝而非「尽力而为」。确有需要可用 `ForceUnsupported` 强开，后果自负。
- **写 EC 的地方只有「强冷模式」一处**，只写四个寄存器：两个手动 PWM 值（`0x021E`/`0x029E`）和两个模式标志（`0x021F`/`0x029F`）。不碰风扇曲线表、不碰阈值、不改 NVRAM。自动模式下完全不写 EC，只读。
- **RTSS 需 7.3.x**，`Plugins\Client\Overlays` 里的布局文件请自行备份。
- **布局文件编码是混合的**（OEM 自带图层名是 GBK 字节），脚本用 latin-1 读写以保持字节不变。不要用记事本另存为 UTF-8，否则中文图层名会变乱码。
- **`LpcIO.bin` 是 LGPL 2.1**，本程序把它作为嵌入资源分发，因此同样适用 LGPL 2.1：可以替换。源码与许可见 <https://github.com/namazso/PawnIO>。

## 文档

- [docs/01-现状与技术总结.md](docs/01-现状与技术总结.md) —— 硬件/固件事实、EC 寄存器表、RTSS 各数据通道调研结论、失败的插件实验与真正原因、AIDA XML 解析器的硬性约束、踩坑速查。要改这个项目或复现这套做法，先读它。
- [docs/02-路线规划与优先级.md](docs/02-路线规划与优先级.md) —— 版本规划、取舍与风险台账。
- [docs/03-实机验收手册.md](docs/03-实机验收手册.md) —— 写 EC 与自启链路的实机验收步骤。

## 许可

本仓库自有代码为 MIT。内嵌的 PawnIO 模块为 LGPL 2.1。
