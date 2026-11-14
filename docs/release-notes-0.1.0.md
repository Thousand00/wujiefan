WujieFan 0.1.0 —— 机械革命无界14+ (ARN39P-TF) 风扇转速监视器

从 EC 读两颗风扇的转速和 CPU 温度。**只读**：从不写 EC 任何寄存器，不碰 BIOS，不改风扇曲线。

## 两个包选一个

| 包 | 大小 | 前提 | 适用 |
| --- | --- | --- | --- |
| `WujieFan-0.1.0-win-x64.zip` | 146 KB | 需装 .NET 8 桌面运行时 | 体积小、已装 .NET 8 的机器 |
| `WujieFan-0.1.0-win-x64-portable.zip` | 62.6 MB | 无 | 免安装运行时，解压即用 |

两个都是解压后直接跑 `WujieFan.exe`。**必须以管理员身份运行** —— EC 读写走 PawnIO 内核驱动，设备权限只放行 Administrators 与 SYSTEM。

## 它能做什么

- 实时显示 Fan1（CPU 侧）、Fan2（GPU 侧）转速，以及 CPU 温度
- 托盘常驻：点 × 收进托盘，双击托盘图标或再启动一次即可唤回窗口
- 开机自启（登录时以管理员身份拉起，可在界面里勾选）
- 把读数发布到 `AIDA64_SensorValues` 共享内存，供 RTSS OverlayEditor 作为数据源引用

## 接入 RTSS 叠加层

程序运行后，`tools\add-wujie-layers.py` 会往 `default.ovl` 里加两个可拖动图层（`Fan1 xxx RPM` / `Fan2 xxx RPM`）。改完必须重启 RTSS.exe，布局没有热重载。

排查顺序：先 `tools\read-aida.py` 看共享内存里有没有数，再 `tools\peek-overlay.py --full` 看 RTSS 有没有把宏替换成数字。

## 硬性前提

- 机型：MECHREVO 无界14+，型号 ARN39P-TF（Ryzen 7 7840HS）。程序启动会校验，不匹配直接拒绝运行（可用 `ForceUnsupported` 强跑，但读数不保证）。
- EC 版本：2.12。不同 EC 版本寄存器布局可能不同。
- 需要 [PawnIO](https://pawnio.eu/) 已安装（提供 `PawnIOLib.dll` 与内核驱动）。

## 许可

本项目 MIT。随包分发的 `LpcIO.bin` 来自 PawnIO vendor 包，**LGPL 2.1**，详见 `THIRDPARTY.md`（含替换方法）。

## 已知限制

- 只支持 64 位 Windows
- 布局脚本假定 RTSS 布局在 `Desktop\default.ovl`；改过路径需自行调整
- `PositionX` 不是绝对像素，负值向右、绝对值过大图层会静默消失