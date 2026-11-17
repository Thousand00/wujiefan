# WujieFan 0.4.1

一轮只读审计之后的修复版。没有新功能：读数、供数、界面布局与 0.4.0 一致，变的是出错时的行为——该拦住的地方现在会拦住，该报警的地方现在会报警。

## 安全问题

- **启动器可被当作提权原语**。原状况：`WujieFanLauncher.exe D:\evil.exe` 会把任意 exe 经 UAC 以管理员身份拉起，任何能写快捷方式或计划任务的普通用户都能利用。现状况：目标写死为同目录 `WujieFan.exe`，命令行不得参与"执行谁"的决定；参数只按白名单透传（当前仅 `--tray`），其余丢弃并留痕。
- **启动前不知道文件被动过**。原状况：无任何完整性检查。现状况：启动前比对手边 `WujieFan.integrity.json` 里的 SHA-256 与长度，不一致则明确警告、拒绝进入后台常驻，但仍保留"手动启动"出口（避免误判让人彻底用不了）。清单由 `make-release.ps1` 生成，便携包单文件布局不会误报。
- **配置损坏时可能倒向强冷**。原状况：`settings.json` 写一半断电即损坏，读失败后走默认值。现状况：原子写（tmp + `File.Replace`，失败再退一次 `Move`）加 `SchemaVersion`，`Save` 返回 bool 不再假装成功，失败原因写进 `%LOCALAPPDATA%\WujieFan\ui.log`；损坏文件降级时**绝不回退到强冷**。

## 硬件安全

- **PWM 没有夹取层**（t1-P0-1）。原状况：有效占空比只有 0–184，>184 实测直接停转，而全仓唯一的"钳位"是各调用点硬编码，新增一个调用点就可能把风扇写停。现状况：`EcAccess.WritePwm` 统一夹取 `[0,184]`，**越界抛异常而非静默夹取**——静默夹取会把调用方的 bug 藏起来。
- **读失败会跳过过热看门狗**（t1-P0-2）。原状况：看门狗只在"EC 回读确认是强冷"时生效，而它的判据恰好否定了它要防的场景：别的软件把标志改回自动后，我们就该交还控制权，于是看门狗不再管。现状况：判据改为"本次会话是否仍持有强冷意图"，**读失败路径也走看门狗**（`CheckWatchdogOnReadError`），阈值为连续 3 次且持续 ≥2 秒。
- **切换失败不回滚**（t1-P0-3）。原状况：`SetMode` 写到一半失败就留在半途，可能一个风扇手动满转、另一个交还 EC，且只写日志。现状况：写入前快照四个寄存器，失败先交还自动、再还原快照；**回滚也失败时**通过 `FanModeResult.Indeterminate` 上抛，徽章改为"未知"、状态栏红色错误并弹一次警告框（读失败路径只留状态栏，避免模态框把界面锁死）。
- **锁泄漏**。原状况：一部分互斥量没拿到就返回失败，调用方据此跳过释放，`Global\Access_ISABUS` 被本进程长期占住，HWiNFO / LHM 再也读不到 EC。现状况：拿不全即把已到手的释放掉，`Release` 无条件执行。

## 发布基线

- **版本号三处发散**。原状况：版本散落在各 `.csproj`，`WujieFan.Core` 干脆没有版本节点，元数据退回默认 `1.0.0`，与包名、主程序版本对不上。现状况：收敛到 `Directory.Build.props` 单一来源，单个 `.csproj` 不得再写 `Version`；`make-release.ps1 -Version` 必填且与 props 比对，不一致直接报错。
- **打包可能混入陈旧 DLL**。原状况：`dotnet publish` 是合并输出，不删除上一次的残留，`dist/app` 里的旧版本 DLL 会被原样打进 zip。现状况：打包前整体清空 `dist/app`，保证产物只含本次 publish 写出的文件。

## 结构性改造（原计划留给 0.6，本版本一次性完成）

