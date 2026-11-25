# Zephyr 仓库的协作约定

给后续接手的人（或 AI 代理）的硬性约束。改代码前先读完这一页。

## 推送前必须开代理

**`git push` / `gh release create` 之前，先确认 Watt Toolkit（进程名 `Steam++`）正在运行，并等 5 秒再推。**

不开代理直接推会得到：

```
fatal: unable to access 'https://github.com/Thousand00/zephyr-fan.git/':
Recv failure: Connection was reset
```

这是本机网络环境的问题，不是仓库或凭证的问题。代理起来之后同一台机器能正常推送。

### 怎么判断"真的推上去了"

`git push` 报成功不代表远端真的更新了，这种网络环境下它可能只成功了一半。
**每次推送后都要核对远端 HEAD 与本地一致：**

```bash
git ls-remote origin master | cut -f1   # 应等于下面这行
git rev-parse HEAD
```

两者不一致、或 `ls-remote` 返回空，就等代理连通后重推。
`gh release create` 同理：`gh release view` 在后台任务完成前会读到 `isDraft: true` 且资产不全，必须等任务结束后再确认一次。

## 发版流程

1. 只改 `Directory.Build.props` 里的 `<Version>` / `<FileVersion>` / `<AssemblyVersion>`（三处一起改，版本号只有这一个来源）。
2. `pwsh -NoProfile -File tools/make-release.ps1 -Version <版本>` —— 脚本自带 `tools/verify.ps1 -FailOnSkip` 门禁，verify 不过不会打包。
3. 打包后**必须实机启动一次**：`dotnet build` 通过 + 单测全绿碰不到启动期异常（0.5.3 就因此发了个启动即崩的包，抛 `Win32Exception (1410) 类已存在`）。
4. 装到 `E:\tools\Zephyr`，跑 `ZephyrLauncher.exe --tray`，确认进程存活、`%LOCALAPPDATA%\Zephyr\ui.log` 无新增异常。
5. 打 tag → 推 tag → `gh release create` → 核对资产 digest 与本地 sha256 一致。
6. 清理 `dist` 里上一版的 zip 与临时目录。

### 发布说明只写改了什么

`docs/release-notes-<版本>.md` 是给用户看的：**只写这一版改了什么、怎么验证的、有什么已知问题**。
不要把审计报告的条目、逐条反驳、以及"报告说 X 但实际是 Y"这类往来写进去 —— 那是
`docs/0x-…处置报告.md` 的职责，两者不要混。用户明确提过这一点。

判断标准：一段话如果只有"读过那份审计报告的人"才关心，就不属于发布说明。

## 测试与构建

- `dotnet test` 的 `--artifacts-path` **必须指向 `%TEMP%` 下的随机目录**。指到工作区里会让 testhost 崩在
  `ProcessManager.OpenProcess → EnsureWatchingForExit`（`Win32Exception (5) 拒绝访问`）。
- 跑 `tools/verify.ps1` 时 `TMP` / `TEMP` 也必须是 `%TEMP%` 下的有效目录；`unset TMP TEMP` 会报
  `无法将自变量绑定到参数 'Path'，因为它为 null`。
- `tools/verify.ps1` 与 `tools/make-release.ps1` 带 UTF-8 **BOM**，编辑后必须还原，否则 Windows PowerShell 5.1 按 GBK 解析中文注释。
- 本机 bash 没有 `unzip` / `perl` / `python3`（`python` 可用，且带 PIL），需要时用 pwsh 或 python。

## 安全红线（不要为了"更顺手"而放松）

- 强冷模式**只升不降**；PWM 超过 184 不是更快而是停转。
- 写 EC 走 `PwmGuard`，不要新增绕过它的写入口。
- 读失败交还自动的判据不要动。
- **CPU 温度不读、过热保护不存在**。0.7.0 起按用户要求拆掉了这一整条链路：不读
  `EcRegisters.CpuTemperature`（除了 `DetectPort()` 探测端口那一次）、`FanSample` 没有
  `Temperature`、RTSS 不发 `<temp>` 节点、设置里没有阈值。**不要"顺手加回来"** ——
  每轮读它要多跑 12 次驱动往返，而且它与"强冷只升不降"的产品定位有冲突：过热保护的
  动作是自动取消强冷，等于给用户的选择加了一个隐蔽的否决权。
- 机型校验失败时 `force` 只解锁观察（只读），不解锁控制。
- 别改 `AIDA64_SensorValues` 共享内存名与 EC 寄存器地址 —— 是外部契约。
- 别改 `SupportedHardware` 里的 `"WUJIE 14"` / `"WUJIE 14+"` —— 是这块主板的产品名，与产品改名无关。
- 别动 `AutoStartManager.Run` 里「先异步起两条读流 → 再 `WaitForExit(15000)`」的顺序。同步 `CopyTo` 会一直读到 EOF，子进程卡住且不关 stdout 时它永不返回，下面那个超时根本没机会执行，调用方（UI 线程）会无限卡住 —— 超时形同虚设。那段 12 行注释就是这次故障的复现说明。
- `AppSettings.LoadMigrateError` / `LoadMigrated` 这类「启动期一次性事实」必须留在 **static** 字段上。`JsonSerializer` 不序列化 static 成员（给它们加 `[JsonInclude]`、`[JsonPropertyName]` 或开 `IncludeFields` 都试过，都不行 —— 这点已实测确认），所以 `Save()` 的读写循环碰不到它们。**改成实例属性就会进 JSON**，`Save()` 之后被覆盖成 null，"旧设置没搬过来"这句提示用户再也看不到。

## 界面

- 全自绘：`FormBorderStyle.None` + `Dpi.Px()` 手算，PerMonitorV2。
- 色值/尺寸只从 `Theme.cs` 取，不要在控件里写死颜色。
- 改完 UI 用反射渲染截图核对（`DrawToBitmap`），不要靠"看起来应该对"。

## src/Zephyr.Core/RuntimeIdentity.cs 的特殊约束

这个文件被**链接编译**进启动器（`src/Zephyr.Launcher/Zephyr.Launcher.csproj` 里的 `<Compile Include="..\Zephyr.Core\RuntimeIdentity.cs" Link="RuntimeIdentity.cs" />`），因为启动器为了缩小攻击面刻意不引用整个 Core。

所以在这个文件里**只能用 `System.*` 类型**，不能 `using Zephyr.Core.*`，也不能引用 Core 里的任何类型 —— 加了会让启动器整个编译不过，而主程序和 Core 都能过，很容易在本地看不出来。改完这个文件请务必构建一次 `Zephyr.Launcher`。
