# Zephyr 0.6.2 发布说明

第四轮审计给出的结论是「0 个实缺陷、4 条 P3 注意点」。这一版处置了其中两条真正值得动手的，其余三条说明了为什么不动。

## 这一版改了什么

### 1. 旧版进程还在跑时会提醒（R2）

这是四条里唯一会让用户**当下就感觉坏掉**的。改名之后，两个版本的单实例互斥量不同名：

- 旧版 `Global\WujieFan_SingleInstance`
- 新版 `Global\Zephyr_SingleInstance`

所以「装完新版、旧版忘了关」这种情形下，两个进程会**同时活着**，然后一起去抢 EC 的两把锁（`Global\Access_ISABUS.HTP.Method` / `Global\Access_EC`）。`FanReader.MutexTimeoutMs` 是 300 毫秒，抢不到就进读失败分支 —— 结果是两个版本**都控制不了风扇**，状态栏反复报「EC 正被占用」。

新版启动时现在会检测旧进程并在状态栏警告一次：

> 检测到旧的 WujieFan 进程还在运行，两个版本会抢 EC 锁导致都控制不了风扇。请先退出旧版。

它**不替你结束进程**。结束别人的进程属于改动用户系统，而且旧版是提权进程，普通权限本来也杀不掉。

这条和已有的「旧计划任务」提示是两个不同的坑，必须分开查：旧任务要等到下次开机才起作用，旧进程是现在就在抢锁。两条同时命中时优先显示进程那条。

### 2. 迁移失败的真正原因不再被吞掉

`AppSettings.LastError` 是「最近一次 Load/Save 的失败原因」，而它会**被之后任何一次 Save 覆盖**。旧配置迁移失败时把它写进 `LastError`，等于用户的快捷键和阈值全回默认了、界面上却什么都看不到 —— 下一次切模式存盘就把原因冲掉了。

新增一个不会被冲掉的独立信号 `AppSettings.LoadMigrateError`，界面据此提示一次：

> 旧版本的设置没能沿用过来，本次按默认设置启动：`<原因>`

顺带修掉一个会**制造假故障**的地方：两个实例同时启动时会撞上同一次迁移，两个都通过 `File.Exists` 检查，只有先到的那个 `File.Copy` 成功，后到的抛「文件已存在」。这其实不是失败（配置就在位），但原代码会把它记成错误。现在只有在文件确实仍不存在时才记录 —— 否则每次开机自启 + 手动点快捷方式都会在日志里留一条假故障。

## 没改的三条，以及为什么

### R1（报告说「两个实例的 LoadMigrated 都成了 true」）—— 报告结论不成立

`LoadMigrated = true` 在 `try` 块内、且位于 `File.Copy` **之后**：

```csharp
try
{
    if (File.Exists(FilePath)) return;
    if (!File.Exists(LegacyFilePath)) return;

    Directory.CreateDirectory(Dir);
    File.Copy(LegacyFilePath, FilePath, overwrite: false);   // 这里抛就跳走

    TryCopyLegacyLog("ui.log");
    ...
    LoadMigrated = true;                                      // 到不了这里
}
catch (Exception ex) { ... }
```

后到的实例在 `File.Copy` 就抛了，执行不到那一行，所以它的 `LoadMigrated` 是 `false`。真实存在的是**相反**的小瑕疵：先到的实例可能只完成了复制、还没走完三行日志复制就被打断，于是它那个实例不提示 —— 那是「少提示一次」，不是「多提示」，配置值在任何情况下都是对的。

### R3（RuntimeIdentity 被链接编译的维护约束）—— 约束本来就在

文件头从 v0.6.0 起就写着：

> 这个文件会被主程序、Core 与启动器三处**链接编译**（见两个 csproj 的 `<Compile Include>`），因为启动器为了缩小攻击面刻意不引用 Core。因此这里不能用任何需要引用 Core 其他类型的写法。

报告建议写进 `AGENTS.md` 更显眼 —— 这一点采纳了，`AGENTS.md` 新增了一节，并补了一条可执行的检查（改完这个文件要单独构建一次 `Zephyr.Launcher`，因为主程序和 Core 都能过的时候启动器可能是坏的）。

### R4（`EnsureOpen` 与 `Apply` 双重重置退避计时器）—— 同意不改

用户主动勾选 RTSS 开关时应该立即响应，而不是被 10 秒退避挡住。报告自己也是这么判的。把它「修」成不被重置，反而是把用户的主动操作降级成「等 10 秒」。

## 测试

单测 252 → **254**。新增两条互为反面：

- `迁移失败必须留下可观测的痕迹` —— 在新配置位置放一个同名**目录**（`File.Exists` 对目录返回 false，于是走到复制那一步，而 `File.Copy` 往目录路径写会抛），断言 `LoadMigrateError` 非空、`LoadMigrated` 为 false、设置回到安全默认。
- `迁移成功之后不该留下失败痕迹` —— 确认上一条不是因为 `LoadMigrateError` 永远非空才通过的。

变异验证：把 `LoadMigrateError = ex.Message;` 删掉，第一条立刻变红。

**一个诚实的缺口**：并发保护那一段（`if (File.Exists(FilePath)) return;`）**没有**被单测钉住 —— 单进程内造不出「两个实例同时迁移」。我试过删掉它跑测试，254 条仍然全过。它现在只有代码里的注释和这条发布说明作为依据。

## 验证范围

**已验：**

- `dotnet build Zephyr.sln -c Release` → 0 警告 0 错误（含单独构建 `Zephyr.Launcher`）。
- `dotnet test` → 254/254。
- `tools/verify.ps1` → 5/5 全过、EXITCODE=0；XML 契约 `ZEPHYR_FAN1/FAN2/CPUT` 正常。
- 打包后实机启动 `E:\tools\Zephyr`，进程存活、`ui.log` 零新增。

**未验：**

- 「旧版进程真的在跑」这条提示没有在实机上真造出两实例并存的场景（需要保留一份 0.5.7 的旧包）。逻辑是 `Process.GetProcessesByName("WujieFan")`，返回数组长度大于 0 即命中。
- 迁移失败提示同理，只在单测里用「同名目录」制造过失败。

## 下载

| 文件 | 大小 | SHA-256 |
| --- | --- | --- |
| `Zephyr-0.6.2-win-x64.zip` | 275,814 B | `4ae0707e17ab4d8f23e15d414f8ca511052f8bed671079822ad2dc7287d3dcdc` |
| `Zephyr-0.6.2-win-x64-portable.zip` | 98,332,331 B | `02247574867b18e00cdc81149b6f24187b109a83fa26e3a1bdc27f3ac4d32f96` |

## 已知问题

- CPU 电压依赖 RTSS 的 LHM 通道，而 `LHMDataProvider.sys` 需要能加载内核驱动。若被安全软件拦截，`/amdcpu/*` 的电压会是一个恒定的占位值 —— 叠加层里的 `CPU Vol` 就会一直显示同一个数字。这不是本程序的问题，Zephyr 不提供电压数据。
- `tools/make-release.ps1` 在打包前会结束正在运行的 Zephyr（否则文件被占用）。装完之后想看运行状态，就不要再打包一次。