- 单测工程 `tests/WujieFan.Core.Tests/`，148 个 test case（84 `[Fact]` + 61 `[InlineData]` + 3 条回读路径断言），本机与 CI 均 148/148 通过。
- `IEcTransport` / `IClock` 抽象：`FanReader` 可注入假传输，写序与回滚路径可在无硬件下断言。
- `MainForm.cs` 从 1618 行拆到 580 行，分出 `FanModeController` / `FanPollingController` / `TrayHost` / `TitleBarChrome` / `RtssPublisher`，全部构造注入、无 DI 容器。
- CI：`.github/workflows/ci.yml` + `tools/verify.ps1` 统一验证入口（build / test / 版本一致性 / autostart-test / XML 契约）。
- **AIDA 写入 seqlock 已评估并否决**：布局是 XML 文本而非定宽槽位，RTSS 一次性取串不读序号；实测拆分成「清首字节→改写→写回首字节」使撕裂从 46 次/40 万读恶化到 8,803 次（191 倍）。保留单次 `Marshal.Copy`，理由与数据见 `docs/01` §14A 与 `docs/02` 文首快照。

## 文档

- `docs/01` 的数字与实测对齐（入库 58 个文件、包体积按发布资产逐字节核对），并新增 §14 记录这轮审计的来源、五份分工与三条 P0 的处理方式。
- `README` 修正两个包的实测体积；`THIRDPARTY.md` 改成如实描述：`LpcIO.bin` 以嵌入资源形式编在 `WujieFan.Core.dll` 里，`dist/app` 内没有可替换的文件，替换须重新编译。
- 新增 `docs/02-路线规划与优先级.md`（规划存档）与本版发布说明。

## 未闭环项

- **写 EC 的实机链路未在非管理员会话验证**。`--fanmode-live` 等回归都是在管理员会话下跑的；"普通用户误启动会怎样"没有实机结论。验收手册见 `docs/03-实机验收手册.md`。
- **签名校验未实现**。当前的 SHA-256 清单与 exe 放在同一个可写目录，能被替换 exe 的人一起替换；它只"检测"不"防御"，属提高成本而非消除路径。彻底修复需要 Authenticode 签名或受保护安装目录，两者都不在本版本范围内，也**没有保留一个默认关闭的分支**。
- **安装目录可写这个提权根因未解决**。"解压即用"意味着普通用户可写程序所在目录，替换 `WujieFan.exe` 后等下次登录即可。该问题的正解（安装到 `Program Files`）与"不改用户电脑配置"的约定冲突，留待单独决策。
- **过热看门狗的两条判据没有自动化断言**：判据实现位于 `src/WujieFan/Ui/FanModeController.cs`（UI 项目、依赖 WinForms），而测试工程只引用 Core，故「92℃ 连续 3 次」与「读失败 3 次且 ≥2s」无法在无 UI 环境下断言。这是分层边界，非遗漏。
- ~~CI 尚未在 GitHub Actions 上实跑~~ → **已实跑通过**（2026-11-17，run `44088236762`）：guard job 13s、build+test+smoke job 1m11s，两个 job 均 success，构建 0 警告、单测 148/148、版本三项一致；其后两次 push 的 CI 同样 success。
- **写 EC 的实机链路仍需管理员会话重跑**：见上方第一条与 `docs/03-实机验收手册.md`。

## 发布

- tag `v0.4.1` → 提交 `1bcd514`（最后一个代码提交；本文件与文档订正提交在其后，不改动二进制内容）。
- Release：https://github.com/Thousand00/wujiefan/releases/tag/v0.4.1
- `WujieFan-0.4.1-win-x64.zip` 265,971 B，sha256 `a50682ec…cb52`，18 entries；
  `WujieFan-0.4.1-win-x64-portable.zip` 98,333,625 B，sha256 `ca3e176a…1683`，11 entries。
  GitHub 侧记录的 digest 与本地逐字节一致；两个包内的 `WujieFan.integrity.json` 与包内文件 sha256/length 逐一相符。
